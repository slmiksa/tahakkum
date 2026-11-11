using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows.Forms;

namespace Tahakkum
{
    // Asks the person here whether to take the offered files. Like the session prompt, reject
    // has the initial focus and accept arms after a delay, so typing cannot accept by accident.
    sealed class FileOfferForm : Form
    {
        const int ArmDelayMs = 1500;
        const int ShownNames = 5;

        readonly FileTransferService service;
        readonly FileOffer offer;
        readonly PillButton accept;
        readonly PillButton reject;
        readonly Label countdown;
        readonly Timer timer;
        readonly DateTime started = DateTime.UtcNow;
        bool answered;

        public FileOfferForm(FileTransferService service, FileOffer offer, string peer)
        {
            this.service = service;
            this.offer = offer;
            Text = Lang.L("طلب إرسال ملفات", "File transfer request");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(500), Theme.S(430));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(28);

            GlyphView glyph = new GlyphView { Glyph = Theme.GlyphFile, GlyphSize = 20f, Tile = Theme.Warning };
            glyph.SetBounds(w - m - Theme.S(48), Theme.S(24), Theme.S(48), Theme.S(48));
            Controls.Add(glyph);

            Label title = Theme.MakeLabel(Lang.L("يريد الطرف الآخر إرسال ملفات إليك", "The other side wants to send you files"), Theme.UiBold(14f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(22), w - 2 * m - Theme.S(60), Theme.S(30));
            Controls.Add(title);

            Label sub = Theme.MakeLabel(Lang.L("من: ", "From: ") + peer, Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight);
            sub.AutoEllipsis = true;
            sub.SetBounds(m, Theme.S(52), w - 2 * m - Theme.S(60), Theme.S(24));
            Controls.Add(sub);

            CardPanel info = new CardPanel();
            info.SetBounds(m, Theme.S(90), w - 2 * m, Theme.S(164));
            Controls.Add(info);

            // The marks stop the size from joining the extension in right-to-left text. The extension must read correctly.
            List<string> lines = offer.Names.Take(ShownNames).Select((n, i) => n + "‏   (‎" + FileTransferService.FormatSize(offer.Sizes[i]) + "‎)‏").ToList();
            if (offer.Names.Length > ShownNames) lines.Add(Lang.L("و ", "and ") + (offer.Names.Length - ShownNames) + Lang.L(" ملفات أخرى", " more files"));
            Label names = Theme.MakeLabel(string.Join("\n", lines.ToArray()), Theme.Ui(10f), Theme.Text, ContentAlignment.TopRight);
            names.AutoEllipsis = true;
            names.SetBounds(Theme.S(14), Theme.S(10), info.Width - Theme.S(28), Theme.S(116));
            info.Controls.Add(names);

            Label total = Theme.MakeLabel(offer.Names.Length + Lang.L(" ملف، الحجم الكلي ", " files, total size ") + FileTransferService.FormatSize(offer.Total), Theme.UiBold(10f), Theme.Muted, ContentAlignment.MiddleRight);
            total.SetBounds(Theme.S(14), Theme.S(128), info.Width - Theme.S(28), Theme.S(26));
            info.Controls.Add(total);

            Label note = Theme.MakeLabel(Lang.L("ستُحفظ في ", "They'll be saved in ") + service.SaveFolder + Lang.L(" ولن يُفتح أي منها تلقائياً. لا تفتح ملفاً لا تثق بمصدره.", " and none will open automatically. Don't open a file you don't trust."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(264), w - 2 * m, Theme.S(52));
            Controls.Add(note);

            countdown = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Warning, ContentAlignment.MiddleRight);
            countdown.SetBounds(m, Theme.S(318), w - 2 * m, Theme.S(24));
            Controls.Add(countdown);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            accept = new PillButton { Text = Lang.L("قبول", "Accept"), Kind = ButtonKind.Success, Glyph = Theme.GlyphCheck };
            accept.SetBounds(w - m - bw, Theme.S(356), bw, Theme.S(50));
            accept.Enabled = false;
            accept.Click += delegate { Answer(true); };
            Controls.Add(accept);

            reject = new PillButton { Text = Lang.L("رفض", "Decline"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphClose };
            reject.SetBounds(m, Theme.S(356), bw, Theme.S(50));
            reject.Click += delegate { Answer(false); };
            Controls.Add(reject);
            CancelButton = reject;
            ActiveControl = reject;

            offer.Cancelled += OnOfferCancelled;

            timer = new Timer { Interval = 200 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
        }

        void Tick()
        {
            double elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            if (elapsed >= ArmDelayMs && !accept.Enabled) accept.Enabled = true;
            int left = Math.Max(0, Proto.FileOfferSeconds - (int)(elapsed / 1000));
            countdown.Text = Lang.L("سيتم الرفض تلقائياً بعد ", "It will be declined automatically in ") + left + Lang.L(" ثانية", " seconds");
            if (left <= 0) Answer(false);
        }

        void OnOfferCancelled()
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
            if (yes) service.Accept(offer); else service.Reject(offer);
            Close();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            SystemSounds.Exclamation.Play();
            Native.FlashWindow(Handle, true);
            reject.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            offer.Cancelled -= OnOfferCancelled;
            if (!answered) service.Reject(offer);
            base.OnFormClosed(e);
        }
    }

    sealed class ProgressStrip : Control
    {
        double value;

        public Color Fill = Theme.Accent;

        public ProgressStrip()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            RightToLeft = RightToLeft.No;
        }

        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Border);
            // Fills from the right, matching the reading direction.
            int w = (int)Math.Round(Width * value);
            using (SolidBrush b = new SolidBrush(Fill))
                e.Graphics.FillRectangle(b, Width - w, 0, w, Height);
        }
    }

    // Shows the current send and receive of a session. It never takes focus by itself, so it
    // does not interrupt the person working, and it owns the session's file prompts.
    sealed class FileTransferWindow : Form
    {
        sealed class Row
        {
            public FileTransfer Transfer;
            public CardPanel Card;
            public Label Status;
            public ProgressStrip Bar;
            public PillButton Action;
            public long LastDone;
            public DateTime LastAt = DateTime.UtcNow;
            public double Speed;
            public TransferState Shown = TransferState.Waiting;
        }

        readonly FileTransferService service;
        readonly string peer;
        readonly Timer timer;
        Row sendRow;
        Row receiveRow;
        bool finished;

        public FileTransferWindow(FileTransferService service, string peer)
        {
            this.service = service;
            this.peer = peer;
            Text = Lang.L("نقل الملفات", "File transfer");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.No;
            Icon = Program.AppIcon;
            ClientSize = new Size(Theme.S(480), Theme.S(100));

            // The network threads post to this window before it is ever shown.
            CreateHandle();
            service.OfferReceived += o => OnUi(() => ShowOffer(o));
            service.TransferStarted += t => OnUi(() => AddTransfer(t));

            timer = new Timer { Interval = 250 };
            timer.Tick += delegate { RefreshRows(); };
            timer.Start();
        }

        void OnUi(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch (Exception) { }
        }

        void ShowOffer(FileOffer offer)
        {
            FileOfferForm form = new FileOfferForm(service, offer, peer);
            form.Show();
            form.Activate();
        }

        // ---- choosing files ----

        public void PickAndSend(IWin32Window owner)
        {
            if (service.Sending)
            {
                ShowWindow();
                return;
            }
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = Lang.L("اختر الملفات المراد إرسالها إلى ", "Choose the files to send to ") + peer;
                dialog.Multiselect = true;
                dialog.CheckFileExists = true;
                dialog.DereferenceLinks = true;
                if (dialog.ShowDialog(owner) == DialogResult.OK) SendPaths(dialog.FileNames);
            }
        }

        public void SendPaths(string[] paths)
        {
            string[] files = paths.Where(File.Exists).ToArray();
            string error = null;
            if (files.Length < paths.Length) error = Lang.L("إرسال المجلدات غير مدعوم. اضغط المجلد في ملف ZIP ثم أرسله.", "Sending folders isn't supported. Zip the folder, then send it.");
            string sendError = service.Send(files);
            if (sendError != null) error = sendError;
            if (error != null)
                MessageBox.Show(this, error, Lang.L("نقل الملفات", "File transfer"), MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
        }

        // ---- rows ----

        void AddTransfer(FileTransfer t)
        {
            Row old = t.Incoming ? receiveRow : sendRow;
            if (old != null)
            {
                Controls.Remove(old.Card);
                old.Card.Dispose();
            }
            Row row = BuildRow(t);
            if (t.Incoming) receiveRow = row; else sendRow = row;
            Controls.Add(row.Card);
            LayoutRows();
            ShowWindow();
        }

        Row BuildRow(FileTransfer t)
        {
            int w = ClientSize.Width - 2 * Theme.S(12);
            Row row = new Row { Transfer = t };
            row.Card = new CardPanel();
            row.Card.Size = new Size(w, Theme.S(112));

            int p = Theme.S(14);
            Label title = Theme.MakeLabel((t.Incoming ? Lang.L("استلام: ", "Receiving: ") : Lang.L("إرسال: ", "Sending: ")) + t.Title, Theme.UiBold(10f), Theme.Text, ContentAlignment.MiddleRight);
            title.AutoEllipsis = true;
            title.SetBounds(p, Theme.S(8), w - 2 * p, Theme.S(26));
            row.Card.Controls.Add(title);

            row.Bar = new ProgressStrip();
            row.Bar.SetBounds(p, Theme.S(38), w - 2 * p, Theme.S(6));
            row.Card.Controls.Add(row.Bar);

            row.Status = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleRight);
            row.Status.AutoEllipsis = true;
            row.Status.SetBounds(Theme.S(124), Theme.S(54), w - Theme.S(124) - p, Theme.S(46));
            row.Card.Controls.Add(row.Status);

            row.Action = new PillButton { Text = Lang.L("إلغاء", "Cancel"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphClose, GlyphSize = 9f };
            row.Action.Font = Theme.UiBold(9f);
            row.Action.TabStop = false;
            row.Action.SetBounds(p, Theme.S(60), Theme.S(104), Theme.S(34));
            row.Action.Click += delegate { OnRowAction(row); };
            row.Card.Controls.Add(row.Action);

            UpdateRow(row);
            return row;
        }

        void OnRowAction(Row row)
        {
            FileTransfer t = row.Transfer;
            if (t.Active)
            {
                t.Cancel();
            }
            else if (t.LastPath != null)
            {
                try { Process.Start("explorer.exe", "/select,\"" + t.LastPath + "\""); }
                catch (Exception) { }
            }
            UpdateRow(row);
        }

        void LayoutRows()
        {
            int y = Theme.S(12);
            foreach (Row row in new[] { receiveRow, sendRow })
            {
                if (row == null) continue;
                row.Card.Location = new Point(Theme.S(12), y);
                y += row.Card.Height + Theme.S(8);
            }
            ClientSize = new Size(ClientSize.Width, y + Theme.S(4));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - Theme.S(12), area.Bottom - Height - Theme.S(12));
        }

        void ShowWindow()
        {
            if (!Visible) Show();
            Theme.DarkTitleBar(Handle);
        }

        void RefreshRows()
        {
            if (!Visible) return;
            if (sendRow != null) UpdateRow(sendRow);
            if (receiveRow != null) UpdateRow(receiveRow);
        }

        void UpdateRow(Row row)
        {
            FileTransfer t = row.Transfer;
            long done = t.Done;
            TransferState state = t.State;
            row.Bar.Value = t.Total > 0 ? (double)done / t.Total : (state == TransferState.Completed ? 1 : 0);

            if (state == TransferState.Running)
            {
                DateTime now = DateTime.UtcNow;
                double seconds = (now - row.LastAt).TotalSeconds;
                if (seconds >= 1)
                {
                    row.Speed = (done - row.LastDone) / seconds;
                    row.LastDone = done;
                    row.LastAt = now;
                }
                row.Status.Text = FileTransferService.FormatSize(done) + " / " + FileTransferService.FormatSize(t.Total)
                    + (row.Speed > 0 ? "   —   " + FileTransferService.FormatSize((long)row.Speed) + "/s" : "");
            }
            else
            {
                row.Status.Text = t.Message ?? "";
            }

            if (state == row.Shown) return;
            row.Shown = state;
            row.Status.ForeColor = state == TransferState.Failed ? Theme.Danger : state == TransferState.Completed ? Theme.Success : Theme.Muted;
            row.Bar.Fill = state == TransferState.Failed ? Theme.Danger : state == TransferState.Completed ? Theme.Success : Theme.Accent;
            row.Bar.Invalidate();
            if (t.Active) return;
            bool canOpen = t.LastPath != null;
            row.Action.Visible = canOpen;
            if (canOpen)
            {
                row.Action.Text = Lang.L("فتح المجلد", "Open folder");
                row.Action.Glyph = Theme.GlyphFolder;
                row.Action.Invalidate();
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void CloseForGood()
        {
            finished = true;
            Close();
        }

        // Closing only hides the window: a running transfer keeps going, and sending again brings it back.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            timer.Dispose();
            base.OnFormClosed(e);
        }
    }
}
