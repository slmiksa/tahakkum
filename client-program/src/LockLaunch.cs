using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Tahakkum
{
    // Shared helpers for the lock-screen feature: a machine-wide log, launching this same program
    // as SYSTEM inside the interactive console session, and reading the name of a desktop.
    static class LockLaunch
    {
        public const string DataPipe = "Tahakkum-Lock-Data";
        public const string SecureDesktop = "Winlogon";

        static readonly object logGate = new object();

        public static string LogPath { get { return Path.Combine(AppFiles.MachineDir, "access.log"); } }

        public static void Log(string line)
        {
            try
            {
                lock (logGate)
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        // Launches "<this exe> <arguments>" as SYSTEM in the active console session, on the default
        // desktop. Only works when the caller already runs as SYSTEM (the service). Returns the
        // process handle, which the caller waits on and then closes; zero on failure.
        public static IntPtr SpawnInConsoleSession(string arguments, out int pid)
        {
            pid = 0;
            uint session = Native.WTSGetActiveConsoleSessionId();
            if (session == 0xFFFFFFFF) return IntPtr.Zero;

            IntPtr token = IntPtr.Zero, dup = IntPtr.Zero, env = IntPtr.Zero;
            try
            {
                if (!Native.OpenProcessToken(Native.GetCurrentProcess(), Native.MAXIMUM_ALLOWED, out token)) return IntPtr.Zero;
                if (!Native.DuplicateTokenEx(token, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out dup)) return IntPtr.Zero;

                uint s = session;
                if (!Native.SetTokenInformation(dup, Native.TokenSessionId, ref s, sizeof(uint)))
                {
                    Log("SetTokenInformation(session) failed: " + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                Native.CreateEnvironmentBlock(out env, dup, false);

                Native.STARTUPINFO si = new Native.STARTUPINFO();
                si.cb = Marshal.SizeOf(typeof(Native.STARTUPINFO));
                si.lpDesktop = "winsta0\\default";
                si.dwFlags = (int)Native.STARTF_USESHOWWINDOW;
                si.wShowWindow = Native.SW_HIDE;

                string exe = Application.ExecutablePath;
                string cmd = "\"" + exe + "\" " + arguments;
                Native.PROCESS_INFORMATION pi;
                bool ok = Native.CreateProcessAsUser(dup, exe, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Native.CREATE_UNICODE_ENVIRONMENT | Native.CREATE_NO_WINDOW, env, null, ref si, out pi);
                if (!ok)
                {
                    Log("CreateProcessAsUser failed: " + Marshal.GetLastWin32Error());
                    return IntPtr.Zero;
                }
                if (pi.hThread != IntPtr.Zero) Native.CloseHandle(pi.hThread);
                pid = pi.dwProcessId;
                return pi.hProcess;
            }
            catch (Exception ex)
            {
                Log("SpawnInConsoleSession error: " + ex.Message);
                return IntPtr.Zero;
            }
            finally
            {
                if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
                if (dup != IntPtr.Zero) Native.CloseHandle(dup);
                if (token != IntPtr.Zero) Native.CloseHandle(token);
            }
        }

        // The name of a desktop handle, e.g. "Default" or "Winlogon".
        public static string DesktopName(IntPtr desktop)
        {
            try
            {
                byte[] buffer = new byte[256];
                int needed;
                if (!Native.GetUserObjectInformation(desktop, Native.UOI_NAME, buffer, buffer.Length, out needed)) return "";
                string name = Encoding.Unicode.GetString(buffer, 0, Math.Max(0, Math.Min(buffer.Length, needed)));
                int nul = name.IndexOf('\0');
                if (nul >= 0) name = name.Substring(0, nul);
                return name;
            }
            catch (Exception) { return ""; }
        }
    }
}
