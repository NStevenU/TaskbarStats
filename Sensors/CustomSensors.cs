using LibreHardwareMonitor.Hardware;

namespace TaskbarStats.Sensors;

/// <summary>
/// Any sensor LibreHardwareMonitor can see (every core temperature, every fan, ...) as a "custom" key. A fixed number of
/// slots lives in the snapshot behind the built-in keys; a sensor is stored in the configuration as
/// "@identifier~name" and gets a slot the first time it is mentioned.
/// </summary>
public static class CustomSensors
{
    public const int Max = 24;

    static readonly string?[] Ids = new string?[Max], Names = new string?[Max];
    static readonly object Lock = new();

    public static bool IsCustom(SensorKey k) => (int)k >= (int)SensorKey.Count;
    static int Index(SensorKey k) => (int)k - (int)SensorKey.Count;
    public static SensorKey KeyAt(int i) => (SensorKey)((int)SensorKey.Count + i);

    public static string Spec(string id, string name) => "@" + id + "~" + name;

    /// <summary>"@id~name" → its slot (allocated on first use); null when the text is not a custom sensor or no slot is left.</summary>
    public static SensorKey? Register(string? spec)
    {
        if (spec == null || spec.Length < 2 || spec[0] != '@') return null;
        int t = spec.IndexOf('~');
        string id = t > 0 ? spec[1..t] : spec[1..];
        string name = t > 0 ? spec[(t + 1)..] : id[(id.LastIndexOf('/') + 1)..];
        lock (Lock)
        {
            int free = -1;
            for (int i = 0; i < Max; i++)
            {
                if (Ids[i] == id) { if (name.Length > 0) Names[i] = name; return KeyAt(i); }
                if (Ids[i] == null && free < 0) free = i;
            }
            if (free < 0) return null;
            Ids[free] = id;
            Names[free] = name;
            return KeyAt(free);
        }
    }

    public static string? Id(SensorKey k) => IsCustom(k) && Index(k) < Max ? Ids[Index(k)] : null;
    public static string Name(SensorKey k) => IsCustom(k) && Index(k) < Max ? Names[Index(k)] ?? "?" : "?";

    /// <summary>"temperature", "fan", "load", ... from an identifier such as /lpc/nct6798d/0/fan/1.</summary>
    public static string KindOf(string id)
    {
        var p = id.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= 2 ? p[^2].ToLowerInvariant() : "";
    }

    public static string Kind(SensorKey k) => Id(k) is { } id ? KindOf(id) : "";

    public static string Format(string kind, double v)
    {
        if (double.IsNaN(v)) return "--";
        return kind switch
        {
            "temperature" => TaskbarStats.Ui.Fmt.Temp(v),
            "fan" => TaskbarStats.Ui.Fmt.Rpm(v),
            "load" or "control" or "level" => $"{(int)Math.Round(v)}%",
            "clock" => v >= 1000 ? $"{v / 1000:0.0}GHz" : $"{(int)Math.Round(v)}MHz",
            "power" => $"{v:0.#}W",
            "voltage" => $"{v:0.00}V",
            "current" => $"{v:0.0#}A",
            "energy" => $"{v:0}mWh",
            "data" => $"{v:0.#}GB",
            "smalldata" => $"{v / 1024:0.0}G",
            "throughput" => TaskbarStats.Ui.Fmt.Speed(v / 1024.0),
            "factor" => $"{v:0.##}",
            "noise" => $"{v:0}dBA",
            "frequency" => $"{v:0}Hz",
            "flow" => $"{v:0}L/h",
            _ => $"{v:0.##}",
        };
    }

    /// <summary>The widest text this kind of sensor shows (keeps the widget from jittering).</summary>
    public static string Template(string kind) => kind switch
    {
        "temperature" => TaskbarStats.Ui.Fmt.TempTemplate,
        "fan" => TaskbarStats.Ui.Fmt.RpmTemplate,
        "load" or "control" or "level" => "100%",
        "clock" => "9.9GHz",
        "power" => "999.9W",
        "voltage" => "9.99V",
        "current" => "99.9A",
        "energy" => "99999mWh",
        "data" => "999.9GB",
        "smalldata" => "99.9G",
        "throughput" => "↓99.9M",
        _ => "9999",
    };
}

/// <summary>Everything LibreHardwareMonitor can read on this PC, scanned once in the background for the picker.</summary>
public static class SensorCatalog
{
    public sealed record Entry(string Id, string Name, string Hardware, string Kind, double Value);

    static readonly object Lock = new();
    static List<Entry> _entries = new();
    static DateTime _scanned = DateTime.MinValue;

    public static bool Scanning { get; private set; }
    public static IReadOnlyList<Entry> Entries { get { lock (Lock) return _entries; } }

    /// <summary>Scan in the background unless a recent scan exists; <paramref name="done"/> runs on a worker thread.</summary>
    public static void Scan(Action done)
    {
        if (Scanning || (DateTime.UtcNow - _scanned).TotalSeconds < 30) return;
        Scanning = true;
        Task.Run(() =>
        {
            var list = new List<Entry>();
            try
            {
                var pc = new Computer
                {
                    IsCpuEnabled = true, IsMotherboardEnabled = true, IsGpuEnabled = true,
                    IsMemoryEnabled = true, IsStorageEnabled = true, IsNetworkEnabled = true, IsBatteryEnabled = true,
                };
                pc.Open();
                void Walk(IHardware h, string top)
                {
                    h.Update();
                    foreach (var s in h.Sensors)
                        if (s.Value is { } v && !float.IsNaN(v))
                            list.Add(new Entry(s.Identifier.ToString(), s.Name, top, CustomSensors.KindOf(s.Identifier.ToString()), v));
                    foreach (var sub in h.SubHardware) Walk(sub, top);
                }
                foreach (var h in pc.Hardware) Walk(h, h.Name);
                pc.Close();
            }
            catch { /* whatever was found stays */ }
            lock (Lock) { _entries = list; _scanned = DateTime.UtcNow; }
            Scanning = false;
            try { done(); } catch { }
        });
    }
}

/// <summary>Reads the custom (picked) sensors. Opens only the hardware groups the picked sensors belong to.</summary>
public sealed class LhmCustomProvider : SensorProvider
{
    static readonly SensorKey[] Keys = Enumerable.Range(0, CustomSensors.Max).Select(CustomSensors.KeyAt).ToArray();

    public override string Id => "lhm";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    Computer? _pc;
    string _sig = "";
    readonly Dictionary<SensorKey, ISensor> _map = new();
    readonly List<IHardware> _hw = new();
    DateTime _retryAt;

    public override void Update(Snapshot s, bool[] wanted)
    {
        var want = Keys.Where(k => wanted[(int)k] && CustomSensors.Id(k) != null).ToList();
        string sig = string.Join(";", want.Select(k => CustomSensors.Id(k)));
        if (sig != _sig) { Close(); _sig = sig; _retryAt = DateTime.MinValue; }

        if (_pc == null && DateTime.UtcNow >= _retryAt) Open(want);
        if (_pc == null)
        {
            foreach (var k in want) s[k] = double.NaN;
            return;
        }

        try
        {
            foreach (var h in _hw) h.Update();
            foreach (var (k, sensor) in _map) s[k] = sensor.Value ?? double.NaN;
        }
        catch
        {
            foreach (var k in want) s[k] = double.NaN;
            Close();
            _retryAt = DateTime.UtcNow.AddSeconds(30);
        }
    }

    void Open(List<SensorKey> want)
    {
        try
        {
            var ids = new Dictionary<string, SensorKey>();
            foreach (var k in want) ids[CustomSensors.Id(k)!] = k;
            if (ids.Count == 0) return;

            var pc = new Computer();
            foreach (var id in ids.Keys)
            {
                string group = id.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (group is "intelcpu" or "amdcpu") pc.IsCpuEnabled = true;
                else if (group is "lpc" or "motherboard" or "ec") pc.IsMotherboardEnabled = true;
                else if (group.StartsWith("gpu")) pc.IsGpuEnabled = true;
                else if (group is "ram" or "memory") pc.IsMemoryEnabled = true;
                else if (group is "nvme" or "hdd" or "ssd" or "storage") pc.IsStorageEnabled = true;
                else if (group == "nic") pc.IsNetworkEnabled = true;
                else if (group == "battery") pc.IsBatteryEnabled = true;
            }
            pc.Open();
            _pc = pc;

            var hw = new HashSet<IHardware>();
            void Walk(IHardware h)
            {
                h.Update();
                foreach (var s in h.Sensors)
                    if (ids.TryGetValue(s.Identifier.ToString(), out var key)) { _map[key] = s; hw.Add(h); }
                foreach (var sub in h.SubHardware) Walk(sub);
            }
            foreach (var h in pc.Hardware) Walk(h);
            _hw.AddRange(hw);
            if (_map.Count == 0) { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
        }
        catch { Close(); _retryAt = DateTime.UtcNow.AddSeconds(30); }
    }

    void Close()
    {
        try { _pc?.Close(); } catch { }
        _pc = null;
        _map.Clear();
        _hw.Clear();
    }

    public override void Idle() { Close(); _sig = ""; }
    public override void Dispose() => Close();
}
