using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Tahakkum
{
    sealed class LicenseFailure : Exception
    {
        public LicenseFailure(string message) : base(message) { }
    }

    sealed class LicenseSettings
    {
        // Filled in by supabase-backend\tools\setup.mjs. While these are empty the program runs
        // without any license check, so a build is never locked out before the server exists.
        public const string DefaultApiUrl = "https://hosmafqkcmcmdpwhzodu.supabase.co/functions/v1";
        public const string DefaultPublicX = "gwa0NDvAhJjfVOLKoesy69ANZ9lICafoL7o3weB3Llk";
        public const string DefaultPublicY = "VNtGdByJ7gHASSJCJpgGenAsq6tEJJoYlcbSDN-x-8A";

        public string ApiUrl = DefaultApiUrl;
        public string PublicX = DefaultPublicX;
        public string PublicY = DefaultPublicY;
    }

    // The license belongs to the machine, not to the Windows user, so it stays valid when
    // another employee signs in. The machine is recognized by its Windows MachineGuid.
    static class LicenseManager
    {
        const int WarnDays = 14;
        const int ClockToleranceHours = 24;
        const int RefreshHours = 24;
        const int RetryMinutes = 60;
        const string DeviceLabel = "tahakkum-device-v1:";

        static readonly object gate = new object();
        static LicenseToken current;
        static string secret;

        // For tests.
        public static LicenseSettings Settings = new LicenseSettings();
        public static string DeviceOverride = null;
        public static Func<DateTime> Clock = () => DateTime.UtcNow;

        public static event Action Changed;

        public static bool Required { get { return Settings.ApiUrl.Length > 0 && Settings.PublicX.Length > 0; } }

        static string PathName { get { return Path.Combine(AppFiles.MachineDir, "license.bin"); } }

        public static LicenseToken Current { get { lock (gate) return current; } }

        public static string Device
        {
            get
            {
                if (DeviceOverride != null) return DeviceOverride;
                string guid = "";
                try
                {
                    using (RegistryKey hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (RegistryKey k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                        guid = k == null ? "" : Convert.ToString(k.GetValue("MachineGuid"));
                }
                catch (Exception) { }
                // Only a hash leaves this machine.
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(DeviceLabel + guid))).Replace("-", "");
            }
        }

        // The signed issue time also catches a clock set back to before the last server contact.
        public static bool IsValid
        {
            get
            {
                if (!Required) return true;
                LicenseToken t = Current;
                if (t == null || t.Device != Device) return false;
                DateTime now = Clock();
                return now < t.ExpiresUtc && now >= t.IssuedUtc.AddHours(-ClockToleranceHours);
            }
        }

        public static string Token
        {
            get
            {
                LicenseToken t = Current;
                return t == null ? "" : t.Raw;
            }
        }

        public static int DaysLeft
        {
            get
            {
                LicenseToken t = Current;
                return t == null ? 0 : (int)Math.Ceiling((t.ExpiresUtc - Clock()).TotalDays);
            }
        }

        public static bool ExpiresSoon { get { return Required && IsValid && DaysLeft <= WarnDays; } }

        // Whether this machine may control Android TV screens over IP. The dashboard turns the
        // feature on per company and per device; the client learns it from the token
        // (`AndroidControl` below). A local opt-in file is honoured too, for a device the owner
        // tests before the dashboard entitlement reaches it.
        public static bool AndroidControlAllowed
        {
            get
            {
                if (Required && !IsValid) return false;
                LicenseToken t = Current;
                if (t != null && t.AndroidControl) return true;
                try { return System.IO.File.Exists(System.IO.Path.Combine(AppFiles.MachineDir, "android-control.on")); }
                catch (Exception) { return false; }
            }
        }

        // Whether this machine may transfer files without the person at the machine approving each
        // transfer. Gated per company and per device from the dashboard, carried in the token
        // (`AutoFileTransfer`). A local opt-in file is honoured too, for testing before the
        // entitlement reaches the device.
        public static bool AutoFileTransferAllowed
        {
            get
            {
                if (Required && !IsValid) return false;
                LicenseToken t = Current;
                if (t != null && t.AutoFileTransfer) return true;
                try { return System.IO.File.Exists(System.IO.Path.Combine(AppFiles.MachineDir, "auto-files.on")); }
                catch (Exception) { return false; }
            }
        }

        public static void Load()
        {
            if (!Required) return;
            try
            {
                if (!File.Exists(PathName)) return;
                string[] lines = File.ReadAllLines(PathName);
                if (lines.Length < 2) return;
                LicenseToken t = LicenseToken.Parse(lines[0], Settings.PublicX, Settings.PublicY);
                if (t == null) return;
                string s = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(lines[1]), null, DataProtectionScope.LocalMachine));
                lock (gate)
                {
                    current = t;
                    secret = s;
                }
            }
            catch (Exception ex)
            {
                AppFiles.Log("تعذرت قراءة ملف الرخصة: " + ex.Message);
            }
        }

        static void Save(LicenseToken t, string s)
        {
            string path = PathName;
            bool created = !File.Exists(path);
            File.WriteAllLines(path, new[]
            {
                t.Raw,
                Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(s), null, DataProtectionScope.LocalMachine))
            });
            if (!created) return;
            // Whichever employee is signed in must be able to renew it. The token is signed,
            // so being able to edit this file gains nothing.
            try
            {
                FileSecurity acl = File.GetAccessControl(path);
                acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
                File.SetAccessControl(path, acl);
            }
            catch (Exception ex)
            {
                AppFiles.Log("تعذر ضبط صلاحيات ملف الرخصة: " + ex.Message);
            }
        }

        static void Raise()
        {
            Action h = Changed;
            if (h != null) h();
        }

        // ---- talking to the licensing server ----

        [DataContract]
        sealed class ApiRequest
        {
            [DataMember(Name = "code", EmitDefaultValue = false)] public string Code;
            [DataMember(Name = "code_id", EmitDefaultValue = false)] public string CodeId;
            [DataMember(Name = "secret", EmitDefaultValue = false)] public string Secret;
            [DataMember(Name = "device")] public string Device;
            [DataMember(Name = "machine")] public string Machine;
        }

        // Filled in by the JSON reader, which the compiler cannot see.
#pragma warning disable 0649
        [DataContract]
        sealed class ApiResponse
        {
            [DataMember(Name = "ok")] public bool Ok;
            [DataMember(Name = "error")] public string Error;
            [DataMember(Name = "token")] public string Token;
            [DataMember(Name = "secret")] public string Secret;
        }
#pragma warning restore 0649

        static ApiResponse Call(string function, ApiRequest body)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Settings.ApiUrl.TrimEnd('/') + "/" + function);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            using (Stream s = req.GetRequestStream())
                new DataContractJsonSerializer(typeof(ApiRequest)).WriteObject(s, body);
            try
            {
                using (WebResponse resp = req.GetResponse())
                    return ReadResponse(resp);
            }
            catch (WebException ex)
            {
                // An error status still carries a JSON body worth reading.
                if (ex.Response == null) throw;
                using (WebResponse resp = ex.Response)
                    return ReadResponse(resp);
            }
        }

        static ApiResponse ReadResponse(WebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
                return (ApiResponse)new DataContractJsonSerializer(typeof(ApiResponse)).ReadObject(s);
        }

        public static void Activate(string code)
        {
            if (!Required) throw new LicenseFailure(Lang.L("هذه النسخة لا تتطلب التفعيل.", "This build doesn't require activation."));
            ApiResponse r;
            try
            {
                r = Call("activate", new ApiRequest { Code = code, Device = Device, Machine = Environment.MachineName });
            }
            catch (Exception ex)
            {
                if (!(ex is WebException || ex is IOException || ex is SerializationException)) throw;
                throw new LicenseFailure(Lang.L("تعذر الوصول إلى خادم التفعيل. تأكد من اتصالك بالإنترنت، وأن جدار الشركة الناري يسمح به.", "Couldn't reach the activation server. Check your internet connection and that the company firewall allows it."));
            }
            if (!r.Ok) throw new LicenseFailure(ErrorText(r.Error));

            LicenseToken t = LicenseToken.Parse(r.Token ?? "", Settings.PublicX, Settings.PublicY);
            if (t == null || t.Device != Device) throw new LicenseFailure(Lang.L("رد غير صالح من خادم التفعيل.", "Invalid response from the activation server."));
            lock (gate)
            {
                current = t;
                secret = r.Secret;
            }
            try { Save(t, r.Secret); }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is CryptographicException)) throw;
                throw new LicenseFailure(Lang.L("تم التفعيل لكن تعذر حفظ الرخصة على هذا الجهاز: ", "Activated, but the license couldn't be saved on this device: ") + ex.Message);
            }
            AppFiles.Log("تم تفعيل البرنامج لشركة " + t.Company + " حتى " + t.ExpiresUtc.ToString("yyyy-MM-dd"));
            Raise();
        }

        static string ErrorText(string error)
        {
            switch (error)
            {
                case "invalid_code": return Lang.L("كود التفعيل غير صحيح. تأكد من كتابته كما هو في الملف.", "The activation code is incorrect. Make sure it matches the file exactly.");
                case "revoked": return Lang.L("تم إلغاء هذا الكود. تواصل مع مسؤول النظام في شركتك.", "This code has been revoked. Contact your company's system administrator.");
                case "expired": return Lang.L("انتهت مدة هذا الكود.", "This code's term has ended.");
                case "other_device": return Lang.L("هذا الكود مفعّل على جهاز آخر. لنقله تواصل مع مسؤول النظام.", "This code is active on another device. To move it, contact the system administrator.");
                case "rate_limited": return Lang.L("محاولات خاطئة كثيرة. حاول بعد 10 دقائق.", "Too many failed attempts. Try again after 10 minutes.");
                default: return Lang.L("تعذر إتمام التفعيل. حاول لاحقاً.", "Couldn't complete activation. Try again later.");
            }
        }

        // Returns false when the server could not be reached.
        public static bool Refresh()
        {
            LicenseToken t;
            string s;
            lock (gate)
            {
                t = current;
                s = secret;
            }
            if (!Required || t == null || s == null) return true;

            ApiResponse r;
            try
            {
                r = Call("refresh", new ApiRequest { CodeId = t.CodeId, Secret = s, Device = Device, Machine = Environment.MachineName });
            }
            catch (Exception ex)
            {
                if (!(ex is WebException || ex is IOException || ex is SerializationException)) throw;
                return false;
            }

            if (r.Ok)
            {
                LicenseToken fresh = LicenseToken.Parse(r.Token ?? "", Settings.PublicX, Settings.PublicY);
                if (fresh == null || fresh.Device != t.Device) return false;
                lock (gate) current = fresh;
                try { Save(fresh, s); }
                catch (Exception ex) { AppFiles.Log("تعذر حفظ الرخصة المحدثة: " + ex.Message); }
                Raise();
                return true;
            }
            if (r.Error == "released" || r.Error == "revoked")
            {
                AppFiles.Log("أُلغيت رخصة هذا الجهاز من الخادم (" + r.Error + ")");
                lock (gate)
                {
                    current = null;
                    secret = null;
                }
                try { File.Delete(PathName); }
                catch (Exception) { }
                Raise();
            }
            // An expired license stays on file: renewing the batch brings it back without a new code.
            return true;
        }

        public static void StartRefreshing()
        {
            if (!Required) return;
            Thread t = new Thread(() =>
            {
                Thread.Sleep(5000);
                while (true)
                {
                    bool ok = false;
                    try { ok = Refresh(); }
                    catch (Exception ex) { AppFiles.Log("خطأ في تحديث الرخصة: " + ex); }
                    Thread.Sleep(TimeSpan.FromMinutes(ok ? RefreshHours * 60 : RetryMinutes));
                }
            });
            t.IsBackground = true;
            t.Name = "license-refresh";
            t.Start();
        }
    }
}
