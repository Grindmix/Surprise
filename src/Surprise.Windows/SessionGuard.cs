using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Surprise.Windows;

public static class SessionGuard
{
    public static bool IsActiveUnlocked()
    {
        int session = Process.GetCurrentProcess().SessionId;
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out var memory, out int size)) return false;
        try { if (size < sizeof(int) || Marshal.ReadInt32(memory) != 0) return false; }
        finally { WTSFreeMemory(memory); }
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
