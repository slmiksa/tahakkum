using System;
using System.IO;
using System.Windows.Forms;

namespace Tahakkum
{
    static class LangTest
    {
        static int f;
        static void C(string n, bool ok) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + n); if (!ok) f++; }

        static int Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tkm-lang-" + Guid.NewGuid().ToString("N"));
            AppFiles.DirOverride = dir;

            Lang.English = false;
            C("default is Arabic", !Lang.English);
            C("L returns Arabic by default", Lang.L("اتصال", "Connect") == "اتصال");
            C("direction is RTL in Arabic", Lang.Rtl == RightToLeft.Yes);

            Lang.Save(true);
            C("Save turns English on", Lang.English);
            C("L returns English now", Lang.L("اتصال", "Connect") == "Connect");
            C("direction is LTR in English", Lang.Rtl == RightToLeft.No);
            C("choice is written to lang.txt", File.Exists(Path.Combine(dir, "lang.txt")));

            Lang.English = false;      // forget in memory
            Lang.Load();               // and read back from disk
            C("Load restores English from disk", Lang.English);

            Lang.Save(false);
            Lang.English = true;
            Lang.Load();
            C("Load restores Arabic from disk", !Lang.English);

            try { Directory.Delete(dir, true); } catch (Exception) { }
            Console.WriteLine(f == 0 ? "ALL PASSED" : f + " FAILED");
            return f == 0 ? 0 : 1;
        }
    }
}
