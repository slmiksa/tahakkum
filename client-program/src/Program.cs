using System;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Windows.Forms;

namespace Tahakkum
{
    static class Program
    {
        static Mutex instance;

        public static Icon AppIcon;

        [STAThread]
        static void Main(string[] args)
        {
            // The lock-screen helper runs as the service or the SYSTEM agent, with no window.
            if (args.Length > 0)
            {
                string verb = args[0].ToLowerInvariant();
                if (verb == "/lockservice") { System.ServiceProcess.ServiceBase.Run(new LockService()); return; }
                if (verb == "/lockagent") { LockAgent.Run(); return; }
                if (verb == "/installlock") { Environment.Exit(LockInstall.RunInstall()); }
                if (verb == "/removelock") { Environment.Exit(LockInstall.RunRemove()); }
            }

            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (Exception) { Native.SetProcessDPIAware(); }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => AppFiles.Log("خطأ في الواجهة: " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => AppFiles.Log("خطأ غير معالج: " + e.ExceptionObject);

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                Theme.Scale = g.DpiX / 96f;

            MainForm.ShowMessage = Native.RegisterWindowMessage("Tahakkum.ShowMainWindow");
            AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            Lang.Load();
            LicenseManager.Load();

            // For IT departments deploying to many machines. Exit code 0 means activated.
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "/activate", StringComparison.OrdinalIgnoreCase))
                    Environment.Exit(ActivateFromCommandLine(args[i + 1]));

            LicenseManager.StartRefreshing();

            bool startInTray = args.Any(a => string.Equals(a, "/tray", StringComparison.OrdinalIgnoreCase));
            string connect = null;
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], "/connect", StringComparison.OrdinalIgnoreCase)) connect = args[i + 1];

            bool first = AcquireSingleInstance(args);
            if (!first && connect == null)
            {
                Native.PostMessage(new IntPtr(0xFFFF), MainForm.ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            // A second copy started only to open a connection does not host.
            HostServer server = null;
            if (first)
            {
                try
                {
                    X509Certificate2 cert = Identity.LoadOrCreateCertificate();
                    server = new HostServer(cert);
                }
                catch (Exception ex)
                {
                    AppFiles.Log("تعذر تجهيز هوية الجهاز: " + ex.Message);
                }
            }

            Application.Run(new MainForm(server, startInTray && connect == null, connect));
        }

        static int ActivateFromCommandLine(string code)
        {
            try
            {
                LicenseManager.Activate(code);
                return 0;
            }
            catch (LicenseFailure f)
            {
                AppFiles.Log("فشل التفعيل من سطر الأوامر: " + f.Message);
                return 1;
            }
        }

        static bool AcquireSingleInstance(string[] args)
        {
            bool created = false;
            bool restarting = args.Any(a => string.Equals(a, "/elevated-restart", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "/restarted", StringComparison.OrdinalIgnoreCase));
            int attempts = restarting ? 20 : 1;
            for (int i = 0; i < attempts && !created; i++)
            {
                instance = new Mutex(true, @"Local\Tahakkum.SingleInstance", out created);
                if (!created)
                {
                    instance.Dispose();
                    instance = null;
                    if (i + 1 < attempts) Thread.Sleep(250);
                }
            }
            return created;
        }

        public static void ReleaseSingleInstance()
        {
            if (instance == null) return;
            try { instance.ReleaseMutex(); instance.Dispose(); }
            catch (Exception) { }
            instance = null;
        }

        // Saves the language choice and restarts the program so every window is rebuilt in it.
        public static void SwitchLanguage(bool english)
        {
            if (english == Lang.English) return;
            Lang.Save(english);
            ReleaseSingleInstance();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "/restarted") { UseShellExecute = true }); }
            catch (Exception) { }
            Application.Exit();
        }
    }
}
