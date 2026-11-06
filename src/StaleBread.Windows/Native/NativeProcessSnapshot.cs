using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using StaleBread.Core.Model;

namespace StaleBread.Windows.Native;

public sealed record NativeSnapshotResult(IReadOnlyList<ProcessSample> Processes, bool Valid, string? Error);

/// <summary>Isolated x64 SYSTEM_PROCESS_INFORMATION adapter. No cross-snapshot PID joins.</summary>
public sealed class NativeProcessSnapshot
{
    private const int MaxBuffer = 32 * 1024 * 1024;
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int kind, nint buffer, int length, out int needed);
    public NativeSnapshotResult Read()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return new([], false, "Unsupported native architecture");
        int size = 1024 * 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(size);
            try
            {
                int status = NtQuerySystemInformation(5, buffer, size, out int needed);
                if (status == unchecked((int)0xC0000004))
                {
                    if (needed < 0 || needed > MaxBuffer - 65536 || size >= MaxBuffer)
                        return new([], false, "Native snapshot capacity exceeded");
                    size = Math.Min(MaxBuffer, Math.Max(size * 2, needed + 65536));
                    continue;
                }
                if (status < 0 || needed < 256 || needed > size)
                    return new([], false, $"Native snapshot status {status:X8}");
                var data = new byte[needed];
                Marshal.Copy(buffer, data, 0, needed);
                return Parse(data, buffer.ToInt64());
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new([], false, "Native snapshot changed during three bounded attempts");
    }
    public static NativeSnapshotResult Parse(byte[] data, long baseAddress)
    {
        var processes = new List<ProcessSample>();
        var pids = new HashSet<int>();
        if (data.Length < 256 || data.Length > MaxBuffer) return new([], false, "Invalid snapshot length");
        int offset = 0;
        try
        {
            while (true)
            {
                if (offset < 0 || offset > data.Length - 256) throw new InvalidDataException();
                var entry = data.AsSpan(offset);
                uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
                if (next != 0 && (next < 256 || next % 8 != 0 || next > data.Length - offset - 256))
                    throw new InvalidDataException();
                int end = next == 0 ? data.Length : checked(offset + (int)next);
                long pidValue = BinaryPrimitives.ReadInt64LittleEndian(entry[80..]);
                long parentValue = BinaryPrimitives.ReadInt64LittleEndian(entry[88..]);
                if (pidValue < 0 || pidValue > int.MaxValue || parentValue < 0 || parentValue > int.MaxValue)
                    throw new InvalidDataException();
                int pid = (int)pidValue, parent = (int)parentValue;
                if (!pids.Add(pid)) throw new InvalidDataException();
                long creation = BinaryPrimitives.ReadInt64LittleEndian(entry[32..]);
                long user = BinaryPrimitives.ReadInt64LittleEndian(entry[40..]);
                long kernel = BinaryPrimitives.ReadInt64LittleEndian(entry[48..]);
                if (user < 0 || kernel < 0 || creation < 0) throw new InvalidDataException();
                ushort length = BinaryPrimitives.ReadUInt16LittleEndian(entry[56..]);
                ushort maximum = BinaryPrimitives.ReadUInt16LittleEndian(entry[58..]);
                long pointer = BinaryPrimitives.ReadInt64LittleEndian(entry[64..]);
                if (length % 2 != 0 || length > maximum) throw new InvalidDataException();
                string name = pid == 0 ? "System Idle Process" : "System";
                if (length > 0)
                {
                    long textOffset = checked(pointer - baseAddress);
                    if (textOffset < offset + 256 || textOffset > end - length) throw new InvalidDataException();
                    name = Encoding.Unicode.GetString(data.AsSpan((int)textOffset, length));
                    if (name.Contains('\0')) throw new InvalidDataException();
                }
                int session = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[100..]));
                int handles = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[96..]));
                processes.Add(new(creation > 0 ? new(pid, creation) : null, pid, parent, name, SessionId: session,
                    CpuTime: TimeSpan.FromTicks(checked(user + kernel)),
                    ReadBytes: BinaryPrimitives.ReadUInt64LittleEndian(entry[232..]),
                    WriteBytes: BinaryPrimitives.ReadUInt64LittleEndian(entry[240..]),
                    OtherBytes: BinaryPrimitives.ReadUInt64LittleEndian(entry[248..]),
                    PrivateBytes: BinaryPrimitives.ReadUInt64LittleEndian(entry[200..]),
                    WorkingSetBytes: BinaryPrimitives.ReadUInt64LittleEndian(entry[144..]), HandleCount: handles));
                if (processes.Count > 100000) throw new InvalidDataException();
                if (next == 0) break;
                offset = end;
            }
            return new(processes.ToArray(), true, null);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        { return new([], false, "Invalid native process layout"); }
    }
}
