namespace TaskbarStats.Sensors;

/// <summary>CPU (total + per logical processor) and physical memory. No driver, no admin rights.</summary>
public sealed unsafe class CpuMemProvider : SensorProvider
{
    static readonly SensorKey[] Keys =
        { SensorKey.CpuTotal, SensorKey.CpuCores, SensorKey.MemUsedPct, SensorKey.MemUsedMb };

    readonly int _n = Math.Min(Environment.ProcessorCount, 64);
    ProcessorPerfInfo[] _prev, _cur;
    bool _havePrev;

    public CpuMemProvider()
    {
        _prev = new ProcessorPerfInfo[_n];
        _cur = new ProcessorPerfInfo[_n];
    }

    public override string Id => "windows";
    public int LogicalProcessors => _n;
    public override IReadOnlyList<SensorKey> Provides => Keys;

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (wanted[(int)SensorKey.CpuTotal] || wanted[(int)SensorKey.CpuCores])
            ReadCpu(s);
        else
            _havePrev = false; // gap in polling: next sample must not average over the gap

        if (wanted[(int)SensorKey.MemUsedPct] || wanted[(int)SensorKey.MemUsedMb])
            ReadMem(s);
    }

    public override void Idle() => _havePrev = false;

    void ReadCpu(Snapshot s)
    {
        int bytes = _n * sizeof(ProcessorPerfInfo);
        int status;
        fixed (ProcessorPerfInfo* p = _cur)
            status = Native.NtQuerySystemInformation(8 /* SystemProcessorPerformanceInformation */, p, bytes, out _);
        if (status != 0) return;

        if (s.Cores.Length != _n) s.Cores = new float[_n];
        if (s.PhysCores.Length != CpuTopology.Physical) s.PhysCores = new float[CpuTopology.Physical];

        if (_havePrev)
        {
            double busyAll = 0, totalAll = 0;
            for (int i = 0; i < _n; i++)
            {
                long idle = _cur[i].IdleTime - _prev[i].IdleTime;
                long total = (_cur[i].KernelTime - _prev[i].KernelTime) + (_cur[i].UserTime - _prev[i].UserTime);
                double pct = total > 0 ? 100.0 * (total - idle) / total : 0;
                s.Cores[i] = (float)Math.Clamp(pct, 0, 100);
                busyAll += total - idle;
                totalAll += total;
            }
            s[SensorKey.CpuTotal] = totalAll > 0 ? Math.Clamp(100.0 * busyAll / totalAll, 0, 100) : 0;

            var map = CpuTopology.CoreThreads;
            for (int c = 0; c < map.Length; c++)
            {
                float sum = 0;
                foreach (int t in map[c]) sum += s.Cores[t];
                s.PhysCores[c] = sum / map[c].Length;
            }
        }

        (_prev, _cur) = (_cur, _prev);
        _havePrev = true;
    }

    static void ReadMem(Snapshot s)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        if (!Native.GlobalMemoryStatusEx(ref m)) return;
        s[SensorKey.MemUsedPct] = m.dwMemoryLoad;
        s[SensorKey.MemUsedMb] = (m.ullTotalPhys - m.ullAvailPhys) / (1024.0 * 1024.0);
    }
}
