// Verifies the lock-screen typing fix without a locked machine: the Unicode character path in
// InputInjector (held-key tracking and release), and that the Msg.Key payload stays readable both
// with the new trailing character field (new viewer) and without it (old viewer).
using System;
using System.Collections.Generic;
using System.IO;

namespace Tahakkum
{
    static class LockInputTest
    {
        static int failures;

        static void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (detail.Length > 0 ? "  [" + detail + "]" : ""));
            if (!ok) failures++;
        }

        // The exact expression HostServer and LockAgent use to read the optional character.
        static int ReadOptionalChar(BinaryReader r)
        {
            return (r.BaseStream.Position + 2 <= r.BaseStream.Length) ? r.ReadUInt16() : 0;
        }

        static int Main()
        {
            // --- InputInjector Unicode path ---
            List<string> events = new List<string>();
            InputInjector.Suppress = true;                 // do not touch this machine's real input
            InputInjector.Observe = what => events.Add(what);
            try
            {
                InputInjector injector = new InputInjector();

                events.Clear();
                injector.Char('م', true);                  // a non-Latin character types the same way
                Check("a character press injects one event", events.Count == 1);

                injector.ReleaseAll();                     // a press left held must be released
                Check("ReleaseAll lets the held character up", events.Count == 2, events.Count.ToString());

                events.Clear();
                injector.Char('A', true);
                injector.Char('A', false);                 // pressed and released: nothing left to release
                int afterPair = events.Count;
                injector.ReleaseAll();
                Check("a released character is not released twice", events.Count == afterPair, "pair=" + afterPair + " total=" + events.Count);

                events.Clear();
                injector.Key(0x41, 0x1E, false, true);     // a normal virtual-key press is still tracked
                injector.ReleaseAll();
                Check("ReleaseAll still releases ordinary keys", events.Count == 2, events.Count.ToString());
            }
            finally
            {
                InputInjector.Suppress = false;
                InputInjector.Observe = null;
            }

            // --- Msg.Key payload compatibility ---
            // New viewer: vk, scan, extended, down, then the character.
            byte[] withChar = Pack.Build(w => { w.Write(0x41); w.Write(0x1E); w.Write(false); w.Write(true); w.Write((ushort)'A'); });
            using (BinaryReader r = Pack.Read(withChar))
            {
                int vk = r.ReadInt32(); int scan = r.ReadInt32(); bool ext = r.ReadBoolean(); bool down = r.ReadBoolean();
                int ch = ReadOptionalChar(r);
                Check("new payload: fields read back", vk == 0x41 && scan == 0x1E && !ext && down);
                Check("new payload: character read back", ch == 'A', ((char)ch).ToString());
            }

            // Old viewer: the four original fields only. The host must not throw and must see no character.
            byte[] noChar = Pack.Build(w => { w.Write(0x42); w.Write(0x30); w.Write(true); w.Write(false); });
            using (BinaryReader r = Pack.Read(noChar))
            {
                int vk = r.ReadInt32(); int scan = r.ReadInt32(); bool ext = r.ReadBoolean(); bool down = r.ReadBoolean();
                int ch = ReadOptionalChar(r);
                Check("old payload: fields read back", vk == 0x42 && scan == 0x30 && ext && !down);
                Check("old payload: no character, no throw", ch == 0);
            }

            // A real key-down carries the character the viewer's layout produces; control keys carry 0.
            Check("CharFor gives a letter for a letter key", ViewerCharProbe(0x41) != 0);
            Check("CharFor gives 0 for Enter", ViewerCharProbe(0x0D) == 0);
            Check("CharFor gives 0 for Backspace", ViewerCharProbe(0x08) == 0);

            Console.WriteLine(failures == 0 ? "ALL PASSED" : (failures + " FAILED"));
            return failures == 0 ? 0 : 1;
        }

        // Reaches ViewerForm.CharFor (private static) through the real code, by scan code from the vk.
        static int ViewerCharProbe(int vk)
        {
            uint scan = Native.MapVirtualKey((uint)vk, 0);
            System.Reflection.MethodInfo m = typeof(ViewerForm).GetMethod("CharFor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (int)m.Invoke(null, new object[] { vk, (int)scan });
        }
    }
}
