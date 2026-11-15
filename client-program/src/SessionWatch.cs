namespace Tahakkum
{
    // Tracks whether the Windows session is genuinely locked (the lock screen is up), as told by the
    // SessionSwitch notification. The host uses this to tell a real lock screen apart from a transient
    // secure desktop such as a UAC elevation prompt: both hide the normal desktop, but only the lock
    // screen must stay password-only. A UAC prompt that appears during an already-consented session is
    // driven like the rest of that session, so the viewer can finish an install that asks for
    // administrator credentials.
    static class SessionWatch
    {
        public static volatile bool Locked;
    }
}
