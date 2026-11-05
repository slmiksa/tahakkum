using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Tahakkum
{
    sealed class ConnectFailure : Exception
    {
        public ConnectFailure(string message) : base(message) { }
    }

    // Returns the password, "" to ask for approval instead, or null to give up.
    delegate string PasswordPrompt(string code, bool failedBefore);

    // Runs the connect + consent handshake on a background thread.
    sealed class ViewerClient
    {
        const int ConnectTimeoutMs = 6000;

        volatile TcpClient tcp;
        volatile bool cancelled;

        public string HostName;
        public string Code;

        public void Cancel()
        {
            cancelled = true;
            TcpClient t = tcp;
            if (t != null)
            {
                try { t.Close(); } catch (Exception) { }
            }
        }

        public static void ParseAddress(string input, out string host, out int port)
        {
            input = input.Trim();
            host = input;
            port = Proto.DefaultPort;
            int colon = input.LastIndexOf(':');
            if (colon > 0 && input.IndexOf(':') == colon)
            {
                int p;
                if (int.TryParse(input.Substring(colon + 1), out p) && p > 0 && p < 65536)
                {
                    host = input.Substring(0, colon);
                    port = p;
                }
            }
        }

        // Removes the spaces people naturally type in an ID. Returns null for anything that is not an ID.
        public static string ParseDeviceId(string input)
        {
            string digits = input.Replace(" ", "").Replace("-", "");
            return System.Text.RegularExpressions.Regex.IsMatch(digits, Proto.DeviceIdPattern) ? digits : null;
        }

        // Reaches a machine outside this network through the relay server. The session's own
        // TLS then runs inside the relay's stream, exactly as it does over a direct connection.
        public Connection ConnectById(string id, Action<string> onWaiting, Func<string, bool> confirmChangedIdentity, PasswordPrompt askPassword)
        {
            RelayConfig config = RelayConfig.Load();
            if (config == null) throw new ConnectFailure("لم يُضبط خادم الاتصال عبر الإنترنت في هذه النسخة.");
            TcpClient client = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            tcp = client;
            return Guarded(client, id, () =>
            {
                string notReached = "تعذر الوصول إلى خادم الاتصال. تأكد من اتصالك بالإنترنت.";
                Open(client, config.Host, config.Port, notReached, notReached, notReached);
                Stream relay;
                try { relay = config.OpenStream(client); }
                catch (Exception ex)
                {
                    if (ex is IOException || ex is AuthenticationException) throw new ConnectFailure("تعذر التحقق من هوية خادم الاتصال.");
                    throw;
                }
                Connection.WritePacket(relay, Msg.RelayDial, Pack.Build(w => w.Write(id)));
                client.ReceiveTimeout = 20000;
                Packet answer = Connection.ReadPacket(relay, Proto.MaxPreApproval);
                if (answer.Type == Msg.RelayOffline) throw new ConnectFailure("هذا الجهاز غير متصل الآن، أو أن المعرّف غير صحيح.");
                if (answer.Type == Msg.RelayRefused) throw new ConnectFailure("محاولات كثيرة خلال وقت قصير. حاول بعد دقيقة.");
                if (answer.Type != Msg.RelayGo) throw new ConnectFailure("رد غير متوقع من خادم الاتصال.");
                string expected;
                using (BinaryReader r = Pack.Read(answer.Data)) expected = r.ReadString();
                return Handshake(client, relay, "id:" + id, expected, onWaiting, confirmChangedIdentity, askPassword);
            });
        }

        // confirmChangedIdentity runs when a host's certificate differs from the pinned one.
        public Connection Connect(string host, int port, Action<string> onWaiting, Func<string, bool> confirmChangedIdentity, PasswordPrompt askPassword)
        {
            TcpClient client = new TcpClient(System.Net.Sockets.AddressFamily.InterNetwork);
            tcp = client;
            return Guarded(client, host, () =>
            {
                Open(client, host, port,
                    "تعذر العثور على هذا العنوان. تأكد من كتابة IP بشكل صحيح.",
                    "لا يوجد رد من الجهاز. تأكد أنه يعمل وعلى نفس الشبكة، وأن الجدار الناري يسمح بالمنفذ " + port + ".",
                    "تعذر الوصول إلى الجهاز. تأكد من الشبكة والجدار الناري.");
                return Handshake(client, client.GetStream(), host, null, onWaiting, confirmChangedIdentity, askPassword);
            });
        }

        void Open(TcpClient client, string host, int port, string notFound, string noReply, string unreachable)
        {
            IAsyncResult ar;
            try
            {
                ar = client.BeginConnect(host, port, null, null);
            }
            catch (SocketException)
            {
                throw new ConnectFailure(notFound);
            }
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                throw new ConnectFailure(noReply);
            try
            {
                client.EndConnect(ar);
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.ConnectionRefused && notFound != unreachable)
                    throw new ConnectFailure("الجهاز موجود لكن البرنامج غير مشغّل عليه، أو استقبال الطلبات متوقف.");
                if (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
                    throw new ConnectFailure(notFound);
                throw new ConnectFailure(unreachable);
            }

            client.NoDelay = true;
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;
            client.ReceiveBufferSize = 512 * 1024;
        }

        Connection Guarded(TcpClient client, string target, Func<Connection> body)
        {
            try
            {
                return body();
            }
            catch (ConnectFailure)
            {
                client.Close();
                if (cancelled) throw new ConnectFailure("");
                throw;
            }
            catch (Exception ex)
            {
                client.Close();
                if (cancelled) throw new ConnectFailure("");
                if (!(ex is IOException || ex is ObjectDisposedException))
                    AppFiles.Log("خطأ أثناء الاتصال بـ " + target + ": " + ex);
                throw new ConnectFailure("انقطع الاتصال بالجهاز قبل اكتمال الطلب.");
            }
        }

        Connection Handshake(TcpClient client, Stream transport, string pinKey, string expectedFingerprint, Action<string> onWaiting, Func<string, bool> confirmChangedIdentity, PasswordPrompt askPassword)
        {
            X509Certificate seen = null;
            SslStream ssl = new SslStream(transport, false, (s, cert, chain, errors) =>
            {
                // The certificate passed here is disposed when the callback returns.
                if (cert == null) return false;
                seen = new X509Certificate2(cert);
                return true;
            });
            try
            {
                ssl.AuthenticateAsClient("tahakkum", null, SslProtocols.Tls12, false);
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is AuthenticationException)
                    throw new ConnectFailure("هذا العنوان لا يشغّل برنامج تحكّم، أو تعذر إنشاء اتصال مشفّر معه.");
                throw;
            }

            string fingerprint = Identity.Fingerprint(seen);
            Code = Identity.VerificationCode(seen);
            if (expectedFingerprint != null && expectedFingerprint != fingerprint)
                throw new ConnectFailure("هوية الجهاز لا تطابق ما سجّله خادم الاتصال، فتم إيقاف الاتصال حمايةً لك.");
            string pinned = KnownHosts.Get(pinKey);
            if (pinned != null && pinned != fingerprint)
            {
                if (!confirmChangedIdentity(Code)) throw new ConnectFailure("تم إلغاء الاتصال لأن هوية الجهاز تغيرت.");
            }
            KnownHosts.Set(pinKey, fingerprint);

            Connection.WritePacket(ssl, Msg.Hello, Pack.Build(w =>
            {
                w.Write(Proto.Magic);
                w.Write(Proto.Version);
                w.Write(Environment.MachineName);
                w.Write(Environment.UserName);
            }));

            client.ReceiveTimeout = (Proto.ConsentSeconds + 15) * 1000;
            while (true)
            {
                Packet p = Connection.ReadPacket(ssl, Proto.MaxPreApproval);
                if (p.Type == Msg.AuthChallenge)
                {
                    AnswerChallenge(ssl, p, fingerprint, askPassword);
                }
                else if (p.Type == Msg.Pending)
                {
                    using (BinaryReader r = Pack.Read(p.Data))
                        HostName = Identity.Sanitize(r.ReadString(), 64);
                    onWaiting(Code);
                }
                else if (p.Type == Msg.Approved)
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    Connection conn = new Connection(client, ssl);
                    conn.MaxIncoming = Proto.MaxFromHost;
                    return conn;
                }
                else if (p.Type == Msg.Denied)
                {
                    throw new ConnectFailure(DenyText(p.Data.Length > 0 ? (DenyReason)p.Data[0] : DenyReason.Rejected));
                }
                else
                {
                    throw new ConnectFailure("رد غير متوقع من الجهاز.");
                }
            }
        }

        void AnswerChallenge(Stream ssl, Packet challenge, string fingerprint, PasswordPrompt askPassword)
        {
            byte[] salt, nonce;
            int iterations;
            bool failedBefore;
            using (BinaryReader r = Pack.Read(challenge.Data))
            {
                salt = r.ReadBytes(16);
                iterations = r.ReadInt32();
                nonce = r.ReadBytes(32);
                failedBefore = r.ReadBoolean();
            }
            // The cost comes from the other side, so a hostile peer must not be able to freeze this machine.
            if (!AccessPassword.ValidIterations(iterations)) throw new ConnectFailure("رد غير متوقع من الجهاز.");

            string password = askPassword(Code, failedBefore);
            if (password == null) throw new ConnectFailure("");
            if (password.Length == 0)
            {
                Connection.WritePacket(ssl, Msg.AskConsent, new byte[0]);
                return;
            }
            byte[] key = AccessPassword.Derive(password, salt, iterations);
            Connection.WritePacket(ssl, Msg.AuthProof, AccessPassword.Proof(key, nonce, fingerprint));
        }

        static string DenyText(DenyReason reason)
        {
            switch (reason)
            {
                case DenyReason.Rejected: return "رفض الشخص أمام الشاشة طلب التحكم.";
                case DenyReason.Busy: return "هذه الشاشة مشغولة بطلب أو جلسة تحكم أخرى.";
                case DenyReason.Timeout: return "لم يرد أحد على الطلب خلال " + Proto.ConsentSeconds + " ثانية.";
                case DenyReason.Version: return "إصدار البرنامج على الجهاز الآخر مختلف. ثبّت نفس الإصدار على الجهازين.";
                case DenyReason.Cooldown: return "تم رفض طلبك قبل قليل. حاول مرة أخرى بعد بضع ثوانٍ.";
                case DenyReason.BadPassword: return "كلمة السر غير صحيحة وتجاوزت عدد المحاولات.";
                case DenyReason.NotLicensed: return "البرنامج على الجهاز الآخر غير مفعّل أو انتهت رخصته.";
                default: return "تم رفض الطلب.";
            }
        }
    }
}
