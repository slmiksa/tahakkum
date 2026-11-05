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

        public AccessPasswordForm()
        {
            Text = "كلمة سر الوصول";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(460), Theme.S(416));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel("الدخول إلى هذا الجهاز بكلمة سر", Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel("من يعرف كلمة السر يفتح شاشتك مباشرة دون رسالة موافقة، ويظهر شريط الجلسة دائماً. تعمل لمستخدم ويندوز الحالي فقط، وتتوقف إذا سجّل مستخدم آخر الدخول على هذا الجهاز.", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(50), w - 2 * m, Theme.S(68));
            Controls.Add(note);

            AddField(first, "كلمة السر (" + AccessPassword.MinLength + " أحرف على الأقل)", Theme.S(120));
            AddField(second, "تأكيد كلمة السر", Theme.S(204));

            error = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(284), w - 2 * m, Theme.S(40));
            Controls.Add(error);

            int gap = Theme.S(10);
            int bw = (w - 2 * m - 2 * gap) / 3;
            PillButton save = new PillButton { Text = "حفظ", Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            save.SetBounds(w - m - bw, Theme.S(344), bw, Theme.S(46));
            save.Click += delegate { Save(); };
            Controls.Add(save);

            PillButton remove = new PillButton { Text = "إلغاء تفعيلها", Kind = ButtonKind.Secondary };
            remove.Font = Theme.UiBold(9f);
            remove.SetBounds(m + bw + gap, Theme.S(344), bw, Theme.S(46));
            remove.Enabled = AccessPassword.IsSet;
            remove.Click += delegate { Remove(); };
            Controls.Add(remove);

            PillButton cancel = new PillButton { Text = "إغلاق", Kind = ButtonKind.Secondary };
            cancel.SetBounds(m, Theme.S(344), bw, Theme.S(46));
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
                error.Text = "كلمة السر قصيرة. استخدم " + AccessPassword.MinLength + " أحرف على الأقل.";
                return;
            }
            if (a != second.Box.Text)
            {
                error.Text = "الكلمتان غير متطابقتين.";
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

    // Adds or edits one entry of the saved address list.
    sealed class ContactForm : Form
    {
        readonly AddressBox nameBox = new AddressBox();
        readonly AddressBox addressBox = new AddressBox();
        readonly Label error;
        readonly string replacing;

        public ContactForm(Contact existing, string address)
        {
            replacing = existing != null ? existing.Address : null;
            Text = existing != null ? "تعديل عنوان" : "إضافة عنوان";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(440), Theme.S(330));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            AddField(nameBox, "الاسم: صاحب العنوان أو الجهاز", Theme.S(18));
            nameBox.Box.MaxLength = ContactBook.MaxName;
            nameBox.Box.Text = existing != null ? existing.Name : "";

            AddField(addressBox, "عنوان IP أو المعرّف من 9 أرقام", Theme.S(110));
            addressBox.Box.MaxLength = ContactBook.MaxAddress;
            addressBox.Box.Text = address ?? "";

            error = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(200), w - 2 * m, Theme.S(44));
            Controls.Add(error);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            PillButton save = new PillButton { Text = "حفظ", Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            save.SetBounds(w - m - bw, Theme.S(254), bw, Theme.S(50));
            save.Click += delegate { Save(); };
            Controls.Add(save);

            PillButton cancel = new PillButton { Text = "إلغاء", Kind = ButtonKind.Secondary };
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
            Text = "تفعيل البرنامج";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(520), Theme.S(330));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel("أدخل كود التفعيل", Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label note = Theme.MakeLabel("يرتبط الكود بهذا الجهاز ويبقى فعالاً عند تغيير مستخدم ويندوز. تبدأ مدته من يوم إصداره، لا من يوم تفعيله.", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            note.SetBounds(m, Theme.S(50), w - 2 * m, Theme.S(48));
            Controls.Add(note);

            box.SetBounds(m, Theme.S(104), w - 2 * m, Theme.S(56));
            box.Box.Font = Theme.UiBold(13f);
            box.Box.MaxLength = 64;
            box.Box.CharacterCasing = CharacterCasing.Upper;
            box.Box.AccessibleName = "كود التفعيل";
            Controls.Add(box);

            status = Theme.MakeLabel("", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.TopRight);
            status.SetBounds(m, Theme.S(170), w - 2 * m, Theme.S(50));
            Controls.Add(status);

            int bw = (w - 2 * m - Theme.S(12)) / 2;
            activate = new PillButton { Text = "تفعيل", Kind = ButtonKind.Primary, Glyph = Theme.GlyphCheck };
            activate.SetBounds(w - m - bw, Theme.S(254), bw, Theme.S(50));
            activate.Click += delegate { Start(); };
            Controls.Add(activate);

            PillButton cancel = new PillButton { Text = "إغلاق", Kind = ButtonKind.Secondary };
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
                status.Text = "اكتب كود التفعيل أولاً.";
                return;
            }
            activate.Enabled = false;
            status.ForeColor = Theme.Muted;
            status.Text = "جاري التفعيل…";
            Thread t = new Thread(() =>
            {
                string error = null;
                try { LicenseManager.Activate(code); }
                catch (LicenseFailure f) { error = f.Message; }
                catch (Exception ex)
                {
                    AppFiles.Log("خطأ أثناء التفعيل: " + ex);
                    error = "حدث خطأ غير متوقع أثناء التفعيل.";
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
            Text = "كلمة سر الجهاز";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            RightToLeft = RightToLeft.Yes;
            ClientSize = new Size(Theme.S(440), Theme.S(320));
            Icon = Program.AppIcon;

            int w = ClientSize.Width;
            int m = Theme.S(24);

            Label title = Theme.MakeLabel("أدخل كلمة السر لفتح الشاشة مباشرة", Theme.UiBold(13f), Theme.Text, ContentAlignment.MiddleRight);
            title.SetBounds(m, Theme.S(16), w - 2 * m, Theme.S(30));
            Controls.Add(title);

            Label sub = Theme.MakeLabel(target + "   •   رمز التحقق " + code, Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            sub.AutoEllipsis = true;
            sub.SetBounds(m, Theme.S(48), w - 2 * m, Theme.S(24));
            Controls.Add(sub);

            box.SetBounds(m, Theme.S(86), w - 2 * m, Theme.S(52));
            box.Box.UseSystemPasswordChar = true;
            box.Box.MaxLength = AccessPassword.MaxLength;
            box.Box.AccessibleName = "كلمة السر";
            Controls.Add(box);

            error = Theme.MakeLabel(failedBefore ? "كلمة السر غير صحيحة. حاول مرة أخرى." : "", Theme.Ui(9.5f), Theme.Danger, ContentAlignment.MiddleRight);
            error.SetBounds(m, Theme.S(146), w - 2 * m, Theme.S(28));
            Controls.Add(error);

            PillButton connect = new PillButton { Text = "اتصال", Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect };
            connect.SetBounds(m, Theme.S(182), w - 2 * m, Theme.S(48));
            connect.Click += delegate
            {
                if (box.Box.Text.Length == 0)
                {
                    error.Text = "اكتب كلمة السر، أو اطلب موافقة الشخص أمام الشاشة.";
                    return;
                }
                Finish(box.Box.Text);
            };
            Controls.Add(connect);
            AcceptButton = connect;

            PillButton ask = new PillButton { Text = "طلب موافقة الشخص أمام الشاشة", Kind = ButtonKind.Secondary };
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
