using System.Runtime.InteropServices;

namespace Tasky;

/// <summary>
/// Notices Windows' permission prompt (User Account Control) by its process: consent.exe runs in your
/// session exactly while a prompt is open or waiting. This doesn't depend on Windows announcing the
/// prompt's window, which it may not do for a dock, so it's the dependable signal.
/// </summary>
internal static class ConsentWatcher
{
    const int SystemProcessInformation = 5;
    const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;

    // x64 SYSTEM_PROCESS_INFORMATION offsets.
    const int NextEntryOffset = 0x00, ImageNameLength = 0x38, ImageNameBuffer = 0x40, UniqueProcessId = 0x50, SessionId = 0x64;

    [DllImport("ntdll.dll")] static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);

    static IntPtr buffer;
    static int capacity;
    static readonly uint OwnSession = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;

    /// <summary>The consent.exe process in this session, if a permission prompt is open; one cheap system call, reusing a buffer.</summary>
    public static uint? Find()
    {
        if (!Environment.Is64BitProcess) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (capacity == 0) Grow(512 * 1024);
            uint status = NtQuerySystemInformation(SystemProcessInformation, buffer, capacity, out int needed);
            if (status == STATUS_INFO_LENGTH_MISMATCH)
            {
                Grow(Math.Max(needed, capacity) + 64 * 1024);
                continue;
            }
            if (status != 0) return null;
            return Scan();
        }
        return null;
    }

    static uint? Scan()
    {
        long offset = 0;
        while (true)
        {
            var entry = buffer + (int)offset;
            int nameBytes = Marshal.ReadInt16(entry, ImageNameLength) & 0xFFFF;
            var name = Marshal.ReadIntPtr(entry, ImageNameBuffer);
            if (nameBytes == "consent.exe".Length * 2 && name != IntPtr.Zero
                && string.Equals(Marshal.PtrToStringUni(name, nameBytes / 2), "consent.exe", StringComparison.OrdinalIgnoreCase)
                && (uint)Marshal.ReadInt32(entry, SessionId) == OwnSession)
                return (uint)Marshal.ReadIntPtr(entry, UniqueProcessId).ToInt64();

            int next = Marshal.ReadInt32(entry, NextEntryOffset);
            if (next == 0) return null;
            offset += next;
        }
    }

    static void Grow(int size)
    {
        if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        buffer = Marshal.AllocHGlobal(size);
        capacity = size;
    }

    /// <summary>The prompt's own windows (it may have hidden ones on your desktop while it waits for you).</summary>
    public static List<IntPtr> Windows(uint consentPid)
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == consentPid) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
