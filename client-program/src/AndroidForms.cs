using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Tahakkum
{
    // A small reusable "type some text" dialog.
    sealed class TextPromptForm : Form
    {
        readonly AddressBox box = new AddressBox();

        public string Value { get { return box.Box.Text; } }

        public TextPromptForm(string caption)
        {
            Text = Lang.L("إدخال نص", "Enter text");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(440), Theme.S(210));
            Icon = Program.AppIcon;

            int w = ClientSize.Width, m = Theme.S(24);
            Label note = Theme.MakeLabel(caption, Theme.Ui(10f), Theme.Text, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(18), w - 2 * m, Theme.S(40));
            Controls.Add(note);

            box.SetBounds(m, Theme.S(64), w - 2 * m, Theme.S(50));
            box.Box.Font = Theme.UiBold(12f);
            box.Box.MaxLength = 500;
            Controls.Add(box);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            PillButton ok = new PillButton { Text = Lang.L("إرسال", "Send"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            ok.SetBounds(w - m - bw, Theme.S(134), bw, Theme.S(48));
            ok.Click += delegate { DialogResult = DialogResult.OK; };
            Controls.Add(ok);
            PillButton cancel = new PillButton { Text = Lang.L("إلغاء", "Cancel"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(134), bw, Theme.S(48));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = ok;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            box.Box.Focus();
        }
    }

    // Asks for the TV's IP (or picks one from the saved list) and connects over adb. On success,
    // Tool and Host are set.
    sealed class AndroidConnectForm : Form
    {
        readonly AddressBox ipBox = new AddressBox();
        readonly Label status;
        readonly PillButton connect;
        readonly Panel savedPanel = new Panel();
        bool busy;

        public AdbTool Tool;
        public string Host;

        public AndroidConnectForm(string prefill)
        {
            Text = Lang.L("التحكم بشاشة أندرويد", "Control an Android screen");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(480), Theme.S(506));
            Icon = Program.AppIcon;

            int w = ClientSize.Width, m = Theme.S(24), inner = w - 2 * m;

            Label title = Theme.MakeLabel(Lang.L("التحكم بتلفزيون أو شاشة أندرويد", "Control an Android TV or screen"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), inner, Theme.S(28));
            Controls.Add(title);

            Label note = Theme.MakeLabel(Lang.L("على التلفزيون مرة واحدة: فعّل \"خيارات المطور\" ثم \"تصحيح الأخطاء عبر الشبكة\". أول اتصال يظهر تنبيه سماح على التلفزيون، اضغط \"سماح\". ثم اكتب عنوان IP الظاهر في إعدادات الشبكة.", "On the TV once: enable \"Developer options\" then \"Network debugging\". The first connection shows an allow prompt on the TV — press \"Allow\". Then enter the IP address shown in the network settings."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(48), inner, Theme.S(80));
            Controls.Add(note);

            Label cap = Theme.MakeLabel(Lang.L("عنوان IP (مثال: 192.168.1.40)", "IP address (e.g. 192.168.1.40)"), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            cap.SetBounds(m, Theme.S(132), inner, Theme.S(20));
            Controls.Add(cap);
            ipBox.SetBounds(m, Theme.S(154), inner, Theme.S(48));
            ipBox.Box.Font = Theme.UiBold(13f);
            ipBox.Box.RightToLeft = RightToLeft.No;
            ipBox.Box.MaxLength = 21;
            if (AdbTool.ValidHost(prefill)) ipBox.Box.Text = prefill;
            ipBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(); } };
            Controls.Add(ipBox);

            PillButton save = new PillButton { Text = Lang.L("حفظ هذا العنوان في القائمة", "Save this address to the list"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphCheck, GlyphSize = 9f };
            save.Font = Theme.UiBold(9.5f);
            save.SetBounds(m, Theme.S(208), inner, Theme.S(32));
            save.Click += delegate { SaveCurrent(); };
            Controls.Add(save);

            status = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            status.SetBounds(m, Theme.S(246), inner, Theme.S(34));
            Controls.Add(status);

            Label savedCap = Theme.MakeLabel(Lang.L("التلفزيونات المحفوظة", "Saved TVs"), Theme.UiBold(10f), Theme.Text, ContentAlignment.MiddleRight);
            savedCap.SetBounds(m, Theme.S(286), inner, Theme.S(22));
            Controls.Add(savedCap);

            savedPanel.SetBounds(m, Theme.S(312), inner, Theme.S(120));
            savedPanel.BackColor = Theme.Card;
            savedPanel.AutoScroll = true;
            Controls.Add(savedPanel);

            int bw = (inner - Theme.S(12)) / 2;
            connect = new PillButton { Text = Lang.L("اتصال", "Connect"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect, GlyphSize = 11f };
            connect.SetBounds(w - m - bw, Theme.S(442), bw, Theme.S(48));
            connect.Click += delegate { Start(); };
            Controls.Add(connect);
            PillButton cancel = new PillButton { Text = Lang.L("إغلاق", "Close"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(442), bw, Theme.S(48));
            cancel.Click += delegate { if (!busy) Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = connect;

            RefreshSaved();
        }

        void RefreshSaved()
        {
            savedPanel.SuspendLayout();
            savedPanel.Controls.Clear();
            List<TvDevice> list = TvBook.Load();
            if (list.Count == 0)
            {
                Label empty = Theme.MakeLabel(Lang.L("لا توجد عناوين محفوظة بعد. اكتب عنوان التلفزيون ثم اضغط \"حفظ\".", "No saved addresses yet. Enter the TV address, then press \"Save\"."), Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleCenter);
                empty.SetBounds(0, Theme.S(4), savedPanel.Width, Theme.S(50));
                savedPanel.Controls.Add(empty);
                savedPanel.ResumeLayout();
                return;
            }
            bool scrolls = list.Count * Theme.S(40) > savedPanel.Height;
            int rowW = savedPanel.Width - (scrolls ? SystemInformation.VerticalScrollBarWidth + Theme.S(4) : 0);
            int y = 0;
            foreach (TvDevice tv in list)
            {
                TvDevice item = tv;
                PillButton row = new PillButton { Text = item.Name + "‏   •   ‎" + item.Address, Kind = ButtonKind.Secondary };
                row.Font = Theme.Ui(9.5f);
                row.SetBounds(Theme.S(36), y, rowW - Theme.S(40), Theme.S(34));
                row.Click += delegate { ipBox.Box.Text = item.Address; Start(); };
                savedPanel.Controls.Add(row);

                PillButton remove = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphDelete, GlyphSize = 9f, AccessibleName = Lang.L("حذف ", "Delete ") + item.Name };
                remove.SetBounds(0, y, Theme.S(32), Theme.S(34));
                remove.Click += delegate { TvBook.Remove(item.Address); RefreshSaved(); };
                savedPanel.Controls.Add(remove);
                y += Theme.S(40);
            }
            savedPanel.ResumeLayout();
        }

        void SaveCurrent()
        {
            if (busy) return;
            string host = ipBox.Box.Text.Trim();
            if (!AdbTool.ValidHost(host))
            {
                status.ForeColor = Theme.Warning;
                status.Text = Lang.L("اكتب عنوان IP صحيحاً قبل الحفظ.", "Enter a valid IP address before saving.");
                return;
            }
            string name;
            using (TextPromptForm prompt = new TextPromptForm(Lang.L("اسم التلفزيون (اختياري، يمكن تركه فارغاً):", "TV name (optional, can be left empty):")))
            {
                prompt.Text = Lang.L("حفظ عنوان تلفزيون", "Save a TV address");
                if (prompt.ShowDialog(this) != DialogResult.OK) return;
                name = prompt.Value;
            }
            string err = TvBook.Save(name, host);
            if (err != null)
            {
                status.ForeColor = Theme.Danger;
                status.Text = err;
                return;
            }
            status.ForeColor = Theme.Success;
            status.Text = Lang.L("تم حفظ العنوان في القائمة.", "The address was saved to the list.");
            RefreshSaved();
        }

        void Start()
        {
            if (busy) return;
            string host = ipBox.Box.Text.Trim();
            if (!AdbTool.ValidHost(host))
            {
                status.ForeColor = Theme.Warning;
                status.Text = Lang.L("اكتب عنوان IP صحيحاً، مثل 192.168.1.40", "Enter a valid IP address, e.g. 192.168.1.40");
                return;
            }
            busy = true;
            connect.Enabled = false;
            ipBox.Box.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = Lang.L("جاري الاتصال بـ ", "Connecting to ") + host + " …";

            Thread t = new Thread(delegate ()
            {
                AdbTool tool;
                AdbTool.ConnectResult r = AdbTool.Connect(host, out tool);
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { Finish(r, tool, host); }));
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void Finish(AdbTool.ConnectResult r, AdbTool tool, string host)
        {
            busy = false;
            connect.Enabled = true;
            ipBox.Box.Enabled = true;
            switch (r)
            {
                case AdbTool.ConnectResult.Connected:
                    Tool = tool;
                    Host = host;
                    AppFiles.Log("اتصال بشاشة أندرويد " + host);
                    DialogResult = DialogResult.OK;
                    break;
                case AdbTool.ConnectResult.Unauthorized:
                    status.ForeColor = Theme.Warning;
                    status.Text = Lang.L("وافق على التصحيح على شاشة التلفزيون، ثم اضغط اتصال من جديد.", "Approve debugging on the TV screen, then press Connect again.");
                    break;
                case AdbTool.ConnectResult.NoAdb:
                    status.ForeColor = Theme.Danger;
                    status.Text = Lang.L("أداة adb غير موجودة بجانب البرنامج. راجع دليل التثبيت.", "The adb tool isn't next to the program. See the setup guide.");
                    break;
                case AdbTool.ConnectResult.BadAddress:
                    status.ForeColor = Theme.Warning;
                    status.Text = Lang.L("عنوان IP غير صحيح.", "Invalid IP address.");
                    break;
                default:
                    status.ForeColor = Theme.Danger;
                    status.Text = Lang.L("تعذّر الوصول إلى التلفزيون. تأكد من تشغيله ومن تفعيل التصحيح عبر الشبكة ومن أنه على نفس الشبكة.", "Couldn't reach the TV. Make sure it's on, network debugging is enabled, and it's on the same network.");
                    break;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            ipBox.Box.Focus();
        }
    }
}
