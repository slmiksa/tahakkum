// Verifies the saved-TV list (TvBook) and the session log, which hold the real logic of two of the
// new features. The lock-screen change can only be exercised on a real locked machine.
using System;
using System.Collections.Generic;
using System.IO;

namespace Tahakkum
{
    static class FeatureTest
    {
        static int failures;
        static void Check(string name, bool ok, string d = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (d.Length > 0 ? "  [" + d + "]" : ""));
            if (!ok) failures++;
        }

        static int Main()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tkm-feat-" + Guid.NewGuid().ToString("N"));
            AppFiles.DirOverride = dir;

            // ---- saved TV list ----
            Check("list starts empty", TvBook.Load().Count == 0);
            Check("a non-IP is rejected", TvBook.Save("x", "not-an-ip") != null);
            Check("an empty address is rejected", TvBook.Save("x", "") != null);
            Check("a valid IP saves", TvBook.Save("تلفزيون الاستقبال", "192.168.1.40") == null);
            Check("a valid IP:port saves", TvBook.Save("", "10.0.0.5:5555") == null);

            List<TvDevice> list = TvBook.Load();
            Check("both are listed", list.Count == 2, list.Count.ToString());
            Check("an empty name falls back to the address", list.Exists(t => t.Name == "10.0.0.5:5555"));
            Check("the name is kept with its address", list.Exists(t => t.Name == "تلفزيون الاستقبال" && t.Address == "192.168.1.40"));

            // Saving the same address again updates the entry instead of duplicating it.
            TvBook.Save("اسم جديد", "192.168.1.40");
            list = TvBook.Load();
            Check("a repeated address updates in place", list.Count == 2 && list.Exists(t => t.Address == "192.168.1.40" && t.Name == "اسم جديد"));
            Check("it is written to tv.txt", File.Exists(Path.Combine(dir, "tv.txt")));

            // A name with control characters cannot corrupt the tab-separated file.
            TvBook.Save("A\tB\nC", "172.16.0.9");
            TvDevice clean = TvBook.Load().Find(t => t.Address == "172.16.0.9");
            Check("a name is cleaned of tabs and line breaks", clean != null && !clean.Name.Contains("\t") && !clean.Name.Contains("\n"), clean != null ? clean.Name : "null");
            Check("that save did not merge rows", TvBook.Load().Count == 3);

            TvBook.Remove("192.168.1.40");
            list = TvBook.Load();
            Check("remove drops exactly that address", list.Count == 2 && !list.Exists(t => t.Address == "192.168.1.40"));

            // ---- session log keeps both directions ----
            AppFiles.Log("بدأت جلسة تحكم إلى 192.168.1.40");
            AppFiles.Log("بدأت جلسة تحكم من 203.0.113.9");
            string log = File.ReadAllText(AppFiles.LogPath);
            Check("the log records an outgoing session", log.Contains("بدأت جلسة تحكم إلى"));
            Check("the log records an incoming session", log.Contains("بدأت جلسة تحكم من"));

            try { Directory.Delete(dir, true); } catch (Exception) { }
            Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
