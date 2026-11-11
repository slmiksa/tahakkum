using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Tahakkum
{
    // Sets or removes the password that opens this machine without the approval prompt.
    sealed class AccessPasswordForm : Form
    {
        readonly AddressBox first = new AddressBox();
        readonly AddressBox second = new AddressBox();
        readonly Label error;
        readonly Label lockStatus;
        readonly PillButton lockButton;

        public AccessPasswordForm()
        {
            Text = Lang.L("كلمة سر الوصول", "Access password");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(460), Theme.S(620));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel(Lang.L("الدخول إلى هذا الجهاز بكلمة سر", "Access this device with a password"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel(Lang.L("من يعرف كلمة السر يفتح شاشتك مباشرة دون رسالة موافقة، ويظهر شريط الجلسة دائماً. تعمل لمستخدم ويندوز الحالي فقط، وتتوقف إذا سجّل مستخدم آخر الدخول على هذا الجهاز.", "Anyone who knows the password opens your screen directly without an approval prompt, and the session bar always shows. It works for the current Windows user only, and stops if another user signs in to this device."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(50), w - 2 * m, Theme.S(68));
            Controls.Add(note);

            AddField(first, Lang.L("كلمة السر (", "Password (") + AccessPassword.MinLength + Lang.L(" أحرف على الأقل)", " characters minimum)"), Theme.S(120));
            AddField(second, Lang.L("تأكيد كلمة السر", "Confirm password"), Theme.S(204));

            error = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(284), w - 2 * m, Theme.S(40));
            Controls.Add(error);

            int gap = Theme.S(10);
            int bw = (w - 2 * m - 2 * gap) / 3;
            PillButton save = new PillButton { Text = Lang.L("حفظ", "Save"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            save.SetBounds(w - m - bw, Theme.S(344), bw, Theme.S(46));
            save.Click += delegate { Save(); };
            Controls.Add(save);

            PillButton remove = new PillButton { Text = Lang.L("إلغاء تفعيلها", "Turn it off"), Kind = ButtonKind.Secondary };
            remove.Font = Theme.UiBold(9f);
            remove.SetBounds(m + bw + gap, Theme.S(344), bw, Theme.S(46));
            remove.Enabled = AccessPassword.IsSet;
            remove.Click += delegate { Remove(); };
            Controls.Add(remove);

            PillButton cancel = new PillButton { Text = Lang.L("إغلاق", "Close"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(344), bw, Theme.S(46));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = save;

            // ---- Lock-screen access (unattended sign-in through the SYSTEM service) ----
            Panel line = new Panel { BackColor = Theme.CardRaised };
            line.SetBounds(m, Theme.S(410), w - 2 * m, 1);
            Controls.Add(line);

            Label lockTitle = Theme.MakeLabel(Lang.L("الوصول إلى شاشة القفل (ويندوز + L)", "Lock-screen access (Windows + L)"), Theme.UiBold(11.5f), Theme.Text, ContentAlignment.MiddleRight);
            lockTitle.SetBounds(m, Theme.S(424), w - 2 * m, Theme.S(26));
            Controls.Add(lockTitle);

            Label lockNote = Theme.MakeLabel(Lang.L("تثبيت خدمة تتيح لمن يعرف كلمة السر الدخول حتى والجهاز مقفول بويندوز+L. يحتاج صلاحية مسؤول مرة واحدة. الدخول من شاشة القفل يكون بكلمة السر فقط. ثبّت البرنامج في مجلد محمي (مثل Program Files) قبل التفعيل.", "Installs a service that lets anyone who knows the password sign in even while the device is locked with Windows+L. Needs administrator permission once. Lock-screen access is by password only. Install the program in a protected folder (such as Program Files) before enabling."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            lockNote.SetBounds(m, Theme.S(454), w - 2 * m, Theme.S(86));
            Controls.Add(lockNote);

            lockStatus = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            lockStatus.SetBounds(m, Theme.S(548), w - 2 * m - Theme.S(150), Theme.S(46));
            Controls.Add(lockStatus);

            lockButton = new PillButton { Kind = ButtonKind.Secondary, Glyph = Theme.GlyphShield, GlyphSize = 10f };
            lockButton.Font = Theme.UiBold(9f);
            lockButton.SetBounds(w - m - Theme.S(140), Theme.S(548), Theme.S(140), Theme.S(46));
            lockButton.Click += delegate { ToggleLock(); };
            Controls.Add(lockButton);
            RefreshLock();
        }

        void RefreshLock()
        {
            bool installed = LockInstall.IsInstalled();
            lockButton.Text = installed ? Lang.L("إزالة الخدمة", "Remove service") : Lang.L("تثبيت الخدمة", "Install service");
            if (installed)
                lockStatus.Text = AccessPassword.IsSet ? Lang.L("مفعّلة: يمكن الدخول من شاشة القفل بكلمة السر.", "On: you can sign in from the lock screen with the password.") : Lang.L("مثبّتة، لكن عيّن كلمة السر أعلاه لتعمل.", "Installed, but set the password above for it to work.");
            else
                lockStatus.Text = Lang.L("غير مفعّلة: الجهاز المقفول لا يُفتح عن بُعد.", "Off: a locked device can't be unlocked remotely.");
        }

        void ToggleLock()
        {
            bool installed = LockInstall.IsInstalled();
            if (!installed && IsUnderUserProfile())
            {
                DialogResult go = MessageBox.Show(this,
                    Lang.L("البرنامج يعمل من مجلد يستطيع المستخدم تعديله، وهذا غير آمن لخدمة تعمل بصلاحيات النظام. يُفضّل نقله إلى مجلد محمي مثل Program Files أولاً. أتريد المتابعة رغم ذلك؟", "The program runs from a folder the user can modify, which isn't safe for a service running as SYSTEM. It's better to move it to a protected folder such as Program Files first. Continue anyway?"),
                    "تنبيه أمني", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (go != DialogResult.Yes) return;
            }
            lockButton.Enabled = false;
            lockButton.Text = Lang.L("جارٍ…", "Working…");
            bool ok = LockInstall.RequestElevated(installed ? "/removelock" : "/installlock");
            lockButton.Enabled = true;
            RefreshLock();
            if (!ok)
                MessageBox.Show(this, installed ? Lang.L("تعذّرت إزالة الخدمة.", "Couldn't remove the service.") : Lang.L("تعذّر تثبيت الخدمة. تأكد من الموافقة على طلب صلاحية المسؤول.", "Couldn't install the service. Make sure you approved the administrator prompt."),
                    Lang.L("تحكّم", "Tahakkum"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static bool IsUnderUserProfile()
        {
            try
            {
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Application.ExecutablePath.StartsWith(profile, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        void AddField(AddressBox box, string caption, int top)
        {
            int w = ClientSize.Width;
            int m = Theme.S(24);
            Label c = Theme.MakeLabel(caption, Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            c.SetBounds(m, top, w - 2 * m, Theme.S(24));
            Controls.Add(c);
            box.SetBounds(m, top + Theme.S(26), w - 2 * m, Theme.S(48));
            box.Box.UseSystemPasswordChar = true;
            box.Box.MaxLength = AccessPassword.MaxLength;
            box.Box.AccessibleName = caption;
            Controls.Add(box);
        }

        void Save()
        {
            string a = first.Box.Text;
            if (a.Length < AccessPassword.MinLength)
            {
                error.Text = Lang.L("كلمة السر قصيرة. استخدم ", "The password is too short. Use ") + AccessPassword.MinLength + Lang.L(" أحرف على الأقل.", " characters minimum.");
                return;
            }
            if (a != second.Box.Text)
            {
                error.Text = Lang.L("الكلمتان غير متطابقتين.", "The passwords don't match.");
                return;
            }
            AccessPassword.Set(a);
            AppFiles.Log("تم تعيين كلمة سر الوصول");
            DialogResult = DialogResult.OK;
        }

        void Remove()
        {
            AccessPassword.Clear();
            AppFiles.Log("تم إلغاء كلمة سر الوصول");
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            first.Box.Focus();
        }
    }

    // The name this person shows to support staff on the network and when they connect.
    sealed class DisplayNameForm : Form
    {
        readonly AddressBox nameBox = new AddressBox();

        public DisplayNameForm()
        {
            Text = Lang.L("اسمك الظاهر", "Your display name");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(440), Theme.S(290));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel(Lang.L("اسمك كما يراه الدعم الفني", "Your name as support sees it"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel(Lang.L("يظهر في قائمة \"في الشبكة\" عند من يبحث عن جهازك، وعند من تتصل به. اكتب اسمك أو اسم قسمك، مثل: سالم - المحاسبة.", "Appears in the \"On network\" list to people searching for your device, and to those you connect to. Enter your name or department, e.g. Salem - Accounting."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(50), w - 2 * m, Theme.S(48));
            Controls.Add(note);

            nameBox.SetBounds(m, Theme.S(110), w - 2 * m, Theme.S(52));
            nameBox.Box.Font = Theme.UiBold(13f);
            nameBox.Box.MaxLength = Profile.MaxName;
            nameBox.Box.Text = Profile.DisplayName;
            nameBox.Box.AccessibleName = Lang.L("الاسم الظاهر", "Display name");
            Controls.Add(nameBox);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            PillButton save = new PillButton { Text = Lang.L("حفظ", "Save"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            save.SetBounds(w - m - bw, Theme.S(214), bw, Theme.S(50));
            save.Click += delegate
            {
                try { Profile.DisplayName = nameBox.Box.Text; }
                catch (Exception ex) { AppFiles.Log("تعذر حفظ الاسم الظاهر: " + ex.Message); }
                DialogResult = DialogResult.OK;
            };
            Controls.Add(save);

            PillButton cancel = new PillButton { Text = Lang.L("إلغاء", "Cancel"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(214), bw, Theme.S(50));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = save;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            nameBox.Box.Focus();
            nameBox.Box.SelectAll();
        }
    }

    // Adds or edits one entry of the saved address list.
    sealed class ContactForm : Form
    {
        readonly AddressBox nameBox = new AddressBox();
        readonly AddressBox addressBox = new AddressBox();
        readonly Label error;
        readonly string replacing;

        public ContactForm(Contact existing, string address, string suggestedName = null)
        {
            replacing = existing != null ? existing.Address : null;
            Text = existing != null ? Lang.L("تعديل عنوان", "Edit address") : Lang.L("إضافة عنوان", "Add address");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(440), Theme.S(330));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            AddField(nameBox, Lang.L("الاسم: صاحب العنوان أو الجهاز", "Name: the address or device owner"), Theme.S(18));
            nameBox.Box.MaxLength = ContactBook.MaxName;
            nameBox.Box.Text = existing != null ? existing.Name : suggestedName ?? "";

            AddField(addressBox, Lang.L("عنوان IP أو المعرّف من 9 أرقام", "IP address or 9-digit ID"), Theme.S(110));
            addressBox.Box.MaxLength = ContactBook.MaxAddress;
            addressBox.Box.Text = address ?? "";

            error = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(200), w - 2 * m, Theme.S(44));
            Controls.Add(error);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            PillButton save = new PillButton { Text = Lang.L("حفظ", "Save"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            save.SetBounds(w - m - bw, Theme.S(254), bw, Theme.S(50));
            save.Click += delegate { Save(); };
            Controls.Add(save);

            PillButton cancel = new PillButton { Text = Lang.L("إلغاء", "Cancel"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(254), bw, Theme.S(50));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = save;
        }

        void AddField(AddressBox box, string caption, int top)
        {
            int w = ClientSize.Width;
            int m = Theme.S(24);
            Label c = Theme.MakeLabel(caption, Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            c.SetBounds(m, top, w - 2 * m, Theme.S(24));
            Controls.Add(c);
            box.SetBounds(m, top + Theme.S(26), w - 2 * m, Theme.S(52));
            box.Box.Font = Theme.UiBold(12f);
            box.Box.AccessibleName = caption;
            Controls.Add(box);
        }

        void Save()
        {
            string failure = ContactBook.Save(nameBox.Box.Text, addressBox.Box.Text, replacing);
            if (failure != null)
            {
                error.Text = failure;
                return;
            }
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            nameBox.Box.Focus();
        }
    }

    // Enters the activation code the company received.
    sealed class ActivationForm : Form
    {
        readonly AddressBox box = new AddressBox();
        readonly Label status;
        readonly PillButton activate;

        public ActivationForm()
        {
            Text = Lang.L("تفعيل البرنامج", "Activate");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(520), Theme.S(330));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel(Lang.L("أدخل كود التفعيل", "Enter the activation code"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel(Lang.L("يرتبط الكود بهذا الجهاز ويبقى فعالاً عند تغيير مستخدم ويندوز. تبدأ مدته من يوم إصداره، لا من يوم تفعيله.", "The code is bound to this device and stays valid when the Windows user changes. Its term starts from its issue date, not its activation date."), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(50), w - 2 * m, Theme.S(48));
            Controls.Add(note);

            box.SetBounds(m, Theme.S(104), w - 2 * m, Theme.S(56));
            box.Box.Font = Theme.UiBold(13f);
            box.Box.MaxLength = 64;
            box.Box.CharacterCasing = CharacterCasing.Upper;
            box.Box.AccessibleName = Lang.L("كود التفعيل", "Activation code");
            Controls.Add(box);

            status = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.TopRight);
            status.SetBounds(m, Theme.S(170), w - 2 * m, Theme.S(50));
            Controls.Add(status);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            activate = new PillButton { Text = Lang.L("تفعيل", "Activate"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            activate.SetBounds(w - m - bw, Theme.S(254), bw, Theme.S(50));
            activate.Click += delegate { Start(); };
            Controls.Add(activate);

            PillButton cancel = new PillButton { Text = Lang.L("إغلاق", "Close"), Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(254), bw, Theme.S(50));
            cancel.Click += delegate { Close(); };
            Controls.Add(cancel);
            CancelButton = cancel;
            AcceptButton = activate;
        }

        void Start()
        {
            string code = box.Box.Text.Trim();
            if (code.Length == 0)
            {
                status.Text = Lang.L("اكتب كود التفعيل أولاً.", "Enter the activation code first.");
                return;
            }
            activate.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = Lang.L("جاري التفعيل…", "Activating…");
            Thread t = new Thread(() =>
            {
                string error = null;
                try { LicenseManager.Activate(code); }
                catch (LicenseFailure f) { error = f.Message; }
                catch (Exception ex)
                {
                    AppFiles.Log("خطأ أثناء التفعيل: " + ex);
                    error = Lang.L("حدث خطأ غير متوقع أثناء التفعيل.", "An unexpected error occurred during activation.");
                }
                try { BeginInvoke((Action)(() => Finished(error))); }
                catch (Exception) { }
            });
            t.IsBackground = true;
            t.Name = "license-activate";
            t.Start();
        }

        void Finished(string error)
        {
            if (error == null)
            {
                DialogResult = DialogResult.OK;
                return;
            }
            activate.Enabled = true;
            status.ForeColor = Theme.Danger;
            status.Text = error;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            box.Box.Focus();
        }
    }

    // Shown on the viewer when the other machine has a password set.
    sealed class PasswordPromptForm : Form
    {
        readonly AddressBox box = new AddressBox();
        readonly Label error;

        // The password, "" to ask for approval instead, or null when closed.
        public string Answer;

        public PasswordPromptForm(string target, string code, bool failedBefore)
        {
            Text = Lang.L("كلمة سر الجهاز", "Device password");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = Lang.Rtl;
            ClientSize = new Size(Theme.S(440), Theme.S(320));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel(Lang.L("أدخل كلمة السر لفتح الشاشة مباشرة", "Enter the password to open the screen directly"), Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label sub = Theme.MakeLabel(target + Lang.L("   •   رمز التحقق ", "   •   Verification code ") + code, Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            sub.AutoEllipsis = true;
            sub.SetBounds(m, Theme.S(48), w - 2 * m, Theme.S(24));
            Controls.Add(sub);

            box.SetBounds(m, Theme.S(86), w - 2 * m, Theme.S(52));
            box.Box.UseSystemPasswordChar = true;
            box.Box.MaxLength = AccessPassword.MaxLength;
            box.Box.AccessibleName = Lang.L("كلمة السر", "Password");
            Controls.Add(box);

            error = Theme.MakeLabel(failedBefore ? Lang.L("كلمة السر غير صحيحة. حاول مرة أخرى.", "The password is incorrect. Try again.") : "", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(146), w - 2 * m, Theme.S(28));
            Controls.Add(error);

            PillButton connect = new PillButton { Text = Lang.L("اتصال", "Connect"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect };
            connect.SetBounds(m, Theme.S(182), w - 2 * m, Theme.S(48));
            connect.Click += delegate
            {
                if (box.Box.Text.Length == 0)
                {
                    error.Text = Lang.L("اكتب كلمة السر، أو اطلب موافقة الشخص أمام الشاشة.", "Enter the password, or ask the person at the screen to approve.");
                    return;
                }
                Finish(box.Box.Text);
            };
            Controls.Add(connect);
            AcceptButton = connect;

            PillButton ask = new PillButton { Text = Lang.L("طلب موافقة الشخص أمام الشاشة", "Ask the person at the screen to approve"), Kind = ButtonKind.Secondary };
            ask.SetBounds(m, Theme.S(240), w - 2 * m, Theme.S(48));
            ask.Click += delegate { Finish(""); };
            Controls.Add(ask);
        }

        void Finish(string value)
        {
            Answer = value;
            DialogResult = DialogResult.OK;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            box.Box.Focus();
        }
    }
}
