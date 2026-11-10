// Verifies the "stuck port" fix: a socket left behind on the control or discovery port must no
// longer block a fresh bind (which previously needed a reboot). Uses ports in the 478xx range so it
// never clashes with a running Tahakkum on 47800/47801.
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Tahakkum
{
    static class PortReuseTest
    {
        static int failures;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        static UdpClient BindReuse(int port)
        {
            UdpClient c = new UdpClient();
            c.ExclusiveAddressUse = false;
            c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            c.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            return c;
        }

        static int Main()
        {
            // 1) The bug: the old plain bind is blocked while the port is still held.
            int p1 = 47811;
            UdpClient oldA = new UdpClient(new IPEndPoint(IPAddress.Any, p1));
            bool oldBlocks = false;
            try { UdpClient oldB = new UdpClient(new IPEndPoint(IPAddress.Any, p1)); oldB.Close(); }
            catch (SocketException) { oldBlocks = true; }
            oldA.Close();
            Check("a held discovery port blocks the old plain bind (reproduces the bug)", oldBlocks);

            // 2) The fix: with address reuse, a second socket binds the still-held port.
            int p2 = 47812;
            UdpClient newA = BindReuse(p2);
            bool newWorks = false; string err = "";
            try { UdpClient newB = BindReuse(p2); newWorks = true; newB.Close(); }
            catch (SocketException ex) { err = ex.SocketErrorCode.ToString(); }
            newA.Close();
            Check("address reuse lets the discovery port re-bind while still held (the fix)", newWorks, err);

            // 3) The real HostServer code path: a listener left on the control port no longer blocks
            //    starting a fresh one (what "enable accept" does, previously stuck until a reboot).
            string dir = Path.Combine(Path.GetTempPath(), "tkm-portfix-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            AppFiles.DirOverride = dir;
            System.Security.Cryptography.X509Certificates.X509Certificate2 cert = Identity.LoadOrCreateCertificate();
            int p3 = 47813;
            HostServer first = new HostServer(cert);
            first.Start(p3);
            Check("the control-port listener starts", first.IsRunning);
            bool secondBinds = false; string herr = "";
            try { HostServer second = new HostServer(cert); second.Start(p3); secondBinds = second.IsRunning; second.Stop(); }
            catch (SocketException ex) { herr = ex.SocketErrorCode.ToString(); }
            first.Stop();
            Check("a held control port no longer blocks a new listener (the real fix)", secondBinds, herr);

            try { Directory.Delete(dir, true); } catch (Exception) { }
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
