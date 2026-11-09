using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TaskbarStats.Sensors;

/// <summary>
/// System drive: used space plus busy time and throughput from IOCTL_DISK_PERFORMANCE on the volume.
/// The volume is opened with no access rights, so no admin rights are needed. The handle is only
/// held while something subscribes.
/// </summary>
public sealed unsafe class DiskProvider : SensorProvider
{
    static readonly SensorKey[] Keys =
        { SensorKey.DiskUsedPct, SensorKey.DiskActivePct, SensorKey.DiskReadKBs, SensorKey.DiskWriteKBs };

    const uint IOCTL_DISK_PERFORMANCE = 0x70020;
    const int PerfSize = 88;

    readonly string _root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    IntPtr _h = new(-1);
    bool _have, _openFailed;
    long _rd, _wr, _idle, _q;

    public override string Id => "windows";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (wanted[(int)SensorKey.DiskUsedPct]) ReadSpace(s);

        if (wanted[(int)SensorKey.DiskActivePct] || wanted[(int)SensorKey.DiskReadKBs] || wanted[(int)SensorKey.DiskWriteKBs])
            ReadPerf(s);
        else
            _have = false;
    }

    void ReadSpace(Snapshot s)
    {
        try
        {
            var d = new DriveInfo(_root);
            if (d.TotalSize > 0) s[SensorKey.DiskUsedPct] = 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize;
        }
        catch { /* drive not ready */ }
    }

    void ReadPerf(Snapshot s)
    {
        if (_h == new IntPtr(-1))
        {
            if (_openFailed) return;
            _h = CreateFileW(@"\\.\" + _root.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_h == new IntPtr(-1)) { _openFailed = true; return; }
        }

        byte* buf = stackalloc byte[PerfSize];
        if (!DeviceIoControl(_h, IOCTL_DISK_PERFORMANCE, null, 0, buf, PerfSize, out _, IntPtr.Zero)) return;

        long rd = *(long*)(buf + 0), wr = *(long*)(buf + 8), idle = *(long*)(buf + 32), q = *(long*)(buf + 56);
        if (_have && q > _q)
        {
            double dq = q - _q;
            double secs = dq / 1e7;
            s[SensorKey.DiskActivePct] = Math.Clamp(100.0 * (1.0 - (idle - _idle) / dq), 0, 100);
            s[SensorKey.DiskReadKBs] = Math.Max(0, (rd - _rd) / secs / 1024.0);
            s[SensorKey.DiskWriteKBs] = Math.Max(0, (wr - _wr) / secs / 1024.0);
        }
        (_rd, _wr, _idle, _q) = (rd, wr, idle, q);
        _have = true;
    }

    public override void Idle() => Close();

    void Close()
    {
        _have = false;
        if (_h != new IntPtr(-1)) { CloseHandle(_h); _h = new IntPtr(-1); }
    }

    public override void Dispose() => Close();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, void* inBuf, int inLen, void* outBuf, int outLen,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}

/// <summary>Network throughput summed over all connected physical adapters (GetIfTable2).</summary>
public sealed unsafe class NetProvider : SensorProvider
{
    static readonly SensorKey[] Keys = { SensorKey.NetDownKBs, SensorKey.NetUpKBs };

    // MIB_IF_ROW2 layout (x64): size 1352, flags byte at 1152, OperStatus at 1156, InOctets at 1208, OutOctets at 1280
    const int RowSize = 1352, OffType = 1128, OffFlags = 1152, OffOper = 1156, OffIn = 1208, OffOut = 1280;
    const int IfTypeLoopback = 24, HardwareInterface = 1, OperUp = 1;

    bool _have;
    ulong _in, _out;
    long _t;

    public override string Id => "windows";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (GetIfTable2(out IntPtr table) != 0) return;
        ulong rx = 0, tx = 0;
        try
        {
            uint n = *(uint*)table;
            byte* rows = (byte*)table + 8;
            for (uint i = 0; i < n; i++)
            {
                byte* r = rows + i * RowSize;
                if (*(uint*)(r + OffType) == IfTypeLoopback) continue;
                if ((*(r + OffFlags) & HardwareInterface) == 0) continue;
                if (*(int*)(r + OffOper) != OperUp) continue;
                rx += *(ulong*)(r + OffIn);
                tx += *(ulong*)(r + OffOut);
            }
        }
        finally { FreeMibTable(table); }

        long now = Stopwatch.GetTimestamp();
        if (_have && now > _t)
        {
            double secs = (now - _t) / (double)Stopwatch.Frequency;
            s[SensorKey.NetDownKBs] = rx >= _in ? (rx - _in) / secs / 1024.0 : 0;     // an adapter vanished: skip the sample
            s[SensorKey.NetUpKBs] = tx >= _out ? (tx - _out) / secs / 1024.0 : 0;
        }
        (_in, _out, _t, _have) = (rx, tx, now, true);
    }

    public override void Idle() => _have = false;

    [DllImport("iphlpapi.dll")] static extern int GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr table);
}
