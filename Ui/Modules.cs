using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>
/// A module is one self-contained unit of the widget (CPU, MEM, SENSORS, ...): a row of items chosen
/// in its configuration. Where it sits is decided by <see cref="WidgetLayout"/>.
/// </summary>
public interface IWidgetModule
{
    string Id { get; }
    SensorKey[] Needs { get; }
    float MinHeight { get; }
    float Width(Theme t);
    bool Prepare(Snapshot s);
    void Draw(Graphics g, float x, float y, float h, Theme t);
}

internal sealed class Module : IWidgetModule
{
    readonly List<IItem> _items;

    public Module(string id, List<IItem> items)
    {
        Id = id;
        _items = items;
        Needs = items.SelectMany(i => i.Needs).Distinct().ToArray();
        MinHeight = items.Max(i => i.MinHeight);
    }

    public string Id { get; }
    public SensorKey[] Needs { get; }
    public float MinHeight { get; }

    public float Width(Theme t)
    {
        float w = 0;
        for (int i = 0; i < _items.Count; i++) w += _items[i].Width(t) + (i > 0 ? Geo.ItemGap : 0);
        return w;
    }

    public bool Prepare(Snapshot s)
    {
        bool changed = false;
        foreach (var it in _items) changed |= it.Prepare(s);
        return changed;
    }

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        foreach (var it in _items)
        {
            it.Draw(g, x, y, h, t);
            x += it.Width(t) + Geo.ItemGap;
        }
    }
}

/// <summary>Empty room between modules, placed in the layout like any other module.</summary>
internal sealed class SpacerModule : IWidgetModule
{
    readonly float _w;
    public SpacerModule(string id, int width) { Id = id; _w = Math.Clamp(width, 0, 400); }
    public string Id { get; }
    public SensorKey[] Needs => Array.Empty<SensorKey>();
    public float MinHeight => 12f;
    public float Width(Theme t) => _w;
    public bool Prepare(Snapshot s) => false;
    public void Draw(Graphics g, float x, float y, float h, Theme t) { }
}

/// <summary>Builds modules from their configuration. Unknown names are skipped, never fatal.</summary>
internal static class ModuleFactory
{
    public static readonly string[] KnownTypes = { "cpu", "gpu", "mem", "disk", "net", "sensors" };

    public static IWidgetModule? Create(ModuleConfig c)
    {
        string type = c.EffectiveType;
        if (type == "spacer") return new SpacerModule(c.Id, c.SpaceWidth ?? 16);
        var names = c.Items is { Count: > 0 } ? c.Items : DefaultItems(type);
        var items = new List<IItem>();
        foreach (var raw in names)
        {
            string nm = raw.Trim();
            if (!IsSlotted(nm)) nm = nm.ToLowerInvariant();            // sensor names keep their case
            var item = CreateItem(c, type, nm);
            if (item != null) items.Add(item);
        }
        return items.Count == 0 ? null : new Module(c.Id, items);
    }

    /// <summary>Items a module type offers, in drawing order.</summary>
    public static string[] ItemNames(string type) => type switch
    {
        "cpu" => new[] { "label", "cores", "history", "bar", "slim", "circle", "percent", "temp" },
        "mem" => new[] { "label", "bar", "slim", "history", "circle", "percent", "used" },
        "gpu" => new[] { "label", "bar", "slim", "history", "circle", "percent", "temp", "vram" },
        "disk" => new[] { "label", "bar", "slim", "history", "circle", "percent", "used", "speeds" },
        "net" => new[] { "label", "speeds", "down", "up", "history" },
        "sensors" => new[] { "label" },                      // plus any number of readout blocks, see IsReadout
        _ => Array.Empty<string>(),
    };

    /// <summary>"s1:Key" is a readout block with one value, "s2:Top|Bottom" one with two stacked values.</summary>
    public static bool IsReadout(string item) => item.Length >= 3 && item[0] == 's' && (item[1] == '1' || item[1] == '2') && item[2] == ':';

    /// <summary>"b:Key", "h:Key", "c:Key": a bar, history or ring graph of one chosen sensor (sensors module).</summary>
    public static bool IsGraph(string item) => item.Length >= 2 && item[1] == ':' && item[0] is 'b' or 'h' or 'c';

    /// <summary>Items whose sensor the user picks: readout blocks and graphs.</summary>
    public static bool IsSlotted(string item) => IsReadout(item) || IsGraph(item);

    public static bool ValidItem(string type, string item) =>
        type == "sensors" ? item == "label" || IsSlotted(item) : ItemNames(type).Contains(item);

    public static List<string> DefaultItems(string type) => type switch
    {
        "cpu" => new() { "label", "cores" },
        "mem" => new() { "label", "bar" },
        "gpu" => new() { "label", "bar" },
        "disk" => new() { "label", "bar" },
        "net" => new() { "label", "speeds" },
        "sensors" => new() { "label", "s2:CpuTemp|CpuTotal", "s2:GpuTemp|GpuLoad", "s2:MemUsedPct|MemUsedMb" },
        _ => new(),
    };

    /// <summary>One item of a module, e.g. for the thumbnails of the settings window.</summary>
    public static IItem? CreateItem(ModuleConfig c, string name) => CreateItem(c, c.EffectiveType, name);

    static IItem? CreateItem(ModuleConfig c, string type, string name)
    {
        if (name == "label")
            return type switch
            {
                "cpu" => new LabelItem("CPU"),
                "mem" => new LabelItem("MEM"),
                "gpu" => new LabelItem("GPU"),
                "disk" => new LabelItem("DSK"),
                "net" => new LabelItem("NET"),
                "sensors" => new LabelItem("SEN"),
                _ => null,
            };

        switch (type)
        {
            case "cpu":
                return name switch
                {
                    "cores" => new CoresItem(c.HideHyperThreading),
                    "history" => new HistoryItem(SensorKey.CpuTotal),
                    "circle" => new CircleItem(SensorKey.CpuTotal),
                    "bar" => new BarItem(SensorKey.CpuTotal),
                    "slim" => new BarItem(SensorKey.CpuTotal, 8f),
                    "percent" => new TextItem(SensorKey.CpuTotal),
                    "temp" => new TextItem(SensorKey.CpuTemp),
                    _ => null,
                };
            case "mem":
                return name switch
                {
                    "bar" => new BarItem(SensorKey.MemUsedPct),
                    "slim" => new BarItem(SensorKey.MemUsedPct, 8f),
                    "history" => new HistoryItem(SensorKey.MemUsedPct),
                    "circle" => new CircleItem(SensorKey.MemUsedPct),
                    "percent" => new TextItem(SensorKey.MemUsedPct),
                    "used" => new TextItem(SensorKey.MemUsedMb),
                    _ => null,
                };
            case "gpu":
                return name switch
                {
                    "bar" => new BarItem(SensorKey.GpuLoad),
                    "slim" => new BarItem(SensorKey.GpuLoad, 8f),
                    "history" => new HistoryItem(SensorKey.GpuLoad),
                    "circle" => new CircleItem(SensorKey.GpuLoad),
                    "percent" => new TextItem(SensorKey.GpuLoad),
                    "temp" => new TextItem(SensorKey.GpuTemp),
                    "vram" => new TextItem(SensorKey.GpuMemUsedMb),
                    _ => null,
                };
            case "disk":
                return name switch
                {
                    "bar" => new BarItem(SensorKey.DiskActivePct),
                    "slim" => new BarItem(SensorKey.DiskActivePct, 8f),
                    "history" => new HistoryItem(SensorKey.DiskActivePct),
                    "circle" => new CircleItem(SensorKey.DiskActivePct),
                    "percent" => new TextItem(SensorKey.DiskActivePct),
                    "used" => new TextItem(SensorKey.DiskUsedPct),
                    "speeds" => new GridItem(new SensorKey?[][]
                        { new SensorKey?[] { SensorKey.DiskReadKBs }, new SensorKey?[] { SensorKey.DiskWriteKBs } }),
                    _ => null,
                };
            case "net":
                return name switch
                {
                    "speeds" => new GridItem(new SensorKey?[][]
                        { new SensorKey?[] { SensorKey.NetDownKBs }, new SensorKey?[] { SensorKey.NetUpKBs } }),
                    "down" => new TextItem(SensorKey.NetDownKBs),
                    "up" => new TextItem(SensorKey.NetUpKBs),
                    "history" => new HistoryItem(SensorKey.NetDownKBs, auto: true),
                    _ => null,
                };
            case "sensors":
                if (IsReadout(name)) return CreateReadout(name);
                if (IsGraph(name) && Fmt.ParseKey(name[2..]) is { } gk)
                    return name[0] switch
                    {
                        'b' => new BarItem(gk),
                        'h' => new HistoryItem(gk),
                        _ => new CircleItem(gk),
                    };
                return null;
        }
        return null;
    }

    /// <summary>A readout block: one value as larger text, or two values stacked in two lines.</summary>
    static IItem CreateReadout(string item)
    {
        int slots = item[1] - '0';
        var keys = item[3..].Split('|').Select(Fmt.ParseKey).ToList();
        while (keys.Count < slots) keys.Add(null);
        if (slots == 1 && keys[0] is { } one) return new TextItem(one);
        return new GridItem(keys.Take(slots).Select(k => new[] { k }).ToArray());
    }
}

/// <summary>
/// Where the modules sit. The widget is made of blocks laid out left to right; a block has a top row
/// and a bottom row, and modules stack to the right inside a row.
///
/// Auto-flow (no explicit layout): a module goes into the top row of the current block if it fits the
/// block width budget, otherwise into the bottom row, otherwise it starts a new block to the right.
/// An explicit layout in the configuration places each module exactly.
/// </summary>
internal sealed class WidgetLayout
{
    public sealed class Block
    {
        public readonly List<IWidgetModule> Top = new(), Bottom = new();
        public List<IWidgetModule> Row(int i) => i == 0 ? Top : Bottom;
    }

    public readonly List<Block> Blocks = new();

    public IEnumerable<IWidgetModule> Modules =>
        Blocks.SelectMany(b => b.Top.Concat(b.Bottom));

    public bool IsEmpty => !Modules.Any();
    public bool TwoRows => Blocks.Any(b => b.Bottom.Count > 0);
    public float Height => TwoRows ? Geo.HeightTwoRows : Geo.HeightOneRow;

    // -------------------------------------------------------------- build

    public static WidgetLayout Build(AppConfig cfg, IReadOnlyDictionary<string, IWidgetModule> mods, Theme t)
    {
        var layout = new WidgetLayout();
        bool twoRows = cfg.Rows >= 2;

        if (cfg.Blocks is { Count: > 0 })
        {
            var placed = new HashSet<string>();
            foreach (var bc in cfg.Blocks)
            {
                var block = new Block();
                foreach (var id in bc.Top ?? new())
                    if (mods.TryGetValue(id, out var m) && placed.Add(id)) block.Top.Add(m);
                foreach (var id in bc.Bottom ?? new())
                    if (mods.TryGetValue(id, out var m) && placed.Add(id)) (twoRows ? block.Bottom : block.Top).Add(m);
                if (block.Top.Count + block.Bottom.Count > 0) layout.Blocks.Add(block);
            }
            return layout;
        }

        // auto-flow in the order of the module list
        float budget = Math.Max(cfg.BlockWidth, 40);
        Block? cur = null;
        foreach (var mc in cfg.Modules)
        {
            if (!mods.TryGetValue(mc.Id, out var m)) continue;
            float w = m.Width(t);
            cur ??= layout.AddBlock();

            if (Fits(cur.Top, w, budget, t)) cur.Top.Add(m);
            else if (twoRows && Fits(cur.Bottom, w, budget, t)) cur.Bottom.Add(m);
            else
            {
                cur = layout.AddBlock();
                cur.Top.Add(m);
            }
        }
        return layout;
    }

    Block AddBlock()
    {
        var b = new Block();
        Blocks.Add(b);
        return b;
    }

    static bool Fits(List<IWidgetModule> row, float w, float budget, Theme t) =>
        row.Count == 0 || RowWidth(row, t) + Geo.ModuleGap + w <= budget;

    static float RowWidth(List<IWidgetModule> row, Theme t)
    {
        float w = 0;
        for (int i = 0; i < row.Count; i++) w += row[i].Width(t) + (i > 0 ? Geo.ModuleGap : 0);
        return w;
    }

    // ------------------------------------------------------------ measure

    public float BlockWidth(Block b, Theme t) => Math.Max(RowWidth(b.Top, t), RowWidth(b.Bottom, t));

    public float TotalWidth(Theme t)
    {
        float w = 2 * Geo.PadH;
        for (int i = 0; i < Blocks.Count; i++) w += BlockWidth(Blocks[i], t) + (i > 0 ? Geo.BlockGap : 0);
        return w;
    }

    /// <summary>Heights of the two row bands. One row uses the whole height; two rows split what is left.</summary>
    (float y0, float h0, float y1, float h1) Bands()
    {
        if (!TwoRows)
        {
            float h = Geo.HeightOneRow - 2 * Geo.PadVOneRow;
            return (Geo.PadVOneRow, h, 0, 0);
        }

        float min0 = RowMin(0), min1 = RowMin(1);
        float avail = Geo.HeightTwoRows - 2 * Geo.PadVTwoRows - Geo.RowGap;
        float extra = Math.Max(0, avail - min0 - min1);
        float h0 = min0 + extra / 2, h1 = min1 + extra / 2;
        return (Geo.PadVTwoRows, h0, Geo.PadVTwoRows + h0 + Geo.RowGap, h1);
    }

    float RowMin(int row)
    {
        float m = 12f;
        foreach (var b in Blocks)
            foreach (var mod in b.Row(row)) m = Math.Max(m, mod.MinHeight);
        return m;
    }

    // --------------------------------------------------------------- draw

    public static void Configure(Graphics g, float scale)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.ResetTransform();
        g.ScaleTransform(scale, scale);
    }

    public void Draw(Graphics g, Theme t)
    {
        var (y0, h0, y1, h1) = Bands();
        float bx = Geo.PadH;
        foreach (var b in Blocks)
        {
            DrawRow(g, b.Top, bx, y0, h0, t);
            DrawRow(g, b.Bottom, bx, y1, h1, t);
            bx += BlockWidth(b, t) + Geo.BlockGap;
        }
    }

    /// <summary>The module under a point (dip, widget coordinates); null on gaps and spacers.</summary>
    public IWidgetModule? HitTest(float px, float py, Theme t)
    {
        var (y0, h0, y1, h1) = Bands();
        float bx = Geo.PadH;
        foreach (var b in Blocks)
        {
            float bw = BlockWidth(b, t);
            if (px >= bx && px < bx + bw)
            {
                bool single = !TwoRows;
                var row = single || py < (y1 > 0 ? y0 + h0 + Geo.RowGap / 2 : float.MaxValue) ? b.Top : b.Bottom;
                float x = bx;
                foreach (var m in row)
                {
                    float w = m.Width(t);
                    if (px >= x && px < x + w) return m is SpacerModule ? null : m;
                    x += w + Geo.ModuleGap;
                }
                return null;
            }
            bx += bw + Geo.BlockGap;
        }
        return null;
    }

    static void DrawRow(Graphics g, List<IWidgetModule> row, float x, float y, float h, Theme t)
    {
        foreach (var m in row)
        {
            m.Draw(g, x, y, h, t);
            x += m.Width(t) + Geo.ModuleGap;
        }
    }
}
