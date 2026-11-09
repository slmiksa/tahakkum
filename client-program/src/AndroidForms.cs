using System;
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
            Text = "إدخال نص";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
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
            PillButton ok = new PillButton { Text = "إرسال", Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            ok.SetBounds(w - m - bw, Theme.S(134), bw, Theme.S(48));
            ok.Click += delegate { DialogResult = DialogResult.OK; };
            Controls.Add(ok);
            PillButton cancel = new PillButton { Text = "إلغاء", Kind = ButtonKind.Secondary };
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

    // Asks for the TV's IP and connects over adb. On success, Tool and Host are set.
    sealed class AndroidConnectForm : Form
    {
        readonly AddressBox ipBox = new AddressBox();
        readonly Label status;
        readonly PillButton connect;
        bool busy;

        public AdbTool Tool;
        public string Host;

        public AndroidConnectForm(string prefill)
        {
            Text = "التحكم بشاشة أندرويد";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(480), Theme.S(340));
            Icon = Program.AppIcon;

            int w = ClientSize.Width, m = Theme.S(24);

            Label title = Theme.MakeLabel("التحكم بتلفزيون أو شاشة أندرويد", Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel("على التلفزيون مرة واحدة: فعّل \"خيارات المطور\" ثم \"تصحيح الأخطاء عبر الشبكة\". أول اتصال يظهر على التلفزيون تنبيه للسماح، اضغط \"سماح\". ثم اكتب عنوان IP الظاهر في إعدادات شبكة التلفزيون.", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(52), w - 2 * m, Theme.S(92));
            Controls.Add(note);

            Label cap = Theme.MakeLabel("عنوان IP (مثال: 192.168.1.40)", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            cap.SetBounds(m, Theme.S(150), w - 2 * m, Theme.S(22));
            Controls.Add(cap);
            ipBox.SetBounds(m, Theme.S(176), w - 2 * m, Theme.S(50));
            ipBox.Box.Font = Theme.UiBold(13f);
            ipBox.Box.RightToLeft = RightToLeft.No;
            ipBox.Box.MaxLength = 21;
            if (AdbTool.ValidHost(prefill)) ipBox.Box.Text = prefill;
            ipBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(); } };
            Controls.Add(ipBox);

            status = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            status.SetBounds(m, Theme.S(234), w - 2 * m, Theme.S(40));
            Controls.Add(status);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            connect = new PillButton { Text = "اتصال", Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect, GlyphSize = 11f };
            connect.SetBounds(w - m - bw, Theme.S(282), bw, Theme.S(48));
            connect.Click += delegate { Start(); };
            Controls.Add(connect);
            PillButton cancel = new PillButton { Text = "إغلاق", Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(282), bw, Theme.S(48));
            cancel.Click += delegate { if (!busy) Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = connect;
        }

        void Start()
        {
            if (busy) return;
            string host = ipBox.Box.Text.Trim();
            if (!AdbTool.ValidHost(host))
            {
                status.ForeColor = Theme.Warning;
                status.Text = "اكتب عنوان IP صحيحاً، مثل 192.168.1.40";
                return;
            }
            busy = true;
            connect.Enabled = false;
            ipBox.Box.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = "جاري الاتصال بـ " + host + " …";

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
                    status.Text = "وافق على التصحيح على شاشة التلفزيون، ثم اضغط اتصال من جديد.";
                    break;
                case AdbTool.ConnectResult.NoAdb:
                    status.ForeColor = Theme.Danger;
                    status.Text = "أداة adb غير موجودة بجانب البرنامج. راجع دليل التثبيت.";
                    break;
                case AdbTool.ConnectResult.BadAddress:
                    status.ForeColor = Theme.Warning;
                    status.Text = "عنوان IP غير صحيح.";
                    break;
                default:
                    status.ForeColor = Theme.Danger;
                    status.Text = "تعذّر الوصول إلى التلفزيون. تأكد من تشغيله ومن تفعيل التصحيح عبر الشبكة ومن أنه على نفس الشبكة.";
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
