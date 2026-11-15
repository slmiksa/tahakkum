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
        const string DiscoveryRule = "Tahakkum Network Discovery";
        const int ListSaved = 0;
        const int ListRecent = 1;
        const int ListNetwork = 2;
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
        readonly ToggleSwitch keepAwakeToggle;
        readonly PillButton passwordButton;
        readonly RelayLink relay;
        readonly Label licenseState;
        readonly PillButton licenseButton;
        readonly System.Windows.Forms.Timer licenseTimer;

        ViewerClient pending;
        readonly List<HostSession> hostSessions = new List<HostSession>();
        readonly Dictionary<HostSession, FileTransferWindow> hostTransfers = new Dictionary<HostSession, FileTransferWindow>();
        readonly PillButton nameButton;
        readonly PillButton networkTab;
        readonly DiscoveryResponder discovery;
        readonly LockScreenLink lockLink;
        SessionBar sessionBar;
        ClipboardSync hostClipboard;
        readonly List<string> localAddresses = new List<string>();
        int scanGeneration;
        bool exiting;
        bool firstVisible = true;
        bool trayHintShown;
        bool licenseWasValid = LicenseManager.IsValid;
        int listMode = ListSaved;

        public static uint ShowMessage;

        public MainForm(HostServer server, bool startInTray, string autoConnect)
        {
            this.server = server;
            this.startInTray = startInTray;
            this.autoConnect = autoConnect;

            Text = Lang.L("تحكّم — التحكم بالشاشات عبر الشبكة", "Tahakkum — Remote Screen Control");
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
            Label subtitle = Theme.MakeLabel(Lang.L("تحكم كامل بالشاشات داخل الشبكة أو عبر الإنترنت، بموافقة الشخص أو بكلمة سر", "Full screen control inside the network or over the internet, with the person's consent or a password"), Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleLeft);
            subtitle.SetBounds(W - margin - Theme.S(626), Theme.S(52), Theme.S(560), Theme.S(24));
            Controls.Add(subtitle);

            // Language toggle: shows the other language and restarts into it.
            PillButton langButton = new PillButton { Text = Lang.English ? "العربية" : "English", Kind = ButtonKind.Secondary };
            langButton.Font = Theme.UiBold(9.5f);
            langButton.SetBounds(Theme.S(430), Theme.S(16), Theme.S(110), Theme.S(34));
            langButton.Click += delegate { Program.SwitchLanguage(!Lang.English); };
            Controls.Add(langButton);

            licenseButton = new PillButton { Text = Lang.L("تفعيل البرنامج", "Activate"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphShield, GlyphSize = 10f };
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

            connectCard.Controls.Add(At(Theme.MakeLabel(Lang.L("التحكم بشاشة أخرى", "Control another screen"), Theme.UiBold(13.5f), Theme.Text, ContentAlignment.MiddleRight), p, Theme.S(18), inner, Theme.S(30)));
            connectCard.Controls.Add(At(Theme.MakeLabel(Lang.L("أدخل عنوان IP داخل الشبكة، أو معرّف الجهاز عبر الإنترنت.", "Enter an IP address on the network, or the device ID over the internet."), Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight), p, Theme.S(48), inner, Theme.S(24)));

            addressBox.SetBounds(p, Theme.S(86), inner, Theme.S(56));
            addressBox.Box.AccessibleName = Lang.L("عنوان IP أو المعرّف", "IP address or ID");
            addressBox.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    ConnectOrCancel();
                }
            };
            connectCard.Controls.Add(addressBox);

            connectButton = new PillButton { Text = Lang.L("اتصال", "Connect"), Kind = ButtonKind.Primary, Glyph = Theme.GlyphConnect, GlyphSize = 12f };
            connectButton.Font = Theme.UiBold(11.5f);
            connectButton.SetBounds(p, Theme.S(154), inner, Theme.S(50));
            connectButton.Click += delegate { ConnectOrCancel(); };
            connectCard.Controls.Add(connectButton);

            connectStatus = Theme.MakeLabel("", Theme.Ui(10f), Theme.Muted, ContentAlignment.TopRight);
            connectCard.Controls.Add(At(connectStatus, p, Theme.S(210), inner, Theme.S(50)));

            // Address book: the saved list, the recent connections, and the machines on this network.
            savedTab = MakeListTab(Lang.L("المحفوظة", "Saved"), ListSaved);
            savedTab.SetBounds(cw - p - Theme.S(96), Theme.S(264), Theme.S(96), Theme.S(32));
            connectCard.Controls.Add(savedTab);
            recentTab = MakeListTab(Lang.L("الأخيرة", "Recent"), ListRecent);
            recentTab.SetBounds(cw - p - Theme.S(182), Theme.S(264), Theme.S(80), Theme.S(32));
            connectCard.Controls.Add(recentTab);
            networkTab = MakeListTab(Lang.L("في الشبكة", "On network"), ListNetwork);
            networkTab.SetBounds(cw - p - Theme.S(292), Theme.S(264), Theme.S(104), Theme.S(32));
            connectCard.Controls.Add(networkTab);
            PillButton addContact = new PillButton { Text = Lang.L("إضافة", "Add"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphAdd, GlyphSize = 9f };
            addContact.Font = Theme.UiBold(9f);
            addContact.SetBounds(p, Theme.S(264), Theme.S(84), Theme.S(32));
            addContact.Click += delegate { EditContact(null, addressBox.Box.Text.Trim()); };
            connectCard.Controls.Add(addContact);

            recentPanel.SetBounds(p, Theme.S(304), inner, connectCard.Height - Theme.S(316) - Theme.S(46));
            recentPanel.BackColor = Theme.Card;
            recentPanel.AutoScroll = true;
            connectCard.Controls.Add(recentPanel);

            PillButton androidButton = new PillButton { Text = Lang.L("التحكم بشاشة أندرويد (TV) عبر IP", "Control an Android screen (TV) over IP"), Kind = ButtonKind.Secondary, Glyph = Theme.GlyphConnect, GlyphSize = 10f };
            androidButton.Font = Theme.UiBold(9.5f);
            androidButton.SetBounds(p, connectCard.Height - Theme.S(42), inner, Theme.S(34));
            androidButton.Click += delegate { OpenAndroid(); };
            connectCard.Controls.Add(androidButton);

            // ---- This machine card ----
            CardPanel hostCard = new CardPanel();
            hostCard.SetBounds(margin, cardTop, cardW, cardH);
            Controls.Add(hostCard);

            hostCard.Controls.Add(At(Theme.MakeLabel(Lang.L("هذا الجهاز", "This device"), Theme.UiBold(13.5f), Theme.Text, ContentAlignment.MiddleRight), p, Theme.S(18), inner, Theme.S(30)));

            hostDot = new StatusDot { BackColor = Theme.Card };
            hostDot.SetBounds(cw - p - Theme.S(14), Theme.S(57), Theme.S(12), Theme.S(12));
            hostCard.Controls.Add(hostDot);
            hostState = Theme.MakeLabel("", Theme.Ui(10f), Theme.Muted, ContentAlignment.MiddleRight);
            hostCard.Controls.Add(At(hostState, p, Theme.S(50), inner - Theme.S(22), Theme.S(26)));

            CardPanel ipTile = new CardPanel { BackColor = Theme.CardRaised };
            ipTile.SetBounds(p, Theme.S(86), inner, Theme.S(118));
            hostCard.Controls.Add(ipTile);
            tileTitle = Theme.MakeLabel(Lang.L("عنوان هذا الجهاز", "This device's address"), Theme.Ui(9.5f), Theme.Muted, ContentAlignment.MiddleRight);
            ipTile.Controls.Add(At(tileTitle, Theme.S(14), Theme.S(8), inner - Theme.S(28), Theme.S(22)));
            mainIp = Theme.MakeLabel("", Theme.UiBold(20f), Theme.Text, ContentAlignment.MiddleCenter);
            mainIp.RightToLeft = RightToLeft.No;
            ipTile.Controls.Add(At(mainIp, Theme.S(48), Theme.S(30), inner - Theme.S(96), Theme.S(44)));
            copyIp = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphCopy, GlyphSize = 12f, AccessibleName = Lang.L("نسخ العنوان", "Copy address") };
            copyIp.SetBounds(Theme.S(10), Theme.S(34), Theme.S(36), Theme.S(36));
            copyIp.Click += delegate { CopyAddress(); };
            ipTile.Controls.Add(copyIp);
            otherIps = Theme.MakeLabel("", Theme.Ui(9f), Theme.Muted, ContentAlignment.MiddleCenter);
            otherIps.RightToLeft = RightToLeft.No;
            otherIps.AutoEllipsis = true;
            ipTile.Controls.Add(At(otherIps, Theme.S(10), Theme.S(78), inner - Theme.S(20), Theme.S(30)));

            hostToggle = new ToggleSwitch { Text = Lang.L("استقبال طلبات التحكم", "Accept control requests") };
            hostCard.Controls.Add(At(hostToggle, p, Theme.S(216), inner, Theme.S(40)));

            internetToggle = new ToggleSwitch { Text = Lang.L("الاستقبال عبر الإنترنت بمعرّف", "Accept over the internet by ID") };
            hostCard.Controls.Add(At(internetToggle, p, Theme.S(252), inner, Theme.S(36)));

            autostart = new ToggleSwitch { Text = Lang.L("التشغيل مع بدء ويندوز", "Start with Windows") };
            autostart.Checked = IsAutostartEnabled();
            autostart.CheckedChanged += delegate { SetAutostart(autostart.Checked); };
            hostCard.Controls.Add(At(autostart, p, Theme.S(288), inner, Theme.S(36)));

            keepAwakeToggle = new ToggleSwitch { Text = Lang.L("منع السكون أثناء الاستقبال", "Prevent sleep while hosting") };
            hostCard.Controls.Add(At(keepAwakeToggle, p, Theme.S(322), inner, Theme.S(36)));

            firewallState = Theme.MakeLabel(Lang.L("الجدار الناري: جاري الفحص…", "Firewall: checking…"), Theme.Ui(10f), Theme.Text, ContentAlignment.MiddleRight);
            hostCard.Controls.Add(At(firewallState, p + Theme.S(130), Theme.S(358), inner - Theme.S(130), Theme.S(40)));
            firewallButton = new PillButton { Text = Lang.L("السماح", "Allow"), Glyph = Theme.GlyphShield, GlyphSize = 10f };
            firewallButton.Font = Theme.UiBold(9f);
            firewallButton.SetBounds(p, Theme.S(362), Theme.S(120), Theme.S(34));
            firewallButton.Visible = false;
            firewallButton.Click += delegate { AddFirewallRule(); };
            hostCard.Controls.Add(firewallButton);

            passwordButton = new PillButton { Glyph = Theme.GlyphShield, GlyphSize = 10f };
            passwordButton.Font = Theme.UiBold(9f);
            passwordButton.SetBounds(p, Theme.S(404), inner, Theme.S(38));
            passwordButton.Click += delegate { OpenPassword(); };
            hostCard.Controls.Add(passwordButton);

            int bottomY = Theme.S(448);
            int half = (inner - Theme.S(10)) / 2;
            PillButton logButton = new PillButton { Text = Lang.L("سجل الجلسات", "Session log"), Glyph = Theme.GlyphHistory, GlyphSize = 10f };
            logButton.Font = Theme.UiBold(9f);
            logButton.SetBounds(p + half + Theme.S(10), bottomY, half, Theme.S(38));
            logButton.Click += delegate { OpenLog(); };
            hostCard.Controls.Add(logButton);

            PillButton adminButton = new PillButton { Text = Lang.L("تشغيل كمسؤول", "Run as administrator"), Glyph = Theme.GlyphAdmin, GlyphSize = 10f };
            adminButton.Font = Theme.UiBold(9f);
            adminButton.SetBounds(p, bottomY, half, Theme.S(38));
            adminButton.Visible = !IsElevated();
            adminButton.Click += delegate { RestartElevated(); };
            hostCard.Controls.Add(adminButton);

            nameButton = new PillButton { Kind = ButtonKind.Secondary, Glyph = Theme.GlyphEdit, GlyphSize = 9f };
            nameButton.Font = Theme.UiBold(9f);
            nameButton.SetBounds(p, Theme.S(490), inner, Theme.S(38));
            nameButton.Click += delegate { EditDisplayName(); };
            hostCard.Controls.Add(nameButton);
            RefreshNameButton();

            hostToggle.Checked = server != null;
            hostToggle.Enabled = server != null;
            hostToggle.CheckedChanged += delegate { ApplyHostToggle(); ApplyInternet(); ApplyKeepAwake(); ApplyLockAccess(); };

            passwordButton.Enabled = server != null;
            internetToggle.Enabled = server != null && RelayConfig.Load() != null;
            internetToggle.Checked = internetToggle.Enabled && System.IO.File.Exists(InternetFlagPath);
            internetToggle.CheckedChanged += delegate { SaveInternetFlag(); ApplyInternet(); };

            keepAwakeToggle.Enabled = server != null;
            keepAwakeToggle.Checked = server != null && System.IO.File.Exists(KeepAwakeFlagPath);
            keepAwakeToggle.CheckedChanged += delegate { SaveKeepAwakeFlag(); ApplyKeepAwake(); };

            if (server != null)
            {
                relay = new RelayLink(server.Certificate, server.HandleRelayed);
                relay.StatusChanged += () => OnUi(RefreshAddresses);
                server.ConsentNeeded += r => OnUi(() => ShowConsent(r));
                server.SessionStarted += s => OnUi(() => BeginHostSession(s));
                server.SessionEnded += s => OnUi(() => EndHostSession(s));
                server.Desk.Changed += () => OnUi(() =>
                {
                    if (sessionBar != null) sessionBar.RefreshViewers();
                    if (hostSessions.Count > 0) UpdateHostState();
                });
                discovery = new DiscoveryResponder(Proto.DiscoveryPort, DescribeThisMachine);
                lockLink = new LockScreenLink();
                server.Lock = lockLink;
            }

            SetupTray();
            RefreshAddresses();
            RefreshRecent();
            ApplyHostToggle();
            ApplyInternet();
            ApplyKeepAwake();
            ApplyLockAccess();
            RefreshPasswordButton();
            CheckFirewallAsync();

            LicenseManager.Changed += OnLicenseChanged;
            licenseTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
            licenseTimer.Tick += delegate { RefreshLicense(); KeepAwake.Reassert(); };
            licenseTimer.Start();
            RefreshLicense();

            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
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
            // Every IPv4 of every active interface (enumeration needs no administrator rights), kept so
            // the copy button can hand over all of them at once.
            localAddresses.Clear();
            localAddresses.AddRange(all);
            string id = relay != null ? relay.Id : null;
            if (id != null)
            {
                tileTitle.Text = Lang.L("معرّف هذا الجهاز للاتصال عبر الإنترنت", "This device's ID for internet connections");
                mainIp.Text = id.Substring(0, 3) + " " + id.Substring(3, 3) + " " + id.Substring(6);
                copyIp.Enabled = true;
                otherIps.Text = all.Count > 0 ? Lang.L("داخل الشبكة: ", "On the network: ") + all[0] : "";
                return;
            }
            tileTitle.Text = Lang.L("عنوان هذا الجهاز", "This device's address");
            mainIp.Text = all.Count > 0 ? all[0] : Lang.L("غير متصل بالشبكة", "Not connected to a network");
            copyIp.Enabled = all.Count > 0;
            if (internetToggle.Checked && hostToggle.Checked && LicenseManager.IsValid) otherIps.Text = Lang.L("جاري الاتصال بخادم الاتصال…", "Connecting to the relay server…");
            else otherIps.Text = all.Count > 1 ? string.Join("   ", all.Skip(1).ToArray()) : Lang.L("المنفذ ", "Port ") + Proto.DefaultPort;
        }

        static string InternetFlagPath { get { return System.IO.Path.Combine(AppFiles.Dir, "internet.on"); } }
        static string KeepAwakeFlagPath { get { return System.IO.Path.Combine(AppFiles.Dir, "awake.on"); } }

        void SaveInternetFlag()
        {
            try
            {
                if (internetToggle.Checked) System.IO.File.WriteAllText(InternetFlagPath, "1");
                else System.IO.File.Delete(InternetFlagPath);
            }
            catch (Exception) { }
        }

        void SaveKeepAwakeFlag()
        {
            try
            {
                if (keepAwakeToggle.Checked) System.IO.File.WriteAllText(KeepAwakeFlagPath, "1");
                else System.IO.File.Delete(KeepAwakeFlagPath);
            }
            catch (Exception) { }
        }

        // Hold off sleep only while actually hosting with the option on, so a laptop that is not
        // receiving still sleeps normally.
        void ApplyKeepAwake()
        {
            KeepAwake.Set(server != null && hostToggle.Checked && keepAwakeToggle.Checked);
        }

        // Host the pipe for the secure-desktop agent only while hosting and the service is installed.
        void ApplyLockAccess()
        {
            if (lockLink == null) return;
            if (hostToggle.Checked && LockInstall.IsInstalled()) lockLink.Start();
            else lockLink.Stop();
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
                licenseState.Text = Lang.L("مرخّص لـ ", "Licensed to ") + Identity.Sanitize(t.Company, 64) + Lang.L("\nمتبقٍ ", "\nRemaining ") + LicenseManager.DaysLeft + Lang.L(" يوماً", " days");
                licenseState.ForeColor = LicenseManager.ExpiresSoon ? Theme.Warning : Theme.Muted;
            }
            else
            {
                licenseState.Text = t != null && LicenseManager.DaysLeft <= 0 ? Lang.L("انتهت رخصة البرنامج", "The license has expired") : Lang.L("البرنامج غير مفعّل", "The program is not activated");
                licenseState.ForeColor = Theme.Danger;
            }
            if (valid != licenseWasValid)
            {
                licenseWasValid = valid;
                if (hostSessions.Count == 0) ApplyHostToggle();
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
            passwordButton.Text = AccessPassword.IsSet ? Lang.L("كلمة سر الوصول: مفعّلة", "Access password: on") : Lang.L("تعيين كلمة سر للوصول المباشر", "Set a password for direct access");
            passwordButton.Invalidate();
        }

        // Refused during a session: otherwise whoever is controlling this machine could
        // set a password and come back later without anyone approving it.
        void OpenPassword()
        {
            if (hostSessions.Count > 0)
            {
                MessageBox.Show(this, Lang.L("لا يمكن تغيير كلمة السر أثناء جلسة تحكم. أنهِ الجلسة أولاً.", "You can't change the password during a session. End the session first."), Lang.L("كلمة سر الوصول", "Access password"), MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1,
                    MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                return;
            }
            using (AccessPasswordForm form = new AccessPasswordForm()) form.ShowDialog(this);
            RefreshPasswordButton();
            ApplyLockAccess();
        }

        void OpenAndroid()
        {
            if (!LicenseManager.IsValid && !OpenActivation()) return;
            if (!LicenseManager.AndroidControlAllowed)
            {
                MessageBox.Show(this, Lang.L("ميزة التحكم بشاشات أندرويد غير مفعّلة لهذا الجهاز. تُفعَّل من لوحة التحكم.", "Android screen control isn't enabled for this device. It's enabled from the dashboard."), Lang.L("شاشة أندرويد", "Android screen"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading);
                return;
            }
            string prefill = addressBox.Box.Text.Trim();
            using (AndroidConnectForm c = new AndroidConnectForm(prefill))
                if (c.ShowDialog(this) == DialogResult.OK && c.Tool != null)
                    new AndroidForm(c.Tool, c.Host).Show(this);
        }

        void CopyAddress()
        {
            // On the internet relay, copy the device ID; otherwise copy every network address so the
            // person can pick whichever one reaches this machine, regardless of their Windows account.
            string payload;
            if (relay != null && relay.Id != null) payload = relay.Id;
            else if (localAddresses.Count > 0) payload = string.Join(Environment.NewLine, localAddresses.ToArray());
            else payload = mainIp.Text;
            try { Clipboard.SetText(payload); }
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
                SetStatus(Lang.L("اكتب عنوان IP أو معرّف الجهاز أولاً.", "Enter an IP address or device ID first."), Theme.Warning);
                addressBox.Box.Focus();
                return;
            }
            string host;
            int port;
            ViewerClient.ParseAddress(input, out host, out port);
            string deviceId = ViewerClient.ParseDeviceId(input);

            ViewerClient client = new ViewerClient();
            pending = client;
            connectButton.Text = Lang.L("إلغاء", "Cancel");
            connectButton.Kind = ButtonKind.Secondary;
            connectButton.Glyph = Theme.GlyphClose;
            connectButton.Invalidate();
            addressBox.Box.Enabled = false;
            SetStatus(Lang.L("جاري الاتصال بـ ", "Connecting to ") + host + " …", Theme.Muted);

            Thread t = new Thread(() =>
            {
                try
                {
                    Action<string> waiting = code => OnUi(() => SetStatus(Lang.L("بانتظار موافقة الشخص أمام الشاشة…\nرمز التحقق: ", "Waiting for the person at the screen to approve…\nVerification code: ") + code, Theme.Accent));
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
                    OnUi(() => ConnectFinished(client, null, input, Lang.L("حدث خطأ غير متوقع أثناء الاتصال.", "An unexpected error occurred while connecting.")));
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
                    Lang.L("هوية الجهاز ", "The device identity ") + host + Lang.L(" تغيرت عن آخر اتصال.\n\nقد يكون البرنامج أُعيد تثبيته، أو أن جهازاً آخر ينتحل هذا العنوان.\nرمز التحقق الجديد: ", " has changed since the last connection.\n\nThe program may have been reinstalled, or another device may be impersonating this address.\nNew verification code: ") + code + Lang.L("\n\nهل تريد المتابعة؟ تأكد من مطابقة الرمز مع الشخص أمام الشاشة.", "\n\nDo you want to continue? Make sure the code matches with the person at the screen."),
                    Lang.L("تحذير أمني", "Security warning"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2,
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
            connectButton.Text = Lang.L("اتصال", "Connect");
            connectButton.Kind = ButtonKind.Primary;
            connectButton.Glyph = Theme.GlyphConnect;
            connectButton.Invalidate();
            addressBox.Box.Enabled = true;

            if (conn == null)
            {
                SetStatus(error ?? "", Theme.Danger);
                return;
            }

            SetStatus(Lang.L("تمت الموافقة. الجلسة مفتوحة.", "Approved. The session is open."), Theme.Success);
            // Record outgoing sessions too, so the log shows both who connected to this machine and
            // the machines this person connected to.
            string peer = client.HostName != null && client.HostName.Length > 0 ? client.HostName + " (" + input + ")" : input;
            AppFiles.Log("بدأت جلسة تحكم إلى " + peer);
            RecentHosts.Touch(input);
            RefreshRecent();
            ViewerForm viewer = new ViewerForm(conn, input, client.HostName, client.Code);
            viewer.FormClosed += delegate { AppFiles.Log("انتهت جلسة التحكم إلى " + peer); };
            viewer.Show();
            viewer.Activate();
        }

        void SetStatus(string text, Color color)
        {
            connectStatus.Text = text;
            connectStatus.ForeColor = color;
        }

        PillButton MakeListTab(string text, int mode)
        {
            PillButton tab = new PillButton { Text = text, Kind = mode == ListSaved ? ButtonKind.Primary : ButtonKind.Secondary };
            tab.Font = Theme.UiBold(9f);
            tab.TabStop = false;
            tab.Click += delegate
            {
                listMode = mode;
                RefreshRecent();
            };
            return tab;
        }

        void RefreshRecent()
        {
            savedTab.Kind = listMode == ListSaved ? ButtonKind.Primary : ButtonKind.Secondary;
            recentTab.Kind = listMode == ListRecent ? ButtonKind.Primary : ButtonKind.Secondary;
            networkTab.Kind = listMode == ListNetwork ? ButtonKind.Primary : ButtonKind.Secondary;
            networkTab.Invalidate();
            savedTab.Invalidate();
            recentTab.Invalidate();

            recentPanel.SuspendLayout();
            recentPanel.Controls.Clear();
            if (listMode == ListSaved) FillSavedList();
            else if (listMode == ListRecent) FillRecentList();
            else FillNetworkList();
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
                ShowListHint(Lang.L("احفظ هنا عناوين الأجهزة التي تتصل بها كثيراً، باسم صاحبها أو الجهاز. اضغط \"إضافة\" للبدء.", "Save here the addresses you connect to often, by their owner or device. Press \"Add\" to start."));
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

                PillButton remove = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphDelete, GlyphSize = 9f, AccessibleName = Lang.L("حذف ", "Delete ") + contact.Name };
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
                ShowListHint(Lang.L("ستظهر هنا آخر العناوين التي اتصلت بها.", "The addresses you've recently connected to will appear here."));
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

                PillButton save = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphAdd, GlyphSize = 9f, AccessibleName = Lang.L("حفظ ", "Save ") + host };
                save.SetBounds(width - Theme.S(72), y, Theme.S(36), Theme.S(36));
                save.Click += delegate { EditContact(null, host); };
                recentPanel.Controls.Add(save);

                PillButton remove = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphClose, GlyphSize = 8f, AccessibleName = Lang.L("حذف ", "Delete ") + host };
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

        void FillNetworkList()
        {
            int mine = ++scanGeneration;
            ShowListHint(Lang.L("جاري البحث عن الأجهزة في هذه الشبكة…", "Searching for devices on this network…"));
            Thread t = new Thread(() =>
            {
                List<FoundDevice> found;
                try { found = DiscoveryClient.Scan(Proto.DiscoveryPort, DiscoveryClient.BroadcastTargets(), 1500, DiscoveryClient.OwnAddresses()); }
                catch (Exception ex)
                {
                    AppFiles.Log("تعذر البحث عن الأجهزة: " + ex.Message);
                    found = new List<FoundDevice>();
                }
                OnUi(() => ShowNetworkList(mine, found));
            });
            t.IsBackground = true;
            t.Name = "discovery-scan";
            t.Start();
        }

        void ShowNetworkList(int generation, List<FoundDevice> found)
        {
            // A newer search, or another tab, has taken over the list since this one started.
            if (generation != scanGeneration || listMode != ListNetwork) return;
            recentPanel.SuspendLayout();
            recentPanel.Controls.Clear();
            if (found.Count == 0)
            {
                ShowListHint(Lang.L("لم يُعثر على أجهزة. يظهر الجهاز هنا إذا كان البرنامج يعمل عليه مع تفعيل استقبال الطلبات، وسُمح له في الجدار الناري. اضغط \"في الشبكة\" للبحث مرة أخرى.", "No devices found. A device appears here if it's running the program with request-accepting on and allowed through the firewall. Press \"On network\" to search again."));
                recentPanel.ResumeLayout();
                return;
            }
            int width = ListWidth(found.Count);
            int y = 0;
            foreach (FoundDevice item in found)
            {
                FoundDevice d = item;
                // The mark keeps the address from reordering around an Arabic name.
                PillButton row = new PillButton { Text = d.Title + "\u200F   •   \u200E" + d.Address, Kind = ButtonKind.Secondary, Glyph = Theme.GlyphMonitor, GlyphSize = 10f };
                row.Font = Theme.Ui(10f);
                row.SetBounds(0, y, width - Theme.S(42), Theme.S(36));
                row.Click += delegate { ConnectTo(d.Port == Proto.DefaultPort ? d.Address : d.Address + ":" + d.Port); };
                recentPanel.Controls.Add(row);

                PillButton save = new PillButton { Kind = ButtonKind.Ghost, Glyph = Theme.GlyphAdd, GlyphSize = 9f, AccessibleName = Lang.L("حفظ ", "Save ") + d.Title };
                save.SetBounds(width - Theme.S(36), y, Theme.S(36), Theme.S(36));
                save.Click += delegate { EditContact(new Contact { Name = d.Title, Address = "" }, d.Address); };
                recentPanel.Controls.Add(save);
                y += Theme.S(40);
            }
            recentPanel.ResumeLayout();
        }

        // What this machine tells others on the network. Nothing at all unless it accepts requests.
        FoundDevice DescribeThisMachine()
        {
            if (server == null || !server.IsRunning || !LicenseManager.IsValid) return null;
            string id = relay != null ? relay.Id : null;
            return new FoundDevice
            {
                Name = Profile.DisplayName,
                User = Environment.UserName,
                Machine = Environment.MachineName,
                Port = Proto.DefaultPort,
                DeviceId = id ?? ""
            };
        }

        void RefreshNameButton()
        {
            string name = Profile.DisplayName;
            nameButton.Text = name.Length > 0 ? Lang.L("اسمك الظاهر للدعم الفني: ", "Your display name for support: ") + name : Lang.L("عيّن اسمك الظاهر للدعم الفني", "Set your display name for support");
            nameButton.Invalidate();
        }

        void EditDisplayName()
        {
            using (DisplayNameForm form = new DisplayNameForm()) form.ShowDialog(this);
            RefreshNameButton();
        }

        void EditContact(Contact existing, string address)
        {
            bool isEdit = existing != null && existing.Address.Length > 0;
            using (ContactForm form = new ContactForm(isEdit ? existing : null, address, existing != null ? existing.Name : null))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
            }
            listMode = ListSaved;
            // Deferred: this can run from a button in the list that is about to be rebuilt.
            BeginInvoke((Action)RefreshRecent);
        }

        void RemoveContact(Contact contact)
        {
            DialogResult answer = MessageBox.Show(this, "حذف \"" + contact.Name + "\" من العناوين المحفوظة؟", Lang.L("حذف عنوان", "Delete address"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2,
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
                hostState.Text = Lang.L("نسخة أخرى من البرنامج تستقبل الطلبات على هذا الجهاز.", "Another copy of the program is accepting requests on this device.");
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
                hostState.Text = Lang.L("المنفذ ", "Port ") + Proto.DefaultPort + Lang.L(" مستخدم من برنامج آخر.", " is used by another program.");
                return;
            }
            if (discovery != null)
            {
                if (hostToggle.Checked) discovery.Start();
                else discovery.Stop();
            }
            hostDot.Color = hostToggle.Checked ? Theme.Success : Theme.Muted;
            hostState.Text = hostToggle.Checked ? Lang.L("جاهز لاستقبال الطلبات على المنفذ ", "Ready to accept requests on port ") + Proto.DefaultPort : Lang.L("لا يستقبل طلبات تحكم", "Not accepting control requests");
            if (hostToggle.Checked && !LicenseManager.IsValid)
            {
                hostDot.Color = Theme.Warning;
                hostState.Text = Lang.L("لن تُقبل الطلبات قبل تفعيل البرنامج", "Requests won't be accepted until the program is activated");
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
            bool first = hostSessions.Count == 0;
            hostSessions.Add(session);
            hostTransfers[session] = new FileTransferWindow(session.Files, session.DisplayName + " (" + session.RemoteIp + ")");
            session.ClipboardFromViewer += text => OnUi(() => { if (hostClipboard != null) hostClipboard.Apply(text); });
            if (first)
            {
                sessionBar = new SessionBar(server.Desk, s =>
                {
                    FileTransferWindow w;
                    return hostTransfers.TryGetValue(s, out w) ? w : null;
                });
                sessionBar.Show();
                // What is copied here goes to everyone watching.
                hostClipboard = new ClipboardSync(text => { foreach (HostSession s in hostSessions.ToArray()) s.SendClipboard(text); });
                // A panic key for the local person, in case the pointer is being moved away from the bar.
                Native.RegisterHotKey(Handle, 1, 0x0007, 0x51);
            }
            else if (sessionBar.Paused)
            {
                session.SetPaused(true);
            }
            sessionBar.RefreshViewers();
            UpdateHostState();
            string who = session.DisplayName + " (" + session.RemoteIp + ")";
            tray.ShowBalloonTip(4000, first ? Lang.L("جلسة تحكم نشطة", "Active control session") : Lang.L("انضم شخص إلى الجلسة", "Someone joined the session"),
                (first ? Lang.L("يتحكم بهذه الشاشة: ", "Controlling this screen: ") : Lang.L("يشاهد الآن أيضاً: ", "Also watching now: ")) + who + Lang.L("\nللإنهاء الفوري: Ctrl + Alt + Shift + Q", "\nTo end immediately: Ctrl + Alt + Shift + Q"), ToolTipIcon.Warning);
        }

        void UpdateHostState()
        {
            hostDot.Color = Theme.Danger;
            hostState.Text = hostSessions.Count == 1
                ? Lang.L("جلسة تحكم نشطة من ", "Active control session from ") + hostSessions[0].RemoteIp
                : Lang.L("جلسة نشطة: ", "Active session: ") + hostSessions.Count + Lang.L(" متصلين", " connected");
        }

        void EndHostSession(HostSession session)
        {
            if (!hostSessions.Remove(session)) return;
            FileTransferWindow w;
            if (hostTransfers.TryGetValue(session, out w))
            {
                w.CloseForGood();
                hostTransfers.Remove(session);
            }
            if (hostSessions.Count > 0)
            {
                if (sessionBar != null) sessionBar.RefreshViewers();
                UpdateHostState();
                return;
            }
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
            ApplyHostToggle();
        }

        void EndAllHostSessions()
        {
            foreach (HostSession s in hostSessions.ToArray()) s.End();
        }

        protected override void DefWndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY) EndAllHostSessions();
            base.DefWndProc(ref m);
        }

        void OnDisplaysChanged(object sender, EventArgs e)
        {
            foreach (HostSession s in hostSessions.ToArray()) s.NotifyDisplaysChanged();
        }

        // Remember whether the machine is at the lock screen, so the host can tell a real lock apart
        // from a UAC elevation prompt (a secure desktop while the session is still unlocked).
        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) SessionWatch.Locked = true;
            else if (e.Reason == SessionSwitchReason.SessionUnlock) SessionWatch.Locked = false;
        }

        // ---- system integration ----

        void CheckFirewallAsync()
        {
            Thread t = new Thread(() =>
            {
                bool present = RuleExists(FirewallRule) && RuleExists(DiscoveryRule);
                OnUi(() =>
                {
                    firewallState.Text = present ? Lang.L("الجدار الناري: البرنامج مسموح له", "Firewall: the program is allowed") : Lang.L("الجدار الناري: قد يمنع الطلبات الواردة", "Firewall: it may block incoming requests");
                    firewallState.ForeColor = present ? Theme.Text : Theme.Warning;
                    firewallButton.Visible = !present;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        static bool RuleExists(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netsh", "advfirewall firewall show rule name=\"" + name + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process proc = Process.Start(psi))
                {
                    proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(5000);
                    return proc.HasExited && proc.ExitCode == 0;
                }
            }
            catch (Exception) { return false; }
        }

        static string RuleCommand(string name, string protocol, int port)
        {
            return "netsh advfirewall firewall add rule name=\"" + name + "\" dir=in action=allow protocol=" + protocol + " localport=" + port
                + " program=\"" + Application.ExecutablePath + "\" profile=domain,private remoteip=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";
        }

        // Inbound is limited to private address ranges on domain and private networks.
        // Only missing rules are added, all under one administrator prompt.
        void AddFirewallRule()
        {
            List<string> commands = new List<string>();
            if (!RuleExists(FirewallRule)) commands.Add(RuleCommand(FirewallRule, "TCP", Proto.DefaultPort));
            if (!RuleExists(DiscoveryRule)) commands.Add(RuleCommand(DiscoveryRule, "UDP", Proto.DiscoveryPort));
            if (commands.Count == 0)
            {
                CheckFirewallAsync();
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", commands.ToArray()));
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
            tray.Text = Lang.L("تحكّم", "Tahakkum");
            tray.Visible = true;
            ContextMenuStrip menu = Theme.MakeMenu();
            menu.ShowCheckMargin = false;
            menu.Items.Add(Lang.L("فتح", "Open"), null, delegate { RestoreFromTray(); });
            menu.Items.Add(Lang.L("خروج", "Exit"), null, delegate { ExitApp(); });
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
                    tray.ShowBalloonTip(3000, Lang.L("تحكّم ما زال يعمل", "Tahakkum is still running"), Lang.L("يستقبل طلبات التحكم من أيقونة شريط المهام. للإغلاق التام: زر أيمن ثم خروج.", "It's accepting control requests from the tray icon. To close completely: right-click then Exit."), ToolTipIcon.Info);
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            LicenseManager.Changed -= OnLicenseChanged;
            licenseTimer.Stop();
            licenseTimer.Dispose();
            EndAllHostSessions();
            KeepAwake.Set(false);
            if (lockLink != null) lockLink.Stop();
            if (discovery != null) discovery.Stop();
            if (relay != null) relay.Stop();
            if (server != null) server.Stop();
            tray.Visible = false;
            tray.Dispose();
            base.OnFormClosed(e);
        }
    }
}
