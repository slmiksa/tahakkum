using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Tahakkum
{
    // Controls one Android TV over adb: shows its screen (polled screenshots) and sends the remote
    // keys, touch, and text. Built for Android TV, where navigation is mostly the D-pad.
    sealed class AndroidForm : Form
    {
        // Android key codes.
        const int KUp = 19, KDown = 20, KLeft = 21, KRight = 22, KCenter = 23;
        const int KBack = 4, KHome = 3, KMenu = 82, KApps = 187;
        const int KVolUp = 24, KVolDown = 25, KPower = 26, KPlay = 85;
        const int KEnter = 66, KDel = 67;

        readonly AdbTool tool;
        readonly RemoteSurface surface = new RemoteSurface();
        readonly Label status;
        readonly object inputGate = new object();
        readonly Queue<Action> inputQueue = new Queue<Action>();
        readonly AutoResetEvent inputSignal = new AutoResetEvent(false);

        volatile bool running = true;
        int intervalMs = 400;
        Point downPoint;
        Point downRemote;
        long downTime;
        bool dragging;

        public AndroidForm(AdbTool tool, string host)
        {
            this.tool = tool;
            Text = "تحكّم — شاشة أندرويد " + host;
            Icon = Program.AppIcon;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(1040), Theme.S(660));
            MinimumSize = new Size(Theme.S(760), Theme.S(520));
            RightToLeft = RightToLeft.No;
            KeyPreview = true;

            int pad = Theme.S(12);
            int panelW = Theme.S(230);

            surface.SetBounds(pad, pad, ClientSize.Width - panelW - 3 * pad, ClientSize.Height - 2 * pad);
            surface.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            surface.Overlay = "جارٍ جلب الصورة…";
            surface.MouseDown += OnSurfaceDown;
            surface.MouseMove += OnSurfaceMove;
            surface.MouseUp += OnSurfaceUp;
            Controls.Add(surface);

            Panel panel = new Panel();
            panel.SetBounds(ClientSize.Width - panelW - pad, pad, panelW, ClientSize.Height - 2 * pad);
            panel.Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            panel.BackColor = Theme.Card;
            Controls.Add(panel);
            BuildRemote(panel, panelW);

            status = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleRight);
            status.SetBounds(pad, ClientSize.Height - Theme.S(26), ClientSize.Width - panelW - 3 * pad, Theme.S(20));
            status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(status);

            FormClosing += delegate { running = false; inputSignal.Set(); };
            FormClosed += delegate { ShutDown(); };
        }

        void BuildRemote(Panel panel, int w)
        {
            int cx = w / 2;
            int y = Theme.S(16);
            Theme.MakeLabel("لوحة الريموت", Theme.UiBold(11f), Theme.Text, ContentAlignment.MiddleCenter);

            Label title = Theme.MakeLabel("لوحة الريموت", Theme.UiBold(11f), Theme.Text, ContentAlignment.MiddleCenter);
            title.SetBounds(0, y, w, Theme.S(24));
            panel.Controls.Add(title);

            int bs = Theme.S(58);           // D-pad button size
            int dy = y + Theme.S(36);
            AddKey(panel, "▲", cx - bs / 2, dy, bs, bs, KUp);
            AddKey(panel, "◀", cx - bs / 2 - bs - Theme.S(6), dy + bs + Theme.S(6), bs, bs, KLeft);
            AddKey(panel, "OK", cx - bs / 2, dy + bs + Theme.S(6), bs, bs, KCenter);
            AddKey(panel, "▶", cx - bs / 2 + bs + Theme.S(6), dy + bs + Theme.S(6), bs, bs, KRight);
            AddKey(panel, "▼", cx - bs / 2, dy + 2 * (bs + Theme.S(6)), bs, bs, KDown);

            int ry = dy + 3 * (bs + Theme.S(6)) + Theme.S(10);
            int half = (w - Theme.S(36)) / 2;
            int gap = Theme.S(12);
            AddKey(panel, "⟵ رجوع", Theme.S(12), ry, half, Theme.S(40), KBack);
            AddKey(panel, "الرئيسية", Theme.S(12) + half + gap, ry, half, Theme.S(40), KHome);
            AddKey(panel, "قائمة", Theme.S(12), ry + Theme.S(48), half, Theme.S(40), KMenu);
            AddKey(panel, "التطبيقات", Theme.S(12) + half + gap, ry + Theme.S(48), half, Theme.S(40), KApps);
            AddKey(panel, "صوت +", Theme.S(12), ry + Theme.S(96), half, Theme.S(40), KVolUp);
            AddKey(panel, "صوت −", Theme.S(12) + half + gap, ry + Theme.S(96), half, Theme.S(40), KVolDown);
            AddKey(panel, "تشغيل/إيقاف", Theme.S(12), ry + Theme.S(144), half, Theme.S(40), KPlay);
            AddKey(panel, "الطاقة", Theme.S(12) + half + gap, ry + Theme.S(144), half, Theme.S(40), KPower);

            PillButton text = new PillButton { Text = "كتابة نص…", Kind = ButtonKind.Secondary, Glyph = Theme.GlyphEdit, GlyphSize = 9f };
            text.Font = Theme.UiBold(9f);
            text.SetBounds(Theme.S(12), ry + Theme.S(196), w - Theme.S(24), Theme.S(40));
            text.Click += delegate { TypeText(); };
            panel.Controls.Add(text);
        }

        void AddKey(Panel panel, string text, int x, int y, int w, int h, int keycode)
        {
            PillButton b = new PillButton { Text = text, Kind = ButtonKind.Secondary };
            b.Font = Theme.UiBold(10f);
            b.SetBounds(x, y, w, h);
            b.Click += delegate { Enqueue(delegate { tool.Key(keycode); }); };
            panel.Controls.Add(b);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            Thread worker = new Thread(InputWorker) { IsBackground = true, Name = "android-input" };
            worker.Start();
            Thread capture = new Thread(CaptureLoop) { IsBackground = true, Name = "android-capture" };
            capture.Start();
        }

        // ---- Screen ----

        void CaptureLoop()
        {
            int failures = 0;
            while (running)
            {
                byte[] png = tool.Screencap();
                if (png == null)
                {
                    failures++;
                    SetStatus(failures >= 3 ? "انقطع الاتصال بالتلفزيون. تأكد من تشغيله وتفعيل التصحيح عبر الشبكة." : "تعذّر جلب الصورة، إعادة المحاولة…");
                    if (failures >= 3) surface.Overlay = "انقطع الاتصال";
                    Sleep(1000);
                    continue;
                }
                failures = 0;
                try
                {
                    Bitmap bmp;
                    using (MemoryStream ms = new MemoryStream(png))
                    using (Bitmap tmp = new Bitmap(ms))
                        bmp = new Bitmap(tmp);
                    Bitmap old;
                    lock (surface.Gate)
                    {
                        old = surface.Back;
                        surface.Back = bmp;
                        surface.Overlay = null;
                    }
                    if (old != null) old.Dispose();
                    BeginInvokeSafe(delegate { surface.Invalidate(); });
                    SetStatus("متصل — يُحدَّث كل " + intervalMs + " مللي ثانية");
                }
                catch (Exception) { }
                Sleep(intervalMs);
            }
        }

        // ---- Touch (maps the click on the shown image to the device's own pixels) ----

        void OnSurfaceDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            surface.Focus();
            downPoint = e.Location;
            downRemote = surface.ToRemote(e.Location);
            downTime = Environment.TickCount;
            dragging = true;
        }

        void OnSurfaceMove(object sender, MouseEventArgs e) { }

        void OnSurfaceUp(object sender, MouseEventArgs e)
        {
            if (!dragging || e.Button != MouseButtons.Left) return;
            dragging = false;
            Point up = surface.ToRemote(e.Location);
            int dx = up.X - downRemote.X, dy = up.Y - downRemote.Y;
            int dist = Math.Abs(dx) + Math.Abs(dy);
            int held = Environment.TickCount - (int)downTime;
            Point from = downRemote, to = up;
            if (dist < 12)
                Enqueue(delegate { tool.Tap(from.X, from.Y); });
            else
                Enqueue(delegate { tool.Swipe(from.X, from.Y, to.X, to.Y, Math.Max(80, Math.Min(800, held))); });
        }

        // ---- Keyboard ----

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            int code = 0;
            switch (keyData)
            {
                case Keys.Up: code = KUp; break;
                case Keys.Down: code = KDown; break;
                case Keys.Left: code = KLeft; break;
                case Keys.Right: code = KRight; break;
                case Keys.Enter: code = KEnter; break;
                case Keys.Back: code = KDel; break;
                case Keys.Escape: code = KBack; break;
            }
            if (code != 0)
            {
                int c = code;
                Enqueue(delegate { tool.Key(c); });
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.KeyChar >= 32 && e.KeyChar != 127)
            {
                string s = e.KeyChar.ToString();
                Enqueue(delegate { tool.Text(s); });
                e.Handled = true;
            }
        }

        void TypeText()
        {
            using (TextPromptForm f = new TextPromptForm("اكتب النص المراد إرساله إلى التلفزيون:"))
                if (f.ShowDialog(this) == DialogResult.OK && f.Value.Length > 0)
                {
                    string s = f.Value;
                    Enqueue(delegate { tool.Text(s); });
                }
        }

        // ---- Input queue: keeps order and never blocks the window ----

        void Enqueue(Action a)
        {
            lock (inputGate) { if (inputQueue.Count < 200) inputQueue.Enqueue(a); }
            inputSignal.Set();
        }

        void InputWorker()
        {
            while (running)
            {
                Action a = null;
                lock (inputGate) { if (inputQueue.Count > 0) a = inputQueue.Dequeue(); }
                if (a == null) { inputSignal.WaitOne(500); continue; }
                try { a(); } catch (Exception) { }
            }
        }

        void ShutDown()
        {
            running = false;
            inputSignal.Set();
            AdbTool t = tool;
            Thread bye = new Thread(delegate () { try { t.Disconnect(); } catch (Exception) { } }) { IsBackground = true };
            bye.Start();
            lock (surface.Gate) { if (surface.Back != null) { surface.Back.Dispose(); surface.Back = null; } }
        }

        void Sleep(int ms)
        {
            int left = ms;
            while (left > 0 && running) { Thread.Sleep(Math.Min(100, left)); left -= 100; }
        }

        void SetStatus(string text)
        {
            BeginInvokeSafe(delegate { status.Text = text; });
        }

        void BeginInvokeSafe(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (Exception) { }
        }
    }
}
