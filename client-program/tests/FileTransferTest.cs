// Runs the file transfer service between two real connections over loopback TLS, with no
// screen, mouse, or windows involved. Everything is written under a temporary folder.
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;

namespace Tahakkum
{
    static class FileTransferTest
    {
        static int failures;
        static string root;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        sealed class Side
        {
            public Connection Conn;
            public FileTransferService Files;
            public FileTransfer Last;
            public FileOffer Offer;
            public Func<FileOffer, bool?> Decide = o => true;
            public int FramesSeen;
        }

        static void Pair(out Side a, out Side b, string name, bool startA = true)
        {
            string dir = Path.Combine(root, name);
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            TcpClient client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            TcpClient server = l.AcceptTcpClient();
            l.Stop();

            SslStream sslServer = new SslStream(server.GetStream(), false);
            SslStream sslClient = new SslStream(client.GetStream(), false, (s, c, ch, e) => true);
            var cert = Identity.LoadOrCreateCertificate();
            var auth = sslServer.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);
            sslClient.AuthenticateAsClient("tahakkum", null, SslProtocols.Tls12, false);
            auth.Wait();

            a = Make(new Connection(client, sslClient), Path.Combine(dir, "a"));
            b = Make(new Connection(server, sslServer), Path.Combine(dir, "b"));
            // The same limits as an approved session: the viewer's incoming side, and the host's.
            a.Conn.MaxIncoming = Proto.MaxFromHost;
            b.Conn.MaxIncoming = Proto.MaxFromViewer;
            if (startA) a.Conn.Start();
            b.Conn.Start();
        }

        static Side Make(Connection conn, string dir)
        {
            Side s = new Side { Conn = conn, Files = new FileTransferService(conn, "test") };
            s.Files.SaveFolder = dir;
            s.Files.TransferStarted += t => s.Last = t;
            s.Files.OfferReceived += o =>
            {
                s.Offer = o;
                bool? d = s.Decide(o);
                if (d == true) s.Files.Accept(o);
                else if (d == false) s.Files.Reject(o);
            };
            conn.Received += (type, data) =>
            {
                if (!s.Files.Handle(type, data) && type == Msg.Frame) Interlocked.Increment(ref s.FramesSeen);
            };
            return s;
        }

        static bool Wait(Func<bool> done, int ms = 20000)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (done()) return true;
                Thread.Sleep(10);
            }
            return done();
        }

        static string Make(string name, long size)
        {
            string dir = Path.Combine(root, "source");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            Random random = new Random(name.GetHashCode());
            using (FileStream fs = new FileStream(path, FileMode.Create))
            {
                byte[] buf = new byte[1024 * 1024];
                long left = size;
                while (left > 0)
                {
                    random.NextBytes(buf);
                    int n = (int)Math.Min(buf.Length, left);
                    fs.Write(buf, 0, n);
                    left -= n;
                }
            }
            return path;
        }

        static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        static int Main()
        {
            root = Path.Combine(Path.GetTempPath(), "tahakkum-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppFiles.DirOverride = Path.Combine(root, "app");
            try
            {
                Names();
                ManyFiles();
                BothDirections();
                NoOverwrite();
                Reject();
                SenderCancels();
                ReceiverCancels();
                ConnectionLost();
                HostilePeer();
                ControlOvertakesData();
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

        static void Names()
        {
            Func<string, string> f = FileTransferService.SafeFileName;
            Check("path traversal cannot leave the folder", f("..\\..\\Windows\\evil.exe") == ".._.._Windows_evil.exe", f("..\\..\\Windows\\evil.exe"));
            Check("forward slashes are replaced", !f("../../x").Contains("/"), f("../../x"));
            Check("absolute path is neutralized", !f("C:\\x.txt").Contains(":") && !f("C:\\x.txt").Contains("\\"), f("C:\\x.txt"));
            Check("alternate data stream is neutralized", !f("a.txt:evil").Contains(":"), f("a.txt:evil"));
            Check("dots only becomes a plain name", f("..") == "file", f(".."));
            Check("empty becomes a plain name", f("") == "file");
            Check("reserved device name is changed", f("CON") == "_CON" && f("nul.txt") == "_nul.txt" && f("COM1.tar.gz") == "_COM1.tar.gz", f("COM1.tar.gz"));
            Check("ordinary names stay", f("تقرير 2026.pdf") == "تقرير 2026.pdf" && f("console.log") == "console.log");
            Check("bidi override cannot disguise an extension", f("photo\u202Egpj.exe") == "photogpj.exe", f("photo\u202Egpj.exe"));
            Check("trailing dots and spaces are removed", f("a.txt. . ") == "a.txt", f("a.txt. . "));
            string longName = f(new string('x', 400) + ".pdf");
            Check("long name keeps its extension", longName.EndsWith(".pdf") && longName.Length <= 160, longName.Length.ToString());
        }

        static void ManyFiles()
        {
            Side a, b;
            Pair(out a, out b, "many");
            string big = Make("big.bin", 30 * 1024 * 1024);
            string empty = Make("empty.txt", 0);
            string exact = Make("exact.bin", Proto.FileChunk);
            string arabic = Make("ملف عربي.txt", 12345);

            Check("offer goes out", a.Files.Send(new[] { big, empty, exact, arabic }) == null);
            Check("second send refused while busy", a.Files.Send(new[] { arabic }) != null);
            bool done = Wait(() => a.Last != null && !a.Last.Active && b.Last != null && !b.Last.Active, 60000);
            Check("transfer finishes on both sides", done && a.Last.State == TransferState.Completed && b.Last.State == TransferState.Completed, a.Last.Message + " / " + (b.Last == null ? "" : b.Last.Message));
            Check("progress reaches the total", a.Last.Done == a.Last.Total && b.Last.Done == b.Last.Total, a.Last.Done + "/" + a.Last.Total);

            foreach (string src in new[] { big, empty, exact, arabic })
            {
                string got = Path.Combine(b.Files.SaveFolder, Path.GetFileName(src));
                Check("identical content: " + Path.GetFileName(src), File.Exists(got) && Hash(got) == Hash(src));
            }
            Check("no partial files left", !Directory.GetFiles(b.Files.SaveFolder, "*.part").Any());
            Check("sending is free again", !a.Files.Sending);
            a.Conn.Close();
        }

        static void BothDirections()
        {
            Side a, b;
            Pair(out a, out b, "both");
            string x = Make("x.bin", 8 * 1024 * 1024);
            string y = Make("y.bin", 8 * 1024 * 1024);
            a.Files.Send(new[] { x });
            b.Files.Send(new[] { y });
            Wait(() => File.Exists(Path.Combine(b.Files.SaveFolder, "x.bin")) && File.Exists(Path.Combine(a.Files.SaveFolder, "y.bin")));
            Check("both directions at once: viewer to host", File.Exists(Path.Combine(b.Files.SaveFolder, "x.bin")) && Hash(Path.Combine(b.Files.SaveFolder, "x.bin")) == Hash(x));
            Check("both directions at once: host to viewer", File.Exists(Path.Combine(a.Files.SaveFolder, "y.bin")) && Hash(Path.Combine(a.Files.SaveFolder, "y.bin")) == Hash(y));
            a.Conn.Close();
        }

        static void NoOverwrite()
        {
            Side a, b;
            Pair(out a, out b, "overwrite");
            Directory.CreateDirectory(b.Files.SaveFolder);
            string existing = Path.Combine(b.Files.SaveFolder, "report.txt");
            File.WriteAllText(existing, "keep me");
            string src = Make("report.txt", 5000);
            a.Files.Send(new[] { src });
            string renamed = Path.Combine(b.Files.SaveFolder, "report (1).txt");
            Wait(() => File.Exists(renamed));
            Check("existing file is untouched", File.ReadAllText(existing) == "keep me");
            Check("new file gets a free name", File.Exists(renamed) && Hash(renamed) == Hash(src));
            a.Conn.Close();
        }

        static void Reject()
        {
            Side a, b;
            Pair(out a, out b, "reject");
            b.Decide = o => false;
            string src = Make("no.bin", 2 * 1024 * 1024);
            a.Files.Send(new[] { src });
            Wait(() => a.Last != null && !a.Last.Active);
            Check("sender learns of the rejection", a.Last.State == TransferState.Failed, a.Last.Message);
            Check("nothing written on rejection", !Directory.Exists(b.Files.SaveFolder) || !Directory.GetFiles(b.Files.SaveFolder).Any());

            // Blocked offers (locked or paused machine) never reach the person.
            bool asked = false;
            b.Decide = o => { asked = true; return true; };
            b.Files.OffersBlocked = () => true;
            a.Files.Send(new[] { src });
            Wait(() => !a.Last.Active);
            Check("blocked offer is refused without a prompt", a.Last.State == TransferState.Failed && !asked, a.Last.Message);
            a.Conn.Close();
        }

        static void SenderCancels()
        {
            Side a, b;
            Pair(out a, out b, "sendercancel");
            string src = Make("cancel.bin", 300 * 1024 * 1024);
            a.Files.Send(new[] { src });
            Wait(() => a.Last != null && a.Last.Done > 1024 * 1024);
            a.Last.Cancel();
            Wait(() => b.Last != null && !b.Last.Active);
            Check("sender cancel stops the receiver", a.Last.State == TransferState.Failed && b.Last.State == TransferState.Failed, b.Last.Message);
            Check("sender cancel leaves no partial file", !Directory.GetFiles(b.Files.SaveFolder).Any());

            // A cancel while the prompt is still open takes the prompt away.
            bool cancelled = false;
            b.Decide = o => { o.Cancelled += () => cancelled = true; return null; };
            a.Files.Send(new[] { src });
            Wait(() => b.Offer != null);
            Thread.Sleep(100);
            a.Last.Cancel();
            Wait(() => cancelled, 3000);
            Check("sender cancel closes an open prompt", cancelled);
            a.Conn.Close();
        }

        static void ReceiverCancels()
        {
            Side a, b;
            Pair(out a, out b, "recvcancel");
            string src = Make("cancel2.bin", 300 * 1024 * 1024);
            a.Files.Send(new[] { src });
            Wait(() => b.Last != null && b.Last.Done > 1024 * 1024);
            b.Last.Cancel();
            Wait(() => !a.Last.Active);
            Check("receiver cancel stops the sender", a.Last.State == TransferState.Failed, a.Last.Message);
            Check("receiver cancel leaves no partial file", !Directory.GetFiles(b.Files.SaveFolder).Any());
            a.Conn.Close();
        }

        static void ConnectionLost()
        {
            Side a, b;
            Pair(out a, out b, "lost");
            string src = Make("lost.bin", 300 * 1024 * 1024);
            a.Files.Send(new[] { src });
            Wait(() => b.Last != null && b.Last.Done > 1024 * 1024);
            a.Conn.Close();
            Wait(() => !a.Last.Active && !b.Last.Active);
            Check("lost connection fails both sides", a.Last.State == TransferState.Failed && b.Last.State == TransferState.Failed);
            Check("lost connection leaves no partial file", !Directory.GetFiles(b.Files.SaveFolder).Any());
        }

        static void HostilePeer()
        {
            Side a, b;
            Pair(out a, out b, "hostile");

            // Data with nobody having accepted anything is ignored and writes nothing.
            byte[] chunk = new byte[1000];
            a.Conn.Send(Msg.FileData, chunk);
            Thread.Sleep(200);
            Check("data without an accepted offer writes nothing", !Directory.Exists(b.Files.SaveFolder) && !b.Conn.IsClosed);

            // More data than was declared must not grow the file.
            a.Conn.Send(Msg.FileOffer, Pack.Build(w => { w.Write(77); w.Write(1); w.Write("..\\..\\small.txt"); w.Write(10L); }));
            Wait(() => b.Last != null);
            Check("hostile name stays in the folder", b.Offer != null && b.Offer.Names[0] == ".._.._small.txt", b.Offer == null ? "" : b.Offer.Names[0]);
            byte[] oversized = new byte[4 + 100];
            Buffer.BlockCopy(BitConverter.GetBytes(77), 0, oversized, 0, 4);
            a.Conn.Send(Msg.FileData, oversized);
            Wait(() => !b.Last.Active);
            Check("oversized data fails the transfer", b.Last.State == TransferState.Failed, b.Last.Message);
            Check("oversized data leaves nothing behind", !Directory.GetFiles(b.Files.SaveFolder).Any());

            // A second offer during an open prompt is refused rather than stacking prompts.
            int prompts = 0;
            b.Decide = o => { prompts++; return null; };
            a.Conn.Send(Msg.FileOffer, Pack.Build(w => { w.Write(80); w.Write(1); w.Write("a"); w.Write(1L); }));
            a.Conn.Send(Msg.FileOffer, Pack.Build(w => { w.Write(81); w.Write(1); w.Write("b"); w.Write(1L); }));
            Thread.Sleep(300);
            Check("offers do not stack prompts", prompts == 1, prompts.ToString());
            a.Conn.Close();

            // Too many files or a negative size is a protocol violation: the session drops.
            Pair(out a, out b, "hostile2");
            a.Conn.Send(Msg.FileOffer, Pack.Build(w => { w.Write(1); w.Write(Proto.MaxFilesPerOffer + 1); }));
            bool threw = false;
            try { b.Files.Handle(Msg.FileOffer, Pack.Build(w => { w.Write(1); w.Write(1); w.Write("x"); w.Write(-5L); })); }
            catch (InvalidDataException) { threw = true; }
            Check("negative size is refused", threw);
            Wait(() => b.Conn.IsClosed, 3000);
            Check("too many files drops the connection", b.Conn.IsClosed);
            a.Conn.Close();
        }

        // Queues far more file data than the socket can take, then checks a later control message jumps ahead.
        static void ControlOvertakesData()
        {
            Side a, b;
            Pair(out a, out b, "priority");
            int seenAt = -1, dataCount = 0;
            b.Conn.Received += (type, data) =>
            {
                if (type == Msg.FileData) dataCount++;
                else if (type == Msg.Ping && seenAt < 0) seenAt = dataCount;
            };
            byte[] blob = new byte[Proto.FileChunk];
            for (int i = 0; i < 400; i++) a.Conn.Send(Msg.FileData, blob);
            a.Conn.Send(Msg.Ping, new byte[8]);
            Wait(() => seenAt >= 0, 10000);
            Check("control message overtakes queued file data", seenAt >= 0 && seenAt < 400, "after " + seenAt + " of 400 chunks");
            a.Conn.Close();

            // Nothing is written until Start, so this checks the exact order the queue produces.
            Pair(out a, out b, "order", false);
            var seen = new System.Collections.Generic.List<string>();
            b.Conn.Received += (type, data) =>
            {
                string item = type.ToString();
                if (type == Msg.MouseMove) item += BitConverter.ToInt32(data, 0);
                lock (seen) seen.Add(item);
            };
            byte[] small = new byte[4];
            Func<int, byte[]> move = x => Pack.Build(w => { w.Write(x); w.Write(0); });
            a.Conn.Send(Msg.FileData, small);
            a.Conn.Send(Msg.MouseMove, move(1));
            a.Conn.Send(Msg.FileData, small);
            a.Conn.Send(Msg.MouseMove, move(2));
            a.Conn.Send(Msg.Key, new byte[10]);
            a.Conn.Send(Msg.MouseMove, move(3));
            a.Conn.Send(Msg.FileData, small);
            a.Conn.Start();
            Wait(() => { lock (seen) return seen.Count >= 6; }, 3000);
            Thread.Sleep(100);
            string order;
            lock (seen) order = string.Join(" ", seen.ToArray());
            Check("newer pointer replaces older, input stays in order, file data keeps its order",
                order == "MouseMove2 Key MouseMove3 FileData FileData FileData", order);
            a.Conn.Close();
        }
    }
}
