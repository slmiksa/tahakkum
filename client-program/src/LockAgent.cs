using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Tahakkum
{
    // Runs as SYSTEM inside the console session (launched by the service). While the machine is
    // locked, the input desktop is the secure "Winlogon" desktop, which an ordinary user process
    // cannot see or drive. This agent follows that desktop on its own thread, captures it, and
    // replays the input the user process forwards, so a password-authenticated viewer can sign in.
    static class LockAgent
    {
        const int MinFrameMs = 50;      // about 20 frames a second is plenty for a login screen
        const int RefineAfterMs = 400;

        static readonly object inputGate = new object();
        static readonly Queue<byte[]> inputQueue = new Queue<byte[]>();

        public static void Run()
        {
            // Match the viewer's pixels, so injected clicks land where they are drawn.
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (Exception) { try { Native.SetProcessDPIAware(); } catch (Exception) { } }
            LockLaunch.Log("agent started (session " + Native.WTSGetActiveConsoleSessionId() + ")");
            // Runs until the service stops it (the service terminates this process).
            while (true)
            {
                try { Serve(); }
                catch (Exception ex) { LockLaunch.Log("agent serve error: " + ex.GetType().Name + " " + ex.Message); }
                Thread.Sleep(1500);
            }
        }

        static void Serve()
        {
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", LockLaunch.DataPipe, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                try { pipe.Connect(4000); }
                catch (TimeoutException) { return; }      // the user process is not asking for help right now
                catch (Exception) { return; }

                LockChannel channel = new LockChannel(pipe);
                channel.Write(LockMsg.Hello, null);

                Thread reader = new Thread(() => ReadInput(channel));
                reader.IsBackground = true;
                reader.Name = "lock-agent-reader";
                reader.Start();

                CaptureSecureDesktop(channel);
                lock (inputGate) inputQueue.Clear();
            }
        }

        static void ReadInput(LockChannel channel)
        {
            try
            {
                byte type;
                byte[] payload;
                while (channel.Read(out type, out payload))
                {
                    if (type == LockMsg.Input && payload.Length > 0)
                        lock (inputGate) { if (inputQueue.Count < 500) inputQueue.Enqueue(payload); }
                }
            }
            catch (Exception) { }
        }

        // The whole capture and injection must happen on one thread that is attached to the input
        // desktop, so SendInput and the screen DC both target the secure desktop.
        static void CaptureSecureDesktop(LockChannel channel)
        {
            ScreenCapturer capturer = null;
            InputInjector injector = new InputInjector();
            Rectangle bounds = Rectangle.Empty;
            string lastDesktop = "";
            bool capturing = false;
            int seq = 0;
            bool force = true;
            long lastChangeMs = 0;
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                while (true)
                {
                    long now = clock.ElapsedMilliseconds;

                    // Follow whichever desktop currently receives input.
                    IntPtr input = Native.OpenInputDesktop(0, false, Native.DESKTOP_RIGHTS);
                    string name = lastDesktop;
                    if (input != IntPtr.Zero)
                    {
                        name = LockLaunch.DesktopName(input);
                        IntPtr current = Native.GetThreadDesktop(Native.GetCurrentThreadId());
                        if (name != lastDesktop || current != input)
                        {
                            if (Native.SetThreadDesktop(input))
                            {
                                lastDesktop = name;
                                if (capturer != null) { capturer.Dispose(); capturer = null; }
                                force = true;
                            }
                        }
                        Native.CloseDesktop(input);
                    }

                    bool secure = string.Equals(name, LockLaunch.SecureDesktop, StringComparison.OrdinalIgnoreCase);
                    if (secure != capturing)
                    {
                        capturing = secure;
                        channel.Write(LockMsg.State, new byte[] { (byte)(secure ? 1 : 0) });
                        if (!secure && capturer != null) { capturer.Dispose(); capturer = null; }
                    }

                    if (!secure)
                    {
                        lock (inputGate) inputQueue.Clear();
                        Thread.Sleep(250);
                        continue;
                    }

                    Rectangle primary = PrimaryBounds();
                    if (capturer == null || primary != bounds)
                    {
                        bounds = primary;
                        if (capturer != null) capturer.Dispose();
                        capturer = new ScreenCapturer(bounds);
                        injector.SetMonitor(bounds);
                        channel.Write(LockMsg.ScreenInfo, BuildScreenInfo(bounds));
                        force = true;
                    }

                    DrainInput(injector);

                    if (!capturer.Grab())
                    {
                        Thread.Sleep(80);
                        continue;
                    }

                    List<Rectangle> rects = capturer.Diff(force);
                    int quality = 75;
                    if (rects.Count > 0)
                    {
                        lastChangeMs = now;
                        if (force) force = false;
                    }
                    else if (now - lastChangeMs >= RefineAfterMs && capturer.HasPendingRefine)
                    {
                        rects = capturer.TakeRefine();
                        quality = 92;
                    }

                    if (rects.Count > 0)
                    {
                        seq++;
                        channel.Write(LockMsg.Frame, BuildFrame(capturer, rects, seq, quality));
                    }

                    int spent = (int)(clock.ElapsedMilliseconds - now);
                    if (spent < MinFrameMs) Thread.Sleep(MinFrameMs - spent);
                }
            }
            finally
            {
                if (capturer != null) capturer.Dispose();
            }
        }

        static void DrainInput(InputInjector injector)
        {
            while (true)
            {
                byte[] payload;
                lock (inputGate)
                {
                    if (inputQueue.Count == 0) break;
                    payload = inputQueue.Dequeue();
                }
                try
                {
                    using (System.IO.BinaryReader r = Pack.Read(payload))
                    {
                        byte kind = r.ReadByte();
                        switch (kind)
                        {
                            case LockMsg.InMove:
                                injector.Move(r.ReadInt32(), r.ReadInt32());
                                break;
                            case LockMsg.InButton:
                                byte btn = r.ReadByte();
                                bool bdown = r.ReadBoolean();
                                injector.Move(r.ReadInt32(), r.ReadInt32());
                                injector.Button(btn, bdown);
                                break;
                            case LockMsg.InWheel:
                                int delta = r.ReadInt32();
                                injector.Wheel(delta, r.ReadBoolean());
                                break;
                            case LockMsg.InKey:
                                int vk = r.ReadInt32();
                                int scan = r.ReadInt32();
                                bool ext = r.ReadBoolean();
                                bool kdown = r.ReadBoolean();
                                int ch = (r.BaseStream.Position + 2 <= r.BaseStream.Length) ? r.ReadUInt16() : 0;
                                // A printable key is typed by its character, so the secure desktop's own
                                // keyboard layout cannot turn the password into something else. One press
                                // per key-down; the matching key-up is nothing to replay for a character.
                                if (ch != 0)
                                {
                                    if (kdown) { injector.Char(ch, true); injector.Char(ch, false); }
                                }
                                else injector.Key(vk, scan, ext, kdown);
                                break;
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        static Rectangle PrimaryBounds()
        {
            try
            {
                Screen s = Screen.PrimaryScreen;
                if (s != null) return s.Bounds;
            }
            catch (Exception) { }
            int w = Native.GetSystemMetrics(0);
            int h = Native.GetSystemMetrics(1);
            return new Rectangle(0, 0, Math.Max(1, w), Math.Max(1, h));
        }

        static byte[] BuildScreenInfo(Rectangle bounds)
        {
            return Pack.Build(w =>
            {
                w.Write(0);                 // current monitor
                w.Write(bounds.Width);
                w.Write(bounds.Height);
                w.Write(1);                 // one monitor reported
                w.Write(bounds.Width);
                w.Write(bounds.Height);
                w.Write(true);              // primary
            });
        }

        static byte[] BuildFrame(ScreenCapturer capturer, List<Rectangle> rects, int seq, int quality)
        {
            byte[][] encoded = new byte[rects.Count][];
            Parallel.For(0, rects.Count, i => { encoded[i] = capturer.Encode(rects[i], quality); });
            return Pack.Build(w =>
            {
                w.Write(seq);
                w.Write(rects.Count);
                for (int i = 0; i < rects.Count; i++)
                {
                    w.Write(rects[i].X);
                    w.Write(rects[i].Y);
                    w.Write(rects[i].Width);
                    w.Write(rects[i].Height);
                    w.Write(encoded[i].Length);
                    w.Write(encoded[i]);
                }
            });
        }
    }
}
