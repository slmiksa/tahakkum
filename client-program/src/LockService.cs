using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace Tahakkum
{
    // A tiny Windows service running as SYSTEM. Its only job is to keep one agent process alive
    // inside the active console session, so that when the machine is locked something with the
    // right privileges is there to show and drive the secure desktop. It never touches the
    // screen or input itself, and it carries no network code.
    sealed class LockService : ServiceBase
    {
        public const string Name = "TahakkumAccess";

        volatile bool running;
        Thread worker;
        int agentPid;

        public LockService()
        {
            ServiceName = Name;
            CanHandleSessionChangeEvent = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            running = true;
            worker = new Thread(KeepAgentAlive);
            worker.IsBackground = true;
            worker.Name = "lock-service";
            worker.Start();
            LockLaunch.Log("service started");
        }

        protected override void OnStop()
        {
            running = false;
            KillAgent();
            LockLaunch.Log("service stopped");
        }

        protected override void OnShutdown()
        {
            running = false;
            KillAgent();
        }

        void KeepAgentAlive()
        {
            while (running)
            {
                try
                {
                    uint session = Native.WTSGetActiveConsoleSessionId();
                    if (session == 0xFFFFFFFF)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    int pid;
                    IntPtr handle = LockLaunch.SpawnInConsoleSession("/lockagent", out pid);
                    if (handle == IntPtr.Zero)
                    {
                        Thread.Sleep(3000);
                        continue;
                    }
                    agentPid = pid;

                    // Watch the agent. Replace it if it exits, or if the console session changes.
                    while (running)
                    {
                        uint waited = Native.WaitForSingleObject(handle, 2000);
                        if (waited != Native.WAIT_TIMEOUT) break;
                        if (Native.WTSGetActiveConsoleSessionId() != session) { KillAgent(); break; }
                    }
                    Native.CloseHandle(handle);
                    agentPid = 0;
                }
                catch (Exception ex)
                {
                    LockLaunch.Log("service loop error: " + ex.Message);
                    Thread.Sleep(3000);
                }
                if (running) Thread.Sleep(800);
            }
        }

        void KillAgent()
        {
            int pid = agentPid;
            agentPid = 0;
            if (pid == 0) return;
            try { Process.GetProcessById(pid).Kill(); }
            catch (Exception) { }
        }
    }
}
