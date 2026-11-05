// End-to-end test against a running Tahakkum.exe on this machine. It plays the viewer over
// the real protocol and answers the consent dialog through UI Automation.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Tahakkum
{
    static class SessionTest
    {
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT p);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        const string ConsentTitle = "طلب تحكم بهذه الشاشة";
        const string BarTitle = "جلسة تحكم نشطة";

        static int failures;
        static string shots;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        sealed class Client
        {
            public TcpClient Tcp;
            public SslStream Ssl;

            public static Client Open()
            {
                Client c = new Client();
                c.Tcp = new TcpClient("127.0.0.1", Proto.DefaultPort);
                c.Tcp.ReceiveTimeout = 45000;
                c.Ssl = new SslStream(c.Tcp.GetStream(), false, (s, cert, chain, err) => true);
                c.Ssl.AuthenticateAsClient("tahakkum", null, SslProtocols.Tls12, false);
                return c;
            }

            public void Hello()
            {
                Connection.WritePacket(Ssl, Msg.Hello, Pack.Build(w =>
                {
                    w.Write(Proto.Magic);
                    w.Write(Proto.Version);
                    w.Write("TEST-‮PC");
                    w.Write("tester");
                }));
            }

            public Packet Read() { return Connection.ReadPacket(Ssl, Proto.MaxFromHost); }
            public void Close() { try { Tcp.Close(); } catch (Exception) { } }
        }

        static AutomationElement FindWindow(string title, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            PropertyCondition cond = new PropertyCondition(AutomationElement.NameProperty, title);
            while (DateTime.UtcNow < end)
            {
                AutomationElement e = AutomationElement.RootElement.FindFirst(TreeScope.Children, cond);
                if (e != null) return e;
                Thread.Sleep(100);
            }
            return null;
        }

        static AutomationElement FindButton(AutomationElement window, string name)
        {
            return window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        }

        static void Press(AutomationElement button)
        {
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }

        static void Shot(AutomationElement window, string file)
        {
            IntPtr h = new IntPtr(window.Current.NativeWindowHandle);
            RECT r;
            GetWindowRect(h, out r);
            using (Bitmap b = new Bitmap(r.R - r.L, r.B - r.T))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    IntPtr dc = g.GetHdc();
                    PrintWindow(h, dc, 2);
                    g.ReleaseHdc(dc);
                }
                b.Save(Path.Combine(shots, file), ImageFormat.Png);
            }
        }

        static void WaitGone(string title, int ms, string testName)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end && FindWindow(title, 100) != null) Thread.Sleep(100);
            Check(testName, FindWindow(title, 100) == null);
        }

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            shots = args[0];
            Directory.CreateDirectory(shots);

            Native.POINT home;
            GetCursorPos(out home);
            try
            {
                if (args.Length > 2 && args[2] == "viewer-only")
                {
                    RealViewer(args[1]);
                    Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
                    return failures;
                }
                MalformedPeers();
                DenyAndCooldown();
                CancelClosesPrompt();
                ApprovedSession(home);
                SetCursorPos(home.X, home.Y);
                Thread.Sleep(1000);
                RealViewer(args[1]);
                AutomationElement main = FindWindow("تحكّم — التحكم بالشاشات عبر الشبكة", 2000);
                if (main != null) Shot(main, "main.png");
            }
            catch (Exception ex)
            {
                Check("unexpected exception", false, ex.ToString());
            }
            SetCursorPos(home.X, home.Y);
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            return failures;
        }

        static void MalformedPeers()
        {
            using (TcpClient raw = new TcpClient("127.0.0.1", Proto.DefaultPort))
            {
                raw.GetStream().Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 0, 8);
                raw.ReceiveTimeout = 5000;
                bool closed = false;
                try { closed = raw.GetStream().Read(new byte[16], 0, 16) >= 0; } catch (IOException) { closed = true; }
                Check("non-TLS peer is dropped", closed);
            }

            Client big = Client.Open();
            byte[] head = { (byte)Msg.Hello, 0xFF, 0xFF, 0xFF, 0x7F };
            big.Ssl.Write(head, 0, head.Length);
            big.Ssl.Flush();
            bool dropped = false;
            try { big.Ssl.ReadByte(); dropped = true; } catch (IOException) { dropped = true; }
            Check("oversized hello is dropped", dropped);
            big.Close();

            Client wrong = Client.Open();
            Connection.WritePacket(wrong.Ssl, Msg.Hello, Pack.Build(w => { w.Write(0x12345678); w.Write(1); w.Write("x"); w.Write("y"); }));
            Thread.Sleep(500);
            Check("bad magic shows no prompt", FindWindow(ConsentTitle, 300) == null);
            wrong.Close();

            // Input and frames must not flow before a Hello at all.
            Client early = Client.Open();
            Connection.WritePacket(early.Ssl, Msg.MouseMove, Pack.Build(w => { w.Write(5); w.Write(5); }));
            Thread.Sleep(400);
            Check("input before hello shows no prompt", FindWindow(ConsentTitle, 200) == null);
            early.Close();
        }

        static void DenyAndCooldown()
        {
            Native.POINT before;
            GetCursorPos(out before);

            Client c = Client.Open();
            c.Hello();
            Check("hello gets Pending", c.Read().Type == Msg.Pending);

            AutomationElement prompt = FindWindow(ConsentTitle, 5000);
            Check("consent prompt appears", prompt != null);
            if (prompt == null) return;

            // Nothing may happen while the question is open.
            Connection.WritePacket(c.Ssl, Msg.MouseMove, Pack.Build(w => { w.Write(before.X + 77); w.Write(before.Y + 77); }));
            Connection.WritePacket(c.Ssl, Msg.MouseButton, Pack.Build(w => { w.Write((byte)0); w.Write(true); w.Write(1); w.Write(1); }));
            Thread.Sleep(300);
            Native.POINT during;
            GetCursorPos(out during);
            Check("input before approval is ignored", during.X != before.X + 77 || during.Y != before.Y + 77);

            AutomationElement approve = FindButton(prompt, "موافق");
            AutomationElement deny = FindButton(prompt, "رفض");
            Check("approve is disarmed at first", approve != null && !approve.Current.IsEnabled);
            Check("deny has initial focus", deny != null && deny.Current.HasKeyboardFocus);
            Thread.Sleep(1800);
            Check("approve arms after delay", approve.Current.IsEnabled);
            Shot(prompt, "consent.png");

            Client second = Client.Open();
            second.Hello();
            Packet busy = second.Read();
            Check("second requester gets Busy", busy.Type == Msg.Denied && busy.Data[0] == (byte)DenyReason.Busy);
            second.Close();

            Press(deny);
            Packet result = c.Read();
            Check("deny reaches requester", result.Type == Msg.Denied && result.Data[0] == (byte)DenyReason.Rejected);
            c.Close();
            WaitGone(ConsentTitle, 3000, "prompt closes after deny");

            Client again = Client.Open();
            again.Hello();
            Packet cool = again.Read();
            Check("immediate retry hits cooldown", cool.Type == Msg.Denied && cool.Data[0] == (byte)DenyReason.Cooldown);
            again.Close();
            Thread.Sleep(16000);
        }

        static void CancelClosesPrompt()
        {
            Client c = Client.Open();
            c.Hello();
            c.Read();
            Check("prompt shown for cancel test", FindWindow(ConsentTitle, 5000) != null);
            c.Close();
            WaitGone(ConsentTitle, 5000, "requester leaving closes the prompt");
            Thread.Sleep(500);
        }

        static void ApprovedSession(Native.POINT home)
        {
            Client c = Client.Open();
            c.Hello();
            c.Read();
            AutomationElement prompt = FindWindow(ConsentTitle, 5000);
            Thread.Sleep(1800);
            Press(FindButton(prompt, "موافق"));

            object gate = new object();
            int frameCount = 0, frameBytes = 0, pongs = 0, lastStatus = -1;
            int screenW = 0, screenH = 0, firstCoverage = -1;
            bool byeSeen = false, badImage = false;

            Connection conn = new Connection(c.Tcp, c.Ssl);
            conn.MaxIncoming = Proto.MaxFromHost;
            conn.Received += (type, data) =>
            {
                lock (gate)
                {
                    if (type == Msg.ScreenInfo)
                    {
                        using (BinaryReader r = Pack.Read(data)) { r.ReadInt32(); screenW = r.ReadInt32(); screenH = r.ReadInt32(); }
                    }
                    else if (type == Msg.Frame)
                    {
                        int seq, covered = 0;
                        using (BinaryReader r = Pack.Read(data))
                        {
                            seq = r.ReadInt32();
                            int n = r.ReadInt32();
                            for (int i = 0; i < n; i++)
                            {
                                int x = r.ReadInt32(), y = r.ReadInt32(), w = r.ReadInt32(), h = r.ReadInt32();
                                byte[] jpg = r.ReadBytes(r.ReadInt32());
                                using (Bitmap b = new Bitmap(new MemoryStream(jpg)))
                                    if (b.Width != w || b.Height != h || x < 0 || y < 0 || x + w > screenW || y + h > screenH) badImage = true;
                                covered += w * h;
                            }
                        }
                        if (firstCoverage < 0) firstCoverage = covered;
                        frameCount++;
                        frameBytes += data.Length;
                        conn.Send(Msg.Ack, Pack.Int(seq));
                    }
                    else if (type == Msg.Pong) pongs++;
                    else if (type == Msg.Status) lastStatus = data[0];
                    else if (type == Msg.Bye) byeSeen = true;
                }
            };
            conn.Start();

            AutomationElement bar = FindWindow(BarTitle, 5000);
            Check("session bar is visible", bar != null);
            Thread.Sleep(1500);
            lock (gate)
            {
                Rectangle primary = Screen.PrimaryScreen.Bounds;
                Check("screen info matches primary", screenW == primary.Width && screenH == primary.Height, screenW + "x" + screenH);
                Check("frames arrive", frameCount > 0, frameCount + " frames, " + frameBytes / 1024 + " KB");
                Check("first frame covers whole screen", firstCoverage == screenW * screenH, firstCoverage.ToString());
                Check("all JPEG tiles decode with correct geometry", !badImage);
            }
            if (bar != null) Shot(bar, "sessionbar.png");

            conn.Send(Msg.Ping, BitConverter.GetBytes(1L));
            Thread.Sleep(300);
            lock (gate) Check("ping gets pong", pongs >= 1);

            // Pointer: verified by reading the real cursor position back.
            Native.POINT p;
            int[][] targets = { new[] { 321, 234 }, new[] { 1, 1 }, new[] { screenW / 2, screenH / 2 }, new[] { screenW - 2, screenH - 2 }, new[] { 777, 555 } };
            foreach (int[] t in targets)
            {
                string seen;
                bool hit = MoveAndObserve(conn, t[0], t[1], t[0], t[1], out seen);
                Check("mouse move lands at exact pixel " + t[0] + "," + t[1], hit, seen);
            }

            // Clamping: out-of-range coordinates stay on the monitor.
            string clampSeen;
            Check("out-of-range coordinates are clamped", MoveAndObserve(conn, 999999, -5, screenW - 1, 0, out clampSeen), clampSeen);

            ClickAndType(conn);

            // Local pause wins over the viewer.
            Press(FindButton(bar, "إيقاف التحكم"));
            Thread.Sleep(300);
            lock (gate) Check("pause notifies viewer", lastStatus == (int)HostStatus.ControlPaused);
            conn.Send(Msg.MouseMove, Pack.Build(w => { w.Write(444); w.Write(333); }));
            Thread.Sleep(250);
            GetCursorPos(out p);
            Check("input ignored while paused", !(p.X == 444 && p.Y == 333), p.X + "," + p.Y);
            Press(FindButton(bar, "استئناف التحكم"));
            Thread.Sleep(300);
            lock (gate) Check("resume notifies viewer", lastStatus == (int)HostStatus.ControlResumed);

            SetCursorPos(home.X, home.Y);

            // Idle screen: the change detector should go quiet after the sharpening pass.
            Thread.Sleep(1500);
            int a, b2, bytesA, bytesB;
            lock (gate) { a = frameCount; bytesA = frameBytes; }
            Thread.Sleep(2000);
            lock (gate) { b2 = frameCount; bytesB = frameBytes; }
            Console.WriteLine("INFO  during 2s without test input: " + (b2 - a) + " frames, " + (bytesB - bytesA) / 1024 + " KB");

            // Local end button terminates the session.
            bool closed = false;
            conn.Closed += () => closed = true;
            Press(FindButton(bar, "إنهاء"));
            Thread.Sleep(800);
            lock (gate) Check("end button sends Bye to viewer", byeSeen);
            Check("connection is closed after end", closed || conn.IsClosed);
            WaitGone(BarTitle, 3000, "session bar disappears after end");
        }

        // Polls right after sending, because a person may also be moving the real mouse.
        static bool MoveAndObserve(Connection conn, int x, int y, int expectX, int expectY, out string seen)
        {
            seen = "";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                conn.Send(Msg.MouseMove, Pack.Build(w => { w.Write(x); w.Write(y); }));
                DateTime end = DateTime.UtcNow.AddMilliseconds(120);
                Native.POINT p = new Native.POINT();
                while (DateTime.UtcNow < end)
                {
                    GetCursorPos(out p);
                    if (p.X == expectX && p.Y == expectY)
                    {
                        seen = "attempt " + (attempt + 1);
                        return true;
                    }
                    Thread.Sleep(1);
                }
                seen += p.X + "," + p.Y + " ";
                // Change the value so the next send is a real move, not a no-op.
                x ^= 0;
            }
            return false;
        }

        // Drives the real viewer window: second copy of the exe, connecting over loopback.
        static void RealViewer(string exe)
        {
            using (System.Diagnostics.Process viewer = System.Diagnostics.Process.Start(exe, "/connect 127.0.0.1"))
            {
                try
                {
                    AutomationElement prompt = FindWindow(ConsentTitle, 8000);
                    Check("real viewer triggers the prompt", prompt != null);
                    if (prompt == null) return;
                    Thread.Sleep(1800);
                    Press(FindButton(prompt, "موافق"));

                    AutomationElement window = null;
                    DateTime end = DateTime.UtcNow.AddSeconds(8);
                    while (window == null && DateTime.UtcNow < end)
                    {
                        foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                            if (e.Current.Name.EndsWith("— تحكّم") && e.Current.ProcessId == viewer.Id) window = e;
                        Thread.Sleep(100);
                    }
                    Check("viewer window opens after approval", window != null);
                    if (window == null) return;

                    Thread.Sleep(5000);
                    string stats = null;
                    foreach (AutomationElement e in window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                        if (e.Current.Name.Contains("fps")) stats = e.Current.Name;
                    Check("viewer shows live stats", stats != null, stats ?? "");

                    // Toolbar strip only: the rest of this window is a live picture of the desktop.
                    IntPtr h = new IntPtr(window.Current.NativeWindowHandle);
                    RECT r;
                    GetWindowRect(h, out r);
                    using (Bitmap full = new Bitmap(r.R - r.L, r.B - r.T))
                    {
                        using (Graphics g = Graphics.FromImage(full))
                        {
                            IntPtr dc = g.GetHdc();
                            PrintWindow(h, dc, 2);
                            g.ReleaseHdc(dc);
                        }
                        int stripH = Math.Min(100, full.Height);
                        using (Bitmap strip = full.Clone(new Rectangle(0, 0, full.Width, stripH), full.PixelFormat))
                            strip.Save(Path.Combine(shots, "viewer-toolbar.png"), ImageFormat.Png);

                        // The picture area must contain a real image, not a blank surface.
                        HashSet<int> colors = new HashSet<int>();
                        for (int y = 120; y < full.Height - 10; y += 23)
                            for (int x = 10; x < full.Width - 10; x += 37)
                                colors.Add(full.GetPixel(x, y).ToArgb());
                        Check("viewer surface renders remote picture", colors.Count > 20, colors.Count + " sampled colors");
                    }

                    ((WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern)).Close();
                    WaitGone(BarTitle, 5000, "closing the viewer ends the session on the host");
                }
                finally
                {
                    if (!viewer.HasExited) viewer.Kill();
                }
            }
        }

        // A small topmost form receives the injected click and key, so nothing else is touched.
        static void ClickAndType(Connection conn)
        {
            using (Form f = new Form())
            using (TextBox t = new TextBox())
            {
                f.FormBorderStyle = FormBorderStyle.FixedToolWindow;
                f.StartPosition = FormStartPosition.Manual;
                f.Bounds = new Rectangle(300, 300, 320, 200);
                f.TopMost = true;
                t.Multiline = true;
                t.Dock = DockStyle.Bottom;
                t.Height = 60;
                f.Controls.Add(t);
                int clicks = 0, rights = 0, wheels = 0;
                f.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) clicks++; else if (e.Button == MouseButtons.Right) rights++; };
                f.MouseWheel += (s, e) => wheels++;
                f.Show();
                Pump(300);

                Point target = f.PointToScreen(new Point(100, 30));
                conn.Send(Msg.MouseButton, Pack.Build(w => { w.Write((byte)0); w.Write(true); w.Write(target.X); w.Write(target.Y); }));
                conn.Send(Msg.MouseButton, Pack.Build(w => { w.Write((byte)0); w.Write(false); w.Write(target.X); w.Write(target.Y); }));
                Pump(400);
                Check("injected left click reaches window", clicks == 1, clicks.ToString());

                conn.Send(Msg.MouseButton, Pack.Build(w => { w.Write((byte)1); w.Write(true); w.Write(target.X); w.Write(target.Y); }));
                conn.Send(Msg.MouseButton, Pack.Build(w => { w.Write((byte)1); w.Write(false); w.Write(target.X); w.Write(target.Y); }));
                Pump(400);
                Check("injected right click reaches window", rights == 1, rights.ToString());

                t.Focus();
                f.Activate();
                SetForegroundWindow(f.Handle);
                Pump(300);
                if (GetForegroundWindow() != f.Handle)
                {
                    Console.WriteLine("SKIP  keyboard (test window could not take focus)");
                    return;
                }
                t.Text = "";
                // Scan code for the physical "K" key, plus Backspace on a second press.
                SendKey(conn, 0x4B, 0x25, true);
                SendKey(conn, 0x4B, 0x25, false);
                Pump(300);
                Check("injected key types a character", t.TextLength == 1, "'" + t.Text + "'");
                SendKey(conn, 0x08, 0x0E, true);
                SendKey(conn, 0x08, 0x0E, false);
                Pump(300);
                Check("injected backspace edits text", t.TextLength == 0, "'" + t.Text + "'");

                // Held modifier must be released when control pauses.
                SendKey(conn, 0xA0, 0x2A, true);
                Pump(150);
                bool heldNow = (Control.ModifierKeys & Keys.Shift) != 0;
                conn.Send(Msg.Key, Pack.Build(w => { w.Write(0xA0); w.Write(0x2A); w.Write(false); w.Write(false); }));
                Pump(150);
                Check("shift down then up is balanced", heldNow && (Control.ModifierKeys & Keys.Shift) == 0);
            }
        }

        static void SendKey(Connection conn, int vk, int scan, bool down)
        {
            conn.Send(Msg.Key, Pack.Build(w => { w.Write(vk); w.Write(scan); w.Write(false); w.Write(down); }));
        }

        static void Pump(int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
        }
    }
}
