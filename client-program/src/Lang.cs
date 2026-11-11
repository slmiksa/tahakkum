using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace Tahakkum
{
    // Two-language interface. L(ar, en) returns the text for the chosen language. The choice is saved
    // and applied when the program (re)starts, so every window is built in one language from the start
    // — simpler and more reliable than re-laying out live controls.
    static class Lang
    {
        public static bool English;   // false = Arabic (the default)

        static string PathName { get { return Path.Combine(AppFiles.Dir, "lang.txt"); } }

        public static void Load()
        {
            try { if (File.Exists(PathName)) English = File.ReadAllText(PathName, Encoding.UTF8).Trim() == "en"; }
            catch (Exception) { }
        }

        public static void Save(bool english)
        {
            English = english;
            try { File.WriteAllText(PathName, english ? "en" : "ar", new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        // The string for the current language.
        public static string L(string ar, string en) { return English ? en : ar; }

        // Form text direction for the current language.
        public static RightToLeft Rtl { get { return English ? RightToLeft.No : RightToLeft.Yes; } }

        // Flips a right-anchored label alignment to the left for English, so titles and captions read
        // from the correct side in each language.
        public static ContentAlignment Near(ContentAlignment arabicRight)
        {
            if (!English) return arabicRight;
            switch (arabicRight)
            {
                case ContentAlignment.TopRight: return ContentAlignment.TopLeft;
                case ContentAlignment.MiddleRight: return ContentAlignment.MiddleLeft;
                case ContentAlignment.BottomRight: return ContentAlignment.BottomLeft;
                case ContentAlignment.TopLeft: return ContentAlignment.TopRight;
                case ContentAlignment.MiddleLeft: return ContentAlignment.MiddleRight;
                case ContentAlignment.BottomLeft: return ContentAlignment.BottomRight;
                default: return arabicRight;
            }
        }
    }
}
