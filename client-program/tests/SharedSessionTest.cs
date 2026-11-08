// Tests network discovery and sessions shared by several viewers, with the real classes over
// loopback. Injected input is observed but suppressed, so the real pointer never moves.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

namespace Tahakkum
{
    static class SharedSessionTest
    {
        const int HostPort = 47893;
        const int DiscoveryPort = 47895;

        static int failures;
        static int injected;
        static readonly List<ConsentRequest> requests = new List<ConsentRequest>();

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (!string.IsNullOrEmpty(detail) ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        static bool Wait(Func<bool> done, int ms = 8000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (done()) return true;
                Thread.Sleep(20);
            }
            return done();
        }

        sealed class Viewer
        {
            public string Name;
            public Connection Conn;
            public volatile bool Mine;
            public volatile string Controller = "";
            public volatile int Count;
            public volatile int States;
            public int Frames;
            public volatile bool Closed;
            public string Failure;
        }

        static Viewer Join(string name)
        {
            Profile.DisplayName = name;
            Viewer v = new Viewer { Name = name };
            try
            {
                v.Conn = new ViewerClient().Connect("127.0.0.1", HostPort, c => { }, c => true, (c, f) => "");
            }
            catch (ConnectFailure f)
            {
                v.Failure = f.Message;
                return v;
            }
            Connection conn = v.Conn;
            conn.Received += (type, data) =>
            {
                if (type == Msg.ControlState)
                {
                    using (BinaryReader r = Pack.Read(data))
                    {
                        v.Mine = r.ReadBoolean();
                        v.Controller = r.ReadString();
                        v.Count = r.ReadInt32();
                    }
                    v.States++;
                }
                else if (type == Msg.Frame)
                {
                    Interlocked.Increment(ref v.Frames);
                    using (BinaryReader r = Pack.Read(data)) conn.Send(Msg.Ack, Pack.Int(r.ReadInt32()));
                }
            };
            conn.Closed += () => v.Closed = true;
            conn.Start();
            return v;
        }

        // Did a mouse move from this viewer reach the injector?
        static bool Drives(Viewer v)
        {
            int before = Volatile.Read(ref injected);
            v.Conn.Send(Msg.MouseMove, Pack.Build(w => { w.Write(10 + before % 50); w.Write(10); }));
            Wait(() => Volatile.Read(ref injected) > before, 700);
            return Volatile.Read(ref injected) > before;
        }

        static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "tahakkum-shared-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppFiles.DirOverride = root;
            LicenseManager.Settings = new LicenseSettings { ApiUrl = "" };
            InputInjector.Suppress = true;
            InputInjector.Observe = what => { if (what == "mouse") Interlocked.Increment(ref injected); };
            try
            {
                DiscoveryTests();
                SharedSession();
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

        static void DiscoveryTests()
        {
            IPAddress[] loopback = { IPAddress.Loopback };
            FoundDevice me = new FoundDevice { Name = "سالم - المحاسبة", User = "salem", Machine = "DESK-1361", Port = Proto.DefaultPort, DeviceId = "142868838" };
            bool answering = true;
            DiscoveryResponder responder = new DiscoveryResponder(DiscoveryPort, () => answering ? me : null);
            responder.Start();
            try
            {
                List<FoundDevice> found = DiscoveryClient.Scan(DiscoveryPort, loopback, 800, null);
                Check("a machine on the network is found", found.Count == 1, found.Count.ToString());
                if (found.Count == 1)
                {
                    Check("it shows the name its owner chose", found[0].Name == "سالم - المحاسبة" && found[0].Title == "سالم - المحاسبة", found[0].Title);
                    Check("with its address, user, machine and id", found[0].Address == "127.0.0.1" && found[0].User == "salem" && found[0].Machine == "DESK-1361" && found[0].DeviceId == "142868838");
                }

                Check("this machine's own addresses can be left out", DiscoveryClient.Scan(DiscoveryPort, loopback, 500, new HashSet<string> { "127.0.0.1" }).Count == 0);

                me.Name = "";
                found = DiscoveryClient.Scan(DiscoveryPort, loopback, 500, null);
                Check("without a chosen name it falls back to the user and machine", found.Count == 1 && found[0].Title == "salem • DESK-1361", found.Count == 1 ? found[0].Title : "");

                me.Name = "مزيف‮gpj.exe\nسطر";
                found = DiscoveryClient.Scan(DiscoveryPort, loopback, 500, null);
                Check("names cannot carry bidi or line breaks", found.Count == 1 && found[0].Name.IndexOfAny(new[] { '‮', '\n' }) < 0, found.Count == 1 ? found[0].Name : "");

                answering = false;
                Check("a machine not accepting requests stays silent", DiscoveryClient.Scan(DiscoveryPort, loopback, 500, null).Count == 0);
                answering = true;

                using (UdpClient raw = new UdpClient(0))
                {
                    raw.Client.ReceiveTimeout = 500;
                    raw.Send(new byte[] { 1, 2, 3 }, 3, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));
                    bool replied = true;
                    try { IPEndPoint from = null; raw.Receive(ref from); } catch (SocketException) { replied = false; }
                    Check("garbage gets no answer", !replied);

                    // The same probe many times in one second: only a few answers come back.
                    byte[] probe = Pack.Build(w => { w.Write(Discovery.Magic); w.Write(Discovery.Probe); w.Write(new byte[Discovery.NonceLength]); });
                    Thread.Sleep(1100);
                    for (int i = 0; i < 20; i++) raw.Send(probe, probe.Length, new IPEndPoint(IPAddress.Loopback, DiscoveryPort));
                    int answers = 0;
                    raw.Client.ReceiveTimeout = 300;
                    try { while (true) { IPEndPoint from = null; raw.Receive(ref from); answers++; } } catch (SocketException) { }
                    Check("answers are rate limited", answers >= 1 && answers <= 10, answers + " of 20");
                }
            }
            finally
            {
                responder.Stop();
            }

            // Something answering with the wrong nonce, as a spoofed reply would, is ignored.
            using (UdpClient fake = new UdpClient(new IPEndPoint(IPAddress.Loopback, DiscoveryPort)))
            {
                Thread liar = new Thread(() =>
                {
                    try
                    {
                        IPEndPoint from = null;
                        fake.Receive(ref from);
                        byte[] reply = Pack.Build(w =>
                        {
                            w.Write(Discovery.Magic); w.Write(Discovery.Reply); w.Write(new byte[Discovery.NonceLength]);
                            w.Write(Proto.Version); w.Write(Proto.DefaultPort); w.Write("مزيف"); w.Write("x"); w.Write("x"); w.Write("");
                        });
                        fake.Send(reply, reply.Length, from);
                    }
                    catch (Exception) { }
                });
                liar.IsBackground = true;
                liar.Start();
                Check("a reply to someone else's question is ignored", DiscoveryClient.Scan(DiscoveryPort, loopback, 600, null).Count == 0);
            }
        }

        static X509Certificate2 NewCertificate()
        {
            using (RSA rsa = RSA.Create(2048))
            {
                CertificateRequest req = new CertificateRequest("CN=shared", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)))
                    return new X509Certificate2(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.UserKeySet);
            }
        }

        static HostSession SessionOf(HostServer server, string nick)
        {
            return server.Desk.Sessions.FirstOrDefault(s => s.Nick == nick);
        }

        static void SharedSession()
        {
            HostServer server = new HostServer(NewCertificate());
            server.ConsentNeeded += r => { lock (requests) requests.Add(r); r.Approve(); };
            server.Start(HostPort);
            try
            {
                Viewer a = Join("فني 1");
                Check("the first viewer connects", a.Failure == null, a.Failure);
                Wait(() => a.States > 0);
                Check("the first viewer controls, as before", a.Mine && a.Count == 1 && a.Controller == "فني 1", a.Controller + " " + a.Count);
                lock (requests) Check("the host sees the viewer's chosen name", requests[0].Nick == "فني 1" && requests[0].Watching == 0);

                Viewer b = Join("فني 2");
                Viewer c = Join("فني 3");
                Check("two more viewers join the same screen", b.Failure == null && c.Failure == null, b.Failure ?? c.Failure);
                Check("everyone hears there are three", Wait(() => a.Count == 3 && b.Count == 3 && c.Count == 3), a.Count + "/" + b.Count + "/" + c.Count);
                lock (requests) Check("the person is told who already watches", requests[1].Watching == 1 && requests[2].Watching == 2);
                Check("only the first controls, and everyone knows who", a.Mine && !b.Mine && !c.Mine && b.Controller == "فني 1" && c.Controller == "فني 1");
                Check("all three see the screen", Wait(() => Volatile.Read(ref a.Frames) > 1 && Volatile.Read(ref b.Frames) > 1 && Volatile.Read(ref c.Frames) > 1), a.Frames + "/" + b.Frames + "/" + c.Frames);

                Check("a watcher's mouse does nothing", !Drives(b) && !Drives(c));
                Check("the controller's mouse works", Drives(a));

                c.Conn.Send(Msg.ControlRequest, null);
                Check("a watcher can take control", Wait(() => c.Mine && !a.Mine && a.Controller == "فني 3" && b.Controller == "فني 3"));
                Check("the old controller's mouse stops working", !Drives(a));
                Check("the new controller's mouse works", Drives(c));

                server.Desk.Give(SessionOf(server, "فني 2"));
                Check("the person at the machine can hand control to anyone", Wait(() => b.Mine && !c.Mine && !a.Mine && a.Controller == "فني 2"));
                Check("and only that person drives it", Drives(b) && !Drives(c));

                Viewer d = Join("فني 4");
                Check("a fourth viewer fits", d.Failure == null && Wait(() => a.Count == 4));
                Viewer e = Join("فني 5");
                Check("a fifth is refused with a clear reason", e.Failure != null && e.Failure.Contains("الحد الأقصى"), e.Failure);

                b.Conn.Close();
                Check("when the controller leaves, control passes to whoever came first", Wait(() => a.Mine && a.Count == 3 && c.Controller == "فني 1"), a.Controller + " " + a.Count);
                Check("and the first viewer drives again", Drives(a));

                SessionOf(server, "فني 4").End();
                Check("one viewer can be disconnected alone", Wait(() => d.Closed && a.Count == 2) && !a.Closed && !c.Closed);

                HostSession controllerSession = SessionOf(server, "فني 1");
                controllerSession.SetPaused(true);
                Check("pausing stops the controller too", !Drives(a));
                controllerSession.SetPaused(false);

                OldViewer();

                foreach (HostSession s in server.Desk.Sessions) s.End();
                Check("ending everything disconnects everyone", Wait(() => a.Closed && c.Closed) && Wait(() => server.Desk.Count == 0));
            }
            finally
            {
                server.Stop();
            }
        }

        // A viewer from before shared sessions sends no name and ignores the new messages.
        static void OldViewer()
        {
            int before;
            lock (requests) before = requests.Count;
            using (TcpClient tcp = new TcpClient("127.0.0.1", HostPort))
            {
                tcp.ReceiveTimeout = 10000;
                SslStream ssl = new SslStream(tcp.GetStream(), false, (s, cert, chain, err) => true);
                ssl.AuthenticateAsClient("tahakkum", null, SslProtocols.Tls12, false);
                Connection.WritePacket(ssl, Msg.Hello, Pack.Build(w =>
                {
                    w.Write(Proto.Magic);
                    w.Write(Proto.Version);
                    w.Write("OLD-PC");
                    w.Write("olduser");
                }));
                bool approved = false;
                for (int i = 0; i < 5 && !approved; i++) approved = Connection.ReadPacket(ssl, Proto.MaxFromHost).Type == Msg.Approved;
                Check("an older viewer without a name still connects", approved);
                lock (requests) Check("and shows as its Windows user", requests.Count > before && requests[requests.Count - 1].Nick == "" && requests[requests.Count - 1].User == "olduser");
            }
        }
    }
}
