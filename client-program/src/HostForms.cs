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
            Text = "طلب تحكم بهذه الشاشة";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(500), Theme.S(420));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(28);

            GlyphView shield = new GlyphView { Glyph = Theme.GlyphShield, GlyphSize = 20f, Tile = Theme.Warning };
            shield.SetBounds(w - m - Theme.S(48), Theme.S(24), Theme.S(48), Theme.S(48));
            Controls.Add(shield);

            Label title = Theme.MakeLabel("شخص يطلب التحكم بهذه الشاشة", Theme.UiBold(14f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(22), w - 2 * m - Theme.S(60), Theme.S(30));
            Controls.Add(title);

            Label sub = Theme.MakeLabel("لن يرى شاشتك أو يتحكم بها إلا إذا وافقت.", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight);
            sub.SetBounds(m, Theme.S(52), w - 2 * m - Theme.S(60), Theme.S(24));
            Controls.Add(sub);

            CardPanel info = new CardPanel();
            info.SetBounds(m, Theme.S(92), w - 2 * m, Theme.S(158));
            Controls.Add(info);
            AddRow(info, 0, "عنوان IP", request.RemoteIp, true);
            AddRow(info, 1, "اسم الجهاز", request.Machine.Length > 0 ? request.Machine : "غير معروف", false);
            AddRow(info, 2, "اسم المستخدم", request.User.Length > 0 ? request.User : "غير معروف", false);
            AddRow(info, 3, "رمز التحقق", request.Code, true);

            Label note = Theme.MakeLabel("عند الموافقة سيرى شاشتك ويتحكم بها، ويمكنه طلب نقل الملفات. يمكنك إنهاء الجلسة في أي وقت. تأكد أن رمز التحقق يطابق ما عنده.", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(260), w - 2 * m, Theme.S(50));
            Controls.Add(note);

            countdown = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Warning, ContentAlignment.MiddleRight);
            countdown.SetBounds(m, Theme.S(310), w - 2 * m, Theme.S(24));
            Controls.Add(countdown);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            approve = new PillButton { Text = "موافق", Kind = ButtonKind.Success, Glyph = Theme.GlyphCheck };
            approve.SetBounds(w - m - bw, Theme.S(346), bw, Theme.S(50));
            approve.Enabled = false;
            approve.Click += delegate { Answer(true); };
            Controls.Add(approve);

            deny = new PillButton { Text = "رفض", Kind = ButtonKind.Secondary, Glyph = Theme.GlyphClose };
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
            countdown.Text = "سيتم الرفض تلقائياً بعد " + left + " ثانية";
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

    // Always-visible strip on the controlled machine while a session runs. It never takes
    // keyboard focus, and it is what lets the local person pause or end the session.
    sealed class SessionBar : Form
    {
        readonly HostSession session;
        readonly Label timeLabel;
        readonly PillButton pause;
        readonly StatusDot dot;
        readonly Timer timer;
        readonly DateTime started = DateTime.UtcNow;
        bool blink;

        public SessionBar(HostSession session, FileTransferWindow transfers)
        {
            this.session = session;
            Text = "جلسة تحكم نشطة";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Card;
            ClientSize = new Size(Theme.S(790), Theme.S(52));

            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.X + (area.Width - Width) / 2, area.Y + Theme.S(8));

            int w = ClientSize.Width;
            dot = new StatusDot { Color = Theme.Danger, BackColor = Theme.Card };
            dot.SetBounds(w - Theme.S(30), Theme.S(19), Theme.S(14), Theme.S(14));
            Controls.Add(dot);

            string who = session.RemoteIp;
            if (session.User.Length > 0) who = session.User + " • " + who;
            Label caption = Theme.MakeLabel("يتحكم بهذه الشاشة: " + who, Theme.UiBold(10f), Theme.Text, ContentAlignment.MiddleRight);
            caption.BackColor = Theme.Card;
            caption.AutoEllipsis = true;
            caption.SetBounds(Theme.S(420), 0, w - Theme.S(456), Theme.S(52));
            Controls.Add(caption);

            timeLabel = Theme.MakeLabel("00:00", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleCenter);
            timeLabel.BackColor = Theme.Card;
            timeLabel.RightToLeft = RightToLeft.No;
            timeLabel.SetBounds(Theme.S(356), 0, Theme.S(60), Theme.S(52));
            Controls.Add(timeLabel);

            PillButton send = new PillButton { Text = "إرسال ملفات", Kind = ButtonKind.Secondary, Glyph = Theme.GlyphFile, GlyphSize = 9f };
            send.Font = Theme.UiBold(9f);
            send.SetBounds(Theme.S(230), Theme.S(9), Theme.S(124), Theme.S(34));
            send.TabStop = false;
            send.Click += delegate { transfers.PickAndSend(this); };
            Controls.Add(send);

            pause = new PillButton { Text = "إيقاف التحكم", Kind = ButtonKind.Secondary, Glyph = Theme.GlyphPause, GlyphSize = 9f };
            pause.Font = Theme.UiBold(9f);
            pause.SetBounds(Theme.S(100), Theme.S(9), Theme.S(124), Theme.S(34));
            pause.TabStop = false;
            pause.Click += delegate { TogglePause(); };
            Controls.Add(pause);

            PillButton end = new PillButton { Text = "إنهاء", Kind = ButtonKind.Danger, Glyph = Theme.GlyphClose, GlyphSize = 9f };
            end.Font = Theme.UiBold(9f);
            end.SetBounds(Theme.S(10), Theme.S(9), Theme.S(84), Theme.S(34));
            end.TabStop = false;
            end.Click += delegate { session.End(); };
            Controls.Add(end);

            timer = new Timer { Interval = 800 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        void TogglePause()
        {
            bool nowPaused = !session.Paused;
            session.SetPaused(nowPaused);
            pause.Text = nowPaused ? "استئناف التحكم" : "إيقاف التحكم";
            pause.Glyph = nowPaused ? Theme.GlyphPlay : Theme.GlyphPause;
            pause.Invalidate();
        }

        void Tick()
        {
            TimeSpan t = DateTime.UtcNow - started;
            timeLabel.Text = ((int)t.TotalMinutes).ToString("00") + ":" + t.Seconds.ToString("00");
            blink = !blink;
            dot.Color = session.Paused ? Theme.Warning : blink ? Theme.Danger : Color.FromArgb(150, 40, 40);
            // Keep the strip above other topmost windows for the whole session.
            TopMost = false;
            TopMost = true;
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

        public ClipboardSync(Action<string> send)
        {
            this.send = send;
            lastSequence = Native.GetClipboardSequenceNumber();
            timer = new Timer { Interval = 700 };
            timer.Tick += delegate { Poll(); };
            timer.Start();
        }

        void Poll()
        {
            uint seq = Native.GetClipboardSequenceNumber();
            if (seq == lastSequence) return;
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
