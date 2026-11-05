// Runs a real session in one process, on its own port, and sends files through the real windows.
// The viewer stays in view-only mode, so nothing moves the mouse or types on this machine.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Tahakkum
{
    static class FileTransferUiTest
    {
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        const int Port = 47899;
        const string OfferTitle = "طلب إرسال ملفات";
        const string TransferTitle = "نقل الملفات";
        const string BarTitle = "جلسة تحكم نشطة";

        static int failures;
        static string shots;
        static string root;
        static Form anchor;
        static HostSession session;
        static FileTransferWindow hostTransfers;
        static SessionBar bar;
        static ViewerForm viewer;
        static FileTransferWindow viewerTransfers;
        static FileTransferService viewerFiles;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        static object Field(object o, string name)
        {
            return o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(o);
        }

        static AutomationElement FindWindow(string title, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            PropertyCondition cond = new PropertyCondition(AutomationElement.NameProperty, title);
            while (DateTime.UtcNow < end)
            {
                AutomationElement e = AutomationElement.RootElement.FindFirst(TreeScope.Children, cond);
                if (e != null) return e;
                Thread.Sleep(100);
            }
            return null;
        }

        static AutomationElement FindButton(AutomationElement window, string name)
        {
            return window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        }

        static void Press(AutomationElement button)
        {
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }

        static void Shot(IntPtr h, string file, int maxHeight = int.MaxValue)
        {
            RECT r;
            GetWindowRect(h, out r);
            using (Bitmap b = new Bitmap(r.R - r.L, r.B - r.T))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    IntPtr dc = g.GetHdc();
                    PrintWindow(h, dc, 2);
                    g.ReleaseHdc(dc);
                }
                using (Bitmap strip = b.Clone(new Rectangle(0, 0, b.Width, Math.Min(maxHeight, b.Height)), b.PixelFormat))
                    strip.Save(Path.Combine(shots, file), ImageFormat.Png);
            }
        }

        static bool WaitFile(string path, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end && !File.Exists(path)) Thread.Sleep(50);
            return File.Exists(path);
        }

        static string MakeFile(string name, int size)
        {
            string path = Path.Combine(root, name);
            byte[] data = new byte[size];
            new Random(size).NextBytes(data);
            File.WriteAllBytes(path, data);
            return path;
        }

        static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs));
        }

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            shots = args[0];
            Directory.CreateDirectory(shots);
            root = Path.Combine(Path.GetTempPath(), "tahakkum-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppFiles.DirOverride = Path.Combine(root, "app");
            LicenseManager.Settings = new LicenseSettings { ApiUrl = "" };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                Theme.Scale = g.DpiX / 96f;
            Program.AppIcon = SystemIcons.Application;

            anchor = new Form { ShowInTaskbar = false, Opacity = 0, Size = new Size(1, 1) };
            anchor.Shown += delegate
            {
                Thread t = new Thread(() =>
                {
                    try { Run(); }
                    catch (Exception ex) { Check("unexpected exception", false, ex.ToString()); }
                    anchor.BeginInvoke((Action)Application.Exit);
                });
                t.IsBackground = true;
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            };
            Application.Run(anchor);
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            AppFiles.DirOverride = null;
            try { Directory.Delete(root, true); } catch (Exception) { }
            return failures;
        }

        static void Run()
        {
            HostServer server = new HostServer(Identity.LoadOrCreateCertificate());
            ManualResetEvent hostReady = new ManualResetEvent(false);
            server.ConsentNeeded += r => r.Approve();
            server.SessionStarted += s => anchor.Invoke((Action)delegate
            {
                session = s;
                s.Files.SaveFolder = Path.Combine(root, "host");
                hostTransfers = new FileTransferWindow(s.Files, "viewer (127.0.0.1)");
                bar = new SessionBar(s, hostTransfers);
                bar.Show();
                hostReady.Set();
            });
            server.Start(Port);

            ViewerClient client = new ViewerClient();
            Connection conn = client.Connect("127.0.0.1", Port, code => { }, code => true, (code, failed) => "");
            anchor.Invoke((Action)delegate
            {
                viewer = new ViewerForm(conn, "127.0.0.1", client.HostName, client.Code);
                viewerFiles = (FileTransferService)Field(viewer, "files");
                viewerFiles.SaveFolder = Path.Combine(root, "viewer");
                viewerTransfers = (FileTransferWindow)Field(viewer, "transfers");
                viewer.GetType().GetMethod("ToggleViewOnly", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(viewer, null);
                viewer.Show();
            });
            Check("session starts", hostReady.WaitOne(8000));
            Thread.Sleep(2000);

            AutomationElement barElement = FindWindow(BarTitle, 3000);
            Check("session bar has a send button", barElement != null && FindButton(barElement, "إرسال ملفات") != null);
            anchor.Invoke((Action)delegate
            {
                Check("viewer toolbar has a send button", ((Control)Field(viewer, "tools")).Controls.Cast<Control>().Any(c => c.Text == "إرسال ملفات"));
                Check("viewer surface accepts dropped files", ((Control)Field(viewer, "surface")).AllowDrop);
                Shot(viewer.Handle, "viewer-toolbar.png", 100);
            });

            // Viewer to host, through the real prompt.
            string big = MakeFile("تقرير المبيعات 2026.bin", 20 * 1024 * 1024);
            anchor.BeginInvoke((Action)delegate { viewerTransfers.SendPaths(new[] { big }); });
            AutomationElement prompt = FindWindow(OfferTitle, 5000);
            Check("offer prompt appears on the receiver", prompt != null);
            if (prompt == null) return;
            AutomationElement accept = FindButton(prompt, "قبول");
            AutomationElement reject = FindButton(prompt, "رفض");
            Check("accept is disarmed at first", accept != null && !accept.Current.IsEnabled);
            // Windows may refuse to bring the prompt forward while another program is in use.
            if (GetForegroundWindow() == new IntPtr(prompt.Current.NativeWindowHandle))
                Check("reject has initial focus", reject != null && reject.Current.HasKeyboardFocus);
            else
                Console.WriteLine("SKIP  reject focus (prompt is not the foreground window)");
            Check("accept never holds the initial focus", accept != null && !accept.Current.HasKeyboardFocus);
            Thread.Sleep(1800);
            Check("accept arms after delay", accept.Current.IsEnabled);
            Shot(new IntPtr(prompt.Current.NativeWindowHandle), "offer.png");
            Press(accept);
            string got = Path.Combine(session.Files.SaveFolder, Path.GetFileName(big));
            Check("file arrives on the host", WaitFile(got, 20000));
            Thread.Sleep(500);
            Check("host copy is identical", File.Exists(got) && Hash(got) == Hash(big));
            ShotTransfers("transfer-done");

            // Host to viewer: rejected first, then accepted.
            string small = MakeFile("notes.bin", 900 * 1024);
            anchor.BeginInvoke((Action)delegate { hostTransfers.SendPaths(new[] { small }); });
            prompt = FindWindow(OfferTitle, 5000);
            Check("host offer reaches the viewer", prompt != null);
            if (prompt == null) return;
            Press(FindButton(prompt, "رفض"));
            Thread.Sleep(800);
            Check("rejecting writes nothing", !File.Exists(Path.Combine(viewerFiles.SaveFolder, "notes.bin")));
            ShotTransfers("transfer-rejected");

            anchor.BeginInvoke((Action)delegate { hostTransfers.SendPaths(new[] { small }); });
            prompt = FindWindow(OfferTitle, 5000);
            Thread.Sleep(1800);
            Press(FindButton(prompt, "قبول"));
            got = Path.Combine(viewerFiles.SaveFolder, "notes.bin");
            Check("file arrives on the viewer", WaitFile(got, 15000));
            Thread.Sleep(500);
            Check("viewer copy is identical", File.Exists(got) && Hash(got) == Hash(small));

            // Pausing blocks new offers from the viewer without any prompt.
            session.SetPaused(true);
            anchor.BeginInvoke((Action)delegate { viewerTransfers.SendPaths(new[] { small }); });
            Check("paused host shows no prompt", FindWindow(OfferTitle, 1200) == null);
            session.SetPaused(false);
            Thread.Sleep(500);

            // The picker opens from the no-activate session bar and stays above it.
            anchor.BeginInvoke((Action)delegate { hostTransfers.PickAndSend(bar); });
            string dialogTitle = "اختر الملفات المراد إرسالها إلى viewer (127.0.0.1)";
            AutomationElement dialog = null;
            DateTime end = DateTime.UtcNow.AddSeconds(6);
            PropertyCondition named = new PropertyCondition(AutomationElement.NameProperty, dialogTitle);
            while (dialog == null && DateTime.UtcNow < end)
            {
                dialog = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, named);
                Thread.Sleep(150);
            }
            Check("file picker opens from the session bar", dialog != null);
            if (dialog != null) ((WindowPattern)dialog.GetCurrentPattern(WindowPattern.Pattern)).Close();
            Thread.Sleep(500);

            if (barElement != null) Shot(new IntPtr(barElement.Current.NativeWindowHandle), "sessionbar-files.png");

            // Ending the session closes the windows it opened.
            anchor.Invoke((Action)delegate { viewer.Close(); });
            Thread.Sleep(1000);
            Check("session ends with the viewer", conn.IsClosed);
            anchor.Invoke((Action)delegate
            {
                bar.Close();
                hostTransfers.CloseForGood();
            });
            server.Stop();
            Thread.Sleep(300);
            Check("no transfer window left behind", FindWindow(TransferTitle, 300) == null);
        }

        static void ShotTransfers(string prefix)
        {
            int i = 0;
            foreach (AutomationElement e in AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, TransferTitle)))
                Shot(new IntPtr(e.Current.NativeWindowHandle), prefix + "-" + (++i) + ".png");
        }
    }
}
