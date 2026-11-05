// Tests password access, device IDs, and the relay with the real classes over loopback.
// All files go to a temporary folder. It captures the screen but never sends mouse or keyboard input.
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Tahakkum
{
    static class AccessTest
    {
        const int HostPort = 47897;

        static int failures;
        static string root;
        static RelayServer relayServer;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        static bool Wait(Func<bool> done, int ms = 10000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (done()) return true;
                Thread.Sleep(20);
            }
            return done();
        }

        static X509Certificate2 NewCertificate(string name)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                CertificateRequest req = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)))
                    return new X509Certificate2(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
        }

        // Records what a connection attempt ended in.
        static string Attempt(Func<ViewerClient, Connection> dial, out Connection conn)
        {
            conn = null;
            try
            {
                conn = dial(new ViewerClient());
                return "connected";
            }
            catch (ConnectFailure f)
            {
                return f.Message;
            }
        }

        static void EndSession(HostServer server, Connection conn, ref HostSession session)
        {
            HostSession s = session;
            session = null;
            if (conn != null) conn.Close();
            if (s != null) s.End();
            Thread.Sleep(600);
        }

        static HostSession current;
        static int consentAsked;
        static bool consentApproves = true;

        static HostServer StartHost(X509Certificate2 cert)
        {
            HostServer server = new HostServer(cert);
            server.ConsentNeeded += r =>
            {
                Interlocked.Increment(ref consentAsked);
                if (consentApproves) r.Approve(); else r.Reject();
            };
            server.SessionStarted += s => current = s;
            return server;
        }

        static int Main()
        {
            root = Path.Combine(Path.GetTempPath(), "tahakkum-access-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppFiles.DirOverride = root;
            LicenseManager.Settings = new LicenseSettings { ApiUrl = "" };
            try
            {
                MachineIdentity();
                Contacts();
                Parsing();
                Password();
                Relay();
            }
            catch (Exception ex)
            {
                Check("unexpected exception", false, ex.ToString());
            }
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            AppFiles.DirOverride = null;
            try { Directory.Delete(root, true); } catch (Exception) { }
            return failures;
        }

        static void MachineIdentity()
        {
            // An identity from the older per-user location carries over, so nobody sees a warning after updating.
            X509Certificate2 older = NewCertificate("older");
            File.WriteAllBytes(Path.Combine(AppFiles.Dir, "identity.bin"), ProtectedData.Protect(older.Export(X509ContentType.Pkcs12, "tahakkum"), null, DataProtectionScope.CurrentUser));
            string first = Identity.Fingerprint(Identity.LoadOrCreateCertificate());
            Check("older identity carries over", first == Identity.Fingerprint(older));
            File.Delete(Path.Combine(AppFiles.Dir, "identity.bin"));
            string second = Identity.Fingerprint(Identity.LoadOrCreateCertificate());
            Check("identity is stable", first == second);
            Check("identity is stored for the machine", File.Exists(Path.Combine(AppFiles.MachineDir, "identity.bin")));
            Check("identity is not stored for the user", !File.Exists(Path.Combine(AppFiles.Dir, "identity.bin")));

            // A new Windows user has no per-user files at all, and the machine identity stays.
            string otherUser = Path.Combine(root, "other-user");
            Directory.CreateDirectory(otherUser);
            AccessPassword.Set("old-employee-pass");
            string accessFile = Path.Combine(AppFiles.Dir, "access.bin");
            File.Move(accessFile, Path.Combine(otherUser, "access.bin"));
            Check("identity survives a different user", Identity.Fingerprint(Identity.LoadOrCreateCertificate()) == first);
            Check("the previous user's password does not apply", !AccessPassword.IsSet);
        }

        static void Contacts()
        {
            Check("the list starts empty", ContactBook.Load().Count == 0);
            Check("a contact saves", ContactBook.Save("جهاز المحاسبة", "192.168.1.20", null) == null);
            Check("an id with spaces saves as digits", ContactBook.Save("سالم", "142 868 838", null) == null && ContactBook.Load().Exists(c => c.Address == "142868838"));
            Check("host name and port are accepted", ContactBook.Save("Server", "pc-1.local:47800", null) == null);
            Check("Arabic names survive reloading", ContactBook.Load().Exists(c => c.Name == "جهاز المحاسبة" && c.Address == "192.168.1.20"));

            Check("a name is required", ContactBook.Save("   ", "10.0.0.1", null) != null);
            Check("an empty address is refused", ContactBook.Save("x", "  ", null) != null);
            Check("an address with spaces is refused", ContactBook.Save("x", "10.0 0.1", null) != null);
            Check("a command-like address is refused", ContactBook.Save("x", "a;del c:\\", null) != null);

            int count = ContactBook.Load().Count;
            ContactBook.Save("اسم جديد", "192.168.1.20", null);
            Check("the same address updates instead of duplicating", ContactBook.Load().Count == count && ContactBook.Load().Exists(c => c.Name == "اسم جديد"));
            ContactBook.Save("اسم جديد", "192.168.1.21", "192.168.1.20");
            Check("editing an address moves the entry", ContactBook.Load().Count == count && !ContactBook.Load().Exists(c => c.Address == "192.168.1.20"));

            ContactBook.Save("سطر\tمزور\nثان\u202Eخدعة", "10.0.0.9", null);
            Contact tricky = ContactBook.Load().Find(c => c.Address == "10.0.0.9");
            Check("tabs, line breaks and bidi cannot corrupt the file", tricky != null && tricky.Name.IndexOfAny(new[] { '\t', '\n', '\u202E' }) < 0, tricky == null ? "" : tricky.Name);
            Check("nothing extra appears after that", ContactBook.Load().Count == count + 1);

            ContactBook.Remove("10.0.0.9");
            Check("removing works", ContactBook.Load().Find(c => c.Address == "10.0.0.9") == null);
            foreach (Contact c in ContactBook.Load()) ContactBook.Remove(c.Address);
            Check("the list empties", ContactBook.Load().Count == 0);

            // A different Windows user has a different folder, so a different list.
            ContactBook.Save("لي", "10.0.0.1", null);
            string mine = AppFiles.DirOverride;
            AppFiles.DirOverride = Path.Combine(root, "another-employee");
            Check("another Windows user sees their own list", ContactBook.Load().Count == 0);
            AppFiles.DirOverride = mine;
            ContactBook.Remove("10.0.0.1");
        }

        static void Parsing()
        {
            Check("id with spaces", ViewerClient.ParseDeviceId("123 456 789") == "123456789");
            Check("id with dashes", ViewerClient.ParseDeviceId("123-456-789") == "123456789");
            Check("ip is not an id", ViewerClient.ParseDeviceId("192.168.1.10") == null);
            Check("short number is not an id", ViewerClient.ParseDeviceId("12345") == null);
            Check("host name is not an id", ViewerClient.ParseDeviceId("server1") == null);

            AccessPassword.Set("correct horse");
            AccessKey key = AccessPassword.Load();
            Check("key is stored without the password", !File.ReadAllText(Path.Combine(AppFiles.Dir, "access.bin")).Contains("correct"));
            byte[] nonce = new byte[32];
            byte[] good = AccessPassword.Proof(AccessPassword.Derive("correct horse", key.Salt, key.Iterations), nonce, "AA");
            Check("right password matches", AccessPassword.SameBytes(good, AccessPassword.Proof(key.Key, nonce, "AA")));
            Check("wrong password differs", !AccessPassword.SameBytes(AccessPassword.Proof(AccessPassword.Derive("wrong", key.Salt, key.Iterations), nonce, "AA"), AccessPassword.Proof(key.Key, nonce, "AA")));
            Check("proof is bound to the certificate", !AccessPassword.SameBytes(good, AccessPassword.Proof(key.Key, nonce, "BB")));
            AccessPassword.Clear();
            Check("clearing removes it", !AccessPassword.IsSet);
        }

        static void Password()
        {
            X509Certificate2 cert = NewCertificate("host");
            HostServer server = StartHost(cert);
            server.Start(HostPort);
            Func<PasswordPrompt, Func<ViewerClient, Connection>> direct = prompt =>
                c => c.Connect("127.0.0.1", HostPort, code => { }, code => true, prompt);
            Connection conn;
            string result;
            bool asked = false;

            // No password set: the approval prompt, exactly as before.
            consentAsked = 0;
            result = Attempt(direct((code, failed) => { asked = true; return "x"; }), out conn);
            Check("without a password, approval is required", result == "connected" && consentAsked == 1 && !asked, result);
            Wait(() => current != null);
            EndSession(server, conn, ref current);

            AccessPassword.Set("correct horse");

            consentAsked = 0;
            result = Attempt(direct((code, failed) => "correct horse"), out conn);
            Check("correct password opens with no prompt", result == "connected" && consentAsked == 0, result);
            Check("session runs", Wait(() => current != null));
            EndSession(server, conn, ref current);

            // Falling back to approval is the viewer's choice, and rejection still works.
            consentAsked = 0;
            consentApproves = false;
            result = Attempt(direct((code, failed) => ""), out conn);
            Check("asking for approval shows the prompt", consentAsked == 1, result);
            Check("rejection still refuses", conn == null);
            consentApproves = true;
            Thread.Sleep(16000);

            consentAsked = 0;
            result = Attempt(direct((code, failed) => null), out conn);
            Check("closing the password box gives up quietly", result == "" && consentAsked == 0, result);

            int prompts = 0;
            bool sawFailure = false;
            consentAsked = 0;
            result = Attempt(direct((code, failed) => { prompts++; sawFailure |= failed; return "wrong password"; }), out conn);
            Check("wrong password is refused after three tries", conn == null && prompts == Proto.PasswordAttemptsPerConnection, prompts + " " + result);
            Check("wrong password is reported", sawFailure);
            Check("wrong password never reaches approval", consentAsked == 0);

            // Five failures lock this source: the password stops being offered at all,
            // even a correct one, while the approval path stays available.
            result = Attempt(direct((code, failed) => "wrong password"), out conn);
            Check("failures build up to the lock", conn == null, result);
            consentAsked = 0;
            asked = false;
            result = Attempt(direct((code, failed) => { asked = true; return "correct horse"; }), out conn);
            Check("locked source gets no password prompt", !asked && consentAsked == 1, result);
            Wait(() => current != null);
            EndSession(server, conn, ref current);

            AccessPassword.Clear();
            server.Stop();
        }

        static void Relay()
        {
            X509Certificate2 relayCert = NewCertificate("relay");
            relayServer = new RelayServer(relayCert, Path.Combine(root, "relay-data"));
            relayServer.Start(0);
            RelayConfig.Override = new RelayConfig { Host = "127.0.0.1", Port = relayServer.Port, Pin = Identity.Fingerprint(relayCert) };

            Connection conn;
            string result;
            result = Attempt(c => c.ConnectById("999999999", code => { }, code => true, (code, failed) => ""), out conn);
            Check("unknown id is reported as offline", conn == null && result.Contains("غير متصل"), result);

            X509Certificate2 hostCert = NewCertificate("relayed-host");
            HostServer server = StartHost(hostCert);
            RelayLink link = new RelayLink(hostCert, server.HandleRelayed);
            link.Start();
            Check("host gets an id", Wait(() => link.Id != null) && ViewerClient.ParseDeviceId(link.Id) == link.Id, link.Id);
            string id = link.Id;

            link.Stop();
            Check("stopping removes the id", link.Id == null);
            Thread.Sleep(300);
            result = Attempt(c => c.ConnectById(id, code => { }, code => true, (code, failed) => ""), out conn);
            Check("stopped host is offline", conn == null && result.Contains("غير متصل"), result);

            link.Start();
            Wait(() => link.Id != null);
            Check("the same machine keeps its id", link.Id == id, link.Id);

            // A session through the relay with the same password check as a direct one.
            AccessPassword.Set("relay password");
            consentAsked = 0;
            int frames = 0;
            result = Attempt(c => c.ConnectById(id, code => { }, code => true, (code, failed) => "relay password"), out conn);
            Check("password session opens through the relay", result == "connected" && consentAsked == 0, result);
            if (conn != null)
            {
                Connection live = conn;
                live.Received += (type, data) =>
                {
                    if (type != Msg.Frame) return;
                    Interlocked.Increment(ref frames);
                    using (BinaryReader r = Pack.Read(data)) live.Send(Msg.Ack, Pack.Int(r.ReadInt32()));
                };
                live.Start();
                Check("screen frames flow through the relay", Wait(() => Volatile.Read(ref frames) >= 2, 8000), frames + " frames");
                string viewerSeen = current != null ? current.RemoteIp : "";
                Check("host sees the viewer's address, from the relay", viewerSeen == "127.0.0.1", viewerSeen);
            }
            EndSession(server, conn, ref current);

            // Approval works through the relay too.
            AccessPassword.Clear();
            Thread.Sleep(300);
            consentAsked = 0;
            result = Attempt(c => c.ConnectById(id, code => { }, code => true, (code, failed) => ""), out conn);
            Check("approval session opens through the relay", result == "connected" && consentAsked == 1, result);
            Wait(() => current != null);
            EndSession(server, conn, ref current);

            // Another program cannot take the id without the private key.
            Check("registering without the private key fails", !RegisterWithStolenCertificate(hostCert));
            Thread.Sleep(200);
            Check("the real host stays registered", link.Id == id);

            // An impostor registered under its own certificate gets its own id, never this one.
            X509Certificate2 other = NewCertificate("other");
            RelayLink otherLink = new RelayLink(other, server.HandleRelayed);
            otherLink.Start();
            Wait(() => otherLink.Id != null);
            Check("another machine gets another id", otherLink.Id != null && otherLink.Id != id, otherLink.Id);
            otherLink.Stop();

            // A relay that lies about the host: the viewer refuses before sending anything.
            X509Certificate2 fake = NewCertificate("man-in-the-middle");
            Check("wrong relay certificate is refused", WrongPinRefused(fake));

            link.Stop();
            relayServer.Stop();
        }

        static bool RegisterWithStolenCertificate(X509Certificate2 stolen)
        {
            using (TcpClient tcp = new TcpClient("127.0.0.1", relayServer.Port))
            {
                tcp.ReceiveTimeout = 5000;
                Stream s = RelayConfig.Override.OpenStream(tcp);
                Connection.WritePacket(s, Msg.RelayRegister, Pack.Build(w =>
                {
                    byte[] raw = stolen.GetRawCertData();
                    w.Write(raw.Length);
                    w.Write(raw);
                    w.Write("");
                }));
                Connection.ReadPacket(s, Proto.MaxPreApproval);
                using (RSA wrongKey = RSA.Create(2048))
                    Connection.WritePacket(s, Msg.RelayProof, wrongKey.SignData(new byte[32], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
                try { return Connection.ReadPacket(s, Proto.MaxPreApproval).Type == Msg.RelayRegistered; }
                catch (IOException) { return false; }
            }
        }

        static bool WrongPinRefused(X509Certificate2 pinned)
        {
            RelayConfig real = RelayConfig.Override;
            RelayConfig.Override = new RelayConfig { Host = real.Host, Port = real.Port, Pin = Identity.Fingerprint(pinned) };
            Connection conn;
            string result = Attempt(c => c.ConnectById("123456789", code => { }, code => true, (code, failed) => ""), out conn);
            RelayConfig.Override = real;
            return conn == null && result.Contains("هوية خادم");
        }
    }
}
