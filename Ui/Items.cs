using System.Drawing;
using System.Drawing.Drawing2D;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>Shared geometry, in device-independent pixels. The whole surface is scaled by the DPI factor.</summary>
internal static class Geo
{
    // total widget height for one / two rows of modules
    public const float HeightOneRow = 36f, HeightTwoRows = 44f;
    public const float PadVOneRow = 4f, PadVTwoRows = 2f;
    public const float RowGap = 2f;

    public const float ItemGap = 4f;      // between items of one module
    public const float ModuleGap = 10f;   // between modules of one row
    public const float BlockGap = 14f;    // between blocks (the "sideways" overflow)
    public const float PadH = 4f;         // outer left/right padding
}

internal static class Fmt
{
    public static bool Fahrenheit, RpmUnit = true, TempUnit = true;

    public static void Configure(AppConfig c)
    {
        Fahrenheit = c.Fahrenheit;
        RpmUnit = c.ShowRpmUnit;
        TempUnit = c.ShowTempUnit;
        Theme.Configure(c);
    }

    public static string Temp(double v) =>
        double.IsNaN(v) ? "--" : Fahrenheit ? $"{(int)Math.Round(v * 9 / 5 + 32)}{(TempUnit ? "°F" : "")}"
                                            : $"{(int)Math.Round(v)}{(TempUnit ? "°C" : "")}";
    public static string Rpm(double v) => double.IsNaN(v) ? "--" : RpmUnit ? $"{(int)Math.Round(v)}rpm" : $"{(int)Math.Round(v)}";
    public static string TempTemplate => Fahrenheit ? (TempUnit ? "212°F" : "212") : (TempUnit ? "100°C" : "100");
    public static string RpmTemplate => RpmUnit ? "9999rpm" : "9999";

    /// <summary>The value as 0..100 for bars, rings and history graphs.</summary>
    public static double Percent(SensorKey k, double v)
    {
        if (double.IsNaN(v)) return v;
        if (CustomSensors.IsCustom(k))
            return CustomSensors.Kind(k) switch
            {
                "fan" => v / 50, "clock" => v / 60, "power" => v, "voltage" => v * 20,
                "data" => v * 100 / 16, "smalldata" => v / 163.84, "throughput" => v / 1048576.0,
                _ => v,
            };
        return k switch
        {
            SensorKey.FanRpm => v / 50,
            SensorKey.MemUsedMb or SensorKey.GpuMemUsedMb => v / 163.84,
            SensorKey.DiskReadKBs or SensorKey.DiskWriteKBs or SensorKey.NetDownKBs or SensorKey.NetUpKBs => v / 1024.0,
            _ => v,
        };
    }
    public static string Pct(double v) => double.IsNaN(v) ? "--" : $"{(int)Math.Round(v)}%";
    public static string Gb(double mb) => double.IsNaN(mb) ? "--" : $"{mb / 1024.0:0.0}G";

    /// <summary>Throughput in KB/s as a short string: 512K, 1.2M, 130M.</summary>
    public static string Speed(double kbs)
    {
        if (double.IsNaN(kbs)) return "--";
        if (kbs < 1000) return $"{(int)kbs}K";
        double m = kbs / 1024.0;
        return m < 100 ? $"{m:0.0}M" : $"{(int)m}M";
    }

    public static string ForKey(SensorKey k, double v) => CustomSensors.IsCustom(k) ? CustomSensors.Format(CustomSensors.Kind(k), v) : k switch
    {
        SensorKey.CpuTemp or SensorKey.GpuTemp => Temp(v),
        SensorKey.MemUsedMb or SensorKey.GpuMemUsedMb => Gb(v),
        SensorKey.NetDownKBs => "\u2193" + Speed(v),
        SensorKey.NetUpKBs => "\u2191" + Speed(v),
        SensorKey.DiskReadKBs => "R" + Speed(v),
        SensorKey.DiskWriteKBs => "W" + Speed(v),
        SensorKey.FanRpm => Rpm(v),
        _ => Pct(v),
    };

    /// <summary>Widest string this sensor can show, used to size the cell so the layout never jitters.</summary>
    public static string Template(SensorKey k) => CustomSensors.IsCustom(k) ? CustomSensors.Template(CustomSensors.Kind(k)) : k switch
    {
        SensorKey.CpuTemp or SensorKey.GpuTemp => TempTemplate,
        SensorKey.MemUsedMb or SensorKey.GpuMemUsedMb => "99.9G",
        SensorKey.NetDownKBs or SensorKey.NetUpKBs => "\u219399.9M",
        SensorKey.DiskReadKBs or SensorKey.DiskWriteKBs => "R99.9M",
        SensorKey.FanRpm => RpmTemplate,
        _ => "100%",
    };

    public static SensorKey? ParseKey(string? name) =>
        name is { Length: > 0 } && name[0] == '@' ? CustomSensors.Register(name) :
        Enum.TryParse<SensorKey>(name, ignoreCase: true, out var k) && k != SensorKey.Count && k != SensorKey.CpuCores
            ? k : null;
}

/// <summary>
/// One visual element inside a module. Width never depends on the height it is given; it draws
/// itself inside the band (x, y, h) its row hands it.
/// </summary>
internal interface IItem
{
    SensorKey[] Needs { get; }
    float MinHeight { get; }
    float Width(Theme t);

    /// <summary>Take what to draw from the snapshot. Returns true if anything visible changed.</summary>
    bool Prepare(Snapshot s);

    void Draw(Graphics g, float x, float y, float h, Theme t);
}

internal static class ItemUtil
{
    public const int Steps = 64;   // value quantisation: redraw only when a bar moves by 1/64

    public static readonly StringFormat Center = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip,
    };

    public static int Quantise(double percent) =>
        double.IsNaN(percent) ? 0 : (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * Steps);

    public static void Box(Graphics g, float x, float y, float w, float h, Theme t)
    {
        using var pen = new Pen(t.Outline, 1f);
        g.DrawRectangle(pen, x + 0.5f, y + 0.5f, w - 1f, h - 1f);
    }

    public static bool Set(ref string field, string value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }

    public static bool Set(ref int field, int value)
    {
        if (field == value) return false;
        field = value;
        return true;
    }
}

/// <summary>Stacked letters (C / P / U) to the left of a graph, like iStat Menus.</summary>
internal sealed class LabelItem : IItem
{
    const float W = 9f;
    readonly string _text;
    public LabelItem(string text) => _text = text;

    public SensorKey[] Needs => Array.Empty<SensorKey>();
    public float MinHeight => 15f;
    public float Width(Theme t) => W;
    public bool Prepare(Snapshot s) => false;

    // The same font, size, weight and letter pitch for every label, whatever row it sits in; only the vertical
    // centre follows the row. Each letter is centred on its own cell by cap height and by its own width.
    const float Em = 7f, Pitch = 5.9f;

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        var font = t.LabelFont(Em);
        float cap = Em * 0.70f;
        float ascent = Em * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
        float y0 = y + (h - _text.Length * Pitch) / 2f;
        using var br = new SolidBrush(t.LabelColor);
        for (int i = 0; i < _text.Length; i++)
        {
            string ch = _text[i].ToString();
            float lw = Theme.TextWidth(ch, font);
            float top = y0 + i * Pitch + (Pitch + cap) / 2f - ascent;
            g.DrawString(ch, font, br, x + (W - lw) / 2f, top, StringFormat.GenericTypographic);
        }
    }
}

/// <summary>One bar per CPU core. Efficiency cores of a hybrid CPU are drawn dimmer.</summary>
internal sealed class CoresItem : IItem
{
    const float BarW = 2.6f, BarGap = 1f, Pad = 1.5f;

    readonly bool _hideHt;
    readonly int _n;
    readonly int[] _bars;

    public CoresItem(bool hideHyperThreading)
    {
        _hideHt = hideHyperThreading;
        _n = hideHyperThreading ? CpuTopology.Physical : CpuTopology.Logical;
        _bars = new int[_n];
    }

    public SensorKey[] Needs { get; } = { SensorKey.CpuCores };
    public float MinHeight => 12f;

    float BoxW => _n * (BarW + BarGap) - BarGap + 2 * Pad;
    public float Width(Theme t) => BoxW;

    public bool Prepare(Snapshot s)
    {
        var src = _hideHt ? s.PhysCores : s.Cores;
        bool changed = false;
        for (int i = 0; i < _n; i++)
            changed |= ItemUtil.Set(ref _bars[i], ItemUtil.Quantise(i < src.Length ? src[i] : 0));
        return changed;
    }

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        ItemUtil.Box(g, x, y, BoxW, h, t);

        float inner = h - 2 * Pad;
        using var perf = new SolidBrush(t.Fg);
        using var eff = new SolidBrush(Color.FromArgb(150, t.Fg));
        using var hot = new SolidBrush(t.Warn);
        for (int i = 0; i < _n; i++)
        {
            float frac = _bars[i] / (float)ItemUtil.Steps;
            float bh = Math.Max(frac * inner, 0.7f);             // idle cores keep a thin baseline
            bool isEff = _hideHt && i < CpuTopology.CoreIsEfficiency.Length && CpuTopology.CoreIsEfficiency[i];
            var brush = frac > 0.85f ? hot : isEff ? eff : perf;
            g.FillRectangle(brush, x + Pad + i * (BarW + BarGap), y + Pad + inner - bh, BarW, bh);
        }
    }
}

/// <summary>Scrolling history graph of one sensor (0..100).</summary>
internal sealed class HistoryItem : IItem
{
    const int Samples = 36;
    const float W = 46f, Pad = 1.5f;

    readonly SensorKey _key;
    readonly float[] _ring = new float[Samples];
    int _head;
    bool _auto, _frozen;

    /// <param name="auto">Scale to the largest recent value (throughput) instead of 0..100.</param>
    public HistoryItem(SensorKey key, bool auto = false)
    {
        _key = key;
        _auto = auto;
        Needs = new[] { key };
    }

    /// <summary>A fixed made-up curve, used for the thumbnails in the settings window.</summary>
    public HistoryItem Demo()
    {
        _frozen = true;
        _auto = false;
        for (int i = 0; i < Samples; i++)
            _ring[i] = (float)(32 + 24 * Math.Sin(i * 0.45) + 14 * Math.Sin(i * 1.3 + 1));
        return this;
    }

    public SensorKey[] Needs { get; }
    public float MinHeight => 12f;
    public float Width(Theme t) => W;

    public bool Prepare(Snapshot s)
    {
        double v = _auto ? s[_key] : Fmt.Percent(_key, s[_key]);
        if (_frozen || double.IsNaN(v)) return false;
        _ring[_head] = (float)(_auto ? Math.Max(v, 0) : Math.Clamp(v, 0, 100));
        _head = (_head + 1) % Samples;
        return true;                                            // moves every tick by design
    }

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        ItemUtil.Box(g, x, y, W, h, t);

        float inner = h - 2 * Pad, iw = W - 2 * Pad;
        float max = 100f;
        if (_auto) { max = 256f; foreach (var r in _ring) max = Math.Max(max, r); }
        var pts = new PointF[Samples + 2];
        float baseY = y + Pad + inner;
        pts[0] = new PointF(x + Pad, baseY);
        for (int i = 0; i < Samples; i++)
        {
            float v = _ring[(_head + i) % Samples];
            pts[i + 1] = new PointF(x + Pad + iw * i / (Samples - 1), baseY - Math.Min(v / max, 1f) * inner);
        }
        pts[Samples + 1] = new PointF(x + Pad + iw, baseY);

        using var br = new SolidBrush(Color.FromArgb(215, t.Fg));
        g.FillPolygon(br, pts);
    }
}

/// <summary>Vertical fill bar (memory use, GPU load, ...).</summary>
internal sealed class BarItem : IItem
{
    const float Pad = 1.5f;
    readonly float W;
    readonly SensorKey _key;
    int _fill;

    public BarItem(SensorKey key, float width = 16f)
    {
        _key = key;
        W = width;
        Needs = new[] { key };
    }

    public SensorKey[] Needs { get; }
    public float MinHeight => 12f;
    public float Width(Theme t) => W;
    public bool Prepare(Snapshot s) => ItemUtil.Set(ref _fill, ItemUtil.Quantise(Fmt.Percent(_key, s[_key])));

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        ItemUtil.Box(g, x, y, W, h, t);
        float inner = h - 2 * Pad;
        float frac = _fill / (float)ItemUtil.Steps;
        float bh = Math.Max(frac * inner, 0.7f);
        using var br = new SolidBrush(frac > 0.9f ? t.Warn : t.Fg);
        g.FillRectangle(br, x + Pad, y + Pad + inner - bh, W - 2 * Pad, bh);
    }
}

/// <summary>Ring graph: the arc is filled clockwise from the top in proportion to the value.</summary>
internal sealed class CircleItem : IItem
{
    const float D = 17f, Stroke = 2.6f;
    readonly SensorKey _key;
    int _fill;

    public CircleItem(SensorKey key)
    {
        _key = key;
        Needs = new[] { key };
    }

    public SensorKey[] Needs { get; }
    public float MinHeight => 14f;
    public float Width(Theme t) => D;
    public bool Prepare(Snapshot s) => ItemUtil.Set(ref _fill, ItemUtil.Quantise(Fmt.Percent(_key, s[_key])));

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        float d = Math.Min(D, h);
        var r = new RectangleF(x + (D - d) / 2 + Stroke / 2, y + (h - d) / 2 + Stroke / 2, d - Stroke, d - Stroke);
        float frac = _fill / (float)ItemUtil.Steps;
        using var track = new Pen(Color.FromArgb(60, t.Fg), Stroke);
        g.DrawEllipse(track, r);
        if (frac <= 0) return;
        using var arc = new Pen(frac > 0.9f ? t.Warn : t.Fg, Stroke);
        g.DrawArc(arc, r, -90f, Math.Max(frac * 360f, 8f));
    }
}

/// <summary>A single value as large text, vertically centred (22%, 55°C, 12.4G).</summary>
internal sealed class TextItem : IItem
{
    readonly SensorKey _key;
    string _text = "";

    public TextItem(SensorKey key)
    {
        _key = key;
        Needs = new[] { key };
    }

    public SensorKey[] Needs { get; }
    public float MinHeight => 14f;
    public float Width(Theme t) => Theme.TextWidth(Fmt.Template(_key), t.ValueLarge);
    public bool Prepare(Snapshot s) => ItemUtil.Set(ref _text, Fmt.ForKey(_key, s[_key]));

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        using var br = new SolidBrush(t.Fg);
        g.DrawString(_text, t.ValueLarge, br, new RectangleF(x, y, Width(t), h), ItemUtil.Center);
    }
}

/// <summary>
/// Sensor readouts as a small grid of text (the SENSORS module): each row of the configuration is one
/// text line, each entry one column; a null entry is an empty cell that keeps its place.
/// </summary>
internal sealed class GridItem : IItem
{
    const float ColGap = 6f, LineH = 11f, EmptyColW = 14f;

    readonly SensorKey?[][] _rows;
    readonly string[][] _text;
    readonly int _cols;

    public GridItem(SensorKey?[][] rows)
    {
        _rows = rows;
        _cols = rows.Max(r => r.Length);
        _text = rows.Select(r => new string[r.Length]).ToArray();
        for (int i = 0; i < _text.Length; i++) Array.Fill(_text[i], "");
        Needs = rows.SelectMany(r => r).Where(k => k.HasValue).Select(k => k!.Value).Distinct().ToArray();
    }

    public SensorKey[] Needs { get; }
    public float MinHeight => _rows.Length * LineH;

    float ColWidth(int col, Theme t)
    {
        float w = 0;
        foreach (var r in _rows)
            if (col < r.Length && r[col] is { } k) w = Math.Max(w, Theme.TextWidth(Fmt.Template(k), t.Value));
        return w > 0 ? w : EmptyColW;
    }

    public float Width(Theme t)
    {
        float w = 0;
        for (int c = 0; c < _cols; c++) w += ColWidth(c, t) + (c > 0 ? ColGap : 0);
        return w;
    }

    public bool Prepare(Snapshot s)
    {
        bool changed = false;
        for (int r = 0; r < _rows.Length; r++)
            for (int c = 0; c < _rows[r].Length; c++)
                if (_rows[r][c] is { } k) changed |= ItemUtil.Set(ref _text[r][c], Fmt.ForKey(k, s[k]));
        return changed;
    }

    public void Draw(Graphics g, float x, float y, float h, Theme t)
    {
        using var br = new SolidBrush(t.Fg);
        float rowH = h / _rows.Length;
        float cx = x;
        for (int c = 0; c < _cols; c++)
        {
            float cw = ColWidth(c, t);
            for (int r = 0; r < _rows.Length; r++)
                if (c < _rows[r].Length && _rows[r][c].HasValue)
                    g.DrawString(_text[r][c], t.Value, br, new RectangleF(cx, y + r * rowH, cw, rowH), ItemUtil.Center);
            cx += cw + ColGap;
        }
    }
}
