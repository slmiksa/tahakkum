using System;
using System.Runtime.InteropServices;

namespace Tahakkum
{
    // Extra Windows calls used only by the lock-screen access feature: launching a process as
    // SYSTEM inside the interactive console session, and following the input desktop so the same
    // thread can reach the secure (Winlogon) desktop while the machine is locked.
    static partial class Native
    {
        public const uint MAXIMUM_ALLOWED = 0x02000000;
        public const int TokenPrimary = 1;
        public const int SecurityImpersonation = 2;
        public const int TokenSessionId = 12;

        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        public const uint CREATE_NO_WINDOW = 0x08000000;
        public const uint STARTF_USESHOWWINDOW = 0x00000001;
        public const short SW_HIDE = 0;

        // Desktop rights needed to read the pixels and inject input.
        public const uint DESKTOP_RIGHTS = 0x01FF;
        public const int UOI_NAME = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(int pid, out uint sessionId);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        public const uint WAIT_TIMEOUT = 0x102;

        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(IntPtr token, int tokenInfoClass, ref uint info, int length);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(IntPtr token, string appName, string cmdLine, IntPtr procAttrs, IntPtr threadAttrs,
            bool inherit, uint flags, IntPtr env, string curDir, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION procInfo);

        [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool DestroyEnvironmentBlock(IntPtr env);

        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetUserObjectInformation(IntPtr obj, int index, byte[] info, int length, out int needed);
    }
}
