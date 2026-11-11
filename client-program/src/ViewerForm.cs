using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Tahakkum
{
    sealed class RemoteSurface : Control
    {
        public readonly object Gate = new object();
        public Bitmap Back;
        public string Overlay;
        public bool FitToWindow = true;

        public RemoteSurface()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            RightToLeft = RightToLeft.No;
        }

        public Rectangle ImageRect()
        {
            lock (Gate)
            {
                if (Back == null) return Rectangle.Empty;
                if (!FitToWindow) return new Rectangle(0, 0, Back.Width, Back.Height);
                double s = Math.Min((double)Width / Back.Width, (double)Height / Back.Height);
                int w = Math.Max(1, (int)Math.Round(Back.Width * s));
                int h = Math.Max(1, (int)Math.Round(Back.Height * s));
                return new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            }
        }

        public Point ToRemote(Point p)
        {
            lock (Gate)
            {
                if (Back == null) return Point.Empty;
                Rectangle r = ImageRect();
                int x = (int)((long)(p.X - r.X) * Back.Width / Math.Max(1, r.Width));
                int y = (int)((long)(p.Y - r.Y) * Back.Height / Math.Max(1, r.Height));
                return new Point(Math.Max(0, Math.Min(Back.Width - 1, x)), Math.Max(0, Math.Min(Back.Height - 1, y)));
            }
        }

        public Rectangle ToLocal(Rectangle remote)
        {
            lock (Gate)
            {
                if (Back == null) return Rectangle.Empty;
                Rectangle r = ImageRect();
                double sx = (double)r.Width / Back.Width;
                double sy = (double)r.Height / Back.Height;
                Rectangle l = new Rectangle(r.X + (int)(remote.X * sx), r.Y + (int)(remote.Y * sy), (int)Math.Ceiling(remote.Width * sx), (int)Math.Ceiling(remote.Height * sy));
                l.Inflate(2, 2);
                return l;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.Black);
            lock (Gate)
            {
                if (Back != null)
                {
                    Rectangle r = ImageRect();
                    bool native = r.Width == Back.Width && r.Height == Back.Height;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = native ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(Back, r);
                    g.CompositingMode = CompositingMode.SourceOver;
                }
            }
            if (!string.IsNullOrEmpty(Overlay))
            {
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(190, 13, 17, 28)))
                    g.FillRectangle(shade, ClientRectangle);
                using (Font f = Theme.UiBold(15f))
                    TextRenderer.DrawText(g, Overlay, f, ClientRectangle, Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.RightToLeft | TextFormatFlags.WordBreak);
            }
        }
    }

    sealed class ViewerForm : Form
    {
        readonly Connection conn;
        readonly string address;
        readonly string code;
        readonly RemoteSurface surface = new RemoteSurface();
        readonly Panel surfaceHost = new Panel();
        readonly Panel toolbar = new Panel();
        readonly FlowLayoutPanel tools = new FlowLayoutPanel();
        readonly Label stats;
        readonly PillButton monitorButton;
        readonly PillButton fitButton;
        readonly PillButton fullButton;
        readonly PillButton viewOnlyButton;
        readonly PillButton controlButton;
        readonly ContextMenuStrip monitorMenu;
        readonly ContextMenuStrip qualityMenu;
        readonly ContextMenuStrip keysMenu;
        readonly System.Windows.Forms.Timer tick;
        readonly HashSet<long> heldKeys = new HashSet<long>();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Native.LowLevelProc hookProc;
        readonly Cursor dotCursor;
        readonly FileTransferService files;
        readonly FileTransferWindow transfers;
        ClipboardSync clipboard;

        IntPtr hook;
        int openMenus;
        bool viewOnly;
        bool remotePaused;
        // A host from before shared sessions never says, and there the only viewer controls.
        volatile bool hasControl = true;
        string controllerName = "";
        int viewerCount = 1;
        bool fullscreen;
        bool closingByUs;
        FormWindowState restoreState;
        Rectangle restoreBounds;

        int invalidatePending;
        Rectangle dirty;
        readonly object dirtyGate = new object();
        int frames;
        long bytes;
        long latency = -1;
        int currentMonitor;
        int remoteW;
        int remoteH;
        Point lastMoveSent = new Point(-1, -1);
        long lastMoveAt;

        public ViewerForm(Connection conn, string address, string hostName, string code)
        {
            this.conn = conn;
            this.address = address;
            this.code = code;
            Text = (string.IsNullOrEmpty(hostName) ? address : hostName + "  (" + address + ")") + Lang.L("  —  رمز التحقق ", "  —  Verification code ") + code + Lang.L(" — تحكّم", " — Tahakkum");
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Program.AppIcon;
            KeyPreview = false;
            RightToLeft = RightToLeft.No;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Theme.S(1280), area.Width - 80), Math.Min(Theme.S(800), area.Height - 60));
            MinimumSize = new Size(Theme.S(760), Theme.S(420));
            hookProc = HookCallback;
            dotCursor = MakeDotCursor();

            toolbar.Dock = DockStyle.Top;
            toolbar.Height = Theme.S(50);
            toolbar.BackColor = Theme.Card;

            tools.Dock = DockStyle.Fill;
            tools.FlowDirection = FlowDirection.RightToLeft;
            tools.WrapContents = false;
            tools.BackColor = Theme.Card;
            tools.Padding = new Padding(Theme.S(6), Theme.S(8), Theme.S(6), 0);

            PillButton end = AddTool(Lang.L("إنهاء", "End"), Theme.GlyphClose, Theme.S(86), ButtonKind.Danger);
            end.Click += delegate { CloseByUser(); };

            files = new FileTransferService(conn, address);
            transfers = new FileTransferWindow(files, string.IsNullOrEmpty(hostName) ? address : hostName + " (" + address + ")");
            PillButton filesButton = AddTool(Lang.L("إرسال ملفات", "Send files"), Theme.GlyphFile, Theme.S(116), ButtonKind.Secondary);
            filesButton.Click += delegate { transfers.PickAndSend(this); };

            // Shown only while others are watching too.
            controlButton = AddTool(Lang.L("أخذ التحكم", "Take control"), Theme.GlyphConnect, Theme.S(124), ButtonKind.Primary);
            controlButton.Visible = false;
            controlButton.Click += delegate { if (!hasControl) conn.Send(Msg.ControlRequest, null); };

            monitorMenu = NewMenu();
            monitorButton = AddTool(Lang.L("الشاشة 1", "Screen 1"), Theme.GlyphMonitor, Theme.S(110), ButtonKind.Secondary);
            monitorButton.Click += delegate { ShowMenu(monitorMenu, monitorButton); };

            fitButton = AddTool(Lang.L("حجم فعلي", "Actual size"), Theme.GlyphFit, Theme.S(112), ButtonKind.Secondary);
            fitButton.Click += delegate { ToggleFit(); };

            fullButton = AddTool(Lang.L("ملء الشاشة", "Full screen"), Theme.GlyphFullscreen, Theme.S(120), ButtonKind.Secondary);
            fullButton.Click += delegate { ToggleFullscreen(); };

            qualityMenu = NewMenu();
            AddQuality(Lang.L("تلقائية (موصى بها)", "Automatic (recommended)"), 0, true);
            AddQuality(Lang.L("عالية", "High"), 1, false);
            AddQuality(Lang.L("متوازنة", "Balanced"), 2, false);
            AddQuality(Lang.L("سرعة أعلى", "Faster"), 3, false);
            PillButton quality = AddTool(Lang.L("الجودة", "Quality"), Theme.GlyphQuality, Theme.S(92), ButtonKind.Secondary);
            quality.Click += delegate { ShowMenu(qualityMenu, quality); };

            keysMenu = NewMenu();
            AddShortcut(Lang.L("زر ويندوز", "Windows key"), 0x5B);
            AddShortcut("Alt + Tab", 0xA4, 0x09);
            AddShortcut(Lang.L("Ctrl + Shift + Esc (مدير المهام)", "Ctrl + Shift + Esc (Task Manager)"), 0xA2, 0xA0, 0x1B);
            AddShortcut(Lang.L("Win + R (تشغيل)", "Win + R (Run)"), 0x5B, 0x52);
            AddShortcut(Lang.L("Win + D (سطح المكتب)", "Win + D (Desktop)"), 0x5B, 0x44);
            AddShortcut(Lang.L("Win + E (المستكشف)", "Win + E (Explorer)"), 0x5B, 0x45);
            AddShortcut(Lang.L("Win + L (قفل الشاشة)", "Win + L (Lock screen)"), 0x5B, 0x4C);
            PillButton keys = AddTool(Lang.L("اختصارات", "Shortcuts"), Theme.GlyphKeyboard, Theme.S(108), ButtonKind.Secondary);
            keys.Click += delegate { ShowMenu(keysMenu, keys); };

            viewOnlyButton = AddTool(Lang.L("عرض فقط", "View only"), Theme.GlyphView, Theme.S(108), ButtonKind.Secondary);
            viewOnlyButton.Click += delegate { ToggleViewOnly(); };

            stats = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleLeft);
            stats.RightToLeft = RightToLeft.No;
            stats.BackColor = Theme.Card;
            stats.Dock = DockStyle.Left;
            stats.Width = Theme.S(200);
            stats.AutoEllipsis = true;
            stats.Padding = new Padding(Theme.S(12), 0, 0, 0);

            toolbar.Controls.Add(tools);
            toolbar.Controls.Add(stats);

            surfaceHost.Dock = DockStyle.Fill;
            surfaceHost.BackColor = Color.Black;
            surfaceHost.RightToLeft = RightToLeft.No;
            surface.Dock = DockStyle.Fill;
            surface.Cursor = dotCursor;
            surface.Overlay = Lang.L("جاري استقبال الشاشة…", "Receiving the screen…");
            surfaceHost.Controls.Add(surface);

            Controls.Add(surfaceHost);
            Controls.Add(toolbar);

            surface.MouseMove += OnSurfaceMove;
            surface.MouseDown += (s, e) => { surface.Focus(); SendButton(e, true); };
            surface.MouseUp += (s, e) => SendButton(e, false);
            surface.MouseWheel += (s, e) => SendWheel(e.Delta, false);
            surface.MouseEnter += delegate { if (ActiveForm == this) surface.Focus(); };
            surface.AllowDrop = true;
            surface.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            surface.DragDrop += (s, e) =>
            {
                // Deferred so a message box here cannot leave Explorer waiting for the drop to finish.
                string[] dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (dropped != null) BeginInvoke((Action)delegate { transfers.SendPaths(dropped); });
            };

            conn.Received += OnMessage;
            conn.Closed += OnConnectionClosed;

            tick = new System.Windows.Forms.Timer { Interval = 1000 };
            tick.Tick += delegate { OnTick(); };
            tick.Start();
        }

        static Cursor MakeDotCursor()
        {
            using (Bitmap b = new Bitmap(16, 16, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(Brushes.Black, 5, 5, 6, 6);
                g.FillEllipse(Brushes.White, 6, 6, 4, 4);
                return new Cursor(b.GetHicon());
            }
        }

        PillButton AddTool(string text, string glyph, int width, ButtonKind kind)
        {
            PillButton b = new PillButton { Text = text, Glyph = glyph, Kind = kind, GlyphSize = 10f };
            b.Font = Theme.UiBold(9f);
            b.Size = new Size(width, Theme.S(34));
            b.Margin = new Padding(Theme.S(4), 0, Theme.S(4), 0);
            b.TabStop = false;
            tools.Controls.Add(b);
            return b;
        }

        ContextMenuStrip NewMenu()
        {
            ContextMenuStrip m = Theme.MakeMenu();
            m.Opened += delegate { openMenus++; };
            m.Closed += delegate { openMenus = Math.Max(0, openMenus - 1); };
            return m;
        }

        void ShowMenu(ContextMenuStrip menu, Control under)
        {
            menu.Show(under, new Point(under.Width - menu.PreferredSize.Width, under.Height + Theme.S(4)));
        }

        void AddQuality(string text, int mode, bool isChecked)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = isChecked;
            item.Click += delegate
            {
                foreach (ToolStripItem other in qualityMenu.Items)
                    ((ToolStripMenuItem)other).Checked = other == item;
                conn.Send(Msg.SetQuality, Pack.Int(mode));
            };
            qualityMenu.Items.Add(item);
        }

        void AddShortcut(string text, params int[] vks)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { SendCombo(vks); };
            keysMenu.Items.Add(item);
        }

        void SendCombo(int[] vks)
        {
            if (viewOnly) return;
            foreach (int vk in vks) SendKey(vk, 0, IsExtendedVk(vk), true, 0);
            for (int i = vks.Length - 1; i >= 0; i--) SendKey(vks[i], 0, IsExtendedVk(vks[i]), false, 0);
        }

        static bool IsExtendedVk(int vk)
        {
            return vk == 0x5B || vk == 0x5C;
        }

        // ---- incoming ----

        void OnMessage(Msg type, byte[] data)
        {
            try
            {
                if (files.Handle(type, data)) return;
                switch (type)
                {
                    case Msg.ScreenInfo: HandleScreenInfo(data); break;
                    case Msg.Frame: HandleFrame(data); break;
                    case Msg.Pong:
                        Interlocked.Exchange(ref latency, clock.ElapsedMilliseconds - BitConverter.ToInt64(data, 0));
                        break;
                    case Msg.Status: HandleStatus((HostStatus)data[0]); break;
                    case Msg.ControlState: HandleControlState(data); break;
                    case Msg.Clipboard:
                        using (BinaryReader r = Pack.Read(data))
                        {
                            string text = r.ReadString();
                            if (text.Length <= Proto.MaxClipboardChars)
                                OnUi(delegate { if (clipboard != null) clipboard.Apply(text); });
                        }
                        break;
                    case Msg.Bye:
                        closingByUs = false;
                        break;
                }
            }
            catch (Exception ex)
            {
                if (ex is OutOfMemoryException || ex is ArgumentException || ex is IOException || ex is ExternalException) return;
                throw;
            }
        }

        void OnUi(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch (Exception) { }
        }

        void HandleScreenInfo(byte[] data)
        {
            using (BinaryReader r = Pack.Read(data))
            {
                int current = r.ReadInt32();
                int w = r.ReadInt32();
                int h = r.ReadInt32();
                int count = r.ReadInt32();
                if (w <= 0 || h <= 0 || w > 32768 || h > 32768 || count < 0 || count > 32) return;
                List<string> names = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    int mw = r.ReadInt32();
                    int mh = r.ReadInt32();
                    bool primary = r.ReadBoolean();
                    names.Add(Lang.L("الشاشة ", "Screen ") + (i + 1) + "  —  " + mw + "×" + mh + (primary ? Lang.L("  (الرئيسية)", "  (Primary)") : ""));
                }
                lock (surface.Gate)
                {
                    if (surface.Back == null || surface.Back.Width != w || surface.Back.Height != h)
                    {
                        if (surface.Back != null) surface.Back.Dispose();
                        surface.Back = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    }
                }
                remoteW = w;
                remoteH = h;
                currentMonitor = current;
                OnUi(delegate { RebuildMonitorMenu(names, current); ApplySurfaceSize(); });
            }
        }

        void RebuildMonitorMenu(List<string> names, int current)
        {
            monitorMenu.Items.Clear();
            for (int i = 0; i < names.Count; i++)
            {
                int index = i;
                ToolStripMenuItem item = new ToolStripMenuItem(names[i]);
                item.Checked = i == current;
                item.Click += delegate { conn.Send(Msg.SelectMonitor, Pack.Int(index)); };
                monitorMenu.Items.Add(item);
            }
            monitorButton.Text = Lang.L("الشاشة ", "Screen ") + (current + 1);
            monitorButton.Enabled = names.Count > 1;
        }

        void HandleFrame(byte[] data)
        {
            int seq;
            using (BinaryReader r = Pack.Read(data))
            {
                seq = r.ReadInt32();
                int count = r.ReadInt32();
                Rectangle union = Rectangle.Empty;
                for (int i = 0; i < count; i++)
                {
                    Rectangle rect = new Rectangle(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                    int len = r.ReadInt32();
                    byte[] jpeg = r.ReadBytes(len);
                    using (MemoryStream ms = new MemoryStream(jpeg))
                    using (Bitmap tile = new Bitmap(ms))
                    {
                        lock (surface.Gate)
                        {
                            if (surface.Back != null)
                                using (Graphics g = Graphics.FromImage(surface.Back))
                                {
                                    g.CompositingMode = CompositingMode.SourceCopy;
                                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                                    g.PixelOffsetMode = PixelOffsetMode.Half;
                                    g.DrawImage(tile, rect);
                                }
                        }
                    }
                    union = union.IsEmpty ? rect : Rectangle.Union(union, rect);
                }
                Interlocked.Increment(ref frames);
                Interlocked.Add(ref bytes, data.Length);
                QueueInvalidate(union);
            }
            conn.Send(Msg.Ack, Pack.Int(seq));
        }

        void QueueInvalidate(Rectangle remote)
        {
            lock (dirtyGate) dirty = dirty.IsEmpty ? remote : Rectangle.Union(dirty, remote);
            if (Interlocked.Exchange(ref invalidatePending, 1) == 1) return;
            OnUi(delegate
            {
                Rectangle r;
                lock (dirtyGate) { r = dirty; dirty = Rectangle.Empty; }
                Interlocked.Exchange(ref invalidatePending, 0);
                if (surface.Overlay == Lang.L("جاري استقبال الشاشة…", "Receiving the screen…")) { surface.Overlay = null; surface.Invalidate(); }
                else surface.Invalidate(surface.ToLocal(r));
            });
        }

        void HandleControlState(byte[] data)
        {
            bool mine;
            string controller;
            int count;
            using (BinaryReader r = Pack.Read(data))
            {
                mine = r.ReadBoolean();
                controller = Identity.Sanitize(r.ReadString(), 64);
                count = r.ReadInt32();
            }
            if (count < 1 || count > 64) return;
            hasControl = mine;
            OnUi(delegate
            {
                bool lost = !mine && controllerName.Length > 0 && viewerCount > 1;
                bool gained = mine && count > 1 && controllerName.Length > 0 && controllerName != controller;
                controllerName = controller;
                viewerCount = count;
                if (!mine) ReleaseHeldKeys();
                controlButton.Visible = count > 1;
                controlButton.Enabled = !mine;
                controlButton.Text = mine ? Lang.L("التحكم معك", "You have control") : Lang.L("أخذ التحكم", "Take control");
                controlButton.Kind = mine ? ButtonKind.Secondary : ButtonKind.Primary;
                controlButton.Invalidate();
                tools.PerformLayout();
                if (lost) ShowNotice(Lang.L("انتقل التحكم إلى ", "Control passed to ") + controller);
                else if (gained) ShowNotice(Lang.L("التحكم معك الآن", "You have control now"));
                ShowStats("");
            });
        }

        // A short note over the picture that clears itself, for things the person should notice.
        void ShowNotice(string text)
        {
            surface.Overlay = text;
            surface.Invalidate();
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 1800 };
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                if (surface.Overlay == text)
                {
                    surface.Overlay = null;
                    surface.Invalidate();
                }
            };
            t.Start();
        }

        void HandleStatus(HostStatus status)
        {
            OnUi(delegate
            {
                switch (status)
                {
                    case HostStatus.Locked:
                        surface.Overlay = Lang.L("الجهاز الآخر مقفل أو تظهر عليه نافذة أمان من ويندوز.\nسيعود العرض تلقائياً عند فتحه.", "The other device is locked or showing a Windows security screen.\nThe view returns automatically when it's unlocked.");
                        break;
                    case HostStatus.Normal:
                        surface.Overlay = null;
                        break;
                    case HostStatus.ControlPaused:
                        remotePaused = true;
                        ReleaseHeldKeys();
                        surface.Overlay = null;
                        break;
                    case HostStatus.ControlResumed:
                        remotePaused = false;
                        break;
                }
                surface.Invalidate();
            });
        }

        void OnConnectionClosed()
        {
            OnUi(delegate
            {
                if (closingByUs) return;
                RemoveHook();
                if (fullscreen) ToggleFullscreen();
                surface.Overlay = Lang.L("انتهت الجلسة أو انقطع الاتصال بالجهاز.", "The session ended or the connection was lost.");
                surface.Invalidate();
                tick.Stop();
                stats.Text = Lang.L("غير متصل", "Disconnected");
            });
        }

        // ---- outgoing input ----

        bool CanControl { get { return !viewOnly && !remotePaused && hasControl && !conn.IsClosed; } }

        void OnSurfaceMove(object sender, MouseEventArgs e)
        {
            if (fullscreen)
            {
                if (e.Y <= 1 && !toolbar.Visible) toolbar.Visible = true;
                else if (e.Y > Theme.S(40) && toolbar.Visible) toolbar.Visible = false;
            }
            if (!CanControl) return;
            Point p = surface.ToRemote(e.Location);
            long now = clock.ElapsedMilliseconds;
            if (p == lastMoveSent || now - lastMoveAt < 8) return;
            lastMoveSent = p;
            lastMoveAt = now;
            conn.Send(Msg.MouseMove, Pack.Build(w => { w.Write(p.X); w.Write(p.Y); }));
        }

        void SendButton(MouseEventArgs e, bool down)
        {
            if (!CanControl) return;
            int button;
            switch (e.Button)
            {
                case MouseButtons.Left: button = 0; break;
                case MouseButtons.Right: button = 1; break;
                case MouseButtons.Middle: button = 2; break;
                case MouseButtons.XButton1: button = 3; break;
                case MouseButtons.XButton2: button = 4; break;
                default: return;
            }
            Point p = surface.ToRemote(e.Location);
            conn.Send(Msg.MouseButton, Pack.Build(w =>
            {
                w.Write((byte)button);
                w.Write(down);
                w.Write(p.X);
                w.Write(p.Y);
            }));
        }

        void SendWheel(int delta, bool horizontal)
        {
            if (!CanControl) return;
            conn.Send(Msg.MouseWheel, Pack.Build(w => { w.Write(delta); w.Write(horizontal); }));
        }

        void SendKey(int vk, int scan, bool extended, bool down, int ch)
        {
            if (conn.IsClosed) return;
            conn.Send(Msg.Key, Pack.Build(w =>
            {
                w.Write(vk);
                w.Write(scan);
                w.Write(extended);
                w.Write(down);
                // The character the viewer's own keyboard layout produces, so the host can type it
                // verbatim at the lock screen regardless of the layout active there. 0 = not printable.
                w.Write((ushort)ch);
            }));
        }

        // Works out the printable character for a key-down, using this machine's active layout and
        // the live state of the modifier keys. Control characters (Enter, Tab, Backspace, Esc) and
        // dead keys return 0, so they travel as virtual keys and keep working as navigation.
        static int CharFor(int vk, int scan)
        {
            try
            {
                byte[] state = new byte[256];
                if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) state[0x10] = 0x80;  // Shift
                if ((Native.GetAsyncKeyState(0xA0) & 0x8000) != 0) state[0xA0] = 0x80;
                if ((Native.GetAsyncKeyState(0xA1) & 0x8000) != 0) state[0xA1] = 0x80;
                if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) state[0x11] = 0x80;  // Ctrl
                if ((Native.GetAsyncKeyState(0xA2) & 0x8000) != 0) state[0xA2] = 0x80;
                if ((Native.GetAsyncKeyState(0xA3) & 0x8000) != 0) state[0xA3] = 0x80;
                if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) state[0x12] = 0x80;  // Alt / AltGr
                if ((Native.GetAsyncKeyState(0xA4) & 0x8000) != 0) state[0xA4] = 0x80;
                if ((Native.GetAsyncKeyState(0xA5) & 0x8000) != 0) state[0xA5] = 0x80;
                if ((Native.GetKeyState(0x14) & 1) != 0) state[0x14] = 1;               // CapsLock toggle
                System.Text.StringBuilder sb = new System.Text.StringBuilder(8);
                // Flag 4 = do not disturb the real keyboard state (so dead keys keep working locally).
                int rc = Native.ToUnicodeEx((uint)vk, (uint)scan, state, sb, sb.Capacity, 4, Native.GetKeyboardLayout(0));
                if (rc == 1 && sb.Length > 0)
                {
                    char c = sb[0];
                    if (c >= 0x20 && c != 0x7F) return c;
                }
            }
            catch (Exception) { }
            return 0;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEHWHEEL && surface.ClientRectangle.Contains(surface.PointToClient(Cursor.Position)))
                SendWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF), true);
            base.WndProc(ref m);
        }

        // The low-level hook is what lets Win, Alt+Tab, and similar keys reach the remote
        // machine instead of acting locally. It only runs while this window is active.
        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && openMenus == 0)
            {
                Native.KBDLLHOOKSTRUCT k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                if ((k.flags & Native.LLKHF_INJECTED) == 0)
                {
                    bool down = wParam.ToInt64() == Native.WM_KEYDOWN || wParam.ToInt64() == Native.WM_SYSKEYDOWN;
                    int vk = (int)k.vkCode;
                    int scan = (int)k.scanCode;
                    bool ext = (k.flags & Native.LLKHF_EXTENDED) != 0;

                    // Ctrl+Alt+Home stays local: it is the way back out of fullscreen.
                    if (vk == 0x24 && IsHeld(0xA2, 0xA3) && IsHeld(0xA4, 0xA5))
                    {
                        if (down && fullscreen) BeginInvoke((Action)ToggleFullscreen);
                        return new IntPtr(1);
                    }

                    if (CanControl)
                    {
                        long id = ((long)vk << 32) | ((long)scan << 1) | (ext ? 1L : 0L);
                        if (down) heldKeys.Add(id); else heldKeys.Remove(id);
                        SendKey(vk, scan, ext, down, down ? CharFor(vk, scan) : 0);
                        return new IntPtr(1);
                    }
                }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        bool IsHeld(int left, int right)
        {
            foreach (long id in heldKeys)
            {
                int vk = (int)(id >> 32);
                if (vk == left || vk == right) return true;
            }
            return false;
        }

        void InstallHook()
        {
            if (hook != IntPtr.Zero) return;
            hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, hookProc, Native.GetModuleHandle(null), 0);
        }

        void RemoveHook()
        {
            if (hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            }
            ReleaseHeldKeys();
        }

        void ReleaseHeldKeys()
        {
            foreach (long id in heldKeys)
                SendKey((int)(id >> 32), (int)((id & 0xFFFFFFFFL) >> 1), (id & 1) != 0, false, 0);
            heldKeys.Clear();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            if (!conn.IsClosed) InstallHook();
            surface.Focus();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            RemoveHook();
        }

        // ---- toolbar actions ----

        void ToggleFit()
        {
            surface.FitToWindow = !surface.FitToWindow;
            fitButton.Text = surface.FitToWindow ? Lang.L("حجم فعلي", "Actual size") : Lang.L("ملاءمة النافذة", "Fit window");
            ApplySurfaceSize();
        }

        void ApplySurfaceSize()
        {
            if (surface.FitToWindow)
            {
                surfaceHost.AutoScroll = false;
                surface.Dock = DockStyle.Fill;
            }
            else
            {
                surface.Dock = DockStyle.None;
                surface.Location = new Point(0, 0);
                surface.Size = new Size(remoteW, remoteH);
                surfaceHost.AutoScroll = true;
            }
            surface.Invalidate();
        }

        void ToggleFullscreen()
        {
            fullscreen = !fullscreen;
            SuspendLayout();
            if (fullscreen)
            {
                restoreState = WindowState;
                restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = Screen.FromControl(this).Bounds;
                toolbar.Visible = false;
                fullButton.Text = Lang.L("نافذة", "Windowed");
                fullButton.Glyph = Theme.GlyphWindowed;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = restoreBounds;
                WindowState = restoreState;
                toolbar.Visible = true;
                fullButton.Text = Lang.L("ملء الشاشة", "Full screen");
                fullButton.Glyph = Theme.GlyphFullscreen;
            }
            ResumeLayout();
        }

        void ToggleViewOnly()
        {
            viewOnly = !viewOnly;
            if (viewOnly) ReleaseHeldKeys();
            viewOnlyButton.Kind = viewOnly ? ButtonKind.Primary : ButtonKind.Secondary;
            viewOnlyButton.Invalidate();
        }

        void CloseByUser()
        {
            Close();
        }

        void OnTick()
        {
            if (conn.IsClosed) return;
            conn.Send(Msg.Ping, BitConverter.GetBytes(clock.ElapsedMilliseconds));
            int f = Interlocked.Exchange(ref frames, 0);
            long b = Interlocked.Exchange(ref bytes, 0);
            long l = Interlocked.Read(ref latency);
            string line = f + " fps   |   " + (b * 8 / 1000000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Mbps";
            if (l >= 0) line += "   |   " + l + " ms";
            ShowStats(line);
        }

        // With others watching, who controls matters more than the frame rate.
        void ShowStats(string numbers)
        {
            string text = numbers;
            if (remotePaused) text = Lang.L("أوقف الطرف الآخر التحكم مؤقتاً", "The other side paused control");
            else if (viewerCount > 1) text = (hasControl ? Lang.L("التحكم معك", "You have control") : Lang.L("يتحكم: ", "Controlling: ") + controllerName) + Lang.L("  •  المتصلون: ", "  •  Connected: ") + viewerCount;
            stats.RightToLeft = text == numbers ? RightToLeft.No : Lang.Rtl;
            stats.TextAlign = text == numbers ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight;
            stats.Text = text;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            clipboard = new ClipboardSync(text => conn.Send(Msg.Clipboard, Pack.Build(w => w.Write(text))));
            conn.Start();
            OnTick();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closingByUs = true;
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            RemoveHook();
            tick.Stop();
            tick.Dispose();
            if (clipboard != null) clipboard.Dispose();
            files.Shutdown();
            transfers.CloseForGood();
            conn.Received -= OnMessage;
            conn.SendAndClose(Msg.Bye, null);
            lock (surface.Gate)
            {
                if (surface.Back != null)
                {
                    surface.Back.Dispose();
                    surface.Back = null;
                }
            }
            base.OnFormClosed(e);
        }
    }
}
