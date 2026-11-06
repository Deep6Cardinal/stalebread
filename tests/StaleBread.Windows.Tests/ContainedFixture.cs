using StaleBread.Core.Model;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StaleBread.Windows.Tests;

internal sealed class ContainedFixture : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    public ProcessIdentity Identity { get; }
    private ContainedFixture(SafeFileHandle job, SafeProcessHandle process, ProcessIdentity id)
    { this.job = job; this.process = process; Identity = id; }
    public static ContainedFixture Start(string mode, int milliseconds)
    {
        if (mode is not ("idle" or "cpu" or "allocate" or "spawn" or "exit-parent") || milliseconds is < 100 or > 10000)
            throw new ArgumentException("Fixture mode or lifetime out of bounds");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StaleBread.sln"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Fixture checkout not found");
        string config = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        string exe = Path.Combine(dir.FullName, "tests", "StaleBread.TestChild", "bin", config, "net10.0-windows", "StaleBread.TestChild.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Build the bounded test fixture first", exe);
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        var info = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<Limits>()))
        { job.Dispose(); throw new Win32Exception(); }
        var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>() };
        var command = new StringBuilder($"\"{exe}\" {Guid.NewGuid():D} {mode} {milliseconds}");
        if (!CreateProcessW(exe, command, 0, 0, false, 0x08000004, 0, dir.FullName, ref startup, out var created))
        { job.Dispose(); throw new Win32Exception(); }
        var process = new SafeProcessHandle(created.Process, true);
        using var thread = new SafeFileHandle(created.Thread, true);
        try
        {
            // Suspended at creation: no child can escape the job before containment.
            if (!AssignProcessToJobObject(job, process))
                throw new InvalidOperationException("Fixture job containment unavailable; destructive integration must not run");
            if (!GetProcessTimes(process, out long birth, out _, out _, out _)) throw new Win32Exception();
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return new(job, process, new(created.Pid, birth));
        }
        catch
        {
            // This is our newly created suspended fixture handle, never a user PID lookup.
            TerminateProcess(process, 2); process.Dispose(); job.Dispose(); throw;
        }
    }
    public void Dispose() { job.Dispose(); process.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Limits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Startup
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes;
        public nint ReservedPointer, In, Out, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Created { public nint Process, Thread; public int Pid, Tid; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref Limits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, nint processSecurity, nint threadSecurity, bool inherit, uint flags, nint environment, string directory, ref Startup startup, out Created created);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(SafeProcessHandle process, uint code);
}
