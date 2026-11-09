using LibreHardwareMonitor.Hardware;

namespace TaskbarStats.Sensors;

/// <summary>
/// CPU package temperature and the motherboard fan speed through LibreHardwareMonitorLib (MPL-2.0) + PawnIO.
/// Only the hardware groups that are subscribed are enabled; created lazily and closed when unsubscribed.
/// Fails soft: values stay NaN.
/// </summary>
public sealed class LhmCpuProvider : SensorProvider
{
    static readonly SensorKey[] Keys = { SensorKey.CpuTemp, SensorKey.FanRpm };
    public override string Id => "lhm";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    Computer? _pc;
    int _flags;                                   // 1 = CPU, 2 = motherboard
    IHardware? _cpu;
    ISensor? _temp;
    readonly List<IHardware> _boards = new();
    readonly List<ISensor> _fans = new();
    DateTime _retryAt;

    public override void Update(Snapshot s, bool[] wanted)
    {
        bool wantTemp = wanted[(int)SensorKey.CpuTemp], wantFan = wanted[(int)SensorKey.FanRpm];
        int flags = (wantTemp ? 1 : 0) | (wantFan ? 2 : 0);

        if (_pc != null && flags != _flags) Close();                    // the set of subscribers changed
        if (_pc == null && DateTime.UtcNow >= _retryAt) Open(flags);
        if (_pc == null)
        {
            if (wantTemp) s[SensorKey.CpuTemp] = double.NaN;
            if (wantFan) s[SensorKey.FanRpm] = double.NaN;
            return;
        }

        try
        {
            if (wantTemp)
            {
                _cpu?.Update();
                s[SensorKey.CpuTemp] = _temp?.Value ?? double.NaN;
            }
            if (wantFan)
            {
                foreach (var b in _boards)
                {
                    b.Update();
                    foreach (var sub in b.SubHardware) sub.Update();
                }
                s[SensorKey.FanRpm] = PickFan();
            }
        }
        catch
        {
            if (wantTemp) s[SensorKey.CpuTemp] = double.NaN;
            if (wantFan) s[SensorKey.FanRpm] = double.NaN;
            Close();
            _retryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }

    /// <summary>The CPU fan if one is named so, otherwise the fastest spinning fan.</summary>
    double PickFan()
    {
        double best = double.NaN;
        foreach (var f in _fans)
            if (f.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase) && f.Value is > 0)
                return f.Value.Value;
        foreach (var f in _fans)
            if (f.Value is { } v && v > 0 && (double.IsNaN(best) || v > best)) best = v;
        return best;
    }

    void Open(int flags)
    {
        try
        {
            _pc = new Computer { IsCpuEnabled = (flags & 1) != 0, IsMotherboardEnabled = (flags & 2) != 0 };
            _pc.Open();
            _flags = flags;

            bool ok = false;
            if ((flags & 1) != 0)
            {
                _cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                _cpu?.Update();
                var temps = _cpu?.Sensors.Where(x => x.SensorType == SensorType.Temperature).ToList();
                _temp = temps?.FirstOrDefault(x => x.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        ?? temps?.FirstOrDefault(x => x.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                        ?? temps?.FirstOrDefault();
                ok |= _temp != null;
            }
            if ((flags & 2) != 0)
            {
                foreach (var b in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard))
                {
                    _boards.Add(b);
                    b.Update();
                    foreach (var sub in b.SubHardware)
                    {
                        sub.Update();
                        _fans.AddRange(sub.Sensors.Where(x => x.SensorType == SensorType.Fan));
                    }
                    _fans.AddRange(b.Sensors.Where(x => x.SensorType == SensorType.Fan));
                }
                ok |= _fans.Count > 0;
            }
            if (!ok) { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
        }
        catch { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
    }

    void Close()
    {
        try { _pc?.Close(); } catch { }
        _pc = null; _cpu = null; _temp = null;
        _boards.Clear(); _fans.Clear();
    }

    public override void Idle() => Close();
    public override void Dispose() => Close();
}
