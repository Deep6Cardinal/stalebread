using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StaleBread.Core.Model;

namespace StaleBread.Windows.Native;

internal static class DisplayOnlyFallback
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Entry
    {
        public uint Size, Usage, Pid;
        public nuint Heap;
        public uint Module, Threads, Parent;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref Entry entry);
    public static IReadOnlyList<ProcessSample> Read()
    {
        var result = new List<ProcessSample>();
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Name = "" };
        if (snapshot.IsInvalid || !Process32FirstW(snapshot, ref entry)) return [];
        do
        {
            // Deliberately no subsequent creation-time join. These rows cannot establish identity.
            if (entry.Pid <= int.MaxValue && entry.Parent <= int.MaxValue)
                result.Add(new(null, (int)entry.Pid, (int)entry.Parent, entry.Name));
        } while (result.Count < 20000 && Process32NextW(snapshot, ref entry));
        return result;
    }
}
