using System;
using System.Security.Cryptography;
using System.Text;

namespace Tahakkum
{
    // A license signed by the licensing server. Shared with the relay server, which checks
    // it with the same public key.
    sealed class LicenseToken
    {
        public string Raw;
        public string CodeId;
        public string Device;
        public string Company;
        public DateTime ExpiresUtc;
        public DateTime IssuedUtc;
        // Entitlement flags (optional 7th field). 'a' = may control Android TV screens over IP.
        public bool AndroidControl;

        // Returns null unless the signature is valid for this public key.
        public static LicenseToken Parse(string raw, string publicX, string publicY)
        {
            try
            {
                string[] parts = raw.Trim().Split('.');
                if (parts.Length != 2) return null;
                byte[] data = FromBase64Url(parts[0]);
                ECParameters key = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = FromBase64Url(publicX), Y = FromBase64Url(publicY) }
                };
                using (ECDsa ec = ECDsa.Create(key))
                {
                    if (!ec.VerifyData(data, FromBase64Url(parts[1]), HashAlgorithmName.SHA256)) return null;
                }

                string[] fields = Encoding.UTF8.GetString(data).Split('|');
                // Six fields originally; a seventh flags field was appended later, so older tokens
                // (and the relay, which shares this file) keep working.
                if ((fields.Length != 6 && fields.Length != 7) || fields[0] != "v1") return null;
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return new LicenseToken
                {
                    Raw = raw.Trim(),
                    CodeId = fields[1],
                    Device = fields[2],
                    ExpiresUtc = epoch.AddSeconds(long.Parse(fields[3])),
                    IssuedUtc = epoch.AddSeconds(long.Parse(fields[4])),
                    Company = Encoding.UTF8.GetString(FromBase64Url(fields[5])),
                    AndroidControl = fields.Length >= 7 && fields[6].IndexOf('a') >= 0
                };
            }
            catch (Exception ex)
            {
                if (ex is FormatException || ex is CryptographicException || ex is ArgumentException || ex is OverflowException) return null;
                throw;
            }
        }

        static byte[] FromBase64Url(string s)
        {
            string b = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
        }
    }
}
