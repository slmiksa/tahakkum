using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;

namespace Tahakkum
{
    // The approval prompt. Deny has the initial focus and Approve arms after a short
    // delay, so a person who happens to be typing cannot approve a request by accident.
    sealed class ConsentForm : Form
    {
        const int ArmDelayMs = 1500;

        readonly ConsentRequest request;
        readonly PillButton approve;
        readonly PillButton deny;
        readonly Label countdown;
        readonly Timer timer;
        readonly DateTime started = DateTime.UtcNow;
        bool answered;

        public ConsentForm(ConsentRequest request)
        {
            this.request = request;
            Text = Lang.L("طلب تحكم بهذه الشاشة", "Request to control this screen");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(500), Theme.S(420));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(28);

            GlyphView shield = new GlyphView { Glyph = Theme.GlyphShield, GlyphSize = 20f, Tile = Theme.Warning };
            shield.SetBounds(w - m - Theme.S(48), Theme.S(24), Theme.S(48), Theme.S(48));
            Controls.Add(shield);

            Label title = Theme.MakeLabel(Lang.L("شخص يطلب التحكم بهذه الشاشة", "Someone is requesting to control this screen"), Theme.UiBold(14f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(22), w - 2 * m - Theme.S(60), Theme.S(30));
            Controls.Add(title);

            string subText = request.Watching > 0
                ? Lang.L("يشاهد شاشتك الآن ", "Currently watching your screen: ") + request.Watching + (request.Watching == 1 ? Lang.L(" شخص", " person") : Lang.L(" أشخاص", " people")) + Lang.L("، وهذا الطلب سينضم إليهم.", ", and this request will join them.")
                : Lang.L("لن يرى شاشتك أو يتحكم بها إلا إذا وافقت.", "They won't see or control your screen unless you approve.");
            Label sub = Theme.MakeLabel(subText, Theme.Ui(10f), request.Watching > 0 ? Theme.Warning : Theme.Muted, ContentAlignment.MiddleRight);
            sub.SetBounds(m, Theme.S(52), w - 2 * m - Theme.S(60), Theme.S(24));
            Controls.Add(sub);

            CardPanel info = new CardPanel();
            info.SetBounds(m, Theme.S(92), w - 2 * m, Theme.S(158));
            Controls.Add(info);
            AddRow(info, 0, Lang.L("عنوان IP", "IP address"), request.RemoteIp, true);
            AddRow(info, 1, Lang.L("اسم الجهاز", "Device name"), request.Machine.Length > 0 ? request.Machine : Lang.L("غير معروف", "Unknown"), false);
            string who = request.User.Length > 0 ? request.User : Lang.L("غير معروف", "Unknown");
            if (request.Nick.Length > 0) who = request.Nick + "  (" + who + ")";
            AddRow(info, 2, Lang.L("الاسم", "Name"), who, request.Nick.Length > 0);
            AddRow(info, 3, Lang.L("رمز التحقق", "Verification code"), request.Code, true);

            Label note = Theme.MakeLabel(Lang.L("عند الموافقة سيرى شاشتك ويتحكم بها، ويمكنه طلب نقل الملفات. يمكنك إنهاء الجلسة في أي وقت. تأكد أن رمز التحقق يطابق ما عنده.", "When you approve, they'll see and control your screen and can request file transfers. You can end the session anytime. Make sure the verification code matches theirs."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(260), w - 2 * m, Theme.S(50));
            Controls.Add(note);

            countdown = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Warning, ContentAlignment.MiddleRight);
            countdown.SetBounds(m, Theme.S(310), w - 2 * m, Theme.S(24));
            Controls.Add(countdown);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            approve = new PillButton { Text = Lang.L("موافق", "Approve"), Kind = ButtonKind.Success, Glyph = Theme.GlyphCheck };
            approve.SetBounds(w - m - bw, Theme.S(346), bw, Theme.S(50));
            approve.Enabled = false;
            approve.Click += delegate { Answer(true); };
            Controls.Add(approve);

            deny = new PillButton { Text = Lang.L("رفض", "Decline"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphClose };
            deny.SetBounds(m, Theme.S(346), bw, Theme.S(50));
            deny.Click += delegate { Answer(false); };
            Controls.Add(deny);
            CancelButton = deny;
            ActiveControl = deny;

            request.Cancelled += OnRequestCancelled;

            timer = new Timer { Interval = 200 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        void AddRow(Control parent, int index, string caption, string value, bool strong)
        {
            int rowH = Theme.S(35);
            int top = Theme.S(9) + index * rowH;
            int pad = Theme.S(16);
            Label c = Theme.MakeLabel(caption, Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight);
            c.SetBounds(parent.Width - pad - Theme.S(120), top, Theme.S(120), rowH);
            parent.Controls.Add(c);
            Label v = Theme.MakeLabel(value, strong ? Theme.UiBold(12f) : Theme.Ui(10.5f), Theme.Text, ContentAlignment.MiddleLeft);
            v.RightToLeft = RightToLeft.No;
            v.SetBounds(pad, top, parent.Width - 2 * pad - Theme.S(130), rowH);
            parent.Controls.Add(v);
        }

        void Tick()
        {
            double elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            if (elapsed >= ArmDelayMs && !approve.Enabled) approve.Enabled = true;
            int left = Math.Max(0, Proto.ConsentSeconds - (int)(elapsed / 1000));
            countdown.Text = Lang.L("سيتم الرفض تلقائياً بعد ", "It will be declined automatically in ") + left + Lang.L(" ثانية", " seconds");
            if (left <= 0) Answer(false);
        }

        void OnRequestCancelled()
        {
            try
            {
                if (IsHandleCreated) BeginInvoke((Action)delegate { answered = true; Close(); });
            }
            catch (Exception) { }
        }

        void Answer(bool yes)
        {
            if (answered) return;
            answered = true;
            if (yes) request.Approve(); else request.Reject();
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            SystemSounds.Exclamation.Play();
            Native.FlashWindow(Handle, true);
            deny.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            request.Cancelled -= OnRequestCancelled;
            if (!answered) request.Reject();
            base.OnFormClosed(e);
        }
    }

    // Always-visible strip on the controlled machine while anyone is connected. It never takes
    // keyboard focus, and it is what lets the local person see who is watching, choose who
    // controls, and pause or end it all.
    sealed class SessionBar : Form
    {
        readonly ControlDesk desk;
        readonly Func<HostSession, FileTransferWindow> transfersFor;
        readonly Label caption;
        readonly Label timeLabel;
        readonly PillButton viewers;
        readonly PillButton pause;
        readonly ContextMenuStrip viewerMenu;
        readonly StatusDot dot;
        readonly Timer timer;
        readonly DateTime started = DateTime.UtcNow;
        bool paused;
        bool blink;

        public SessionBar(ControlDesk desk, Func<HostSession, FileTransferWindow> transfersFor)
        {
            this.desk = desk;
            this.transfersFor = transfersFor;
            Text = Lang.L("جلسة تحكم نشطة", "Active control session");
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            ClientSize = new Size(Theme.S(820), Theme.S(52));

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + Theme.S(8));

            int w = ClientSize.Width;
            dot = new StatusDot { Color = Theme.Danger, BackColor = Theme.Card };
            dot.SetBounds(w - Theme.S(30), Theme.S(19), Theme.S(14), Theme.S(14));
            Controls.Add(dot);

            caption = Theme.MakeLabel("", Theme.UiBold(10f), Theme.Text, ContentAlignment.MiddleRight);
            caption.BackColor = Theme.Card;
            caption.AutoEllipsis = true;
            caption.SetBounds(Theme.S(450), 0, w - Theme.S(486), Theme.S(52));
            Controls.Add(caption);

            timeLabel = Theme.MakeLabel("00:00", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleCenter);
            timeLabel.BackColor = Theme.Card;
            timeLabel.RightToLeft = RightToLeft.No;
            timeLabel.SetBounds(Theme.S(386), 0, Theme.S(60), Theme.S(52));
            Controls.Add(timeLabel);

            viewerMenu = Theme.MakeMenu();
            viewerMenu.ShowCheckMargin = false;
            viewers = new PillButton { Text = Lang.L("المتصلون", "Connected"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphView, GlyphSize = 9f };
            viewers.Font = Theme.UiBold(9f);
            viewers.SetBounds(Theme.S(230), Theme.S(9), Theme.S(154), Theme.S(34));
            viewers.TabStop = false;
            viewers.Click += delegate { ShowViewers(); };
            Controls.Add(viewers);

            pause = new PillButton { Text = Lang.L("إيقاف التحكم", "Pause control"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphPause, GlyphSize = 9f };
            pause.Font = Theme.UiBold(9f);
            pause.SetBounds(Theme.S(100), Theme.S(9), Theme.S(124), Theme.S(34));
            pause.TabStop = false;
            pause.Click += delegate { TogglePause(); };
            Controls.Add(pause);

            PillButton end = new PillButton { Text = Lang.L("إنهاء", "End"), Kind = ButtonKind.Danger, Glyph = Theme.GlyphClose, GlyphSize = 9f };
            end.Font = Theme.UiBold(9f);
            end.SetBounds(Theme.S(10), Theme.S(9), Theme.S(84), Theme.S(34));
            end.TabStop = false;
            end.Click += delegate { foreach (HostSession s in desk.Sessions) s.End(); };
            Controls.Add(end);

            timer = new Timer { Interval = 800 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            RefreshViewers();
        }

        public bool Paused { get { return paused; } }

        static string Who(HostSession s)
        {
            return s.DisplayName + " (" + s.RemoteIp + ")";
        }

        public void RefreshViewers()
        {
            HostSession[] all = desk.Sessions;
            HostSession controller = desk.Controller;
            if (all.Length == 1) caption.Text = Lang.L("يتحكم بهذه الشاشة: ", "Controlling this screen: ") + Who(all[0]);
            else if (all.Length > 1) caption.Text = Lang.L("يشاهد ", "Watching ") + all.Length + Lang.L(" • يتحكم: ", " • controlling: ") + (controller != null ? controller.DisplayName : Lang.L("لا أحد", "no one"));
            viewers.Text = Lang.L("المتصلون (", "Connected (") + all.Length + ")";
            viewers.Invalidate();
        }

        // Each person: hand them control, send them files, or disconnect them alone.
        void ShowViewers()
        {
            viewerMenu.Items.Clear();
            HostSession controller = desk.Controller;
            foreach (HostSession item in desk.Sessions)
            {
                HostSession s = item;
                ToolStripMenuItem entry = new ToolStripMenuItem(Who(s) + (s == controller ? Lang.L("  —  يتحكم الآن", "  —  controlling now") : ""));
                ToolStripMenuItem give = new ToolStripMenuItem(Lang.L("إعطاء التحكم", "Give control"));
                give.Enabled = s != controller;
                give.Click += delegate { desk.Give(s); };
                ToolStripMenuItem files = new ToolStripMenuItem(Lang.L("إرسال ملفات", "Send files"));
                files.Click += delegate
                {
                    FileTransferWindow w = transfersFor(s);
                    if (w != null) w.PickAndSend(this);
                };
                ToolStripMenuItem drop = new ToolStripMenuItem(Lang.L("فصل هذا الشخص", "Disconnect this person"));
                drop.Click += delegate { s.End(); };
                entry.DropDownItems.AddRange(new ToolStripItem[] { give, files, drop });
                StyleDropDown(entry);
                viewerMenu.Items.Add(entry);
            }
            viewerMenu.Show(viewers, new Point(viewers.Width - viewerMenu.PreferredSize.Width, viewers.Height + Theme.S(4)));
        }

        static void StyleDropDown(ToolStripMenuItem item)
        {
            ToolStripDropDownMenu menu = (ToolStripDropDownMenu)item.DropDown;
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.RightToLeft = Lang.Rtl;
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Text;
        }

        void TogglePause()
        {
            paused = !paused;
            foreach (HostSession s in desk.Sessions) s.SetPaused(paused);
            pause.Text = paused ? Lang.L("استئناف التحكم", "Resume control") : Lang.L("إيقاف التحكم", "Pause control");
            pause.Glyph = paused ? Theme.GlyphPlay : Theme.GlyphPause;
            pause.Invalidate();
        }

        void Tick()
        {
            TimeSpan t = DateTime.UtcNow - started;
            timeLabel.Text = ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
            blink = !blink;
            dot.Color = paused ? Theme.Warning : blink ? Theme.Danger : Color.FromArgb(150, 40, 40);
            // Keep the strip above other topmost windows, except while its own list is open over it.
            if (viewerMenu.Visible) return;
            TopMost = false;
            TopMost = true;
        }

        // Keep the bar visible to the person at this machine, but out of the captured image the viewer
        // sees — so it no longer covers the top of the remote view, while the consent indicator stays
        // on the real screen. Falls back to WDA_MONITOR on Windows older than 10 v2004.
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { if (!Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE)) Native.SetWindowDisplayAffinity(Handle, Native.WDA_MONITOR); }
            catch (Exception) { }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Danger, 2f))
                e.Graphics.DrawRectangle(p, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            viewerMenu.Dispose();
            base.OnFormClosed(e);
        }
    }

    // Two-way text clipboard sharing for the life of a session. Must be created on the UI thread.
    sealed class ClipboardSync : IDisposable
    {
        readonly Timer timer;
        readonly Action<string> send;
        uint lastSequence;
        string lastText;
        bool primed;

        public ClipboardSync(Action<string> send)
        {
            this.send = send;
            lastSequence = Native.GetClipboardSequenceNumber();
            timer = new Timer { Interval = 400 };
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        void Poll()
        {
            uint seq = Native.GetClipboardSequenceNumber();
            // The first tick always runs, so text copied before the session started is shared too;
            // after that, only a real change is sent.
            if (seq == lastSequence && primed) return;
            primed = true;
            lastSequence = seq;
            try
            {
                if (!Clipboard.ContainsText()) return;
                string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                if (text == lastText || text.Length == 0 || text.Length > Proto.MaxClipboardChars) return;
                lastText = text;
                send(text);
            }
            catch (Exception) { }
        }

        public void Apply(string text)
        {
            if (string.IsNullOrEmpty(text) || text == lastText) return;
            lastText = text;
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                lastSequence = Native.GetClipboardSequenceNumber();
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            timer.Stop();
            timer.Dispose();
        }
    }
}
