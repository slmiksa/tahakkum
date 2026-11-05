using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace Tahakkum
{
    // Where the relay server is. A company running its own server sets it in
    // %ProgramData%\Tahakkum\server.txt: "host[:port]", then optionally "pin=<certificate SHA-256>".
    sealed class RelayConfig
    {
        // The server the product ships with. Empty until one is deployed.
        const string DefaultServer = "82.47.165.29:443";
        // SHA-256 of the server's certificate, for a server reached by IP address with its own certificate.
        const string DefaultPin = "C7185EA8874BFCA42C53DCB897EB68AA52136A197885D4EF5780172E944AE896";

        public static RelayConfig Override = null;

        public string Host;
        public int Port = Proto.RelayPort;
        public string Pin;

        public static RelayConfig Load()
        {
            if (Override != null) return Override;
            string line = DefaultServer;
            string pin = DefaultPin.Length > 0 ? DefaultPin : null;
            try
            {
                string path = Path.Combine(AppFiles.MachineDir, "server.txt");
                if (File.Exists(path))
                {
                    string[] lines = File.ReadAllLines(path);
                    pin = null;
                    if (lines.Length > 0) line = lines[0].Trim();
                    if (lines.Length > 1 && lines[1].Trim().StartsWith("pin=")) pin = lines[1].Trim().Substring(4).Replace(":", "").ToUpperInvariant();
                }
            }
            catch (Exception) { }
            if (line.Length == 0) return null;

            RelayConfig c = new RelayConfig { Host = line, Pin = pin };
            int colon = line.LastIndexOf(':');
            int port;
            if (colon > 0 && int.TryParse(line.Substring(colon + 1), out port) && port > 0 && port < 65536)
            {
                c.Host = line.Substring(0, colon);
                c.Port = port;
            }
            return c;
        }

        // A pinned certificate is compared directly; otherwise the certificate must be publicly valid for the host name.
        public Stream OpenStream(TcpClient tcp)
        {
            SslStream ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, errors) =>
            {
                if (cert == null) return false;
                if (Pin != null) return Identity.Fingerprint(cert) == Pin;
                return errors == SslPolicyErrors.None;
            });
            ssl.AuthenticateAsClient(Host, null, SslProtocols.Tls12, false);
            return ssl;
        }
    }

    // Keeps this machine reachable by ID: an outbound connection to the relay, so no inbound
    // firewall rule is needed. The relay can only ask; each viewer still goes through the
    // same password or approval step as a direct connection.
    sealed class RelayLink
    {
        const int PingMs = 15000;
        const int SilenceMs = 45000;
        const int ConnectTimeoutMs = 8000;

        readonly X509Certificate2 certificate;
        readonly Action<TcpClient, Stream, string> incoming;
        readonly object gate = new object();
        volatile bool running;
        volatile string id;
        TcpClient current;
        int generation;

        public event Action StatusChanged;

        public RelayLink(X509Certificate2 certificate, Action<TcpClient, Stream, string> incoming)
        {
            this.certificate = certificate;
            this.incoming = incoming;
        }

        public string Id { get { return id; } }

        public void Start()
        {
            int mine;
            lock (gate)
            {
                if (running) return;
                running = true;
                mine = ++generation;
            }
            Background(() => Loop(mine), "relay-link");
        }

        public void Stop()
        {
            TcpClient t;
            lock (gate)
            {
                running = false;
                generation++;
                t = current;
                current = null;
                Monitor.PulseAll(gate);
            }
            if (t != null)
            {
                try { t.Close(); } catch (Exception) { }
            }
            SetId(null);
        }

        static void Background(ThreadStart body, string name)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        void SetId(string value)
        {
            id = value;
            Action h = StatusChanged;
            if (h != null) h();
        }

        bool IsCurrent(int mine)
        {
            lock (gate) return running && generation == mine;
        }

        void Loop(int mine)
        {
            int delay = 2000;
            while (IsCurrent(mine))
            {
                bool registered = false;
                try
                {
                    RunOnce(mine, ref registered);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is AuthenticationException || ex is InvalidDataException))
                        AppFiles.Log("خطأ في الاتصال بخادم الاتصال: " + ex);
                }
                if (!IsCurrent(mine)) return;
                SetId(null);
                delay = registered ? 2000 : Math.Min(60000, delay * 2);
                lock (gate)
                {
                    if (running && generation == mine) Monitor.Wait(gate, delay);
                }
            }
        }

        static TcpClient Dial(RelayConfig config)
        {
            TcpClient tcp = new TcpClient(AddressFamily.InterNetwork);
            IAsyncResult ar = tcp.BeginConnect(config.Host, config.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
            {
                tcp.Close();
                throw new IOException("relay timeout");
            }
            tcp.EndConnect(ar);
            tcp.NoDelay = true;
            tcp.ReceiveTimeout = SilenceMs;
            tcp.SendTimeout = 15000;
            return tcp;
        }

        void RunOnce(int mine, ref bool registered)
        {
            RelayConfig config = RelayConfig.Load();
            if (config == null) throw new IOException("no relay configured");
            TcpClient tcp = Dial(config);
            lock (gate)
            {
                if (running && generation == mine) current = tcp;
                else
                {
                    tcp.Close();
                    return;
                }
            }
            try
            {
                Stream s = config.OpenStream(tcp);
                Connection.WritePacket(s, Msg.RelayRegister, Pack.Build(w =>
                {
                    byte[] raw = certificate.GetRawCertData();
                    w.Write(raw.Length);
                    w.Write(raw);
                    w.Write(LicenseManager.Token);
                }));
                Packet challenge = Connection.ReadPacket(s, Proto.MaxPreApproval);
                if (challenge.Type != Msg.RelayChallenge) throw new InvalidDataException("relay challenge");

                // Signing proves ownership of this certificate, so nobody else can take over this machine's ID.
                using (RSA key = certificate.GetRSAPrivateKey())
                    Connection.WritePacket(s, Msg.RelayProof, key.SignData(Proto.RelaySigned(challenge.Data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

                Packet result = Connection.ReadPacket(s, Proto.MaxPreApproval);
                if (result.Type != Msg.RelayRegistered) throw new InvalidDataException("relay registration");
                using (BinaryReader r = Pack.Read(result.Data)) SetId(r.ReadString());
                registered = true;

                Background(() => PingLoop(s, tcp), "relay-ping");
                while (IsCurrent(mine))
                {
                    Packet p = Connection.ReadPacket(s, Proto.MaxPreApproval);
                    if (p.Type != Msg.RelayIncoming) continue;
                    byte[] token;
                    string viewerIp;
                    using (BinaryReader r = Pack.Read(p.Data))
                    {
                        token = r.ReadBytes(16);
                        viewerIp = Identity.Sanitize(r.ReadString(), 64);
                    }
                    Background(() => AcceptViewer(config, token, viewerIp), "relay-accept");
                }
            }
            finally
            {
                try { tcp.Close(); } catch (Exception) { }
            }
        }

        static void PingLoop(Stream s, TcpClient tcp)
        {
            try
            {
                while (tcp.Connected)
                {
                    Thread.Sleep(PingMs);
                    Connection.WritePacket(s, Msg.RelayPing, new byte[0]);
                }
            }
            catch (Exception) { }
        }

        void AcceptViewer(RelayConfig config, byte[] token, string viewerIp)
        {
            TcpClient tcp = null;
            try
            {
                tcp = Dial(config);
                tcp.ReceiveBufferSize = 512 * 1024;
                Stream s = config.OpenStream(tcp);
                Connection.WritePacket(s, Msg.RelayAccept, token);
                Packet go = Connection.ReadPacket(s, Proto.MaxPreApproval);
                if (go.Type != Msg.RelayGo) throw new InvalidDataException("relay go");
                incoming(tcp, s, viewerIp);
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is AuthenticationException || ex is InvalidDataException))
                    AppFiles.Log("خطأ في استقبال اتصال عبر الخادم: " + ex);
                if (tcp != null)
                {
                    try { tcp.Close(); } catch (Exception) { }
                }
            }
        }
    }
}
