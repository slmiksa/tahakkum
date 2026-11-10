using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace Tahakkum
{
    sealed class FoundDevice
    {
        public string Name = "";
        public string User = "";
        public string Machine = "";
        public string Address = "";
        public string DeviceId = "";
        public int Port;

        // The name the person chose comes first; without one, who and which machine.
        public string Title
        {
            get { return Name.Length > 0 ? Name : (User.Length > 0 ? User + " • " + Machine : Machine); }
        }
    }

    // Lets support staff find machines on the local network by the name their owners chose.
    // Only machines that accept requests answer, only to private addresses, and only with what
    // the person chose to show. Connecting still needs their approval or their password.
    static class Discovery
    {
        public const int Magic = 0x544B4453;
        public const byte Probe = 1;
        public const byte Reply = 2;
        public const int NonceLength = 16;
        public const int MaxPacket = 1024;

        public static bool IsPrivate(IPAddress a)
        {
            if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
            if (a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
    }

    sealed class DiscoveryResponder
    {
        const int RepliesPerSecond = 5;

        readonly int port;
        readonly Func<FoundDevice> describe;
        readonly Dictionary<string, KeyValuePair<long, int>> recent = new Dictionary<string, KeyValuePair<long, int>>();
        UdpClient socket;
        volatile bool running;

        public DiscoveryResponder(int port, Func<FoundDevice> describe)
        {
            this.port = port;
            this.describe = describe;
        }

        public void Start()
        {
            if (running) return;
            try
            {
                // Reuse the address so a socket left behind by a previous, unclean exit does not
                // block binding until a reboot. UdpClient binds in its constructor, so the option
                // is set on a fresh socket before binding it here.
                UdpClient c = new UdpClient();
                c.ExclusiveAddressUse = false;
                c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                c.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                // Keep a child process (the bundled adb server lingers in the background) from
                // inheriting this socket and holding the port open after the program closes.
                try { Native.SetHandleInformation(c.Client.Handle, Native.HANDLE_FLAG_INHERIT, 0); } catch (Exception) { }
                socket = c;
            }
            catch (SocketException ex)
            {
                AppFiles.Log("تعذر تشغيل اكتشاف الأجهزة على المنفذ " + port + ": " + ex.SocketErrorCode);
                return;
            }
            running = true;
            UdpClient mine = socket;
            Thread t = new Thread(() => Loop(mine));
            t.IsBackground = true;
            t.Name = "discovery";
            t.Start();
        }

        public void Stop()
        {
            running = false;
            UdpClient s = socket;
            socket = null;
            if (s != null)
            {
                try { s.Close(); } catch (Exception) { }
            }
        }

        void Loop(UdpClient s)
        {
            while (running)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data;
                try { data = s.Receive(ref from); }
                catch (SocketException) { if (running && s == socket) continue; return; }
                catch (ObjectDisposedException) { return; }
                try
                {
                    byte[] reply = Answer(data, from);
                    if (reply != null) s.Send(reply, reply.Length, from);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is SocketException || ex is ObjectDisposedException)) AppFiles.Log("خطأ في اكتشاف الأجهزة: " + ex.Message);
                }
            }
        }

        byte[] Answer(byte[] data, IPEndPoint from)
        {
            if (!Discovery.IsPrivate(from.Address) || data.Length != 4 + 1 + Discovery.NonceLength) return null;
            if (BitConverter.ToInt32(data, 0) != Discovery.Magic || data[4] != Discovery.Probe) return null;
            if (!Allowed(from.Address.ToString())) return null;

            byte[] nonce = new byte[Discovery.NonceLength];
            Buffer.BlockCopy(data, 5, nonce, 0, nonce.Length);
            FoundDevice me = describe();
            if (me == null) return null;
            return Pack.Build(w =>
            {
                w.Write(Discovery.Magic);
                w.Write(Discovery.Reply);
                w.Write(nonce);
                w.Write(Proto.Version);
                w.Write(me.Port);
                w.Write(me.Name);
                w.Write(me.User);
                w.Write(me.Machine);
                w.Write(me.DeviceId);
            });
        }

        // A few answers per second per sender, so this cannot be used to flood someone.
        bool Allowed(string source)
        {
            long second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            lock (recent)
            {
                if (recent.Count > 1000) recent.Clear();
                KeyValuePair<long, int> seen;
                if (recent.TryGetValue(source, out seen) && seen.Key == second)
                {
                    if (seen.Value >= RepliesPerSecond) return false;
                    recent[source] = new KeyValuePair<long, int>(second, seen.Value + 1);
                }
                else
                {
                    recent[source] = new KeyValuePair<long, int>(second, 1);
                }
                return true;
            }
        }
    }

    static class DiscoveryClient
    {
        // Every network this machine is on, plus the general broadcast address.
        public static List<IPAddress> BroadcastTargets()
        {
            List<IPAddress> list = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        byte[] a = u.Address.GetAddressBytes();
                        byte[] m = u.IPv4Mask.GetAddressBytes();
                        for (int i = 0; i < 4; i++) a[i] = (byte)(a[i] | ~m[i]);
                        IPAddress broadcast = new IPAddress(a);
                        if (!list.Contains(broadcast)) list.Add(broadcast);
                    }
                }
            }
            catch (Exception) { }
            return list;
        }

        public static HashSet<string> OwnAddresses()
        {
            HashSet<string> set = new HashSet<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork) set.Add(u.Address.ToString());
            }
            catch (Exception) { }
            return set;
        }

        public static List<FoundDevice> Scan(int port, IEnumerable<IPAddress> targets, int waitMs, ICollection<string> skip)
        {
            Dictionary<string, FoundDevice> found = new Dictionary<string, FoundDevice>();
            byte[] nonce = new byte[Discovery.NonceLength];
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            byte[] probe = Pack.Build(w =>
            {
                w.Write(Discovery.Magic);
                w.Write(Discovery.Probe);
                w.Write(nonce);
            });

            using (UdpClient s = new UdpClient(new IPEndPoint(IPAddress.Any, 0)))
            {
                s.EnableBroadcast = true;
                foreach (IPAddress target in targets)
                {
                    try { s.Send(probe, probe.Length, new IPEndPoint(target, port)); }
                    catch (SocketException) { }
                }

                DateTime end = DateTime.UtcNow.AddMilliseconds(waitMs);
                while (true)
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) break;
                    s.Client.ReceiveTimeout = left;
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data;
                    try { data = s.Receive(ref from); }
                    catch (SocketException) { break; }
                    FoundDevice d = Parse(data, nonce, from);
                    if (d != null && (skip == null || !skip.Contains(d.Address))) found[d.Address] = d;
                }
            }

            List<FoundDevice> list = new List<FoundDevice>(found.Values);
            list.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        // Anything that does not answer our own probe exactly is ignored.
        static FoundDevice Parse(byte[] data, byte[] nonce, IPEndPoint from)
        {
            if (data.Length > Discovery.MaxPacket || !Discovery.IsPrivate(from.Address)) return null;
            try
            {
                using (BinaryReader r = Pack.Read(data))
                {
                    if (r.ReadInt32() != Discovery.Magic || r.ReadByte() != Discovery.Reply) return null;
                    byte[] echo = r.ReadBytes(Discovery.NonceLength);
                    if (!AccessPassword.SameBytes(echo, nonce)) return null;
                    r.ReadInt32();
                    int port = r.ReadInt32();
                    IPAddress address = from.Address.IsIPv4MappedToIPv6 ? from.Address.MapToIPv4() : from.Address;
                    FoundDevice d = new FoundDevice
                    {
                        Port = port > 0 && port < 65536 ? port : Proto.DefaultPort,
                        Address = address.ToString(),
                        Name = Identity.Sanitize(r.ReadString(), Profile.MaxName),
                        User = Identity.Sanitize(r.ReadString(), 64),
                        Machine = Identity.Sanitize(r.ReadString(), 64),
                    };
                    d.DeviceId = ViewerClient.ParseDeviceId(r.ReadString()) ?? "";
                    return d;
                }
            }
            catch (Exception ex)
            {
                if (ex is EndOfStreamException || ex is IOException || ex is ArgumentException) return null;
                throw;
            }
        }
    }
}
