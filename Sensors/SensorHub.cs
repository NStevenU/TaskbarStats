namespace TaskbarStats.Sensors;

/// <summary>Logical sensors a widget module can ask for. Providers decide how to fill them.</summary>
public enum SensorKey
{
    CpuTotal,
    CpuCores,      // per-logical-processor usage, see Snapshot.Cores
    CpuTemp,
    MemUsedPct,
    MemUsedMb,
    GpuLoad,
    GpuTemp,
    GpuMemUsedMb,
    DiskUsedPct,   // used space of the system drive
    DiskActivePct, // time the system drive was busy
    DiskReadKBs,
    DiskWriteKBs,
    NetDownKBs,    // all physical adapters together
    NetUpKBs,
    FanRpm,        // CPU fan (or the fastest fan) from the motherboard
    GpuFanPct,
    Count
}

/// <summary>Latest values. NaN means "not available".</summary>
public sealed class Snapshot
{
    public readonly double[] V = new double[(int)SensorKey.Count + CustomSensors.Max];

    /// <summary>Usage per logical processor, 0..100.</summary>
    public float[] Cores = Array.Empty<float>();

    /// <summary>Usage per physical core (Hyper-Threading siblings averaged), 0..100.</summary>
    public float[] PhysCores = Array.Empty<float>();

    public Snapshot() => Array.Fill(V, double.NaN);
    public double this[SensorKey k] { get => V[(int)k]; set => V[(int)k] = value; }
}

public abstract class SensorProvider : IDisposable
{
    /// <summary>Stable name used by the "components" switches of the settings.</summary>
    public abstract string Id { get; }

    public abstract IReadOnlyList<SensorKey> Provides { get; }

    /// <summary>Whether the source exists on this PC (null = cannot tell cheaply).</summary>
    public virtual bool? Available => null;

    /// <summary>Read only what is flagged in <paramref name="wanted"/>.</summary>
    public abstract void Update(Snapshot s, bool[] wanted);

    /// <summary>Called when nothing this provider supplies is subscribed; release resources here.</summary>
    public virtual void Idle() { }

    public virtual void Dispose() { }
}

/// <summary>
/// Reference-counted subscriptions: a provider is only polled while at least one
/// visible module needs one of its sensors.
/// </summary>
public sealed class SensorHub : IDisposable
{
    readonly int[] _refs = new int[(int)SensorKey.Count + CustomSensors.Max];
    readonly bool[] _wanted = new bool[(int)SensorKey.Count + CustomSensors.Max];
    readonly List<SensorProvider> _providers = new();

    readonly HashSet<string> _disabled = new();

    public Snapshot Current { get; } = new();

    public IReadOnlyList<SensorProvider> Providers => _providers;

    public void Add(SensorProvider p) => _providers.Add(p);

    /// <summary>Switch components off: they are not polled any more and their values read as "not available".</summary>
    public void SetDisabled(IEnumerable<string>? ids)
    {
        var now = new HashSet<string>(ids ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var p in _providers)
        {
            bool off = now.Contains(p.Id);
            if (off && !_disabled.Contains(p.Id))
            {
                p.Idle();
                foreach (var k in p.Provides) Current[k] = double.NaN;
            }
        }
        _disabled.Clear();
        foreach (var id in now) _disabled.Add(id);
    }

    public bool IsDisabled(string id) => _disabled.Contains(id);

    public void Subscribe(IEnumerable<SensorKey> keys)
    {
        foreach (var k in keys) _refs[(int)k]++;
    }

    public void Unsubscribe(IEnumerable<SensorKey> keys)
    {
        foreach (var k in keys)
            if (_refs[(int)k] > 0) _refs[(int)k]--;
    }

    public void Update()
    {
        for (int i = 0; i < _wanted.Length; i++) _wanted[i] = _refs[i] > 0;

        foreach (var p in _providers)
        {
            if (_disabled.Contains(p.Id)) continue;
            bool any = false;
            foreach (var k in p.Provides)
                if (_wanted[(int)k]) { any = true; break; }

            if (any) p.Update(Current, _wanted);
            else p.Idle();
        }
    }

    public void Dispose()
    {
        foreach (var p in _providers) p.Dispose();
        _providers.Clear();
    }
}
