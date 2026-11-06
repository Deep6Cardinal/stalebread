using System.Runtime.InteropServices;
using StaleBread.Core.Model;

namespace StaleBread.Windows.Collection;

public static class SystemMetrics
{
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    public static SystemSample Read()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? new(status.Load, status.AvailablePhysical, Environment.ProcessorCount)
            : new(null, null, Environment.ProcessorCount);
    }
}
