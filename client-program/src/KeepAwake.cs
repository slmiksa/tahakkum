using System;

namespace Tahakkum
{
    // Keeps the machine from going to sleep while it is accepting control requests, so a viewer
    // can always reach it. The display is still allowed to turn off to save power; only system
    // sleep and hibernation are held off. The state lives on whichever thread calls this (the UI
    // thread), and is re-asserted on a timer so it survives for as long as the program runs.
    static class KeepAwake
    {
        const uint ES_CONTINUOUS = 0x80000000;
        const uint ES_SYSTEM_REQUIRED = 0x00000001;

        static bool on;

        public static void Set(bool value)
        {
            on = value;
            Reassert();
        }

        public static void Reassert()
        {
            try
            {
                Native.SetThreadExecutionState(on ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
            }
            catch (Exception) { }
        }
    }
}
