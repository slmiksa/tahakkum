using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Tahakkum
{
    enum Msg : byte
    {
        Hello = 1, Pending = 2, Approved = 3, Denied = 4, ScreenInfo = 5, Frame = 6, Ack = 7,
        MouseMove = 8, MouseButton = 9, MouseWheel = 10, Key = 11, SelectMonitor = 12,
        SetQuality = 13, Clipboard = 14, Ping = 15, Pong = 16, Status = 17, Bye = 18,
        FileOffer = 19, FileAnswer = 20, FileData = 21, FileAck = 22, FileCancel = 23,
        AuthChallenge = 24, AuthProof = 25, AskConsent = 26,
        // Between a program and the relay server, outside the end-to-end encrypted session.
        RelayRegister = 40, RelayChallenge = 41, RelayProof = 42, RelayRegistered = 43, RelayIncoming = 44,
        RelayAccept = 45, RelayDial = 46, RelayGo = 47, RelayOffline = 48, RelayPing = 49, RelayPong = 50, RelayRefused = 51
    }

    enum DenyReason : byte { Rejected = 1, Busy = 2, Timeout = 3, Version = 4, Cooldown = 5, BadPassword = 6, NotLicensed = 7 }

    enum HostStatus : byte { Normal = 0, Locked = 1, ControlPaused = 2, ControlResumed = 3 }

    static class Proto
    {
        public const int Magic = 0x544B4D31;
        public const int Version = 3;
        public const int DefaultPort = 47800;
        public const int MaxPreApproval = 4 * 1024;
        public const int MaxFromViewer = 2 * 1024 * 1024;
        public const int MaxFromHost = 128 * 1024 * 1024;
        public const int MaxClipboardChars = 512 * 1024;
        public const int ConsentSeconds = 30;
        public const int RelayPort = 443;
        public const string RelaySignLabel = "tahakkum-relay-v1";
        public const string DeviceIdPattern = @"^\d{9}$";
        public const int PasswordAttemptsPerConnection = 3;

        // What a host signs to prove to the relay server that it owns its certificate.
        public static byte[] RelaySigned(byte[] nonce)
        {
            byte[] label = System.Text.Encoding.ASCII.GetBytes(RelaySignLabel);
            byte[] all = new byte[label.Length + nonce.Length];
            Buffer.BlockCopy(label, 0, all, 0, label.Length);
            Buffer.BlockCopy(nonce, 0, all, label.Length, nonce.Length);
            return all;
        }

        public const int FileChunk = 64 * 1024;
        public const int FileWindow = 16;
        public const int MaxFilesPerOffer = 500;
        public const int FileOfferSeconds = 60;
    }

    sealed class Packet
    {
        public Msg Type;
        public byte[] Data;
    }

    // Framed message channel over TLS: one reader thread, one writer thread.
    sealed class Connection : IDisposable
    {
        readonly TcpClient tcp;
        readonly Stream stream;
        readonly LinkedList<Packet> queue = new LinkedList<Packet>();
        readonly object gate = new object();
        bool closed;
        bool closeWhenDrained;
        int closedRaised;

        public volatile int MaxIncoming = Proto.MaxPreApproval;
        public event Action<Msg, byte[]> Received;
        public event Action Closed;

        public Connection(TcpClient tcp, Stream stream)
        {
            this.tcp = tcp;
            this.stream = stream;
        }

        public void Start()
        {
            StartThread(ReadLoop, "net-read");
            StartThread(WriteLoop, "net-write");
        }

        public void SetTimeouts(int receiveMs, int sendMs)
        {
            try
            {
                tcp.ReceiveTimeout = receiveMs;
                tcp.SendTimeout = sendMs;
            }
            catch (Exception) { }
        }

        static void StartThread(ThreadStart body, string name)
        {
            Thread t = new Thread(body);
            t.IsBackground = true;
            t.Name = name;
            t.Start();
        }

        public void Send(Msg type, byte[] data)
        {
            lock (gate)
            {
                if (closed || closeWhenDrained) return;
                Packet packet = new Packet { Type = type, Data = data ?? new byte[0] };
                if (type == Msg.FileData)
                {
                    queue.AddLast(packet);
                }
                else
                {
                    // Input and screen updates overtake queued file data, so a transfer does not make the session lag.
                    LinkedListNode<Packet> after = queue.Last;
                    while (after != null && after.Value.Type == Msg.FileData) after = after.Previous;
                    // A newer pointer position makes any unsent one worthless.
                    if (type == Msg.MouseMove && after != null && after.Value.Type == Msg.MouseMove)
                    {
                        LinkedListNode<Packet> stale = after;
                        after = after.Previous;
                        queue.Remove(stale);
                    }
                    if (after == null) queue.AddFirst(packet);
                    else queue.AddAfter(after, packet);
                }
                Monitor.PulseAll(gate);
            }
        }

        public void SendAndClose(Msg type, byte[] data)
        {
            lock (gate)
            {
                if (closed || closeWhenDrained) return;
                queue.AddLast(new Packet { Type = type, Data = data ?? new byte[0] });
                closeWhenDrained = true;
                Monitor.PulseAll(gate);
            }
        }

        public static void WritePacket(Stream s, Msg type, byte[] data)
        {
            byte[] buf = new byte[5 + data.Length];
            buf[0] = (byte)type;
            buf[1] = (byte)data.Length;
            buf[2] = (byte)(data.Length >> 8);
            buf[3] = (byte)(data.Length >> 16);
            buf[4] = (byte)(data.Length >> 24);
            Buffer.BlockCopy(data, 0, buf, 5, data.Length);
            s.Write(buf, 0, buf.Length);
            s.Flush();
        }

        public static Packet ReadPacket(Stream s, int maxSize)
        {
            byte[] head = new byte[5];
            ReadExact(s, head, 5);
            int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
            if (len < 0 || len > maxSize) throw new InvalidDataException("packet size");
            byte[] data = new byte[len];
            ReadExact(s, data, len);
            return new Packet { Type = (Msg)head[0], Data = data };
        }

        static void ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
        }

        void ReadLoop()
        {
            try
            {
                while (true)
                {
                    Packet p = ReadPacket(stream, MaxIncoming);
                    Action<Msg, byte[]> h = Received;
                    if (h != null) h(p.Type, p.Data);
                }
            }
            catch (Exception) { }
            Close();
        }

        void WriteLoop()
        {
            try
            {
                while (true)
                {
                    Packet p;
                    lock (gate)
                    {
                        while (queue.Count == 0 && !closed && !closeWhenDrained) Monitor.Wait(gate);
                        if (closed) return;
                        if (queue.Count == 0) break;
                        p = queue.First.Value;
                        queue.RemoveFirst();
                    }
                    WritePacket(stream, p.Type, p.Data);
                }
            }
            catch (Exception) { }
            Close();
        }

        public void Close()
        {
            lock (gate)
            {
                closed = true;
                queue.Clear();
                Monitor.PulseAll(gate);
            }
            try { stream.Close(); } catch (Exception) { }
            try { tcp.Close(); } catch (Exception) { }
            if (Interlocked.Exchange(ref closedRaised, 1) == 0)
            {
                Action h = Closed;
                if (h != null) h();
            }
        }

        public bool IsClosed
        {
            get { lock (gate) return closed; }
        }

        public void Dispose()
        {
            Close();
        }
    }

    static class Pack
    {
        public static byte[] Build(Action<BinaryWriter> body)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                body(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Read(byte[] data)
        {
            return new BinaryReader(new MemoryStream(data));
        }

        public static byte[] Int(int v)
        {
            return BitConverter.GetBytes(v);
        }
    }
}
