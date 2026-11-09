using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

internal sealed partial class SettingsForm
{
    const float SideW = 240;

    static readonly (string Id, string Title, string Icon)[] Nav =
    {
        ("general", "General", "general"), ("cpu", "CPU", "cpu"), ("gpu", "GPU", "gpu"), ("mem", "Memory", "memory"),
        ("disk", "Disks", "disk"), ("net", "Network", "network"), ("sensors", "Sensors", "sensor"), ("layout", "Layout", "layout"), ("sections", "Sections", "section"),
    };

    static readonly (string Id, string Title, string Icon) AboutNav = ("about", "About", "about");

    // order shown in the sensor palette
    static readonly SensorKey[] SensorOrder =
    {
        SensorKey.CpuTotal, SensorKey.CpuTemp, SensorKey.GpuLoad, SensorKey.GpuTemp, SensorKey.GpuMemUsedMb,
        SensorKey.MemUsedPct, SensorKey.MemUsedMb, SensorKey.DiskActivePct, SensorKey.DiskUsedPct,
        SensorKey.DiskReadKBs, SensorKey.DiskWriteKBs, SensorKey.NetDownKBs, SensorKey.NetUpKBs,
        SensorKey.FanRpm, SensorKey.GpuFanPct,
    };

    // ------------------------------------------------------------------ names

    static string TypeTitle(string type) => type switch
    {
        "cpu" => "CPU", "gpu" => "GPU", "mem" => "Memory", "disk" => "Disks", "net" => "Network", "sensors" => "Sensors", "spacer" => "Space", _ => type,
    };

    static string TypeIcon(string type) => type switch
    {
        "cpu" => "cpu", "gpu" => "gpu", "mem" => "memory", "disk" => "disk", "net" => "network", "sensors" => "sensor", _ => "general",
    };

    static string ItemTitle(string type, string name) => name switch
    {
        "label" => "Label",
        "cores" => "Core bars",
        "history" => "History graph",
        "circle" => "Circle graph",
        "bar" => "Bar",
        "percent" => type == "disk" ? "Activity" : "Percent",
        "temp" => "Temperature",
        "used" => type == "disk" ? "Used space" : "Used memory",
        "vram" => "VRAM used",
        "speeds" => type == "disk" ? "Read / write" : "Up / down",
        "down" => "Download",
        "up" => "Upload",
        "slim" => "Slim bar",
        _ when ModuleFactory.IsReadout(name) => name[1] == '1' ? "1 readout" : "2 readouts",
        _ when ModuleFactory.IsGraph(name) => name[0] switch { 'b' => "Bar graph", 'h' => "History graph", _ => "Circle graph" },
        _ => name,
    };

    static string SensorName(SensorKey k) => k switch
    {
        SensorKey.CpuTotal => "CPU %", SensorKey.CpuTemp => "CPU °C", SensorKey.MemUsedPct => "MEM %",
        SensorKey.MemUsedMb => "MEM GB", SensorKey.GpuLoad => "GPU %", SensorKey.GpuTemp => "GPU °C",
        SensorKey.GpuMemUsedMb => "VRAM GB", SensorKey.DiskActivePct => "DISK %", SensorKey.DiskUsedPct => "DISK used",
        SensorKey.DiskReadKBs => "DISK R", SensorKey.DiskWriteKBs => "DISK W",
        SensorKey.NetDownKBs => "NET ↓", SensorKey.NetUpKBs => "NET ↑",
        SensorKey.FanRpm => "FAN RPM", SensorKey.GpuFanPct => "GPU FAN %",
        _ when CustomSensors.IsCustom(k) => CustomSensors.Name(k),
        _ => k.ToString(),
    };

    // ------------------------------------------------------------- navigation

    void DrawNav(Graphics g, float H)
    {
        float y = TitleH + 6;
        foreach (var n in Nav) { NavItem(g, n, y); y += 42; }
        NavItem(g, AboutNav, H - 10 - 38);
    }

    void NavItem(Graphics g, (string Id, string Title, string Icon) n, float y)
    {
        var r = new RectangleF(8, y, SideW - 16, 38);
        bool sel = _page == n.Id;
        if (sel) Box(g, r, 6, _pal.NavSel);
        else if (Hov(r)) Box(g, r, 6, _pal.Hover);
        if (sel) Box(g, new RectangleF(r.X, r.Y + 11, 3, 16), 1.5f, _pal.Accent);
        Icons.Draw(g, n.Icon, new RectangleF(r.X + 16, r.Y + 10, 18, 18), _pal.Text);
        Txt(g, n.Title, _fBody, _pal.Text, new RectangleF(r.X + 46, r.Y, r.Width - 50, r.Height));
        string id = n.Id;
        Add(r, () => { _page = id; Invalidate(); });
    }

    void Section(Graphics g, string title, float x, float y, float w, string? hint = null)
    {
        Txt(g, title, _fSec, _pal.Text, new RectangleF(x, y, w, 28));
        if (hint != null) Txt(g, hint, _fSmall, _pal.Dim, new RectangleF(x, y, w, 28), StringAlignment.Far);
    }

    // ---------------------------------------------------------- settings rows

    delegate void RowControl(RectangleF row);

    /// <summary>A card of settings rows (title + description on the left, a control on the right).</summary>
    float RowsCard(Graphics g, float x, float y, float w, List<(string Title, string? Desc, RowControl? Ctl)> rows, float extra = 0)
    {
        const float RowH = 56;
        float h = rows.Count * RowH + extra;
        CardBox(g, new RectangleF(x, y, w, h));
        float ry = y;
        for (int i = 0; i < rows.Count; i++)
        {
            var (title, desc, ctl) = rows[i];
            if (i > 0)
            {
                using var pen = new Pen(_pal.Stroke);
                g.DrawLine(pen, x + 16, ry, x + w - 16, ry);
            }
            if (desc == null)
                Txt(g, title, _fBody, _pal.Text, new RectangleF(x + 18, ry, w - 320, RowH));
            else
            {
                Txt(g, title, _fBody, _pal.Text, new RectangleF(x + 18, ry + 9, w - 320, 20));
                Txt(g, desc, _fSmall, _pal.Dim, new RectangleF(x + 18, ry + 29, w - 320, 18));
            }
            ctl?.Invoke(new RectangleF(x, ry, w, RowH));
            ry += RowH;
        }
        return y + h;
    }

    static RectangleF RightOf(RectangleF row, float w, float h) =>
        new(row.Right - 18 - w, row.Y + (row.Height - h) / 2, w, h);

    // ---------------------------------------------------------- thumbnails

    IItem? Thumb(ModuleConfig m, string name)
    {
        if (ModuleFactory.IsGraph(name)) name = name[..2] + "CpuTotal";          // a stand-in picture for any sensor
        string key = m.Id + ":" + name;
        if (_thumbs.TryGetValue(key, out var it)) return it;
        it = ModuleFactory.CreateItem(m, name);
        if (it is HistoryItem hi) hi.Demo();
        it?.Prepare(Demo);                      // fixed sample values: the thumbnails never move
        _thumbs[key] = it;
        return it;
    }

    float ThumbW(IItem it, float k) => it.Width(_thTheme!) * k;

    void DrawThumb(Graphics g, IItem it, RectangleF area, float k)
    {
        float h = Math.Max(it.MinHeight, 24f);
        float w = it.Width(_thTheme!);
        var st = g.Save();
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.TranslateTransform(area.X + (area.Width - w * k) / 2, area.Y + (area.Height - h * k) / 2);
        g.ScaleTransform(k, k);
        it.Draw(g, 0, 0, h, _thTheme!);
        g.Restore(st);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    /// <summary>Left-to-right flow with wrapping; returns the rectangles and the bottom edge.</summary>
    static (List<RectangleF> Rects, float Bottom) Flow(IReadOnlyList<float> widths, float x, float y, float maxW, float h, float gap)
    {
        var rects = new List<RectangleF>();
        float cx = x, cy = y;
        foreach (float w in widths)
        {
            if (cx > x && cx + w > x + maxW) { cx = x; cy += h + gap; }
            rects.Add(new RectangleF(cx, cy, w, h));
            cx += w + gap;
        }
        return (rects, cy + h);
    }

    // ------------------------------------------------------------ module page

    const float Kt = 1.5f;            // thumbnail scale of the active strip

    void DrawModulePage(Graphics g, string type, float x0, float cw)
    {
        var m = Mod(type);
        bool sensors = type == "sensors";
        var active = ActiveItems(m);

        float y = 20;
        Section(g, "Active Modules", x0, y, cw, "Drag to reorder, drag out to remove");
        y += 36;

        // ---- active strip
        var strip = new RectangleF(x0, y, cw, 72);
        CardBox(g, strip);
        var widths = new List<float>();
        foreach (var n in active)
            widths.Add(ModuleFactory.IsSlotted(n) ? 104 : (Thumb(m, n) is { } t ? ThumbW(t, Kt) : 20) + 28);
        var (rects, _) = Flow(widths, strip.X + 12, strip.Y + 10, cw - 24, 52, 8);

        var stripHit = Add(strip);
        stripHit.CanDrop = o => o is ItemDrag;
        stripHit.Drop = (o, pt) => DropItem(m, (ItemDrag)o, rects.Count(r => r.X + r.Width / 2 < pt.X));
        stripHit.Indicator = pt =>
        {
            int idx = rects.Count(r => r.X + r.Width / 2 < pt.X);
            float bx = idx == 0 ? rects[0].X - 5 : rects[idx - 1].Right + 4;
            return new RectangleF(bx - 1, strip.Y + 8, 3, strip.Height - 16);
        };

        for (int i = 0; i < active.Count; i++)
        {
            string name = active[i];
            var r = rects[i];
            bool dim = _dragging is ItemDrag { From: >= 0 } d && d.From == i;
            Add(r, null, new ItemDrag(name, i));
            if (ModuleFactory.IsReadout(name))
            {
                DrawReadoutTile(g, m, i, name, r, dim);
                continue;
            }
            if (ModuleFactory.IsGraph(name))
            {
                DrawGraphTile(g, m, i, name, r, dim);
                continue;
            }
            CtlBox(g, r, 6, Hov(r));
            if (Thumb(m, name) is { } it && !dim) DrawThumb(g, it, r, Kt);
        }
        y += strip.Height + 20;

        // ---- available items
        Section(g, "Available Modules", x0, y, cw, "Drag in, or click to add");
        y += 36;

        var rest = ModuleFactory.ItemNames(type).Where(n => !active.Contains(n)).ToList();
        if (sensors) { rest.Add("s1:"); rest.Add("s2:|"); rest.Add("b:"); rest.Add("h:"); rest.Add("c:"); }
        var aw = new List<float>();
        foreach (var n in rest)
            aw.Add(ModuleFactory.IsReadout(n)
                ? 52 + Theme.TextWidth(ItemTitle(type, n), _fBody) + 24
                : (Thumb(m, n) is { } t ? ThumbW(t, 1.15f) : 20) + Theme.TextWidth(ItemTitle(type, n), _fBody) + 40);
        var (ar, abottom) = Flow(aw, x0 + 12, y + 12, cw - 24, 48, 8);
        float availH = Math.Max(rest.Count == 0 ? 56 : abottom - y + 12, 72);
        var avail = new RectangleF(x0, y, cw, availH);
        bool removing = _dragging is ItemDrag { From: >= 0 } && active.Count > 1;
        CardBox(g, avail);
        if (removing) Box(g, avail, 8, Blend(Color.Transparent, _pal.Danger, 0.12f), _pal.Danger, 1.5f);
        var availHit = Add(avail);
        availHit.CanDrop = o => o is ItemDrag { From: >= 0 } && active.Count > 1;
        availHit.Drop = (o, _) => RemoveItem(m, ((ItemDrag)o).From);
        if (rest.Count == 0)
            Txt(g, "Every item is in use", _fSmall, _pal.Dim, avail, StringAlignment.Center);
        for (int i = 0; i < rest.Count; i++)
        {
            string name = rest[i];
            var r = ar[i];
            CtlBox(g, r, 6, Hov(r));
            if (ModuleFactory.IsReadout(name))
            {
                int slots = name[1] - '0';
                for (int k = 0; k < slots; k++)
                {
                    var cell = slots == 2
                        ? new RectangleF(r.X + 12, r.Y + 7 + k * 18, 28, 15)
                        : new RectangleF(r.X + 12, r.Y + 7, 28, 34);
                    Box(g, cell, 4, Color.Transparent, _pal.Dim, 1f, true);
                }
                Txt(g, ItemTitle(type, name), _fBody, _pal.Text, new RectangleF(r.X + 52, r.Y, r.Width - 56, r.Height));
            }
            else if (Thumb(m, name) is { } it)
            {
                float tw = ThumbW(it, 1.15f);
                DrawThumb(g, it, new RectangleF(r.X + 12, r.Y, tw, r.Height), 1.15f);
                Txt(g, ItemTitle(type, name), _fBody, _pal.Text, new RectangleF(r.X + 20 + tw, r.Y, r.Width - tw - 24, r.Height));
            }
            string nm = name;
            Add(r, () => { var l = ActiveItems(m); l.Add(nm); m.Items = l; Commit(); }, new ItemDrag(nm, -1));
        }
        y += availH + 20;

        // ---- sensors to put into the readout blocks
        if (sensors)
        {
            Section(g, "Sensors", x0, y, cw, "Drag into a readout block, or click to fill the next empty slot");
            y += 36;
            var sw = SensorOrder.Select(_ => 90f).ToList();
            var (sr, sbottom) = Flow(sw, x0 + 12, y + 12, cw - 24, 30, 6);
            var sCard = new RectangleF(x0, y, cw, sbottom - y + 12);
            bool clearing = _dragging is SenDrag { Block: >= 0 };
            CardBox(g, sCard);
            if (clearing) Box(g, sCard, 8, Blend(Color.Transparent, _pal.Danger, 0.12f), _pal.Danger, 1.5f);
            var sHit = Add(sCard);
            sHit.CanDrop = o => o is SenDrag { Block: >= 0 };
            sHit.Drop = (o, _) => ClearSlot(m, (SenDrag)o);
            for (int i = 0; i < SensorOrder.Length; i++)
            {
                var k = SensorOrder[i];
                var t = sr[i];
                CtlBox(g, t, 6, Hov(t));
                Txt(g, SensorName(k), _fSmall, _pal.Text, t, StringAlignment.Center);
                Add(t, () => FillFirstEmpty(m, k), new SenDrag(k, -1, -1));
            }
            y += sCard.Height + 20;
        }

        // ---- settings of the whole module
        Section(g, "Settings", x0, y, cw);
        y += 36;

        var rows = new List<(string, string?, RowControl?)>
        {
            ("Show in taskbar", "Place or remove it on the Layout page",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, IsPlaced(type), () =>
                {
                    if (IsPlaced(type)) Unplace(type); else PlaceAtEnd(type);
                })),
        };
        if (sensors)
        {
            rows.Add(("Temperature unit", "Applies to every temperature on the taskbar",
                row => Segmented(g, RightOf(row, 150, 30), new[] { "°C", "°F" }, _cfg.Fahrenheit ? 1 : 0,
                    i => { _cfg.Fahrenheit = i == 1; Commit(); })));
            rows.Add(("Show temperature unit", "Write °C / °F after temperatures",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, _cfg.ShowTempUnit, () =>
                {
                    _cfg.ShowTempUnit = !_cfg.ShowTempUnit;
                    Commit();
                })));
            rows.Add(("Show rpm unit", "Write \"rpm\" after fan speeds",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, _cfg.ShowRpmUnit, () =>
                {
                    _cfg.ShowRpmUnit = !_cfg.ShowRpmUnit;
                    Commit();
                })));
        }
        if (type == "cpu")
            rows.Add(("One bar per physical core", "Leave out the Hyper-Threading siblings in the core bars",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, m.HideHyperThreading, () =>
                {
                    m.HideHyperThreading = !m.HideHyperThreading;
                    Commit();
                })));
        RowsCard(g, x0, y, cw, rows);
    }

    // ------------------------------------------------------ font dialog

    bool _fontPick;
    float _fontScroll;
    List<string>? _families;
    readonly Dictionary<string, Font> _fontCache = new();

    void DrawFontDialog(Graphics g, float W, float H)
    {
        if (!_fontPick) return;
        _families ??= new System.Drawing.Text.InstalledFontCollection().Families
            .Select(f => f.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        using (var dim = new SolidBrush(Color.FromArgb(_dark ? 120 : 70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, W, H);
        Add(new RectangleF(0, 0, W, H), () => { _fontPick = false; Invalidate(); });

        float pw = Math.Min(W - SideW - 40, 660), ph = Math.Min(H - 48, 520);
        var card = new RectangleF((W - pw) / 2 + SideW / 4, (H - ph) / 2, pw, ph);
        Box(g, new RectangleF(card.X, card.Y + 4, card.Width, card.Height), 10, Color.FromArgb(_dark ? 110 : 50, 0, 0, 0));
        Box(g, card, 9, _dark ? Color.FromArgb(252, 40, 40, 44) : Color.FromArgb(253, 249, 249, 249), _pal.Stroke);
        Add(card, () => { });
        Txt(g, "Choose a font", _fSec, _pal.Text, new RectangleF(card.X + 20, card.Y + 14, pw - 40, 28));

        var view = new RectangleF(card.X + 8, card.Y + 52, pw - 16, ph - 52 - 12);
        var clip = g.Save();
        g.SetClip(view);
        float x = view.X + 12, wmax = view.Width - 24, y = view.Y - _fontScroll;
        var (rects, bottom) = Flow(_families.Select(_ => 190f).ToList(), x, y, wmax, 34, 6);
        for (int i = 0; i < _families.Count; i++)
        {
            var r = rects[i];
            var vis = RectangleF.Intersect(r, view);
            if (vis.IsEmpty) continue;
            string name = _families[i];
            bool on = name.Equals(_cfg.TextFont, StringComparison.OrdinalIgnoreCase);
            if (on) Box(g, r, 6, Color.FromArgb(55, _pal.Accent), _pal.Accent, 1.2f);
            else CtlBox(g, r, 6, Hov(vis));
            if (!_fontCache.TryGetValue(name, out var f))
            {
                try { f = new Font(name, 14f, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { f = _fBody; }
                _fontCache[name] = f;
            }
            Txt(g, name, f, _pal.Text, new RectangleF(r.X + 10, r.Y, r.Width - 14, r.Height));
            Add(vis, () => { _cfg.TextFont = name; _fontPick = false; Commit(); });
        }
        float maxS = Math.Max(0, bottom - y - view.Height + 8);
        if (_fontScroll > maxS) { _fontScroll = maxS; Invalidate(); }
        g.Restore(clip);
    }

    // ------------------------------------------------------ space width dialog

    string? _spaceEdit;

    void DrawSpaceDialog(Graphics g, float W, float H)
    {
        if (_spaceEdit == null) return;
        var m = _cfg.Modules.FirstOrDefault(x => x.Id == _spaceEdit);
        if (m == null) { _spaceEdit = null; return; }

        using (var dim = new SolidBrush(Color.FromArgb(_dark ? 120 : 70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, W, H);
        Add(new RectangleF(0, 0, W, H), () => { _spaceEdit = null; Invalidate(); });

        const float pw = 400, ph = 176;
        var card = new RectangleF((W - pw) / 2 + SideW / 4, (H - ph) / 2, pw, ph);
        Box(g, new RectangleF(card.X, card.Y + 4, card.Width, card.Height), 10, Color.FromArgb(_dark ? 110 : 50, 0, 0, 0));
        Box(g, card, 9, _dark ? Color.FromArgb(252, 40, 40, 44) : Color.FromArgb(253, 249, 249, 249), _pal.Stroke);
        Add(card, () => { });

        int w = m.SpaceWidth ?? 16;
        Txt(g, "Space width", _fSec, _pal.Text, new RectangleF(card.X + 20, card.Y + 14, pw - 40, 28));
        Txt(g, "Empty room between modules, in pixels", _fSmall, _pal.Dim, new RectangleF(card.X + 20, card.Y + 42, pw - 40, 18));
        Stepper(g, new RectangleF(card.X + 20, card.Y + 74, pw - 40, 36), $"{w} px",
            () => { m.SpaceWidth = Math.Max(0, w - 1); Commit(); },
            () => { m.SpaceWidth = Math.Min(400, w + 1); Commit(); });

        int[] presets = { 4, 8, 16, 24, 32, 48 };
        float bw = (pw - 40 - 5 * 6) / 6f;
        for (int i = 0; i < presets.Length; i++)
        {
            int v = presets[i];
            var b = new RectangleF(card.X + 20 + i * (bw + 6), card.Y + 126, bw, 30);
            if (w == v) Box(g, b, 6, Color.FromArgb(55, _pal.Accent), _pal.Accent, 1.2f);
            else CtlBox(g, b, 6, Hov(b));
            Txt(g, v.ToString(), _fBody, _pal.Text, b, StringAlignment.Center);
            Add(b, () => { m.SpaceWidth = v; Commit(); });
        }
    }

    // ------------------------------------------------------ sensor picker popup

    (ModuleConfig M, int Idx, int Slot, RectangleF Anchor)? _pick;
    float _pickScroll;
    string _pickFilter = "all";

    static string Ell(string s) => s.Length > 15 ? s[..14] + "…" : s;

    static readonly (string Id, string Title)[] PickFilters =
    {
        ("all", "All"), ("temperature", "Temp"), ("fan", "Fan"), ("load", "Load"),
        ("clock", "Clock"), ("power", "Power"), ("other", "Other"),
    };

    static string FilterOf(string kind) => kind switch
    {
        "temperature" => "temperature",
        "fan" => "fan",
        "load" or "control" or "level" => "load",
        "clock" => "clock",
        "power" or "voltage" or "current" or "energy" => "power",
        _ => "other",
    };

    static string BuiltinFilter(SensorKey k) => k switch
    {
        SensorKey.CpuTemp or SensorKey.GpuTemp => "temperature",
        SensorKey.FanRpm or SensorKey.GpuFanPct => "fan",
        SensorKey.CpuTotal or SensorKey.GpuLoad or SensorKey.MemUsedPct or SensorKey.DiskActivePct or SensorKey.DiskUsedPct => "load",
        _ => "other",
    };

    /// <summary>Dialog for one readout slot: every sensor on this PC, grouped by hardware, filtered by kind.</summary>
    void DrawPicker(Graphics g, float W, float H)
    {
        if (_pick is not { } p) return;
        var items = ActiveItems(p.M);
        if (_page != "sensors" || p.Idx >= items.Count || !ModuleFactory.IsSlotted(items[p.Idx])) { _pick = null; return; }
        string cur = Slots(items[p.Idx])[p.Slot];
        string? curId = cur.StartsWith('@') ? (CustomSensors.Register(cur) is { } ck ? CustomSensors.Id(ck) : null) : null;

        // dim everything behind; a click outside closes
        using (var dim = new SolidBrush(Color.FromArgb(_dark ? 120 : 70, 0, 0, 0))) g.FillRectangle(dim, 0, 0, W, H);
        Add(new RectangleF(0, 0, W, H), () => { _pick = null; Invalidate(); });

        float pw = Math.Min(W - SideW - 40, 660), ph = Math.Min(H - 48, 520);
        var card = new RectangleF((W - pw) / 2 + SideW / 4, (H - ph) / 2, pw, ph);
        Box(g, new RectangleF(card.X, card.Y + 4, card.Width, card.Height), 10, Color.FromArgb(_dark ? 110 : 50, 0, 0, 0));
        Box(g, card, 9, _dark ? Color.FromArgb(252, 40, 40, 44) : Color.FromArgb(253, 249, 249, 249), _pal.Stroke);
        Add(card, () => { });

        string where = ModuleFactory.IsGraph(items[p.Idx]) ? "graph" : Slots(items[p.Idx]).Length == 2 ? (p.Slot == 0 ? "top line" : "bottom line") : "readout";
        Txt(g, "Choose a sensor for the " + where, _fSec, _pal.Text, new RectangleF(card.X + 20, card.Y + 14, pw - 120, 28));
        var none = new RectangleF(card.Right - 92, card.Y + 14, 72, 28);
        CtlBox(g, none, 6, Hov(none));
        Txt(g, "None", _fBody, _pal.Dim, none, StringAlignment.Center);
        Add(none, () => SetPick(p, ""));

        int fi = Math.Max(0, Array.FindIndex(PickFilters, f => f.Id == _pickFilter));
        Segmented(g, new RectangleF(card.X + 20, card.Y + 52, pw - 40, 30), PickFilters.Select(f => f.Title).ToArray(), fi,
            i => { _pickFilter = PickFilters[i].Id; _pickScroll = 0; });

        var view = new RectangleF(card.X + 8, card.Y + 94, pw - 16, ph - 94 - 12);
        var clip = g.Save();
        g.SetClip(view);

        float x = view.X + 12, wmax = view.Width - 24, y = view.Y - _pickScroll;
        float startY = y;

        void Chips(IReadOnlyList<(string Label, string Spec, bool On)> chips)
        {
            var widths = chips.Select(c => Theme.TextWidth(c.Label, _fBody) + 26).ToList();
            var (rects, bottom) = Flow(widths, x, y, wmax, 28, 6);
            for (int i = 0; i < chips.Count; i++)
            {
                var r = rects[i];
                var vis = RectangleF.Intersect(r, view);
                if (vis.IsEmpty) continue;
                if (chips[i].On) Box(g, r, 6, Color.FromArgb(55, _pal.Accent), _pal.Accent, 1.2f);
                else CtlBox(g, r, 6, Hov(vis));
                Txt(g, chips[i].Label, _fBody, _pal.Text, r, StringAlignment.Center);
                string spec = chips[i].Spec;
                Add(vis, () => SetPick(p, spec));
            }
            y = bottom + 8;
        }

        void Header(string text)
        {
            if (y + 24 > view.Y && y < view.Bottom)
                Txt(g, text, _fSemi, _pal.Dim, new RectangleF(x, y, wmax, 22));
            y += 26;
        }

        // the sensors every PC has
        var basic = SensorOrder.Where(k => _pickFilter == "all" || BuiltinFilter(k) == _pickFilter)
            .Select(k => (SensorName(k), k.ToString(), cur == k.ToString())).ToList();
        if (basic.Count > 0) { Header("Basic"); Chips(basic); }

        // everything LibreHardwareMonitor sees
        var entries = SensorCatalog.Entries.Where(e => _pickFilter == "all" || FilterOf(e.Kind) == _pickFilter).ToList();
        foreach (var grp in entries.GroupBy(e => e.Hardware))
        {
            Header(grp.Key);
            Chips(grp.Select(e => ($"{e.Name}   {CustomSensors.Format(e.Kind, e.Value)}",
                                   CustomSensors.Spec(e.Id, e.Name), e.Id == curId)).ToList());
        }
        if (entries.Count == 0)
        {
            string msg = SensorCatalog.Scanning ? "Scanning sensors…"
                : _cfg.DisabledComponents.Contains("lhm") ? "The LibreHardwareMonitor component is off (General → Components)"
                : "No more sensors found. CPU and motherboard sensors need administrator rights";
            if (y < view.Bottom) Txt(g, msg, _fSmall, _pal.Dim, new RectangleF(x, y, wmax, 22));
            y += 26;
        }

        float total = y - startY;
        float maxS = Math.Max(0, total - view.Height + 8);
        if (_pickScroll > maxS) { _pickScroll = maxS; Invalidate(); }
        g.Restore(clip);
    }

    void SetPick((ModuleConfig M, int Idx, int Slot, RectangleF Anchor) p, string value)
    {
        var l = ActiveItems(p.M);
        if (p.Idx < l.Count && ModuleFactory.IsSlotted(l[p.Idx]))
        {
            SetSlot(l, p.Idx, p.Slot, value);
            p.M.Items = l;
            _pick = null;
            Commit();
        }
        else _pick = null;
    }

    /// <summary>A graph of one chosen sensor in the strip: click to choose the sensor.</summary>
    void DrawGraphTile(Graphics g, ModuleConfig m, int idx, string item, RectangleF r, bool dim)
    {
        CtlBox(g, r, 6, Hov(r));
        if (!dim && Thumb(m, item) is { } th) DrawThumb(g, th, new RectangleF(r.X, r.Y + 3, r.Width, 30), 1.1f);
        var key = Fmt.ParseKey(item[2..]);
        Txt(g, key is { } k ? Ell(SensorName(k)) : "Choose sensor", _fSmall, key == null || dim ? _pal.Dim : _pal.Text,
            new RectangleF(r.X, r.Bottom - 20, r.Width, 18), StringAlignment.Center);
        Add(r, () => { _pick = (m, idx, 0, r); _pickScroll = 0; _pickFilter = "all"; SensorCatalog.Scan(() => BeginInvoke(Invalidate)); },
            new ItemDrag(item, idx));
    }

    /// <summary>A readout block in the strip: one or two slots that take sensors dragged onto them.</summary>
    void DrawReadoutTile(Graphics g, ModuleConfig m, int idx, string item, RectangleF r, bool dim)
    {
        CtlBox(g, r, 6, Hov(r));
        var slots = Slots(item);
        for (int k = 0; k < slots.Length; k++)
        {
            int slot = k;
            var cell = slots.Length == 2
                ? new RectangleF(r.X + 6, r.Y + 4 + k * 24, r.Width - 12, 20)
                : new RectangleF(r.X + 6, r.Y + 8, r.Width - 12, 36);
            var key = Fmt.ParseKey(slots[k]);
            bool slotDim = dim || _dragging is SenDrag sd && sd.Block == idx && sd.Slot == k;
            if (key is { } kk)
            {
                Box(g, cell, 5, Color.FromArgb(44, _pal.Accent), _pal.Accent, 1.1f);
                Txt(g, Ell(SensorName(kk)), _fSmall, slotDim ? _pal.Dim : _pal.Text, cell, StringAlignment.Center);
            }
            else Box(g, cell, 5, Color.Transparent, _pal.Dim, 1f, true);

            var hit = Add(cell, () => { _pick = (m, idx, slot, cell); _pickScroll = 0; _pickFilter = "all"; SensorCatalog.Scan(() => BeginInvoke(Invalidate)); }, key is { } k2 ? new SenDrag(k2, idx, slot) : null);
            hit.CanDrop = o => o is SenDrag;
            hit.Drop = (o, _) => DropSensor(m, (SenDrag)o, idx, slot);
        }
    }

    // ------------------------------------------------------------ layout page

    const float ChipH = 44, ChipGap = 6, ChipW = 104, SpaceW = 56;

    static float ChipWidth(string type) => type == "spacer" ? SpaceW : ChipW;

    void DrawLayoutPage(Graphics g, float x0, float cw)
    {
        float y = 20;
        Section(g, "Preview", x0, y, cw);
        y += 36;
        DrawPreview(g, new RectangleF(x0, y, cw, 84));
        y += 84 + 20;

        // ---- modules palette
        Section(g, "Modules", x0, y, cw, "Drag into the layout below");
        y += 36;
        var types = ModuleFactory.KnownTypes.Concat(new[] { "spacer" }).ToArray();
        var tw = types.Select(t => Theme.TextWidth(TypeTitle(t), _fBody) + 62).ToList();
        var (pr, pbottom) = Flow(tw, x0 + 12, y + 12, cw - 24, 40, 8);
        var pal = new RectangleF(x0, y, cw, pbottom - y + 12);
        bool removing = _dragging is ModDrag { Id: not null };
        CardBox(g, pal);
        if (removing) Box(g, pal, 8, Blend(Color.Transparent, _pal.Danger, 0.12f), _pal.Danger, 1.5f);
        var palHit = Add(pal);
        palHit.CanDrop = o => o is ModDrag { Id: not null };
        palHit.Drop = (o, _) => Unplace(((ModDrag)o).Id!);
        for (int i = 0; i < types.Length; i++)
        {
            string type = types[i];
            var r = pr[i];
            bool spacer = type == "spacer";
            bool placed = !spacer && IsPlaced(type);
            var c = placed ? _pal.Dim : _pal.Text;
            CtlBox(g, r, 6, Hov(r));
            if (spacer) DrawSpaceGlyph(g, new RectangleF(r.X + 10, r.Y + 11, 22, 18), c);
            else Icons.Draw(g, TypeIcon(type), new RectangleF(r.X + 12, r.Y + 11, 18, 18), c);
            Txt(g, TypeTitle(type), _fBody, c, new RectangleF(r.X + 40, r.Y, r.Width - 44, r.Height));
            Add(r, () =>
            {
                if (spacer) DropModule(new ModDrag(null, "spacer"), 0, Lane(0).Count);
                else if (placed) { _page = type; Invalidate(); }
                else PlaceAtEnd(type);
            }, new ModDrag(placed ? type : null, type));
        }
        y += pal.Height + 20;

        // ---- lanes
        Section(g, "Taskbar layout", x0, y, cw, "Drag modules and spaces into the rows, in any order");
        y += 36;
        bool two = _cfg.Rows >= 2;
        float h = 14 + (two ? 2 * 54 + 10 : 54) + 12 + 30 + 14;
        DrawCanvas(g, new RectangleF(x0, y, cw, h), two);
    }

    void DrawSpaceGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = new Pen(c, 1.4f);
        float cy = r.Y + r.Height / 2;
        g.DrawLine(pen, r.X + 1, r.Y + 2, r.X + 1, r.Bottom - 2);
        g.DrawLine(pen, r.Right - 1, r.Y + 2, r.Right - 1, r.Bottom - 2);
        g.DrawLine(pen, r.X + 3, cy, r.Right - 3, cy);
        g.DrawLine(pen, r.X + 3, cy, r.X + 7, cy - 3);
        g.DrawLine(pen, r.X + 3, cy, r.X + 7, cy + 3);
        g.DrawLine(pen, r.Right - 3, cy, r.Right - 7, cy - 3);
        g.DrawLine(pen, r.Right - 3, cy, r.Right - 7, cy + 3);
    }

    void DrawPreview(Graphics g, RectangleF r)
    {
        CardBox(g, r);

        if (_pvLayout == null)
        {
            _pvLayout = WidgetLayout.Build(_cfg, Program.BuildModules(_cfg), _thTheme!);
            foreach (var m in _pvLayout.Modules) m.Prepare(_hub.Current);
        }

        float w = _pvLayout.TotalWidth(_thTheme!), hh = _pvLayout.Height;
        float k = Math.Min(Math.Min((r.Width - 40) / Math.Max(w, 1f), (r.Height - 16) / hh), 2.0f);

        var state = g.Save();
        g.SetClip(new RectangleF(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2));
        float s = S;
        var page = g.Transform;                                   // the page's own scroll / title-bar offset (pixels)
        float ox = page.OffsetX, oy = page.OffsetY;
        page.Dispose();
        WidgetLayout.Configure(g, k * s);
        g.TranslateTransform((r.X + 16) * s + ox, (r.Y + (r.Height - hh * k) / 2) * s + oy, MatrixOrder.Append);
        _pvLayout.Draw(g, _thTheme!);
        g.Restore(state);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    void DrawChip(Graphics g, RectangleF r, string type, bool dim)
    {
        CtlBox(g, r, 6, Hov(r));
        var c = dim ? _pal.Dim : _pal.Text;
        if (type == "spacer")
        {
            DrawSpaceGlyph(g, new RectangleF(r.X + 16, r.Y + 13, 24, 18), c);
            return;
        }
        Icons.Draw(g, TypeIcon(type), new RectangleF(r.X + 11, r.Y + 13, 18, 18), c);
        Txt(g, TypeTitle(type), _fBody, c, new RectangleF(r.X + 38, r.Y, r.Width - 40, r.Height));
    }

    void DrawCanvas(Graphics g, RectangleF r, bool two)
    {
        CardBox(g, r);
        float laneH = ChipH + 10;
        float x = r.X + 14, laneW = r.Width - 28;

        for (int row = 0; row < (two ? 2 : 1); row++)
        {
            int rw = row;
            var lane = new RectangleF(x, r.Y + 14 + row * (laneH + 10), laneW, laneH);
            var list = Lane(row);
            Box(g, lane, 6, Color.Transparent, _pal.Dim, 1f, true);
            if (list.Count == 0)
                Txt(g, rw == 0 ? "Top row" : "Bottom row", _fSmall, _pal.Dim, lane, StringAlignment.Center);

            var chips = new List<RectangleF>();
            float cx = lane.X + ChipGap;
            foreach (var id in list)
            {
                float w = ChipWidth(Mod(id).EffectiveType);
                chips.Add(new RectangleF(cx, lane.Y + 5, w, ChipH));
                cx += w + ChipGap;
            }

            var laneHit = Add(lane);
            laneHit.CanDrop = o => o is ModDrag;
            laneHit.Drop = (o, pt) => DropModule((ModDrag)o, rw, chips.Count(c => c.X + c.Width / 2 < pt.X));
            laneHit.Indicator = pt =>
            {
                int idx = chips.Count(c => c.X + c.Width / 2 < pt.X);
                float bx = idx == 0 ? lane.X + 2 : chips[idx - 1].Right + ChipGap / 2;
                return new RectangleF(bx - 1.5f, lane.Y + 4, 3, lane.Height - 8);
            };

            for (int i = 0; i < list.Count; i++)
            {
                string id = list[i], type = Mod(id).EffectiveType;
                var chip = chips[i];
                DrawChip(g, chip, type, _dragging is ModDrag d && d.Id == id);
                Add(chip, () => { if (type != "spacer") { _page = type; Invalidate(); } else { _spaceEdit = id; Invalidate(); } }, new ModDrag(id, type));
            }
        }

        // the rows switch changes which lanes exist
        float sy = r.Bottom - 14 - 30;
        Txt(g, "Rows", _fBody, _pal.Dim, new RectangleF(r.X + 18, sy, 60, 30));
        Segmented(g, new RectangleF(r.X + 70, sy, 150, 30), new[] { "1 row", "2 rows" }, _cfg.Rows >= 2 ? 1 : 0,
            i => { _cfg.Rows = i + 1; Commit(); });
    }

    // ----------------------------------------------------------- general page

    void DrawGeneralPage(Graphics g, float x0, float cw)
    {
        float y = 20;
        Section(g, "Taskbar widget", x0, y, cw);
        y += 36;

        string[] anchors = { "left", "center", "right" }, themes = { "auto", "dark", "light" };
        var rows = new List<(string, string?, RowControl?)>
        {
            ("Position on the taskbar", "Where the widget sits",
                row => Segmented(g, RightOf(row, 240, 30), new[] { "Left", "Center", "Right" }, Array.IndexOf(anchors, _cfg.Anchor),
                    i => { _cfg.Anchor = anchors[i]; Commit(); })),
            ("Horizontal offset", "Distance from the anchor, in pixels",
                row => Stepper(g, RightOf(row, 170, 30), $"{_cfg.OffsetX}",
                    () => { _cfg.OffsetX -= 2; Commit(); }, () => { _cfg.OffsetX += 2; Commit(); })),
            ("Vertical offset", "Nudge up or down, in pixels",
                row => Stepper(g, RightOf(row, 170, 30), $"{_cfg.OffsetY}",
                    () => { _cfg.OffsetY -= 1; Commit(); }, () => { _cfg.OffsetY += 1; Commit(); })),
            ("Widget theme", "Auto follows the taskbar",
                row => Segmented(g, RightOf(row, 240, 30), new[] { "Auto", "Dark", "Light" }, Array.IndexOf(themes, _cfg.Theme),
                    i => { _cfg.Theme = themes[i]; Commit(); })),
            ("Update interval", "Sensors are only read while shown",
                row => Stepper(g, RightOf(row, 170, 30), $"{_cfg.IntervalMs / 1000.0:0.0#} s",
                    () => { _cfg.IntervalMs = Math.Max(250, _cfg.IntervalMs - 250); Commit(); },
                    () => { _cfg.IntervalMs = Math.Min(10000, _cfg.IntervalMs + 250); Commit(); })),
        };
        y = RowsCard(g, x0, y, cw, rows) + 20;

        Section(g, "Text", x0, y, cw, "Percent, temperature and readout values");
        y += 36;
        y = RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("Font", _cfg.TextFont,
                row => Button(g, RightOf(row, 150, 30), "Change…", () => { _fontPick = true; _fontScroll = 0; Invalidate(); })),
            ("Weight", "Regular or bold",
                row => Segmented(g, RightOf(row, 200, 30), new[] { "Regular", "Bold" }, _cfg.TextBold ? 1 : 0,
                    i => { _cfg.TextBold = i == 1; Commit(); })),
            ("Size", "Value text height in pixels; large values are 25% bigger",
                row => Stepper(g, RightOf(row, 170, 30), $"{_cfg.TextSize:0.#} px",
                    () => { _cfg.TextSize = Math.Max(7f, _cfg.TextSize - 0.5f); Commit(); },
                    () => { _cfg.TextSize = Math.Min(16f, _cfg.TextSize + 0.5f); Commit(); })),
        }) + 20;

        Section(g, "Components", x0, y, cw);
        y += 36;
        var comps = new (string Id, string Title, string Desc)[]
        {
            ("windows", "Windows counters", "CPU, memory, disk and network. No driver needed"),
            ("nvidia", "NVIDIA NVML", "NVIDIA GPU load, temperature, memory and fan"),
            ("lhm", "LibreHardwareMonitor", "CPU temperature, fans, GPU sensors. Needs administrator"),
            ("smc", "Apple SMC", "Temperature and fans on Boot Camp Macs. Read only"),
        };
        var crows = new List<(string, string?, RowControl?)>();
        foreach (var c in comps)
        {
            var id = c.Id;
            string desc = c.Desc;
            crows.Add((c.Title, desc, row =>
            {
                var r = RightOf(row, 42, 21);
                Switch(g, r.X, r.Y, !_cfg.DisabledComponents.Contains(id), () =>
                {
                    if (!_cfg.DisabledComponents.Remove(id)) _cfg.DisabledComponents.Add(id);
                    Commit();
                });
            }));
        }
        y = RowsCard(g, x0, y, cw, crows) + 20;

        Section(g, "Windows", x0, y, cw);
        y += 36;
        RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("Run as administrator", "Asks for administrator rights when opened, for CPU and motherboard sensors",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, _cfg.RunAsAdmin, () =>
                {
                    _cfg.RunAsAdmin = !_cfg.RunAsAdmin;
                    Commit();
                })),
            ("Start with Windows", "Starts at sign-in as administrator (Task Scheduler, no prompt)",
                row => Switch(g, RightOf(row, 42, 21).X, RightOf(row, 42, 21).Y, _autoOn, () =>
                {
                    bool want = !_autoOn;
                    if (Autostart.Set(want)) _autoOn = want;
                    else _autoOn = Autostart.IsEnabled();
                    Invalidate();
                })),
        });
    }

    // ----------------------------------------------------------- sections page

    void DrawSectionsPage(Graphics g, float x0, float cw)
    {
        float y = 20;
        Section(g, "Click popups", x0, y, cw, "What each module shows when you click it on the taskbar");
        y += 36;
        y = RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("Processes listed", "How many programs the top-process lists show",
                row => Stepper(g, RightOf(row, 170, 30), $"{_cfg.ProcessCount}",
                    () => { _cfg.ProcessCount = Math.Max(1, _cfg.ProcessCount - 1); Commit(); },
                    () => { _cfg.ProcessCount = Math.Min(15, _cfg.ProcessCount + 1); Commit(); })),
        }) + 20;

        foreach (var type in new[] { "cpu", "gpu", "mem", "disk", "net", "sensors" })
        {
            Section(g, TypeTitle(type), x0, y, cw);
            y += 36;
            var rows = new List<(string, string?, RowControl?)>();
            foreach (var (id, title, desc) in PopupSections.For(type))
            {
                string key = type + "." + id;
                rows.Add((title, desc, row =>
                {
                    var r = RightOf(row, 42, 21);
                    Switch(g, r.X, r.Y, !_cfg.DisabledSections.Contains(key), () =>
                    {
                        if (!_cfg.DisabledSections.Remove(key)) _cfg.DisabledSections.Add(key);
                        Commit();
                    });
                }));
            }
            y = RowsCard(g, x0, y, cw, rows) + 20;
        }
    }

    // ------------------------------------------------------------- about page

    void DrawAboutPage(Graphics g, float x0, float cw)
    {
        float y = 20;
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        Icons.DrawApp(g, new RectangleF(x0, y, 80, 80));
        Txt(g, "TaskbarStats", _fBig, _pal.Text, new RectangleF(x0 + 98, y + 16, cw - 98, 30));
        Txt(g, $"Version {ver?.ToString(3) ?? "1.0.0"}  ·  usage and temperature on the taskbar", _fSmall, _pal.Dim,
            new RectangleF(x0 + 98, y + 46, cw - 98, 18));
        y += 100;

        Section(g, "Files", x0, y, cw);
        y += 36;
        y = RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("Settings file", AppConfig.FilePath,
                row => Button(g, RightOf(row, 150, 30), "Open folder", () =>
                {
                    try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.Dir}\"") { UseShellExecute = true }); }
                    catch { }
                })),
        }) + 20;

        Section(g, "Components", x0, y, cw);
        y += 36;
        y = RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("LibreHardwareMonitor", "CPU temperature, fans, GPU sensors (MPL-2.0)", null),
            ("NVIDIA NVML", "NVIDIA GPU load, temperature, memory and fan", null),
            ("Apple SMC", "Boot Camp Macs: temperature and fans (read only)", null),
            ("Windows counters", "CPU, memory, disk and network without any driver", null),
        }) + 20;

        Section(g, "Credits", x0, y, cw);
        y += 36;
        y = RowsCard(g, x0, y, cw, new List<(string, string?, RowControl?)>
        {
            ("Made by NSteven", "TaskbarStats © 2026 NSteven. Apache License 2.0", null),
            ("LibreHardwareMonitor", "MPL-2.0. Sensor reading, with its libraries", null),
            ("PawnIO", "By namazso. GPL-2.0 + exception. Optional driver", null),
            ("System.Management", "Microsoft. MIT License", null),
            ("HidSharp", "Apache License 2.0", null),
            ("Xanmono", "By GGBotNet. SIL Open Font License 1.1. Taskbar label font", null),
            ("Apple, NVIDIA, AMD, Intel, Windows", "Names belong to their owners. This app is not affiliated with them", null),
        });
    }
}
