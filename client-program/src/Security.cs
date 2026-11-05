using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Tahakkum
{
    static class AppFiles
    {
        static readonly object logGate = new object();

        // Tests point this at a temporary folder so they never touch the real files.
        public static string DirOverride = null;

        // Follows the Windows user: session log, recent hosts, and the access password.
        public static string Dir
        {
            get
            {
                string d = DirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tahakkum");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        // Follows the machine: it stays the same when another employee signs in.
        public static string MachineDir
        {
            get
            {
                string d = DirOverride != null ? Path.Combine(DirOverride, "machine") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tahakkum");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string LogPath { get { return Path.Combine(Dir, "sessions.log"); } }

        public static void Log(string line)
        {
            try
            {
                lock (logGate)
                    File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }
    }

    static class Identity
    {
        const string PfxPassword = "tahakkum";

        // The host's TLS identity belongs to the machine, so the people connecting to it see
        // no identity warning when another employee signs in.
        public static X509Certificate2 LoadOrCreateCertificate()
        {
            string path = Path.Combine(AppFiles.MachineDir, "identity.bin");
            byte[] pfx = ReadPfx(path, DataProtectionScope.LocalMachine);
            if (pfx != null) return new X509Certificate2(pfx, PfxPassword, X509KeyStorageFlags.UserKeySet);

            // Older versions kept the identity per user; keep it so existing pins stay valid.
            pfx = ReadPfx(Path.Combine(AppFiles.Dir, "identity.bin"), DataProtectionScope.CurrentUser);
            if (pfx == null)
            {
                using (RSA rsa = RSA.Create(2048))
                {
                    CertificateRequest req = new CertificateRequest("CN=Tahakkum " + Environment.MachineName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    using (X509Certificate2 created = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10)))
                        pfx = created.Export(X509ContentType.Pkcs12, PfxPassword);
                }
            }
            try { File.WriteAllBytes(path, ProtectedData.Protect(pfx, null, DataProtectionScope.LocalMachine)); }
            catch (Exception ex) { AppFiles.Log("تعذر حفظ هوية الجهاز: " + ex.Message); }
            return new X509Certificate2(pfx, PfxPassword, X509KeyStorageFlags.UserKeySet);
        }

        static byte[] ReadPfx(string path, DataProtectionScope scope)
        {
            if (!File.Exists(path)) return null;
            try
            {
                byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(path), null, scope);
                using (X509Certificate2 c = new X509Certificate2(pfx, PfxPassword, X509KeyStorageFlags.UserKeySet))
                    return c.HasPrivateKey && c.NotAfter > DateTime.Now.AddDays(30) ? pfx : null;
            }
            catch (Exception) { return null; }
        }

        public static string Fingerprint(X509Certificate cert)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
        }

        // Short code shown on both ends. A man in the middle would present a different
        // certificate, so the two sides would show different codes.
        public static string VerificationCode(X509Certificate cert)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(cert.GetRawCertData());
                int n = (h[0] << 16 | h[1] << 8 | h[2]) % 1000000;
                string s = n.ToString("D6", CultureInfo.InvariantCulture);
                return s.Substring(0, 3) + " " + s.Substring(3);
            }
        }

        // Names in the hello come from the remote side: strip control and bidi characters
        // so they cannot visually spoof the consent dialog.
        public static string Sanitize(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder b = new StringBuilder();
            foreach (char c in s)
            {
                UnicodeCategory cat = char.GetUnicodeCategory(c);
                if (cat == UnicodeCategory.Control || cat == UnicodeCategory.Format || cat == UnicodeCategory.LineSeparator || cat == UnicodeCategory.ParagraphSeparator) continue;
                b.Append(c);
                if (b.Length >= maxLen) break;
            }
            return b.ToString();
        }
    }

    sealed class AccessKey
    {
        public byte[] Salt;
        public int Iterations;
        public byte[] Key;
    }

    // The optional password that opens a session without the approval prompt. It follows the
    // Windows user on purpose: when a machine moves to a new employee, the previous
    // employee's password stops working. Only a derived key is stored, under DPAPI.
    static class AccessPassword
    {
        public const int MinLength = 8;
        public const int MaxLength = 128;
        const int DefaultIterations = 200000;
        const int MinIterations = 10000;
        const int MaxIterations = 2000000;
        const string ProofLabel = "tahakkum-access-v1";

        static string PathName { get { return Path.Combine(AppFiles.Dir, "access.bin"); } }

        public static bool IsSet { get { return Load() != null; } }

        public static void Set(string password)
        {
            byte[] salt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            byte[] key = Derive(password, salt, DefaultIterations);
            byte[] plain = Pack.Build(w =>
            {
                w.Write(DefaultIterations);
                w.Write(salt);
                w.Write(key);
            });
            File.WriteAllBytes(PathName, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
        }

        public static void Clear()
        {
            File.Delete(PathName);
        }

        public static AccessKey Load()
        {
            try
            {
                if (!File.Exists(PathName)) return null;
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(PathName), null, DataProtectionScope.CurrentUser);
                using (BinaryReader r = Pack.Read(plain))
                    return new AccessKey { Iterations = r.ReadInt32(), Salt = r.ReadBytes(16), Key = r.ReadBytes(32) };
            }
            catch (Exception) { return null; }
        }

        public static bool ValidIterations(int n)
        {
            return n >= MinIterations && n <= MaxIterations;
        }

        public static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        // The proof covers the certificate the viewer actually saw. A relay or a machine in the
        // middle presents a different certificate, so a proof made for it fails at the real host.
        public static byte[] Proof(byte[] key, byte[] nonce, string fingerprint)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
                return hmac.ComputeHash(Pack.Build(w =>
                {
                    w.Write(ProofLabel);
                    w.Write(nonce);
                    w.Write(fingerprint);
                }));
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    // Trust-on-first-use pinning of each host's certificate.
    static class KnownHosts
    {
        static readonly object gate = new object();
        static string PathName { get { return Path.Combine(AppFiles.Dir, "known_hosts.txt"); } }

        static Dictionary<string, string> Load()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(PathName))
                    foreach (string line in File.ReadAllLines(PathName))
                    {
                        int i = line.LastIndexOf(' ');
                        if (i > 0) map[line.Substring(0, i)] = line.Substring(i + 1);
                    }
            }
            catch (Exception) { }
            return map;
        }

        public static string Get(string host)
        {
            lock (gate)
            {
                string v;
                return Load().TryGetValue(host, out v) ? v : null;
            }
        }

        public static void Set(string host, string fingerprint)
        {
            lock (gate)
            {
                Dictionary<string, string> map = Load();
                map[host] = fingerprint;
                try { File.WriteAllLines(PathName, map.Select(p => p.Key + " " + p.Value)); }
                catch (Exception) { }
            }
        }
    }

    sealed class Contact
    {
        public string Name;
        public string Address;
    }

    // The saved address list. It follows the Windows user, so each employee keeps their own.
    static class ContactBook
    {
        public const int MaxName = 60;
        public const int MaxAddress = 253;
        static readonly object gate = new object();

        static string PathName { get { return Path.Combine(AppFiles.Dir, "contacts.txt"); } }

        public static List<Contact> Load()
        {
            lock (gate) return Read();
        }

        static List<Contact> Read()
        {
            List<Contact> list = new List<Contact>();
            try
            {
                if (File.Exists(PathName))
                    foreach (string line in File.ReadAllLines(PathName, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0 && tab < line.Length - 1)
                            list.Add(new Contact { Name = line.Substring(0, tab), Address = line.Substring(tab + 1) });
                    }
            }
            catch (Exception) { }
            return list;
        }

        // A device ID is stored as plain digits. Anything else must look like an IP or host name, with an optional port.
        public static string NormalizeAddress(string input)
        {
            string s = (input ?? "").Trim();
            string digits = s.Replace(" ", "").Replace("-", "");
            if (digits.Length == 9 && digits.All(char.IsDigit)) return digits;
            if (s.Length == 0 || s.Length > MaxAddress) return null;
            foreach (char c in s)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == ':'))) return null;
            return s;
        }

        // Returns an error for the person, or null when saved. Saving an address
        // that is already listed updates that entry rather than adding a second one.
        public static string Save(string name, string address, string replacing)
        {
            string cleanName = Identity.Sanitize(name, MaxName).Trim();
            string cleanAddress = NormalizeAddress(address);
            if (cleanName.Length == 0) return "اكتب اسم صاحب العنوان أو الجهاز.";
            if (cleanAddress == null) return "اكتب عنوان IP صحيحاً أو معرّفاً من 9 أرقام.";
            lock (gate)
            {
                List<Contact> list = Read();
                list.RemoveAll(c => string.Equals(c.Address, cleanAddress, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.Address, replacing, StringComparison.OrdinalIgnoreCase));
                list.Add(new Contact { Name = cleanName, Address = cleanAddress });
                return Write(list) ? null : "تعذر حفظ العنوان على هذا الجهاز.";
            }
        }

        public static void Remove(string address)
        {
            lock (gate)
            {
                List<Contact> list = Read();
                list.RemoveAll(c => string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase));
                Write(list);
            }
        }

        static bool Write(List<Contact> list)
        {
            try
            {
                File.WriteAllLines(PathName, list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Name + "\t" + c.Address), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException) return false;
                throw;
            }
        }
    }

    static class RecentHosts
    {
        static string PathName { get { return Path.Combine(AppFiles.Dir, "recent.txt"); } }

        public static List<string> Load()
        {
            try
            {
                if (File.Exists(PathName))
                    return File.ReadAllLines(PathName).Where(l => l.Trim().Length > 0).Take(5).ToList();
            }
            catch (Exception) { }
            return new List<string>();
        }

        public static void Touch(string host)
        {
            List<string> list = Load();
            list.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, host);
            Save(list);
        }

        public static void Remove(string host)
        {
            List<string> list = Load();
            list.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
            Save(list);
        }

        static void Save(List<string> list)
        {
            try { File.WriteAllLines(PathName, list.Take(5)); }
            catch (Exception) { }
        }
    }
}
