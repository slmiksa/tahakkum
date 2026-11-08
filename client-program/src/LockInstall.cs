using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Windows.Forms;

namespace Tahakkum
{
    // Installs, removes, and checks the lock-screen access service. Installing and removing need
    // administrator rights, so the user interface relaunches the program elevated to do them.
    static class LockInstall
    {
        public const string DisplayName = "تحكّم - الوصول إلى شاشة القفل";
        const string Description = "يتيح للدعم الفني المصرّح له بكلمة السر رؤية شاشة قفل ويندوز وتسجيل الدخول عن بُعد.";

        public static bool IsInstalled()
        {
            try
            {
                foreach (ServiceController sc in ServiceController.GetServices())
                    if (string.Equals(sc.ServiceName, LockService.Name, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch (Exception) { }
            return false;
        }

        // Runs the actual service control commands. Assumes the process is already elevated.
        public static int RunInstall()
        {
            string exe = Application.ExecutablePath;
            string bin = "binPath= \"\\\"" + exe + "\\\" /lockservice\"";
            int r = Sc("create " + LockService.Name + " " + bin + " start= auto");
            if (r != 0 && r != 1073) return r;          // 1073 = already exists
            Sc("config " + LockService.Name + " DisplayName= \"" + DisplayName + "\"");
            Sc("description " + LockService.Name + " \"" + Description + "\"");
            Sc("failure " + LockService.Name + " reset= 60 actions= restart/5000/restart/5000/restart/5000");
            return Sc("start " + LockService.Name);
        }

        public static int RunRemove()
        {
            Sc("stop " + LockService.Name);
            return Sc("delete " + LockService.Name);
        }

        static int Sc(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("sc.exe", arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    LockLaunch.Log("sc " + arguments + " -> " + p.ExitCode + (output.Trim().Length > 0 ? " | " + output.Trim() : ""));
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                LockLaunch.Log("sc " + arguments + " failed: " + ex.Message);
                return -1;
            }
        }

        // Relaunches this program elevated to install or remove, and waits for it to finish.
        public static bool RequestElevated(string verb)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, verb);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit(60000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch (Exception)
            {
                return false;       // the user dismissed the elevation prompt, or it failed
            }
        }
    }
}
