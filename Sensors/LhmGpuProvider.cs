using LibreHardwareMonitor.Hardware;

namespace TaskbarStats.Sensors;

/// <summary>
/// AMD (and Intel) GPUs through LibreHardwareMonitorLib. It only works while the NVIDIA provider has nothing:
/// on a PC with an NVIDIA GPU it never opens anything. Fails soft: values stay as they are.
/// </summary>
public sealed class LhmGpuProvider : SensorProvider
{
    static readonly SensorKey[] Keys = { SensorKey.GpuLoad, SensorKey.GpuTemp, SensorKey.GpuMemUsedMb, SensorKey.GpuFanPct };

    readonly NvmlProvider _nvidia;
    Computer? _pc;
    IHardware? _gpu;
    ISensor? _load, _temp, _mem, _fan;
    DateTime _retryAt;

    public LhmGpuProvider(NvmlProvider nvidia) => _nvidia = nvidia;

    public override string Id => "lhm";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (_nvidia.Active) { Close(); return; }
        if (_gpu == null && DateTime.UtcNow >= _retryAt) Open();
        if (_gpu == null) return;

        try
        {
            _gpu.Update();
            if (wanted[(int)SensorKey.GpuLoad] && _load?.Value is { } l) s[SensorKey.GpuLoad] = l;
            if (wanted[(int)SensorKey.GpuTemp] && _temp?.Value is { } t) s[SensorKey.GpuTemp] = t;
            if (wanted[(int)SensorKey.GpuMemUsedMb] && _mem?.Value is { } m) s[SensorKey.GpuMemUsedMb] = m;
            if (wanted[(int)SensorKey.GpuFanPct] && _fan?.Value is { } f) s[SensorKey.GpuFanPct] = f;
        }
        catch { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
    }

    void Open()
    {
        try
        {
            _pc = new Computer { IsGpuEnabled = true };
            _pc.Open();
            _gpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel);
            if (_gpu == null) { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); return; }

            _gpu.Update();
            var sensors = _gpu.Sensors;
            _temp = sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    ?? sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature);
            _load = sensors.FirstOrDefault(x => x.SensorType == SensorType.Load && x.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    ?? sensors.FirstOrDefault(x => x.SensorType == SensorType.Load);
            _mem = sensors.FirstOrDefault(x => x.SensorType == SensorType.SmallData && x.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase));
            _fan = sensors.FirstOrDefault(x => x.SensorType == SensorType.Control && x.Name.Contains("Fan", StringComparison.OrdinalIgnoreCase));
        }
        catch { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
    }

    void Close()
    {
        try { _pc?.Close(); } catch { }
        _pc = null; _gpu = null; _load = _temp = _mem = _fan = null;
    }

    public override void Idle() => Close();
    public override void Dispose() => Close();
}
