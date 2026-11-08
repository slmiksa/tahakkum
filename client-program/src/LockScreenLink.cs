using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Tahakkum
{
    // The user process side of the lock-screen feature. It hosts a local pipe that only SYSTEM and
    // the signed-in user may open, waits for the SYSTEM agent to connect, and fans the secure-desktop
    // frames out to every password-authenticated viewer. Input from the controlling viewer is
    // forwarded back to the agent. When the machine is not locked, this stays quiet.
    sealed class LockScreenLink
    {
        readonly object gate = new object();
        volatile bool running;
        volatile bool secureActive;
        Thread thread;
        NamedPipeServerStream pipe;
        LockChannel channel;

        // Raised on a background thread. Handlers only hand bytes to a thread-safe connection queue.
        public event Action<byte[]> ScreenInfoReceived;
        public event Action<byte[]> FrameReceived;
        public event Action SecureActiveChanged;

        public bool SecureActive { get { return secureActive; } }

        // The most recent screen dimensions from the agent, so a viewer that starts watching
        // mid-stream still learns the size before the next frame.
        public byte[] LastScreenInfo { get; private set; }

        public void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Name = "lock-link";
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            lock (gate)
            {
                SetSecure(false);
                try { if (pipe != null) pipe.Dispose(); } catch (Exception) { }
                pipe = null;
                channel = null;
            }
        }

        // Forwarded by the controlling, password-authenticated viewer only.
        public void SendInput(byte[] payload)
        {
            LockChannel c = channel;
            if (c == null || !secureActive) return;
            try { c.Write(LockMsg.Input, payload); }
            catch (Exception) { }
        }

        static PipeSecurity BuildSecurity()
        {
            PipeSecurity security = new PipeSecurity();
            SecurityIdentifier system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
            using (WindowsIdentity me = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return security;
        }

        void Loop()
        {
            while (running)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(LockLaunch.DataPipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, BuildSecurity());
                    lock (gate) { pipe = server; }
                    server.WaitForConnection();

                    LockChannel c = new LockChannel(server);
                    lock (gate) { channel = c; }
                    Serve(c);
                }
                catch (Exception) { }
                finally
                {
                    SetSecure(false);
                    lock (gate)
                    {
                        channel = null;
                        if (pipe == server) pipe = null;
                    }
                    try { if (server != null) server.Dispose(); } catch (Exception) { }
                }
                if (running) Thread.Sleep(500);
            }
        }

        void Serve(LockChannel c)
        {
            byte type;
            byte[] payload;
            while (running && c.Read(out type, out payload))
            {
                switch (type)
                {
                    case LockMsg.State:
                        SetSecure(payload.Length > 0 && payload[0] == 1);
                        break;
                    case LockMsg.ScreenInfo:
                        LastScreenInfo = payload;
                        Raise(ScreenInfoReceived, payload);
                        break;
                    case LockMsg.Frame:
                        Raise(FrameReceived, payload);
                        break;
                }
            }
        }

        void SetSecure(bool value)
        {
            if (secureActive == value) return;
            secureActive = value;
            Action h = SecureActiveChanged;
            if (h != null) { try { h(); } catch (Exception) { } }
        }

        static void Raise(Action<byte[]> handler, byte[] payload)
        {
            if (handler != null) { try { handler(payload); } catch (Exception) { } }
        }
    }
}
