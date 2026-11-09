using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Tahakkum
{
    // Drives an Android TV over the network with Google's adb tool, the same path scrcpy uses:
    // adb runs as the shell user, which is allowed to read the screen and inject input, so we get
    // full control without rooting the device or installing anything on it. One instance per device.
    sealed class AdbTool
    {
        // Only a plain IPv4, optionally with a port. Anything else is refused, so nothing the user
        // types can turn into another adb argument or a shell command.
        static readonly Regex HostPattern = new Regex(@"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?::(\d{1,5}))?$");

        readonly string serial;

        AdbTool(string serial) { this.serial = serial; }

        public string Serial { get { return serial; } }

        // Finds adb.exe next to the program (vendor\platform-tools or beside the exe), then the PATH.
        public static string Path()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                string[] candidates = new[]
                {
                    System.IO.Path.Combine(dir, "platform-tools\\adb.exe"),
                    System.IO.Path.Combine(dir, "vendor\\platform-tools\\adb.exe"),
                    System.IO.Path.Combine(dir, "adb.exe")
                };
                foreach (string c in candidates)
                    if (File.Exists(c)) return c;
            }
            catch (Exception) { }
            return "adb"; // fall back to the PATH; Available() confirms it actually runs
        }

        public static bool Available()
        {
            string outText, errText;
            return Run(Path(), "version", 8000, out outText, out errText) == 0 && outText.IndexOf("Android Debug Bridge", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool ValidHost(string input)
        {
            Match m = HostPattern.Match((input ?? "").Trim());
            if (!m.Success) return false;
            for (int i = 1; i <= 4; i++)
                if (int.Parse(m.Groups[i].Value) > 255) return false;
            if (m.Groups[5].Success)
            {
                int port = int.Parse(m.Groups[5].Value);
                if (port < 1 || port > 65535) return false;
            }
            return true;
        }

        public enum ConnectResult { Connected, Unauthorized, Unreachable, NoAdb, BadAddress }

        // Connects to "ip" or "ip:port" (default 5555). The first time, the TV shows an
        // authorization prompt that the person there must accept once.
        public static ConnectResult Connect(string input, out AdbTool tool)
        {
            tool = null;
            string host = (input ?? "").Trim();
            if (!ValidHost(host)) return ConnectResult.BadAddress;
            if (host.IndexOf(':') < 0) host += ":5555";
            if (!Available()) return ConnectResult.NoAdb;

            string outText, errText;
            Run(Path(), "connect " + host, 12000, out outText, out errText);
            string reply = (outText + " " + errText).ToLowerInvariant();
            if (reply.Contains("connected")) // "connected to" or "already connected to"
            {
                AdbTool t = new AdbTool(host);
                if (t.State() == "device") { tool = t; return ConnectResult.Connected; }
                if (t.State() == "unauthorized") return ConnectResult.Unauthorized;
                return ConnectResult.Unreachable;
            }
            return ConnectResult.Unreachable;
        }

        public string State()
        {
            string outText, errText;
            if (Run(Path(), "-s " + serial + " get-state", 8000, out outText, out errText) != 0) return (errText + outText).ToLowerInvariant().Contains("unauthorized") ? "unauthorized" : "offline";
            return outText.Trim().ToLowerInvariant();
        }

        // A PNG of the current screen, or null if it could not be captured.
        public byte[] Screencap()
        {
            return RunBinary("-s " + serial + " exec-out screencap -p", 8000);
        }

        public void Tap(int x, int y)
        {
            Fire("-s " + serial + " shell input tap " + x + " " + y);
        }

        public void Swipe(int x1, int y1, int x2, int y2, int ms)
        {
            Fire("-s " + serial + " shell input swipe " + x1 + " " + y1 + " " + x2 + " " + y2 + " " + ms);
        }

        public void Key(int keycode)
        {
            Fire("-s " + serial + " shell input keyevent " + keycode);
        }

        // Types text. Spaces and the shell's special characters are escaped so only text is sent.
        public void Text(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            StringBuilder sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c == ' ') { sb.Append("%s"); continue; }
                if (c < 32 || c == '"') continue; // control chars and quotes are dropped
                if ("()<>|;&*\\~`$'".IndexOf(c) >= 0) sb.Append('\\');
                sb.Append(c);
            }
            if (sb.Length == 0) return;
            Fire("-s " + serial + " shell input text \"" + sb + "\"");
        }

        public void Disconnect()
        {
            string outText, errText;
            Run(Path(), "disconnect " + serial, 6000, out outText, out errText);
        }

        void Fire(string args)
        {
            string outText, errText;
            Run(Path(), args, 8000, out outText, out errText);
        }

        static int Run(string exe, string args, int timeoutMs, out string stdout, out string stderr)
        {
            stdout = ""; stderr = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    stdout = p.StandardOutput.ReadToEnd();
                    stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return -2; }
                    return p.ExitCode;
                }
            }
            catch (Exception) { return -1; }
        }

        static byte[] RunBinary(string args, int timeoutMs)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path(), args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                using (MemoryStream ms = new MemoryStream())
                {
                    p.StandardOutput.BaseStream.CopyTo(ms);
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } return null; }
                    if (p.ExitCode != 0) return null;
                    byte[] data = ms.ToArray();
                    return data.Length > 8 ? data : null;
                }
            }
            catch (Exception) { return null; }
        }
    }
}
