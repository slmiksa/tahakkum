// Gives each machine a permanent ID and joins a viewer to a host when neither can reach the
// other directly. It only forwards bytes: the session's TLS runs end to end inside its streams.
//
//   TahakkumRelay <port> <certificate.pfx> <pfx password> <data folder> [<license key x> <license key y>]
//
// With the license public key, only machines holding a valid license can register and get an ID.
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
using System.Text;
using System.Threading;

namespace Tahakkum
{
    sealed class RelayServer
    {
        const int DialsPerMinute = 30;
        const int RegistrationsPerHour = 60;
        const int PairTimeoutMs = 10000;
        const int ControlSilenceMs = 60000;
        // A session sends pings every second; this also covers a password or approval prompt staying open.
        const int PipeSilenceMs = 180000;
        const int PipeBuffer = 64 * 1024;

        sealed class Control
        {
            public string Id;
            public string Fingerprint;
            public Stream Stream;
            public TcpClient Tcp;
            public readonly object WriteGate = new object();
        }

        sealed class Waiting
        {
            public Stream HostStream;
            public TcpClient HostTcp;
            public readonly ManualResetEvent Paired = new ManualResetEvent(false);
            public readonly ManualResetEvent Finished = new ManualResetEvent(false);
        }

        readonly X509Certificate2 certificate;
        readonly string idsPath;
        readonly object gate = new object();
        readonly Dictionary<string, string> idByFingerprint = new Dictionary<string, string>();
        readonly Dictionary<string, string> fingerprintById = new Dictionary<string, string>();
        readonly Dictionary<string, Control> online = new Dictionary<string, Control>();
        readonly Dictionary<string, Waiting> waiting = new Dictionary<string, Waiting>();
        readonly Dictionary<string, List<DateTime>> attempts = new Dictionary<string, List<DateTime>>();
        readonly RandomNumberGenerator random = RandomNumberGenerator.Create();
        readonly string licenseX;
        readonly string licenseY;
        TcpListener listener;
        volatile bool running;

        public RelayServer(X509Certificate2 certificate, string dataFolder, string licenseX = null, string licenseY = null)
        {
            this.certificate = certificate;
            this.licenseX = licenseX;
            this.licenseY = licenseY;
            Directory.CreateDirectory(dataFolder);
            idsPath = Path.Combine(dataFolder, "ids.txt");
            if (File.Exists(idsPath))
                foreach (string line in File.ReadAllLines(idsPath))
                {
                    string[] parts = line.Split(' ');
                    if (parts.Length != 2) continue;
                    idByFingerprint[parts[0]] = parts[1];
                    fingerprintById[parts[1]] = parts[0];
                }
        }

        public int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }

        public void Start(int port)
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(256);
            running = true;
            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch (Exception) { }
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                Thread t = new Thread(() => Handle(client));
                t.IsBackground = true;
                t.Start();
            }
        }

        void Handle(TcpClient tcp)
        {
            try
            {
                string ip = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address.ToString();
                if (ip.StartsWith("::ffff:")) ip = ip.Substring(7);
                tcp.NoDelay = true;
                tcp.ReceiveTimeout = 10000;
                tcp.SendTimeout = 15000;

                SslStream ssl = new SslStream(tcp.GetStream(), false);
                ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                Packet first = Connection.ReadPacket(ssl, Proto.MaxPreApproval);
                switch (first.Type)
                {
                    case Msg.RelayRegister: HandleRegister(tcp, ssl, first.Data, ip); break;
                    case Msg.RelayDial: HandleDial(tcp, ssl, first.Data, ip); break;
                    case Msg.RelayAccept: HandleAccept(tcp, ssl, first.Data); break;
                }
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is AuthenticationException || ex is ObjectDisposedException || ex is InvalidDataException || ex is SocketException))
                    Console.Error.WriteLine(ex);
            }
            finally
            {
                try { tcp.Close(); } catch (Exception) { }
            }
        }

        bool WithinLimit(string kind, string ip, int limit, TimeSpan window)
        {
            string key = kind + " " + ip;
            lock (attempts)
            {
                List<DateTime> list;
                if (!attempts.TryGetValue(key, out list)) attempts[key] = list = new List<DateTime>();
                DateTime since = DateTime.UtcNow - window;
                list.RemoveAll(t => t < since);
                if (list.Count >= limit) return false;
                list.Add(DateTime.UtcNow);
                return true;
            }
        }

        string IdFor(string fingerprint)
        {
            lock (gate)
            {
                string id;
                if (idByFingerprint.TryGetValue(fingerprint, out id)) return id;
                byte[] b = new byte[4];
                do
                {
                    random.GetBytes(b);
                    id = (100000000 + BitConverter.ToUInt32(b, 0) % 900000000).ToString();
                }
                while (fingerprintById.ContainsKey(id));
                File.AppendAllText(idsPath, fingerprint + " " + id + "\n");
                idByFingerprint[fingerprint] = id;
                fingerprintById[id] = fingerprint;
                return id;
            }
        }

        void HandleRegister(TcpClient tcp, Stream s, byte[] data, string ip)
        {
            if (!WithinLimit("register", ip, RegistrationsPerHour, TimeSpan.FromHours(1)))
            {
                Connection.WritePacket(s, Msg.RelayRefused, new byte[0]);
                return;
            }
            X509Certificate2 claimed;
            string license;
            using (BinaryReader r = Pack.Read(data))
            {
                claimed = new X509Certificate2(r.ReadBytes(r.ReadInt32()));
                license = r.ReadString();
            }
            if (licenseX != null)
            {
                LicenseToken token = LicenseToken.Parse(license, licenseX, licenseY);
                if (token == null || token.ExpiresUtc <= DateTime.UtcNow)
                {
                    Connection.WritePacket(s, Msg.RelayRefused, new byte[0]);
                    return;
                }
            }

            byte[] nonce = new byte[32];
            random.GetBytes(nonce);
            Connection.WritePacket(s, Msg.RelayChallenge, nonce);
            Packet proof = Connection.ReadPacket(s, Proto.MaxPreApproval);
            if (proof.Type != Msg.RelayProof) return;
            using (RSA key = claimed.GetRSAPublicKey())
            {
                if (key == null) return;
                if (!key.VerifyData(Proto.RelaySigned(nonce), proof.Data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return;
            }

            string fingerprint;
            using (SHA256 sha = SHA256.Create())
                fingerprint = BitConverter.ToString(sha.ComputeHash(claimed.GetRawCertData())).Replace("-", "");
            Control control = new Control { Id = IdFor(fingerprint), Fingerprint = fingerprint, Stream = s, Tcp = tcp };
            Control previous;
            lock (gate)
            {
                online.TryGetValue(control.Id, out previous);
                online[control.Id] = control;
            }
            if (previous != null)
            {
                try { previous.Tcp.Close(); } catch (Exception) { }
            }

            try
            {
                lock (control.WriteGate)
                    Connection.WritePacket(s, Msg.RelayRegistered, Pack.Build(w => w.Write(control.Id)));
                tcp.ReceiveTimeout = ControlSilenceMs;
                while (true)
                {
                    Packet p = Connection.ReadPacket(s, Proto.MaxPreApproval);
                    if (p.Type == Msg.RelayPing)
                        lock (control.WriteGate) Connection.WritePacket(s, Msg.RelayPong, new byte[0]);
                }
            }
            finally
            {
                lock (gate)
                {
                    Control now;
                    if (online.TryGetValue(control.Id, out now) && now == control) online.Remove(control.Id);
                }
            }
        }

        void HandleDial(TcpClient tcp, Stream viewer, byte[] data, string ip)
        {
            if (!WithinLimit("dial", ip, DialsPerMinute, TimeSpan.FromMinutes(1)))
            {
                Connection.WritePacket(viewer, Msg.RelayRefused, new byte[0]);
                return;
            }
            string id;
            using (BinaryReader r = Pack.Read(data)) id = r.ReadString();

            Control control;
            lock (gate) online.TryGetValue(id, out control);
            if (control == null)
            {
                Connection.WritePacket(viewer, Msg.RelayOffline, new byte[0]);
                return;
            }

            byte[] token = new byte[16];
            random.GetBytes(token);
            string tokenKey = Convert.ToBase64String(token);
            Waiting w = new Waiting();
            lock (gate) waiting[tokenKey] = w;
            try
            {
                try
                {
                    lock (control.WriteGate)
                        Connection.WritePacket(control.Stream, Msg.RelayIncoming, Pack.Build(x => { x.Write(token); x.Write(ip); }));
                }
                catch (IOException) { }

                if (!w.Paired.WaitOne(PairTimeoutMs))
                {
                    Connection.WritePacket(viewer, Msg.RelayOffline, new byte[0]);
                    return;
                }
            }
            finally
            {
                lock (gate) waiting.Remove(tokenKey);
            }

            try
            {
                Connection.WritePacket(viewer, Msg.RelayGo, Pack.Build(x => x.Write(control.Fingerprint)));
                Connection.WritePacket(w.HostStream, Msg.RelayGo, new byte[0]);
                tcp.ReceiveTimeout = PipeSilenceMs;
                w.HostTcp.ReceiveTimeout = PipeSilenceMs;

                Thread back = new Thread(() => Pump(w.HostStream, viewer, tcp, w.HostTcp));
                back.IsBackground = true;
                back.Start();
                Pump(viewer, w.HostStream, tcp, w.HostTcp);
                back.Join();
            }
            finally
            {
                w.Finished.Set();
            }
        }

        void HandleAccept(TcpClient tcp, Stream host, byte[] token)
        {
            Waiting w;
            string key = Convert.ToBase64String(token);
            lock (gate)
            {
                if (!waiting.TryGetValue(key, out w)) return;
                waiting.Remove(key);
            }
            w.HostStream = host;
            w.HostTcp = tcp;
            w.Paired.Set();
            w.Finished.WaitOne();
        }

        static void Pump(Stream from, Stream to, TcpClient a, TcpClient b)
        {
            byte[] buffer = new byte[PipeBuffer];
            try
            {
                while (true)
                {
                    int n = from.Read(buffer, 0, buffer.Length);
                    if (n <= 0) break;
                    to.Write(buffer, 0, n);
                    to.Flush();
                }
            }
            catch (Exception) { }
            try { a.Close(); } catch (Exception) { }
            try { b.Close(); } catch (Exception) { }
        }
    }

    static class RelayProgram
    {
        static int Main(string[] args)
        {
            if (args.Length != 4 && args.Length != 6)
            {
                Console.Error.WriteLine("usage: TahakkumRelay <port> <certificate.pfx> <pfx password> <data folder> [<license key x> <license key y>]");
                return 2;
            }
            RelayServer server = new RelayServer(new X509Certificate2(args[1], args[2]), args[3], args.Length == 6 ? args[4] : null, args.Length == 6 ? args[5] : null);
            server.Start(int.Parse(args[0]));
            Console.WriteLine("relay listening on port " + server.Port);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
    }
}
