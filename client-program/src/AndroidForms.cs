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
        readonly PillButton usbButton;
        readonly Panel savedPanel = new Panel();
        readonly bool phoneAllowed;
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
            // The phone-over-USB feature is owner-granted. When it isn't enabled from the dashboard,
            // nothing about the phone is shown (no button, no hint, and the instructions mention only
            // the TV), and the dialog shrinks by the height of the hidden button so there is no gap.
            // A company admin can never enable it. The actual connection is enforced per device type
            // in Finish(), so a disallowed phone can't be reached even by typing its IP.
            phoneAllowed = LicenseManager.PhoneControlAllowed;
            int yo = phoneAllowed ? 0 : 66; // how far the controls below the phone section move up
            ClientSize = new Size(Theme.S(480), Theme.S(560 - yo));
            Icon = Program.AppIcon;

            int w = ClientSize.Width, m = Theme.S(24), inner = w - 2 * m;

            Label title = Theme.MakeLabel(Lang.L("التحكم بتلفزيون أو شاشة أندرويد", "Control an Android TV or screen"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), inner, Theme.S(28));
            Controls.Add(title);

            Label note = Theme.MakeLabel(phoneAllowed
                ? Lang.L("على التلفزيون مرة واحدة: فعّل \"خيارات المطور\" ثم \"تصحيح الأخطاء عبر الشبكة\"، واكتب عنوان IP. للجوال: فعّل المطوّر وتصحيح أخطاء USB، وصّله بالكابل، ثم استخدم الزر أدناه.", "On the TV once: enable \"Developer options\" then \"Network debugging\", and enter the IP. For a phone: enable Developer options and USB debugging, plug it in by cable, then use the button below.")
                : Lang.L("على التلفزيون مرة واحدة: فعّل \"خيارات المطور\" ثم \"تصحيح الأخطاء عبر الشبكة\"، ثم اكتب عنوان IP.", "On the TV once: enable \"Developer options\" then \"Network debugging\", then enter the IP."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(48), inner, Theme.S(72));
            Controls.Add(note);

            // Created even when hidden so the field is never null (Start/Finish touch its Enabled).
            usbButton = new PillButton { Text = Lang.L("جوال عبر USB — تفعيل واتصال تلقائي", "Phone over USB — auto-enable & connect"), Kind = ButtonKind.Secondary };
            usbButton.Font = Theme.UiBold(10f);
            usbButton.Enabled = phoneAllowed;
            usbButton.Click += delegate { StartUsb(); };
            if (phoneAllowed)
            {
                usbButton.SetBounds(m, Theme.S(124), inner, Theme.S(40));
                Controls.Add(usbButton);

                Label usbHint = Theme.MakeLabel(Lang.L("وصّل الجوال بـ USB ثم اضغط الزر: يفعّل الاتصال اللاسلكي ويتصل تلقائياً.", "Plug the phone in by USB, then press the button: it switches on wireless and connects."), Theme.Ui(8.5f), Theme.Muted, ContentAlignment.TopRight);
                usbHint.SetBounds(m, Theme.S(166), inner, Theme.S(16));
                Controls.Add(usbHint);
            }

            Label cap = Theme.MakeLabel(Lang.L("أو اكتب عنوان IP يدوياً (مثال: 192.168.1.40)", "Or enter an IP address manually (e.g. 192.168.1.40)"), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            cap.SetBounds(m, Theme.S(190 - yo), inner, Theme.S(20));
            Controls.Add(cap);
            ipBox.SetBounds(m, Theme.S(212 - yo), inner, Theme.S(48));
            ipBox.Box.Font = Theme.UiBold(13f);
            ipBox.Box.RightToLeft = RightToLeft.No;
            ipBox.Box.MaxLength = 21;
            if (AdbTool.ValidHost(prefill)) ipBox.Box.Text = prefill;
            ipBox.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Start(); } };
            Controls.Add(ipBox);

            PillButton save = new PillButton { Text = Lang.L("حفظ هذا العنوان في القائمة", "Save this address to the list"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphCheck, GlyphSize = 9f };
            save.Font = Theme.UiBold(9.5f);
            save.SetBounds(m, Theme.S(266 - yo), inner, Theme.S(32));
            save.Click += delegate { SaveCurrent(); };
            Controls.Add(save);

            status = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            status.SetBounds(m, Theme.S(304 - yo), inner, Theme.S(34));
            Controls.Add(status);

            Label savedCap = Theme.MakeLabel(Lang.L("الأجهزة المحفوظة", "Saved devices"), Theme.UiBold(10f), Theme.Text, ContentAlignment.MiddleRight);
            savedCap.SetBounds(m, Theme.S(344 - yo), inner, Theme.S(22));
            Controls.Add(savedCap);

            savedPanel.SetBounds(m, Theme.S(370 - yo), inner, Theme.S(112));
            savedPanel.BackColor = Theme.Card;
            savedPanel.AutoScroll = true;
            Controls.Add(savedPanel);

            int bw = (inner - Theme.S(12)) / 2;
            connect = new PillButton { Text = Lang.L("اتصال", "Connect"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect, GlyphSize = 11f };
            connect.SetBounds(w - m - bw, Theme.S(496 - yo), bw, Theme.S(48));
            connect.Click += delegate { Start(); };
            Controls.Add(connect);
            PillButton cancel = new PillButton { Text = Lang.L("إغلاق", "Close"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(496 - yo), bw, Theme.S(48));
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
            usbButton.Enabled = false;
            ipBox.Box.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = Lang.L("جاري الاتصال بـ ", "Connecting to ") + host + " …";

            Thread t = new Thread(delegate ()
            {
                AdbTool tool;
                AdbTool.ConnectResult r = AdbTool.Connect(host, out tool);
                // Enforce the entitlement by what actually answered: a television needs TV control, a
                // phone (anything not a TV) needs the owner-only phone control. A device the user is
                // not allowed to reach is disconnected at once, so typing its IP changes nothing.
                bool denied = false, isTv = false;
                if (r == AdbTool.ConnectResult.Connected && tool != null)
                {
                    isTv = tool.IsTelevision();
                    bool allow = isTv ? LicenseManager.AndroidControlAllowed : LicenseManager.PhoneControlAllowed;
                    if (!allow) { tool.Disconnect(); tool = null; denied = true; }
                }
                bool fdenied = denied, fisTv = isTv;
                try
                {
                    if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { Finish(r, tool, host, fdenied, fisTv); }));
                }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        // One-tap phone setup over USB: find the cabled phone, switch it to wireless, read its IP,
        // then connect over Wi-Fi with the normal flow.
        void StartUsb()
        {
            if (busy || !phoneAllowed) return;
            busy = true;
            connect.Enabled = false;
            usbButton.Enabled = false;
            ipBox.Box.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = Lang.L("جاري تجهيز الجوال عبر USB…", "Preparing the phone over USB…");

            Thread t = new Thread(delegate ()
            {
                string host = null, error = null;
                if (!AdbTool.Available())
                    error = Lang.L("أداة adb غير موجودة بجانب البرنامج.", "The adb tool isn't next to the program.");
                else
                {
                    bool unauth;
                    string serial = AdbTool.FindUsbSerial(out unauth);
                    if (serial == null)
                        error = unauth
                            ? Lang.L("وافق على \"السماح بتصحيح USB\" على شاشة الجوال، ثم أعد المحاولة.", "Accept \"Allow USB debugging\" on the phone, then try again.")
                            : Lang.L("لم يُعثر على جوال موصول بـ USB. وصّل الجوال وفعّل تصحيح أخطاء USB.", "No USB phone found. Connect the phone with USB debugging enabled.");
                    else if (!AdbTool.EnableTcpip(serial))
                        error = Lang.L("تعذّر تفعيل الاتصال اللاسلكي على الجوال. أعد المحاولة.", "Couldn't switch the phone to wireless. Try again.");
                    else
                    {
                        Thread.Sleep(1500); // let adb restart in TCP mode before reading the address
                        host = AdbTool.DeviceIp(serial);
                        if (host == null)
                            error = Lang.L("تعذّرت قراءة عنوان الجوال. تأكد من اتصاله بالواي فاي.", "Couldn't read the phone's IP. Make sure it's on Wi-Fi.");
                    }
                }
                string fhost = host, ferror = error;
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(delegate { UsbReady(fhost, ferror); })); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        void UsbReady(string host, string error)
        {
            busy = false;
            connect.Enabled = true;
            usbButton.Enabled = phoneAllowed;
            ipBox.Box.Enabled = true;
            if (error != null)
            {
                status.ForeColor = Theme.Warning;
                status.Text = error;
                return;
            }
            ipBox.Box.Text = host;
            status.ForeColor = Theme.Success;
            status.Text = Lang.L("تم تجهيز الجوال. يمكنك فصل USB الآن. جاري الاتصال…", "Phone ready. You can unplug USB now. Connecting…");
            Start(); // connect wirelessly to the phone's IP
        }

        void Finish(AdbTool.ConnectResult r, AdbTool tool, string host, bool denied, bool isTv)
        {
            busy = false;
            connect.Enabled = true;
            usbButton.Enabled = phoneAllowed;
            ipBox.Box.Enabled = true;
            if (denied)
            {
                status.ForeColor = Theme.Warning;
                status.Text = isTv
                    ? Lang.L("التحكم بشاشات أندرويد (TV) غير مفعّل لهذا الجهاز. يُفعَّل من لوحة التحكم.", "Android TV control isn't enabled for this device. It's enabled from the dashboard.")
                    : Lang.L("التحكم بالجوال غير مفعّل لهذا الجهاز. يفعّله مالك اللوحة من لوحة التحكم.", "Phone control isn't enabled for this device. The dashboard owner enables it.");
                return;
            }
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
