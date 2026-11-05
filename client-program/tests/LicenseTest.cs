// Drives the desktop program's license code against supabase-backend\tests\dev-server.ts, which runs
// the real database migration and function logic. All files go to a temporary folder.
//
//   LicenseTest <path to the supabase-backend folder>
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

namespace Tahakkum
{
    static class LicenseTest
    {
        const int HostPort = 47896;

        [DataContract]
        sealed class ServerInfo
        {
            [DataMember(Name = "url")] public string Url;
            [DataMember(Name = "x")] public string X;
            [DataMember(Name = "y")] public string Y;
        }

        [DataContract]
        sealed class Issued
        {
            [DataMember(Name = "batch")] public string Batch;
            [DataMember(Name = "codes")] public string[] Codes;
        }

        static int failures;
        static string url;
        static string root;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (!string.IsNullOrEmpty(detail) ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        static string Post(string path, string json)
        {
            using (WebClient web = new WebClient())
            {
                web.Encoding = Encoding.UTF8;
                web.Headers[HttpRequestHeader.ContentType] = "application/json";
                return web.UploadString(url + path, json);
            }
        }

        static Issued Issue(int quantity, int months)
        {
            string body = Post("/test/issue", "{\"quantity\":" + quantity + ",\"months\":" + months + "}");
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                return (Issued)new DataContractJsonSerializer(typeof(Issued)).ReadObject(ms);
        }

        static void Batch(string action, string batch, string extra = "")
        {
            Post("/test/" + action, "{\"batch\":\"" + batch + "\"" + extra + "}");
        }

        static string ActivateError(string code)
        {
            try
            {
                LicenseManager.Activate(code);
                return null;
            }
            catch (LicenseFailure f)
            {
                return f.Message;
            }
        }

        // A fresh machine from the program's point of view: no license loaded, none on disk.
        static void NewMachine(string device)
        {
            LicenseManager.DeviceOverride = device;
            string file = Path.Combine(AppFiles.MachineDir, "license.bin");
            if (File.Exists(file)) File.Delete(file);
            ReloadFromDisk();
        }

        static void ReloadFromDisk()
        {
            typeof(LicenseManager).GetField("current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, null);
            typeof(LicenseManager).GetField("secret", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).SetValue(null, null);
            LicenseManager.Load();
        }

        static X509Certificate2 NewCertificate(string name)
        {
            using (RSA rsa = RSA.Create(2048))
            {
                CertificateRequest req = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 c = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1)))
                    return new X509Certificate2(c.Export(X509ContentType.Pkcs12, "x"), "x", X509KeyStorageFlags.UserKeySet);
            }
        }

        static int Main(string[] args)
        {
            root = Path.Combine(Path.GetTempPath(), "tahakkum-license-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            AppFiles.DirOverride = root;

            ProcessStartInfo psi = new ProcessStartInfo("node", "tests/dev-server.ts");
            psi.WorkingDirectory = args[0];
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            Process node = Process.Start(psi);
            try
            {
                string line = node.StandardOutput.ReadLine();
                ServerInfo info;
                using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(line)))
                    info = (ServerInfo)new DataContractJsonSerializer(typeof(ServerInfo)).ReadObject(ms);
                url = info.Url;

                NotConfigured();
                LicenseManager.Settings = new LicenseSettings { ApiUrl = info.Url, PublicX = info.X, PublicY = info.Y };
                Activation();
                Expiry();
                Refreshing();
                Tampering(info);
                Gating();
                Relay(info);
            }
            catch (Exception ex)
            {
                Check("unexpected exception", false, ex.ToString());
            }
            finally
            {
                try { node.Kill(); } catch (Exception) { }
            }
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            AppFiles.DirOverride = null;
            try { Directory.Delete(root, true); } catch (Exception) { }
            return failures;
        }

        static void NotConfigured()
        {
            LicenseManager.Settings = new LicenseSettings { ApiUrl = "", PublicX = "" };
            Check("a build with no server configured needs no license", !LicenseManager.Required && LicenseManager.IsValid);
        }

        static void Activation()
        {
            string device = new string('A', 64);
            NewMachine(device);
            Check("unactivated machine is invalid", LicenseManager.Required && !LicenseManager.IsValid);

            string error = ActivateError("TKM-00000-00000-00000-00000");
            Check("wrong code is refused", error != null && error.Contains("غير صحيح"), error);
            Check("still invalid after a wrong code", !LicenseManager.IsValid);

            Issued issued = Issue(2, 6);
            error = ActivateError(issued.Codes[0].ToLowerInvariant().Replace("-", " "));
            Check("code activates, typed loosely", error == null, error);
            Check("license is valid", LicenseManager.IsValid);
            Check("company name arrives intact", LicenseManager.Current.Company == "شركة الاختبار", LicenseManager.Current.Company);
            int days = LicenseManager.DaysLeft;
            Check("about six months remain", days >= 178 && days <= 186, days.ToString());

            ReloadFromDisk();
            Check("license survives a restart", LicenseManager.IsValid && LicenseManager.Current.Company == "شركة الاختبار");

            string file = Path.Combine(AppFiles.MachineDir, "license.bin");
            Check("license is in the machine folder, not the user's", file.StartsWith(AppFiles.MachineDir) && File.Exists(file));
            bool usersCanRenew = false;
            foreach (System.Security.AccessControl.FileSystemAccessRule rule in File.GetAccessControl(file).GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                if (((System.Security.Principal.SecurityIdentifier)rule.IdentityReference).IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinUsersSid) && (rule.FileSystemRights & System.Security.AccessControl.FileSystemRights.Modify) == System.Security.AccessControl.FileSystemRights.Modify) usersCanRenew = true;
            Check("the next employee can renew the license file", usersCanRenew);
            Check("the file does not hold the secret in the clear", !File.ReadAllText(file).Contains("secret"));

            // Copying the file to another machine gives nothing.
            LicenseManager.DeviceOverride = new string('B', 64);
            Check("copied license is invalid on another machine", !LicenseManager.IsValid);
            error = ActivateError(issued.Codes[0]);
            Check("the code refuses a second machine", error != null && error.Contains("جهاز آخر"), error);

            LicenseManager.DeviceOverride = device;
            Check("the original machine stays valid", LicenseManager.IsValid);
            error = ActivateError(issued.Codes[0]);
            Check("the same machine can activate again", error == null, error);

            // A real machine identity is stable and is a hash.
            LicenseManager.DeviceOverride = null;
            string real = LicenseManager.Device;
            Check("real device id is a stable hash", real.Length == 64 && real == LicenseManager.Device, real.Substring(0, 8) + "…");
        }

        static void Expiry()
        {
            NewMachine(new string('C', 64));
            Issued issued = Issue(1, 6);
            LicenseManager.Activate(issued.Codes[0]);

            Func<DateTime> realClock = LicenseManager.Clock;
            DateTime expires = LicenseManager.Current.ExpiresUtc;
            LicenseManager.Clock = () => expires.AddDays(-30);
            Check("not close to expiry at 30 days", LicenseManager.IsValid && !LicenseManager.ExpiresSoon);
            LicenseManager.Clock = () => expires.AddDays(-5);
            Check("warning shows in the last two weeks", LicenseManager.IsValid && LicenseManager.ExpiresSoon);
            LicenseManager.Clock = () => expires.AddSeconds(1);
            Check("expires at the batch date", !LicenseManager.IsValid);

            // Works offline: no server involved in any of the checks above.
            DateTime issuedAt = LicenseManager.Current.IssuedUtc;
            LicenseManager.Clock = () => issuedAt.AddDays(-3);
            Check("setting the clock back does not revive it", !LicenseManager.IsValid);
            LicenseManager.Clock = () => issuedAt.AddHours(-2);
            Check("a clock slightly off is tolerated", LicenseManager.IsValid);
            LicenseManager.Clock = realClock;

            Issued old = Issue(1, 6);
            Batch("expire", old.Batch);
            NewMachine(new string('D', 64));
            string error = ActivateError(old.Codes[0]);
            Check("an expired batch cannot activate", error != null && error.Contains("انتهت"), error);
        }

        static void Refreshing()
        {
            NewMachine(new string('E', 64));
            Issued issued = Issue(1, 6);
            LicenseManager.Activate(issued.Codes[0]);
            DateTime first = LicenseManager.Current.ExpiresUtc;

            Batch("extend", issued.Batch, ",\"months\":12");
            Check("refresh reaches the server", LicenseManager.Refresh());
            Check("an extension arrives with no new code", LicenseManager.Current.ExpiresUtc > first.AddMonths(11), LicenseManager.Current.ExpiresUtc.ToString("yyyy-MM-dd"));
            ReloadFromDisk();
            Check("the extended license is saved", LicenseManager.Current.ExpiresUtc > first.AddMonths(11));

            // Renewing after it ran out brings the machine back with no new code.
            Batch("expire", issued.Batch);
            LicenseManager.Refresh();
            Check("expired license stays on file", LicenseManager.Current != null);
            Batch("extend", issued.Batch, ",\"months\":3");
            LicenseManager.Refresh();
            Check("late renewal revives the machine", LicenseManager.IsValid);

            Batch("release", issued.Batch);
            LicenseManager.Refresh();
            Check("releasing removes the license", !LicenseManager.IsValid && LicenseManager.Current == null);
            Check("releasing removes the file", !File.Exists(Path.Combine(AppFiles.MachineDir, "license.bin")));

            NewMachine(new string('F', 64));
            Check("the released code works on the replacement", ActivateError(issued.Codes[0]) == null);
            Batch("revoke", issued.Batch);
            LicenseManager.Refresh();
            Check("revoking removes the license", !LicenseManager.IsValid);

            // The server being down must never cost a machine its license.
            NewMachine(new string('1', 64));
            Issued live = Issue(1, 6);
            LicenseManager.Activate(live.Codes[0]);
            LicenseSettings good = LicenseManager.Settings;
            LicenseManager.Settings = new LicenseSettings { ApiUrl = "http://127.0.0.1:9", PublicX = good.PublicX, PublicY = good.PublicY };
            Check("unreachable server is reported", !LicenseManager.Refresh());
            Check("unreachable server keeps the license", LicenseManager.IsValid);
            string error = ActivateError("TKM-00000-00000-00000-00000");
            Check("unreachable server gives a clear message", error != null && error.Contains("خادم التفعيل"), error);
            LicenseManager.Settings = good;
        }

        static void Tampering(ServerInfo info)
        {
            string raw = LicenseManager.Token;
            Check("real token parses", LicenseToken.Parse(raw, info.X, info.Y) != null);

            string[] parts = raw.Split('.');
            string payload = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(parts[0])));
            string[] fields = payload.Split('|');
            fields[3] = "4102444800"; // Year 2100.
            string forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("|", fields))).Replace('+', '-').Replace('/', '_').TrimEnd('=') + "." + parts[1];
            Check("a changed expiry fails the signature", LicenseToken.Parse(forged, info.X, info.Y) == null);
            Check("garbage is refused", LicenseToken.Parse("abc", info.X, info.Y) == null && LicenseToken.Parse("a.b", info.X, info.Y) == null && LicenseToken.Parse("", info.X, info.Y) == null);

            using (ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                byte[] sig = other.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256);
                string selfMade = parts[0] + "." + Convert.ToBase64String(sig).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                Check("a token signed with another key is refused", LicenseToken.Parse(selfMade, info.X, info.Y) == null);
            }

            // Editing the saved file is the same as having no license.
            string file = Path.Combine(AppFiles.MachineDir, "license.bin");
            string[] lines = File.ReadAllLines(file);
            string saved = lines[0];
            lines[0] = forged;
            File.WriteAllLines(file, lines);
            ReloadFromDisk();
            Check("an edited license file is rejected", !LicenseManager.IsValid);
            lines[0] = saved;
            File.WriteAllLines(file, lines);
            ReloadFromDisk();
            Check("the untouched file loads again", LicenseManager.IsValid);
        }

        static string Pad(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            return b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
        }

        static void Gating()
        {
            HostServer server = new HostServer(NewCertificate("gated-host"));
            HostSession session = null;
            int asked = 0;
            server.ConsentNeeded += r => { Interlocked.Increment(ref asked); r.Approve(); };
            server.SessionStarted += s => session = s;
            server.Start(HostPort);
            Func<string> dial = () =>
            {
                try
                {
                    Connection c = new ViewerClient().Connect("127.0.0.1", HostPort, code => { }, code => true, (code, failed) => "");
                    c.Close();
                    return null;
                }
                catch (ConnectFailure f) { return f.Message; }
            };

            string live = LicenseManager.DeviceOverride;
            string result = dial();
            Check("a licensed host accepts", result == null && asked == 1, result);
            for (int i = 0; i < 50 && session == null; i++) Thread.Sleep(50);
            if (session != null) session.End();
            Thread.Sleep(600);

            // Another device hash means this copy of the license does not apply here.
            LicenseManager.DeviceOverride = new string('9', 64);
            asked = 0;
            result = dial();
            Check("an unlicensed host refuses before any prompt", result != null && result.Contains("غير مفعّل") && asked == 0, result);
            LicenseManager.DeviceOverride = live;
            server.Stop();
        }

        static void Relay(ServerInfo info)
        {
            X509Certificate2 relayCert = NewCertificate("relay");
            RelayServer relayServer = new RelayServer(relayCert, Path.Combine(root, "relay"), info.X, info.Y);
            relayServer.Start(0);
            RelayConfig.Override = new RelayConfig { Host = "127.0.0.1", Port = relayServer.Port, Pin = Identity.Fingerprint(relayCert) };
            RelayLink link = new RelayLink(NewCertificate("relayed"), (t, s, ip) => { });

            link.Start();
            for (int i = 0; i < 100 && link.Id == null; i++) Thread.Sleep(50);
            Check("a licensed machine gets an id", link.Id != null, link.Id);
            link.Stop();

            // Same relay, no token to present: refused, and it never gets an id.
            string device = LicenseManager.DeviceOverride;
            NewMachine(new string('8', 64));
            RelayLink unlicensed = new RelayLink(NewCertificate("unlicensed"), (t, s, ip) => { });
            unlicensed.Start();
            Thread.Sleep(1500);
            Check("an unlicensed machine gets no id", unlicensed.Id == null);
            unlicensed.Stop();

            relayServer.Stop();
            LicenseManager.DeviceOverride = device;
        }
    }
}
