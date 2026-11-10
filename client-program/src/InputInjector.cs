using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace Tahakkum
{
    // Replays the viewer's mouse and keyboard on this machine, and remembers what is
    // held down so nothing stays stuck when the session ends or is paused.
    sealed class InputInjector
    {
        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        // Marks a held entry as a Unicode character rather than a virtual key, so ReleaseAll
        // lets it up the same way it was pressed. Key ids never reach this bit.
        const int UnicodeFlag = 1 << 28;

        readonly object gate = new object();
        readonly HashSet<int> heldKeys = new HashSet<int>();
        readonly HashSet<int> heldButtons = new HashSet<int>();
        Rectangle monitor;

        public void SetMonitor(Rectangle bounds)
        {
            lock (gate) monitor = bounds;
        }

        public void Move(int x, int y)
        {
            lock (gate)
            {
                if (monitor.Width <= 0) return;
                x = Math.Max(0, Math.Min(monitor.Width - 1, x));
                y = Math.Max(0, Math.Min(monitor.Height - 1, y));
                int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
                int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
                int vw = Math.Max(2, Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN));
                int vh = Math.Max(2, Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
                // Windows converts back with a floor, so round up to land on the exact pixel.
                int ax = (int)(((long)(monitor.X + x - vx) * 65536 + vw - 1) / vw);
                int ay = (int)(((long)(monitor.Y + y - vy) * 65536 + vh - 1) / vh);
                SendMouse(ax, ay, 0, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK);
            }
        }

        public void Button(int button, bool down)
        {
            uint flags;
            uint data = 0;
            switch (button)
            {
                case 0: flags = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP; break;
                case 1: flags = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP; break;
                case 2: flags = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP; break;
                case 3: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 1; break;
                case 4: flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP; data = 2; break;
                default: return;
            }
            lock (gate)
            {
                if (down) heldButtons.Add(button); else heldButtons.Remove(button);
                SendMouse(0, 0, data, flags);
            }
        }

        public void Wheel(int delta, bool horizontal)
        {
            delta = Math.Max(-12000, Math.Min(12000, delta));
            lock (gate) SendMouse(0, 0, unchecked((uint)delta), horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL);
        }

        public void Key(int vk, int scan, bool extended, bool down)
        {
            if (vk <= 0 || vk > 0xFF) return;
            lock (gate)
            {
                if (down) heldKeys.Add(vk | (scan << 8) | (extended ? 1 << 24 : 0));
                else heldKeys.Remove(vk | (scan << 8) | (extended ? 1 << 24 : 0));
                SendKey(vk, scan, extended, down);
            }
        }

        // Types a character by its Unicode value, so the result does not depend on the keyboard
        // layout that happens to be active on the target desktop. Used at the lock screen, where the
        // secure desktop's layout may not match the password the viewer actually typed.
        public void Char(int ch, bool down)
        {
            if (ch <= 0 || ch > 0xFFFF) return;
            lock (gate)
            {
                if (down) heldKeys.Add(ch | UnicodeFlag); else heldKeys.Remove(ch | UnicodeFlag);
                SendChar(ch, down);
            }
        }

        public void ReleaseAll()
        {
            lock (gate)
            {
                foreach (int k in heldKeys)
                {
                    if ((k & UnicodeFlag) != 0) SendChar(k & 0xFFFF, false);
                    else SendKey(k & 0xFF, (k >> 8) & 0xFFFF, (k & (1 << 24)) != 0, false);
                }
                heldKeys.Clear();
                List<int> buttons = new List<int>(heldButtons);
                heldButtons.Clear();
                foreach (int b in buttons)
                {
                    uint flags, data = 0;
                    switch (b)
                    {
                        case 0: flags = Native.MOUSEEVENTF_LEFTUP; break;
                        case 1: flags = Native.MOUSEEVENTF_RIGHTUP; break;
                        case 2: flags = Native.MOUSEEVENTF_MIDDLEUP; break;
                        case 3: flags = Native.MOUSEEVENTF_XUP; data = 1; break;
                        default: flags = Native.MOUSEEVENTF_XUP; data = 2; break;
                    }
                    SendMouse(0, 0, data, flags);
                }
            }
        }

        // For tests: see what would be injected without moving this machine's real pointer.
        public static Action<string> Observe = null;
        public static bool Suppress = false;

        static bool Pass(string what)
        {
            Action<string> h = Observe;
            if (h != null) h(what);
            return !Suppress;
        }

        static void SendMouse(int dx, int dy, uint data, uint flags)
        {
            if (!Pass("mouse")) return;
            Native.INPUT[] input = new Native.INPUT[1];
            input[0].type = Native.INPUT_MOUSE;
            input[0].u.mi.dx = dx;
            input[0].u.mi.dy = dy;
            input[0].u.mi.mouseData = data;
            input[0].u.mi.dwFlags = flags;
            Native.SendInput(1, input, InputSize);
        }

        // Scan codes keep the host's own keyboard layout in charge of the typed character.
        // A few keys are reported by the hook in a way that does not round-trip as a scan code.
        static bool NeedsVirtualKey(int vk, int scan)
        {
            if (scan == 0) return true;
            if (vk == 0x90 || vk == 0x13 || vk == 0x2C || vk == 0xA1) return true;
            return vk >= 0xA6 && vk <= 0xB7;
        }

        static void SendKey(int vk, int scan, bool extended, bool down)
        {
            if (!Pass("key")) return;
            Native.INPUT[] input = new Native.INPUT[1];
            input[0].type = Native.INPUT_KEYBOARD;
            uint flags = down ? 0 : Native.KEYEVENTF_KEYUP;
            if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            if (NeedsVirtualKey(vk, scan))
            {
                input[0].u.ki.wVk = (ushort)vk;
            }
            else
            {
                input[0].u.ki.wScan = (ushort)scan;
                flags |= Native.KEYEVENTF_SCANCODE;
            }
            input[0].u.ki.dwFlags = flags;
            Native.SendInput(1, input, InputSize);
        }

        static void SendChar(int ch, bool down)
        {
            if (!Pass("key")) return;
            Native.INPUT[] input = new Native.INPUT[1];
            input[0].type = Native.INPUT_KEYBOARD;
            input[0].u.ki.wVk = 0;
            input[0].u.ki.wScan = (ushort)ch;
            uint flags = Native.KEYEVENTF_UNICODE;
            if (!down) flags |= Native.KEYEVENTF_KEYUP;
            input[0].u.ki.dwFlags = flags;
            Native.SendInput(1, input, InputSize);
        }
    }
}
