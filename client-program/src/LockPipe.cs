using System;
using System.IO;

namespace Tahakkum
{
    // The small framed protocol on the pipe between the user process and the SYSTEM agent.
    // Screen info and frame payloads are byte-for-byte the same as what a normal session sends to
    // a viewer, so the user process can forward them straight through.
    static class LockMsg
    {
        public const byte ScreenInfo = 1;
        public const byte Frame = 2;
        public const byte Input = 3;   // one replayed mouse or keyboard event
        public const byte State = 4;   // 1 = agent is on the secure desktop and capturing, 0 = not
        public const byte Hello = 5;

        public const byte InMove = 1;
        public const byte InButton = 2;
        public const byte InWheel = 3;
        public const byte InKey = 4;
    }

    // Length-prefixed messages over a pipe stream. One writer and one reader thread per side.
    sealed class LockChannel
    {
        const int MaxMessage = 8 * 1024 * 1024;

        readonly Stream stream;
        readonly object writeGate = new object();
        readonly byte[] header = new byte[5];

        public LockChannel(Stream stream)
        {
            this.stream = stream;
        }

        public void Write(byte type, byte[] payload)
        {
            if (payload == null) payload = new byte[0];
            lock (writeGate)
            {
                byte[] h = new byte[5];
                int n = payload.Length;
                h[0] = (byte)n; h[1] = (byte)(n >> 8); h[2] = (byte)(n >> 16); h[3] = (byte)(n >> 24);
                h[4] = type;
                stream.Write(h, 0, 5);
                if (n > 0) stream.Write(payload, 0, n);
                stream.Flush();
            }
        }

        // Returns false when the pipe is closed. Throws on a malformed or oversized message.
        public bool Read(out byte type, out byte[] payload)
        {
            type = 0;
            payload = null;
            if (!ReadExact(header, 5)) return false;
            int n = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
            type = header[4];
            if (n < 0 || n > MaxMessage) throw new InvalidDataException("lock frame size");
            byte[] buf = new byte[n];
            if (n > 0 && !ReadExact(buf, n)) return false;
            payload = buf;
            return true;
        }

        bool ReadExact(byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int r = stream.Read(buffer, read, count - read);
                if (r <= 0) return false;
                read += r;
            }
            return true;
        }
    }
}
