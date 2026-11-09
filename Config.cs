using System.Text.Json;

namespace TaskbarStats;

public sealed class ModuleConfig
{
    /// <summary>Unique name of this module instance; the layout refers to it.</summary>
    public string Id { get; set; } = "";

    /// <summary>cpu | mem | gpu | disk | net | sensors. Defaults to <see cref="Id"/>.</summary>
    public string? Type { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>spacer: width in pixels (default 16).</summary>
    public int? SpaceWidth { get; set; }

    /// <summary>
    /// What the module shows, left to right. cpu: label, cores, history, bar, percent, temp.
    /// mem: label, bar, history, percent, used. gpu: label, bar, history, percent, temp, vram.
    /// sensors: label plus readout blocks "s1:Key" / "s2:KeyTop|KeyBottom". spacer: none. Empty means the module's default.
    /// </summary>
    public List<string>? Items { get; set; }

    /// <summary>Legacy sensors grid (one list per text line); converted to readout blocks on load.</summary>
    public List<List<string>>? Rows { get; set; }

    /// <summary>cpu module: one bar per physical core instead of per logical processor.</summary>
    public bool HideHyperThreading { get; set; } = true;

    internal string EffectiveType => (Type ?? Id).Trim().ToLowerInvariant();
}

/// <summary>One block of the widget: a top row and a bottom row of module ids, each stacking to the right.</summary>
public sealed class BlockConfig
{
    public List<string>? Top { get; set; }
    public List<string>? Bottom { get; set; }
}

public sealed class AppConfig
{
    /// <summary>Config schema version; older files are replaced by the defaults.</summary>
    public int Version { get; set; } = 3;

    /// <summary>Sensor polling / redraw interval.</summary>
    public int IntervalMs { get; set; } = 2000;

    /// <summary>Ask for administrator rights whenever the program is opened (CPU and motherboard sensors need them).</summary>
    public bool RunAsAdmin { get; set; } = true;

    /// <summary>Show temperatures in Fahrenheit.</summary>
    public bool Fahrenheit { get; set; }

    /// <summary>Write the °C / °F sign after temperatures.</summary>
    public bool ShowTempUnit { get; set; } = true;

    /// <summary>Font of the value text on the taskbar (percent, temperature, readouts), its weight and size in pixels.</summary>
    public string TextFont { get; set; } = "Segoe UI";
    public bool TextBold { get; set; }
    public float TextSize { get; set; } = 10f;

    /// <summary>Write "rpm" after fan speeds.</summary>
    public bool ShowRpmUnit { get; set; } = true;

    /// <summary>Click-popup sections switched off, as "module.section" (e.g. "cpu.procs").</summary>
    public List<string> DisabledSections { get; set; } = new();

    /// <summary>How many processes the popup lists.</summary>
    public int ProcessCount { get; set; } = 5;

    /// <summary>Set once the Apple SMC driver has been seen and the component switched on automatically.</summary>
    public bool SmcAutoChecked { get; set; }

    /// <summary>left | center | right, relative to the taskbar.</summary>
    public string Anchor { get; set; } = "left";

    /// <summary>Offsets in device-independent pixels (96 dpi).</summary>
    public int OffsetX { get; set; } = 8;
    public int OffsetY { get; set; } = 0;

    /// <summary>auto | dark | light</summary>
    public string Theme { get; set; } = "auto";

    /// <summary>Components (sensor sources) switched off: cpumem, disk, net, nvidia, amd, lhm, smc.</summary>
    public List<string> DisabledComponents { get; set; } = new() { "smc" };

    /// <summary>1 = a single row of modules, 2 = top and bottom rows.</summary>
    public int Rows { get; set; } = 2;

    /// <summary>
    /// Auto-flow width budget (dip) of one block, used when <see cref="Blocks"/> is empty: a module that does
    /// not fit the top row drops to the bottom row, and when that is full too it starts a new block to the right.
    /// </summary>
    public int BlockWidth { get; set; } = 130;

    /// <summary>Explicit placement. Leave null/empty to let the modules flow in the order of <see cref="Modules"/>.</summary>
    public List<BlockConfig>? Blocks { get; set; } = new()
    {
        new() { Top = new() { "cpu", "mem" }, Bottom = new() { "sensors" } },
    };

    /// <summary>One-time conversions of older files: sensors grid to readout blocks, several blocks to one.</summary>
    void Normalize()
    {
        foreach (var m in Modules.Where(m => m.EffectiveType == "sensors"))
        {
            if (m.Items == null || !m.Items.Any(i => i.Trim().Equals("grid", StringComparison.OrdinalIgnoreCase))) continue;
            var rows = m.Rows is { Count: > 0 } ? m.Rows : new List<List<string>>
                { new() { "CpuTemp", "GpuTemp", "MemUsedPct" }, new() { "CpuTotal", "GpuLoad", "MemUsedMb" } };
            var items = new List<string>();
            foreach (var i in m.Items)
            {
                if (!i.Trim().Equals("grid", StringComparison.OrdinalIgnoreCase)) { items.Add(i); continue; }
                int cols = rows.Max(r => r.Count);
                int slots = rows.Count >= 2 ? 2 : 1;
                for (int c = 0; c < cols; c++)
                    items.Add($"s{slots}:" + string.Join("|", rows.Take(slots).Select(r => c < r.Count ? r[c] : "")));
            }
            m.Items = items;
            m.Rows = null;
        }

        if (Blocks is { Count: > 1 })
        {
            var one = new BlockConfig { Top = new(), Bottom = new() };
            foreach (var b in Blocks)
            {
                one.Top.AddRange(b.Top ?? new());
                one.Bottom.AddRange(b.Bottom ?? new());
            }
            Blocks = new() { one };
        }
    }

    public List<ModuleConfig> Modules { get; set; } = new()
    {
        new() { Id = "cpu", Items = new() { "label", "cores" } },
        new() { Id = "mem", Items = new() { "label", "bar" } },
        new()
        {
            Id = "sensors",
            Items = new() { "label", "s2:CpuTemp|CpuTotal", "s2:GpuTemp|GpuLoad", "s2:MemUsedPct|MemUsedMb" },
        },
        new() { Id = "gpu", Enabled = false, Items = new() { "label", "bar" } },
        new() { Id = "disk", Enabled = false, Items = new() { "label", "bar" } },
        new() { Id = "net", Enabled = false, Items = new() { "label", "speeds" } },
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskbarStats");

    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <param name="overridePath">Explicit config file (used for testing); the default file is never written then.</param>
    public static AppConfig Load(string? overridePath = null)
    {
        string file = overridePath ?? FilePath;
        try
        {
            if (File.Exists(file))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(file), Json);
                if (cfg != null && cfg.Version >= 3)
                {
                    cfg.IntervalMs = Math.Clamp(cfg.IntervalMs, 250, 10_000);
                    cfg.Rows = cfg.Rows >= 2 ? 2 : 1;
                    cfg.ProcessCount = Math.Clamp(cfg.ProcessCount, 1, 15);
                    cfg.DisabledSections ??= new();
                    cfg.Normalize();
                    return cfg;
                }
            }
        }
        catch { /* fall back to defaults on a corrupt file */ }

        var def = new AppConfig();
        if (overridePath == null) def.Save();
        return def;
    }

    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* read-only profile etc.: not fatal */ }
    }
}
