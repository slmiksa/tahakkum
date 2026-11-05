using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tahakkum
{
    sealed class MainForm : Form
    {
        const string FirewallRule = "Tahakkum Remote Control";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "Tahakkum";

        readonly HostServer server;
        readonly bool startInTray;
        readonly string autoConnect;

        readonly AddressBox addressBox = new AddressBox();
        readonly PillButton connectButton;
        readonly Label connectStatus;
        readonly Panel recentPanel = new Panel();
        readonly PillButton savedTab;
        readonly PillButton recentTab;
        readonly ToggleSwitch hostToggle;
        readonly StatusDot hostDot;
        readonly Label hostState;
        readonly Label mainIp;
        readonly Label otherIps;
        readonly PillButton copyIp;
        readonly Label firewallState;
        readonly PillButton firewallButton;
        readonly ToggleSwitch autostart;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Label tileTitle;
        readonly ToggleSwitch internetToggle;
        readonly PillButton passwordButton;
        readonly RelayLink relay;
        readonly Label licenseState;
        readonly PillButton licenseButton;
        readonly System.Windows.Forms.Timer licenseTimer;

        ViewerClient pending;
        HostSession activeSession;
        SessionBar sessionBar;
        FileTransferWindow hostTransfers;
        ClipboardSync hostClipboard;
        bool exiting;
        bool firstVisible = true;
        bool trayHintShown;
        bool licenseWasValid = LicenseManager.IsValid;
        bool showingSaved = true;

        public static uint ShowMessage;

        public MainForm(HostServer server, bool startInTray, string autoConnect)
        {
            this.server = server;
            this.startInTray = startInTray;
            this.autoConnect = autoConnect;

            Text = "تحكّم — التحكم بالشاشات عبر الشبكة";
            Icon = Program.AppIcon;
            BackColor = Theme.Bg;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(980), Theme.S(640));
            RightToLeft = RightToLeft.No;

            int W = ClientSize.Width;
            int margin = Theme.S(24);

            // Header
            LogoView logo = new LogoView { BackColor = Theme.Bg };
            logo.SetBounds(W - margin - Theme.S(56), Theme.S(14), Theme.S(56), Theme.S(56));
            Controls.Add(logo);
            WordmarkView wordmark = new WordmarkView { BackColor = Theme.Bg };
            wordmark.SetBounds(W - margin - Theme.S(186), Theme.S(12), Theme.S(120), Theme.S(40));
            Controls.Add(wordmark);
            // MiddleLeft is the leading edge in a right-to-left label, so this sits next to the logo.
            Label subtitle = Theme.MakeLabel("تحكم كامل بالشاشات داخل الشبكة أو عبر الإنترنت، بموافقة الشخص أو بكلمة سر", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleLeft);
            subtitle.SetBounds(W - margin - Theme.S(626), Theme.S(52), Theme.S(560), Theme.S(24));
            Controls.Add(subtitle);

            licenseButton = new PillButton { Text = "تفعيل البرنامج", Kind = ButtonKind.Primary, Glyph = Theme.GlyphShield, GlyphSize = 10f };
            licenseButton.Font = Theme.UiBold(9f);
            licenseButton.SetBounds(margin, Theme.S(24), Theme.S(130), Theme.S(36));
            licenseButton.Click += delegate { OpenActivation(); };
            Controls.Add(licenseButton);
            licenseState = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleRight);
            licenseState.AutoEllipsis = true;
            licenseState.SetBounds(margin + Theme.S(136), Theme.S(18), Theme.S(190), Theme.S(48));
            Controls.Add(licenseState);

            int cardTop = Theme.S(84);
            int cardH = ClientSize.Height - cardTop - margin;
            int gap = Theme.S(16);
            int cardW = (W - 2 * margin - gap) / 2;

            // ---- Connect card (right, the primary reading position) ----
            CardPanel connectCard = new CardPanel();
            connectCard.SetBounds(W - margin - cardW, cardTop, cardW, cardH);
            Controls.Add(connectCard);
            int cw = connectCard.Width;
            int p = Theme.S(24);
            int inner = cw - 2 * p;

            connectCard.Controls.Add(At(Theme.MakeLabel("التحكم بشاشة أخرى", Theme.UiBold(13.5f), Theme.Text, ContentAlignment.MiddleRight), p, Theme.S(18), inner, Theme.S(30)));
            connectCard.Controls.Add(At(Theme.MakeLabel("أدخل عنوان IP داخل الشبكة، أو معرّف الجهاز عبر الإنترنت.", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight), p, Theme.S(48), inner, Theme.S(24)));

            addressBox.SetBounds(p, Theme.S(86), inner, Theme.S(56));
            addressBox.Box.AccessibleName = "عنوان IP أو المعرّف";
            addressBox.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ConnectOrCancel();
                }
            };
            connectCard.Controls.Add(addressBox);

            connectButton = new PillButton { Text = "اتصال", Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect, GlyphSize = 12f };
            connectButton.Font = Theme.UiBold(11.5f);
            connectButton.SetBounds(p, Theme.S(154), inner, Theme.S(50));
            connectButton.Click += delegate { ConnectOrCancel(); };
            connectCard.Controls.Add(connectButton);

            connectStatus = Theme.MakeLabel("", Theme.Ui(10f), Theme.Muted, ContentAlignment.TopRight);
            connectCard.Controls.Add(At(connectStatus, p, Theme.S(210), inner, Theme.S(50)));

            // Address book: the saved list, and the recent connections beside it.
            savedTab = MakeListTab("العناوين المحفوظة", true);
            savedTab.SetBounds(cw - p - Theme.S(146), Theme.S(264), Theme.S(146), Theme.S(32));
            connectCard.Controls.Add(savedTab);
            recentTab = MakeListTab("الأخيرة", false);
            recentTab.SetBounds(cw - p - Theme.S(232), Theme.S(264), Theme.S(80), Theme.S(32));
            connectCard.Controls.Add(recentTab);
            PillButton addContact = new PillButton { Text = "إضافة", Kind = ButtonKind.Secondary, Glyph = Theme.GlyphAdd, GlyphSize = 9f };
            addContact.Font = Theme.UiBold(9f);
            addContact.SetBounds(p, Theme.S(264), Theme.S(84), Theme.S(32));
            addContact.Click += delegate { EditContact(null, addressBox.Box.Text.Trim()); };
            connectCard.Controls.Add(addContact);

            recentPanel.SetBounds(p, Theme.S(304), inner, connectCard.Height - Theme.S(316));
            recentPanel.BackColor = Theme.Card;
            recentPanel.AutoScroll = true;
            connectCard.Controls.Add(recentPanel);

            // ---- This machine card ----
            CardPanel hostCard = new CardPanel();
            hostCard.SetBounds(margin, cardTop, cardW, cardH);
            Controls.Add(hostCard);

            hostCard.Controls.Add(At(Theme.MakeLabel("هذا الجهاز", Theme.UiBold(13.5f), Theme.Text, ContentAlignment.MiddleRight), p, Theme.S(18), inner, Theme.S(30)));

            hostDot = new StatusDot { BackColor = Theme.Card };
            hostDot.SetBounds(cw - p - Theme.S(14), Theme.S(57), Theme.S(12), Theme.S(12));
            hostCard.Controls.Add(hostDot);
            hostState = Theme.MakeLabel("", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight);
            hostCard.Controls.Add(At(hostState, p, Theme.S(50), inner - Theme.S(22), Theme.S(26)));

            CardPanel ipTile = new CardPanel { BackColor = Theme.CardRaised };
            ipTile.SetBounds(p, Theme.S(86), inner, Theme.S(118));
            hostCard.Controls.Add(ipTile);
            tileTitle = Theme.MakeLabel("عنوان هذا الجهاز", Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            ipTile.Controls.Add(At(tileTitle, Theme.S(14), Theme.S(8), inner - Theme.S(28), Theme.S(22)));
            mainIp = Theme.MakeLabel("", Theme.UiBold(20f), Theme.Text, ContentAlignment.MiddleCenter);
            mainIp.RightToLeft = RightToLeft.No;
            ipTile.Controls.Add(At(mainIp, Theme.S(48), Theme.S(30), inner - Theme.S(96), Theme.S(44)));
            copyIp = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphCopy, GlyphSize = 12f, AccessibleName = "نسخ العنوان" };
            copyIp.SetBounds(Theme.S(10), Theme.S(34), Theme.S(36), Theme.S(36));
            copyIp.Click += delegate { CopyAddress(); };
            ipTile.Controls.Add(copyIp);
            otherIps = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleCenter);
            otherIps.RightToLeft = RightToLeft.No;
            otherIps.AutoEllipsis = true;
            ipTile.Controls.Add(At(otherIps, Theme.S(10), Theme.S(78), inner - Theme.S(20), Theme.S(30)));

            hostToggle = new ToggleSwitch { Text = "استقبال طلبات التحكم" };
            hostCard.Controls.Add(At(hostToggle, p, Theme.S(216), inner, Theme.S(40)));

            internetToggle = new ToggleSwitch { Text = "الاستقبال عبر الإنترنت بمعرّف" };
            hostCard.Controls.Add(At(internetToggle, p, Theme.S(252), inner, Theme.S(36)));

            autostart = new ToggleSwitch { Text = "التشغيل مع بدء ويندوز" };
            autostart.Checked = IsAutostartEnabled();
            autostart.CheckedChanged += delegate { SetAutostart(autostart.Checked); };
            hostCard.Controls.Add(At(autostart, p, Theme.S(288), inner, Theme.S(36)));

            firewallState = Theme.MakeLabel("الجدار الناري: جاري الفحص…", Theme.Ui(10f), Theme.Text, ContentAlignment.MiddleRight);
            hostCard.Controls.Add(At(firewallState, p + Theme.S(130), Theme.S(324), inner - Theme.S(130), Theme.S(40)));
            firewallButton = new PillButton { Text = "السماح", Glyph = Theme.GlyphShield, GlyphSize = 10f };
            firewallButton.Font = Theme.UiBold(9f);
            firewallButton.SetBounds(p, Theme.S(328), Theme.S(120), Theme.S(34));
            firewallButton.Visible = false;
            firewallButton.Click += delegate { AddFirewallRule(); };
            hostCard.Controls.Add(firewallButton);

            passwordButton = new PillButton { Glyph = Theme.GlyphShield, GlyphSize = 10f };
            passwordButton.Font = Theme.UiBold(9f);
            passwordButton.SetBounds(p, Theme.S(370), inner, Theme.S(38));
            passwordButton.Click += delegate { OpenPassword(); };
            hostCard.Controls.Add(passwordButton);

            int bottomY = Theme.S(416);
            int half = (inner - Theme.S(10)) / 2;
            PillButton logButton = new PillButton { Text = "سجل الجلسات", Glyph = Theme.GlyphHistory, GlyphSize = 10f };
            logButton.Font = Theme.UiBold(9f);
            logButton.SetBounds(p + half + Theme.S(10), bottomY, half, Theme.S(38));
            logButton.Click += delegate { OpenLog(); };
            hostCard.Controls.Add(logButton);

            PillButton adminButton = new PillButton { Text = "تشغيل كمسؤول", Glyph = Theme.GlyphAdmin, GlyphSize = 10f };
            adminButton.Font = Theme.UiBold(9f);
            adminButton.SetBounds(p, bottomY, half, Theme.S(38));
            adminButton.Visible = !IsElevated();
            adminButton.Click += delegate { RestartElevated(); };
            hostCard.Controls.Add(adminButton);

            Label adminNote = Theme.MakeLabel(IsElevated() ? "البرنامج يعمل كمسؤول، ويمكن التحكم بنوافذ المسؤول." : "شغّله كمسؤول إذا احتجت التحكم بالبرامج التي تعمل بصلاحية المسؤول.", Theme.Ui(9f), Theme.Muted, ContentAlignment.TopRight);
            hostCard.Controls.Add(At(adminNote, p, Theme.S(460), inner, Theme.S(42)));

            hostToggle.Checked = server != null;
            hostToggle.Enabled = server != null;
            hostToggle.CheckedChanged += delegate { ApplyHostToggle(); ApplyInternet(); };

            passwordButton.Enabled = server != null;
            internetToggle.Enabled = server != null && RelayConfig.Load() != null;
            internetToggle.Checked = internetToggle.Enabled && System.IO.File.Exists(InternetFlagPath);
            internetToggle.CheckedChanged += delegate { SaveInternetFlag(); ApplyInternet(); };

            if (server != null)
            {
                relay = new RelayLink(server.Certificate, server.HandleRelayed);
                relay.StatusChanged += () => OnUi(RefreshAddresses);
                server.ConsentNeeded += r => OnUi(() => ShowConsent(r));
                server.SessionStarted += s => OnUi(() => BeginHostSession(s));
                server.SessionEnded += s => OnUi(() => EndHostSession(s));
            }

            SetupTray();
            RefreshAddresses();
            RefreshRecent();
            ApplyHostToggle();
            ApplyInternet();
            RefreshPasswordButton();
            CheckFirewallAsync();

            LicenseManager.Changed += OnLicenseChanged;
            licenseTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            licenseTimer.Tick += delegate { RefreshLicense(); };
            licenseTimer.Start();
            RefreshLicense();

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        }

        static Control At(Control c, int x, int y, int w, int h)
        {
            c.SetBounds(x, y, w, h);
            return c;
        }

        void OnUi(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
            }
            catch (Exception) { }
        }

        protected override void SetVisibleCore(bool value)
        {
            if (firstVisible && startInTray)
            {
                firstVisible = false;
                CreateHandle();
                value = false;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Theme.DarkTitleBar(Handle);
            addressBox.Box.Focus();
            if (!string.IsNullOrEmpty(autoConnect))
            {
                addressBox.Box.Text = autoConnect;
                ConnectOrCancel();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (ShowMessage != 0 && m.Msg == ShowMessage) RestoreFromTray();
            base.WndProc(ref m);
        }

        // ---- addresses ----

        void OnNetworkChanged(object sender, EventArgs e)
        {
            OnUi(RefreshAddresses);
        }

        void RefreshAddresses()
        {
            List<string> withGateway = new List<string>();
            List<string> others = new List<string>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                    foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        (hasGateway ? withGateway : others).Add(a.Address.ToString());
                    }
                }
            }
            catch (Exception) { }
            List<string> all = withGateway.Concat(others).ToList();
            string id = relay != null ? relay.Id : null;
            if (id != null)
            {
                tileTitle.Text = "معرّف هذا الجهاز للاتصال عبر الإنترنت";
                mainIp.Text = id.Substring(0, 3) + " " + id.Substring(3, 3) + " " + id.Substring(6);
                copyIp.Enabled = true;
                otherIps.Text = all.Count > 0 ? "داخل الشبكة: " + all[0] : "";
                return;
            }
            tileTitle.Text = "عنوان هذا الجهاز";
            mainIp.Text = all.Count > 0 ? all[0] : "غير متصل بالشبكة";
            copyIp.Enabled = all.Count > 0;
            if (internetToggle.Checked && hostToggle.Checked && LicenseManager.IsValid) otherIps.Text = "جاري الاتصال بخادم الاتصال…";
            else otherIps.Text = all.Count > 1 ? string.Join("   ", all.Skip(1).ToArray()) : "المنفذ " + Proto.DefaultPort;
        }

        static string InternetFlagPath { get { return System.IO.Path.Combine(AppFiles.Dir, "internet.on"); } }

        void SaveInternetFlag()
        {
            try
            {
                if (internetToggle.Checked) System.IO.File.WriteAllText(InternetFlagPath, "1");
                else System.IO.File.Delete(InternetFlagPath);
            }
            catch (Exception) { }
        }

        void ApplyInternet()
        {
            if (relay == null) return;
            if (hostToggle.Checked && internetToggle.Checked && LicenseManager.IsValid) relay.Start();
            else relay.Stop();
            RefreshAddresses();
        }

        // ---- license ----

        void OnLicenseChanged()
        {
            OnUi(RefreshLicense);
        }

        void RefreshLicense()
        {
            bool required = LicenseManager.Required;
            licenseButton.Visible = required;
            licenseState.Visible = required;
            if (!required) return;

            bool valid = LicenseManager.IsValid;
            LicenseToken t = LicenseManager.Current;
            licenseButton.Visible = !valid || LicenseManager.ExpiresSoon;
            if (valid)
            {
                licenseState.Text = "مرخّص لـ " + Identity.Sanitize(t.Company, 64) + "\nمتبقٍ " + LicenseManager.DaysLeft + " يوماً";
                licenseState.ForeColor = LicenseManager.ExpiresSoon ? Theme.Warning : Theme.Muted;
            }
            else
            {
                licenseState.Text = t != null && LicenseManager.DaysLeft <= 0 ? "انتهت رخصة البرنامج" : "البرنامج غير مفعّل";
                licenseState.ForeColor = Theme.Danger;
            }
            if (valid != licenseWasValid)
            {
                licenseWasValid = valid;
                if (activeSession == null) ApplyHostToggle();
                ApplyInternet();
            }
        }

        bool OpenActivation()
        {
            using (ActivationForm form = new ActivationForm()) form.ShowDialog(this);
            RefreshLicense();
            return LicenseManager.IsValid;
        }

        void RefreshPasswordButton()
        {
            passwordButton.Text = AccessPassword.IsSet ? "كلمة سر الوصول: مفعّلة" : "تعيين كلمة سر للوصول المباشر";
            passwordButton.Invalidate();
        }

        // Refused during a session: otherwise whoever is controlling this machine could
        // set a password and come back later without anyone approving it.
        void OpenPassword()
        {
            if (activeSession != null)
            {
                MessageBox.Show(this, "لا يمكن تغيير كلمة السر أثناء جلسة تحكم. أنهِ الجلسة أولاً.", "كلمة سر الوصول", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                return;
            }
            using (AccessPasswordForm form = new AccessPasswordForm()) form.ShowDialog(this);
            RefreshPasswordButton();
        }

        void CopyAddress()
        {
            try { Clipboard.SetText(mainIp.Text); }
            catch (Exception) { }
        }

        // ---- connecting to another screen ----

        void ConnectOrCancel()
        {
            if (pending != null)
            {
                pending.Cancel();
                return;
            }
            if (!LicenseManager.IsValid && !OpenActivation()) return;
            string input = addressBox.Box.Text.Trim();
            if (input.Length == 0)
            {
                SetStatus("اكتب عنوان IP أو معرّف الجهاز أولاً.", Theme.Warning);
                addressBox.Box.Focus();
                return;
            }
            string host;
            int port;
            ViewerClient.ParseAddress(input, out host, out port);
            string deviceId = ViewerClient.ParseDeviceId(input);

            ViewerClient client = new ViewerClient();
            pending = client;
            connectButton.Text = "إلغاء";
            connectButton.Kind = ButtonKind.Secondary;
            connectButton.Glyph = Theme.GlyphClose;
            connectButton.Invalidate();
            addressBox.Box.Enabled = false;
            SetStatus("جاري الاتصال بـ " + host + " …", Theme.Muted);

            Thread t = new Thread(() =>
            {
                try
                {
                    Action<string> waiting = code => OnUi(() => SetStatus("بانتظار موافقة الشخص أمام الشاشة…\nرمز التحقق: " + code, Theme.Accent));
                    Func<string, bool> confirm = code => ConfirmChangedIdentity(deviceId ?? host, code);
                    PasswordPrompt prompt = (code, failed) => AskPassword(deviceId ?? host, code, failed);
                    Connection conn = deviceId != null
                        ? client.ConnectById(deviceId, waiting, confirm, prompt)
                        : client.Connect(host, port, waiting, confirm, prompt);
                    OnUi(() => ConnectFinished(client, conn, input, null));
                }
                catch (ConnectFailure f)
                {
                    OnUi(() => ConnectFinished(client, null, input, f.Message));
                }
                catch (Exception)
                {
                    OnUi(() => ConnectFinished(client, null, input, "حدث خطأ غير متوقع أثناء الاتصال."));
                }
            });
            t.IsBackground = true;
            t.Name = "viewer-connect";
            t.Start();
        }

        bool ConfirmChangedIdentity(string host, string code)
        {
            bool accepted = false;
            Invoke((Action)delegate
            {
                DialogResult r = MessageBox.Show(this,
                    "هوية الجهاز " + host + " تغيرت عن آخر اتصال.\n\nقد يكون البرنامج أُعيد تثبيته، أو أن جهازاً آخر ينتحل هذا العنوان.\nرمز التحقق الجديد: " + code + "\n\nهل تريد المتابعة؟ تأكد من مطابقة الرمز مع الشخص أمام الشاشة.",
                    "تحذير أمني", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                accepted = r == DialogResult.Yes;
            });
            return accepted;
        }

        string AskPassword(string target, string code, bool failedBefore)
        {
            string answer = null;
            Invoke((Action)delegate
            {
                using (PasswordPromptForm form = new PasswordPromptForm(target, code, failedBefore))
                {
                    form.ShowDialog(this);
                    answer = form.Answer;
                }
            });
            return answer;
        }

        void ConnectFinished(ViewerClient client, Connection conn, string input, string error)
        {
            if (pending == client) pending = null;
            connectButton.Text = "اتصال";
            connectButton.Kind = ButtonKind.Primary;
            connectButton.Glyph = Theme.GlyphConnect;
            connectButton.Invalidate();
            addressBox.Box.Enabled = true;

            if (conn == null)
            {
                SetStatus(error ?? "", Theme.Danger);
                return;
            }

            SetStatus("تمت الموافقة. الجلسة مفتوحة.", Theme.Success);
            RecentHosts.Touch(input);
            RefreshRecent();
            ViewerForm viewer = new ViewerForm(conn, input, client.HostName, client.Code);
            viewer.Show();
            viewer.Activate();
        }

        void SetStatus(string text, Color color)
        {
            connectStatus.Text = text;
            connectStatus.ForeColor = color;
        }

        PillButton MakeListTab(string text, bool saved)
        {
            PillButton tab = new PillButton { Text = text, Kind = saved ? ButtonKind.Primary : ButtonKind.Secondary };
            tab.Font = Theme.UiBold(9f);
            tab.TabStop = false;
            tab.Click += delegate
            {
                showingSaved = saved;
                RefreshRecent();
            };
            return tab;
        }

        void RefreshRecent()
        {
            savedTab.Kind = showingSaved ? ButtonKind.Primary : ButtonKind.Secondary;
            recentTab.Kind = showingSaved ? ButtonKind.Secondary : ButtonKind.Primary;
            savedTab.Invalidate();
            recentTab.Invalidate();

            recentPanel.SuspendLayout();
            recentPanel.Controls.Clear();
            if (showingSaved) FillSavedList();
            else FillRecentList();
            recentPanel.ResumeLayout();
        }

        // Leaves room for the scroll bar when the rows will not all fit.
        int ListWidth(int rows)
        {
            bool scrolls = rows * Theme.S(40) > recentPanel.Height;
            return recentPanel.Width - (scrolls ? SystemInformation.VerticalScrollBarWidth + Theme.S(4) : 0);
        }

        void ConnectTo(string address)
        {
            if (pending != null) return;
            addressBox.Box.Text = address;
            ConnectOrCancel();
        }

        void ShowListHint(string text)
        {
            Label hint = Theme.MakeLabel(text, Theme.Ui(9.5f), Theme.Muted, ContentAlignment.TopRight);
            hint.BackColor = Theme.Card;
            hint.SetBounds(0, Theme.S(4), recentPanel.Width, Theme.S(60));
            recentPanel.Controls.Add(hint);
        }

        void FillSavedList()
        {
            List<Contact> list = ContactBook.Load();
            if (list.Count == 0)
            {
                ShowListHint("احفظ هنا عناوين الأجهزة التي تتصل بها كثيراً، باسم صاحبها أو الجهاز. اضغط \"إضافة\" للبدء.");
                return;
            }
            int width = ListWidth(list.Count);
            int y = 0;
            foreach (Contact item in list)
            {
                Contact contact = item;
                // The mark keeps a Latin address from reordering around an Arabic name.
                PillButton row = new PillButton { Text = contact.Name + "‏   •   ‎" + contact.Address, Kind = ButtonKind.Secondary };
                row.Font = Theme.Ui(10f);
                row.SetBounds(0, y, width - Theme.S(78), Theme.S(36));
                row.Click += delegate { ConnectTo(contact.Address); };
                recentPanel.Controls.Add(row);

                PillButton edit = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphEdit, GlyphSize = 9f, AccessibleName = "تعديل " + contact.Name };
                edit.SetBounds(width - Theme.S(72), y, Theme.S(36), Theme.S(36));
                edit.Click += delegate { EditContact(contact, contact.Address); };
                recentPanel.Controls.Add(edit);

                PillButton remove = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphDelete, GlyphSize = 9f, AccessibleName = "حذف " + contact.Name };
                remove.SetBounds(width - Theme.S(36), y, Theme.S(36), Theme.S(36));
                remove.Click += delegate { RemoveContact(contact); };
                recentPanel.Controls.Add(remove);
                y += Theme.S(40);
            }
        }

        void FillRecentList()
        {
            List<string> list = RecentHosts.Load();
            if (list.Count == 0)
            {
                ShowListHint("ستظهر هنا آخر العناوين التي اتصلت بها.");
                return;
            }
            int width = ListWidth(list.Count);
            int y = 0;
            foreach (string item in list)
            {
                string host = item;
                PillButton row = new PillButton { Text = host, Kind = ButtonKind.Secondary, Glyph = Theme.GlyphMonitor, GlyphSize = 10f };
                row.Font = Theme.Ui(10f);
                row.SetBounds(0, y, width - Theme.S(78), Theme.S(36));
                row.Click += delegate { ConnectTo(host); };
                recentPanel.Controls.Add(row);

                PillButton save = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphAdd, GlyphSize = 9f, AccessibleName = "حفظ " + host };
                save.SetBounds(width - Theme.S(72), y, Theme.S(36), Theme.S(36));
                save.Click += delegate { EditContact(null, host); };
                recentPanel.Controls.Add(save);

                PillButton remove = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphClose, GlyphSize = 8f, AccessibleName = "حذف " + host };
                remove.SetBounds(width - Theme.S(36), y, Theme.S(36), Theme.S(36));
                remove.Click += delegate
                {
                    RecentHosts.Remove(host);
                    BeginInvoke((Action)RefreshRecent);
                };
                recentPanel.Controls.Add(remove);
                y += Theme.S(40);
            }
        }

        void EditContact(Contact existing, string address)
        {
            using (ContactForm form = new ContactForm(existing, address))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
            }
            showingSaved = true;
            // Deferred: this can run from a button in the list that is about to be rebuilt.
            BeginInvoke((Action)RefreshRecent);
        }

        void RemoveContact(Contact contact)
        {
            DialogResult answer = MessageBox.Show(this, "حذف \"" + contact.Name + "\" من العناوين المحفوظة؟", "حذف عنوان", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
                MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
            if (answer != DialogResult.Yes) return;
            ContactBook.Remove(contact.Address);
            BeginInvoke((Action)RefreshRecent);
        }

        // ---- being controlled ----

        void ApplyHostToggle()
        {
            if (server == null)
            {
                hostDot.Color = Theme.Muted;
                hostState.Text = "نسخة أخرى من البرنامج تستقبل الطلبات على هذا الجهاز.";
                return;
            }
            try
            {
                if (hostToggle.Checked) server.Start(Proto.DefaultPort);
                else server.Stop();
            }
            catch (SocketException)
            {
                hostToggle.Checked = false;
                hostDot.Color = Theme.Danger;
                hostState.Text = "المنفذ " + Proto.DefaultPort + " مستخدم من برنامج آخر.";
                return;
            }
            hostDot.Color = hostToggle.Checked ? Theme.Success : Theme.Muted;
            hostState.Text = hostToggle.Checked ? "جاهز لاستقبال الطلبات على المنفذ " + Proto.DefaultPort : "لا يستقبل طلبات تحكم";
            if (hostToggle.Checked && !LicenseManager.IsValid)
            {
                hostDot.Color = Theme.Warning;
                hostState.Text = "لن تُقبل الطلبات قبل تفعيل البرنامج";
            }
        }

        void ShowConsent(ConsentRequest request)
        {
            ConsentForm form = new ConsentForm(request);
            form.Show();
            form.Activate();
        }

        void BeginHostSession(HostSession session)
        {
            activeSession = session;
            hostDot.Color = Theme.Danger;
            hostState.Text = "جلسة تحكم نشطة من " + session.RemoteIp;
            hostTransfers = new FileTransferWindow(session.Files, session.User.Length > 0 ? session.User + " (" + session.RemoteIp + ")" : session.RemoteIp);
            sessionBar = new SessionBar(session, hostTransfers);
            sessionBar.Show();
            hostClipboard = new ClipboardSync(session.SendClipboard);
            session.ClipboardFromViewer += text => OnUi(() => { if (hostClipboard != null) hostClipboard.Apply(text); });
            // A panic key for the local person, in case the pointer is being moved away from the bar.
            Native.RegisterHotKey(Handle, 1, 0x0007, 0x51);
            tray.ShowBalloonTip(4000, "جلسة تحكم نشطة", "يتحكم بهذه الشاشة: " + session.RemoteIp + "\nللإنهاء الفوري: Ctrl + Alt + Shift + Q", ToolTipIcon.Warning);
        }

        void EndHostSession(HostSession session)
        {
            if (activeSession != session) return;
            activeSession = null;
            Native.UnregisterHotKey(Handle, 1);
            if (hostClipboard != null)
            {
                hostClipboard.Dispose();
                hostClipboard = null;
            }
            if (sessionBar != null)
            {
                sessionBar.Close();
                sessionBar = null;
            }
            if (hostTransfers != null)
            {
                hostTransfers.CloseForGood();
                hostTransfers = null;
            }
            ApplyHostToggle();
        }

        protected override void DefWndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && activeSession != null) activeSession.End();
            base.DefWndProc(ref m);
        }

        void OnDisplaysChanged(object sender, EventArgs e)
        {
            HostSession s = activeSession;
            if (s != null) s.NotifyDisplaysChanged();
        }

        // ---- system integration ----

        void CheckFirewallAsync()
        {
            Thread t = new Thread(() =>
            {
                bool present = false;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + FirewallRule + "\"");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    psi.RedirectStandardOutput = true;
                    using (Process proc = Process.Start(psi))
                    {
                        proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        present = proc.HasExited && proc.ExitCode == 0;
                    }
                }
                catch (Exception) { }
                OnUi(() =>
                {
                    firewallState.Text = present ? "الجدار الناري: البرنامج مسموح له" : "الجدار الناري: قد يمنع الطلبات الواردة";
                    firewallState.ForeColor = present ? Theme.Text : Theme.Warning;
                    firewallButton.Visible = !present;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Inbound is limited to private address ranges on domain and private networks.
        void AddFirewallRule()
        {
            string args = "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=TCP localport=" + Proto.DefaultPort
                + " program=\"" + Application.ExecutablePath + "\" profile=domain,private remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", args);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (Process proc = Process.Start(psi)) proc.WaitForExit(10000);
            }
            catch (Exception) { }
            CheckFirewallAsync();
        }

        static bool IsElevated()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        void RestartElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.Arguments = "/elevated-restart";
                Program.ReleaseSingleInstance();
                Process.Start(psi);
                ExitApp();
            }
            catch (Exception) { }
        }

        static bool IsAutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch (Exception) { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\" /tray");
                    else k.DeleteValue(RunValue, false);
                }
            }
            catch (Exception) { }
        }

        static void OpenLog()
        {
            try
            {
                if (!System.IO.File.Exists(AppFiles.LogPath)) AppFiles.Log("بدء السجل");
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + AppFiles.LogPath + "\"") { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        void SetupTray()
        {
            tray.Icon = Program.AppIcon;
            tray.Text = "تحكّم";
            tray.Visible = true;
            ContextMenuStrip menu = Theme.MakeMenu();
            menu.ShowCheckMargin = false;
            menu.Items.Add("فتح", null, delegate { RestoreFromTray(); });
            menu.Items.Add("خروج", null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreFromTray(); };
        }

        void RestoreFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void ExitApp()
        {
            exiting = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            bool hosting = server != null && server.IsRunning;
            if (!exiting && e.CloseReason == CloseReason.UserClosing && hosting)
            {
                // Closing keeps the machine reachable; the tray icon stays visible the whole time.
                e.Cancel = true;
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "تحكّم ما زال يعمل", "يستقبل طلبات التحكم من أيقونة شريط المهام. للإغلاق التام: زر أيمن ثم خروج.", ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            LicenseManager.Changed -= OnLicenseChanged;
            licenseTimer.Stop();
            licenseTimer.Dispose();
            HostSession s = activeSession;
            if (s != null) s.End();
            if (relay != null) relay.Stop();
            if (server != null) server.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
