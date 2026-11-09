using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>
/// Settings window in the style of the Windows Clock app: Mica backdrop, navigation on the left, cards on the
/// right. Everything is drawn by hand into one premultiplied-alpha surface (so the Mica shows through where
/// nothing is drawn) and hit-tested through a region list registered while painting. Modules and their
/// items are tiles that are dragged in, out and around; colours follow the Windows light/dark mode and accent
/// colour; every change is applied to the running widget and saved immediately.
/// </summary>
internal sealed partial class SettingsForm : Form
{
    // ------------------------------------------------------------------ types

    sealed record ModDrag(string? Id, string Type);          // a module tile: Id = the placed module, null from the palette
    sealed record ItemDrag(string Name, int From);           // an item tile of a module page: From = index in the strip, -1 = available
    sealed record SenDrag(SensorKey Key, int Block, int Slot); // a sensor tile (Block -1) or a filled readout slot

    sealed class Hit
    {
        public RectangleF R;
        public Action? Click;
        public object? Drag;
        public Func<object, bool>? CanDrop;
        public Action<object, PointF>? Drop;
        public Func<PointF, RectangleF?>? Indicator;
        public bool Fixed;                      // window coordinates (not scrolled with the page)
    }

    sealed class Pal
    {
        // fills carry alpha: they are laid over the Mica backdrop
        public Color Card, Control, Hover, NavSel, Stroke, Text, Dim, Accent, AccentText, Danger, Base;
    }

    // ------------------------------------------------------------------ state

    readonly AppConfig _cfg;
    readonly SensorHub _hub;
    readonly Action<AppConfig> _apply;

    /// <summary>Fixed values for the thumbnails of the module pages, so they stay still.</summary>
    static readonly Snapshot Demo = MakeDemo();

    static Snapshot MakeDemo()
    {
        var s = new Snapshot();
        s[SensorKey.CpuTotal] = 37; s[SensorKey.CpuTemp] = 52; s[SensorKey.MemUsedPct] = 54; s[SensorKey.MemUsedMb] = 17400;
        s[SensorKey.GpuLoad] = 42; s[SensorKey.GpuTemp] = 58; s[SensorKey.GpuMemUsedMb] = 3100; s[SensorKey.GpuFanPct] = 40;
        s[SensorKey.DiskUsedPct] = 61; s[SensorKey.DiskActivePct] = 23; s[SensorKey.DiskReadKBs] = 2400; s[SensorKey.DiskWriteKBs] = 800;
        s[SensorKey.NetDownKBs] = 3400; s[SensorKey.NetUpKBs] = 420; s[SensorKey.FanRpm] = 1450;
        int[] pat = { 62, 18, 85, 30, 48, 12, 70, 25, 40, 8, 55, 33, 20, 66, 15, 44 };
        s.Cores = Enumerable.Range(0, CpuTopology.Logical).Select(i => (float)pat[i % pat.Length]).ToArray();
        s.PhysCores = Enumerable.Range(0, CpuTopology.Physical).Select(i => (float)pat[(i * 2) % pat.Length]).ToArray();
        return s;
    }

    readonly List<Hit> _hits = new();
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    Pal _pal = new();
    bool _dark, _mica;
    string _page = "general";
    Hit? _hover, _down;
    bool _fixed;
    float _scroll, _contentBottom, _lastBottom;
    string _lastPage = "";
    PointF _downPt, _dragPt;
    object? _dragging;
    bool _autoOn;
    int _ticks;

    Theme? _thTheme;
    WidgetLayout? _pvLayout;
    readonly Dictionary<string, IItem?> _thumbs = new();

    // paint surface (premultiplied ARGB DIB shared with a memory DC)
    Bitmap? _surf;
    IntPtr _dc, _hbm;
    int _sw, _sh;

    // fonts, in dip (pixel units; the graphics carries the DPI scale)
    readonly Font _fBody, _fSmall, _fSemi, _fSec, _fBig;

    float S => DeviceDpi / 96f;

    // -------------------------------------------------------------------- init

    public SettingsForm(AppConfig cfg, SensorHub hub, Action<AppConfig> apply)
    {
        _cfg = cfg;
        _hub = hub;
        _apply = apply;
        EnsureExplicitBlocks();
        EnsureModules();
        _autoOn = Autostart.IsEnabled();

        string text = FontFamilyExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        string semi = FontFamilyExists("Segoe UI Semibold") ? "Segoe UI Semibold" : "Segoe UI";
        _fBody = new Font(text, 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSmall = new Font(text, 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSemi = new Font(semi, 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSec = new Font(semi, 19f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBig = new Font(semi, 26f, FontStyle.Regular, GraphicsUnit.Pixel);

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);

        Text = "TaskbarStats";
        Icon = Icons.MakeAppIcon();
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size((int)(980 * S), (int)(720 * S));
        _mica = Environment.OSVersion.Version.Build >= 22621;
        UpdatePalette();

        _timer.Tick += (_, _) =>
        {
            // only the layout preview is live; the widget itself keeps the sensor values fresh
            if (_page == "layout")
            {
                if (_pvLayout != null) foreach (var m in _pvLayout.Modules) m.Prepare(_hub.Current);
                Invalidate();
            }
            _ticks++;
            if (_ticks >= 2 && Environment.GetEnvironmentVariable("TS_SHOT") is { Length: > 0 } f && !File.Exists(f))
                SaveShot(f);
        };
        _timer.Start();

        if (Environment.GetEnvironmentVariable("TS_PAGE") is { Length: > 0 } pg) _page = pg;     // test hook
    }

    static bool FontFamilyExists(string name)
    {
        try { using var f = new FontFamily(name); return true; }
        catch (ArgumentException) { return false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _thTheme?.Dispose();
        _fBody.Dispose(); _fSmall.Dispose(); _fSemi.Dispose(); _fSec.Dispose(); _fBig.Dispose();
        FreeSurface();
        base.OnFormClosed(e);
    }

    /// <summary>Test aid: the surface laid over a stand-in for the Mica backdrop.</summary>
    void SaveShot(string path)
    {
        if (_surf == null) return;
        using var bmp = new Bitmap(_sw, _sh, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var bg = new LinearGradientBrush(new Rectangle(0, 0, _sw, _sh),
                _dark ? Color.FromArgb(20, 30, 62) : Color.FromArgb(215, 228, 245),
                _dark ? Color.FromArgb(52, 30, 44) : Color.FromArgb(240, 226, 232), 35f);
            g.FillRectangle(bg, 0, 0, _sw, _sh);
            g.DrawImage(_surf, 0, 0);
        }
        bmp.Save(path, ImageFormat.Png);
    }

    // ---------------------------------------------------- Windows light / dark

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int L, R, T, B; }

    [DllImport("dwmapi.dll")]
    static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    static bool AppsUseLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    static Color SystemAccent(bool dark)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int v)
            {
                // stored as AABBGGRR
                var c = Color.FromArgb(v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF);
                return dark ? Blend(c, Color.White, 0.25f) : c;
            }
        }
        catch { }
        return dark ? Color.FromArgb(96, 205, 255) : Color.FromArgb(0, 103, 192);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    void UpdatePalette()
    {
        _dark = Environment.GetEnvironmentVariable("TS_LIGHT") == "1" ? false : !AppsUseLight();   // TS_LIGHT: test hook
        var accent = SystemAccent(_dark);
        bool brightAccent = accent.GetBrightness() > 0.55f;
        _pal = _dark
            ? new Pal
            {
                Card = Color.FromArgb(13, 255, 255, 255), Control = Color.FromArgb(16, 255, 255, 255),
                Hover = Color.FromArgb(26, 255, 255, 255), NavSel = Color.FromArgb(18, 255, 255, 255),
                Stroke = Color.FromArgb(28, 255, 255, 255), Text = Color.White, Dim = Color.FromArgb(165, 255, 255, 255),
                Danger = Color.FromArgb(255, 123, 105), Base = Color.FromArgb(32, 32, 32),
            }
            : new Pal
            {
                Card = Color.FromArgb(150, 255, 255, 255), Control = Color.FromArgb(170, 255, 255, 255),
                Hover = Color.FromArgb(220, 255, 255, 255), NavSel = Color.FromArgb(16, 0, 0, 0),
                Stroke = Color.FromArgb(26, 0, 0, 0), Text = Color.FromArgb(228, 0, 0, 0), Dim = Color.FromArgb(158, 0, 0, 0),
                Danger = Color.FromArgb(196, 43, 28), Base = Color.FromArgb(243, 243, 243),
            };
        _pal.Accent = accent;
        _pal.AccentText = brightAccent ? Color.Black : Color.White;

        _thTheme?.Dispose();
        _pvLayout = null;
        _thTheme = new Theme(!_dark);          // thumbnails are drawn with the same light/dark rules as the widget
        _thumbs.Clear();
        if (IsHandleCreated) ApplyWindowStyle();
    }

    /// <summary>Immersive dark mode for the caption, Mica behind everything.</summary>
    void ApplyWindowStyle()
    {
        int dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        if (!_mica) return;

        int backdrop = 2;                                                    // DWMSBT_MAINWINDOW = Mica
        if (DwmSetWindowAttribute(Handle, 38, ref backdrop, 4) != 0) { _mica = false; return; }
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };              // sheet of glass: the whole window
        DwmExtendFrameIntoClientArea(Handle, ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowStyle();

        // the title bar is ours (the sidebar reaches the top); only the caption buttons stay with Windows
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        int wantW = (int)(980 * S), wantH = (int)((720 + TitleH) * S);
        Size = new Size(Width + (wantW - ClientSize.Width), Height + (wantH - ClientSize.Height));
        CenterToScreen();
    }

    const float TitleH = 30;

    [DllImport("dwmapi.dll")]
    static extern bool DwmDefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, out IntPtr result);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    protected override void WndProc(ref Message m)
    {
        // WM_SETTINGCHANGE "ImmersiveColorSet": Windows switched light/dark or changed the accent colour
        if (m.Msg == 0x001A && m.LParam != IntPtr.Zero && Marshal.PtrToStringUni(m.LParam) == "ImmersiveColorSet")
        {
            UpdatePalette();
            Invalidate();
        }

        switch (m.Msg)
        {
            case 0x0083 when m.WParam != IntPtr.Zero:                  // WM_NCCALCSIZE: the client area starts at the very top
            {
                int top = Marshal.ReadInt32(m.LParam, 4);
                base.WndProc(ref m);
                Marshal.WriteInt32(m.LParam, 4, top);
                return;
            }
            case 0x0084:                                               // WM_NCHITTEST
            {
                if (DwmDefWindowProc(m.HWnd, m.Msg, m.WParam, m.LParam, out IntPtr r)) { m.Result = r; return; }
                base.WndProc(ref m);
                long lp = m.LParam.ToInt64();
                var pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (m.Result == (IntPtr)1 && pt.Y >= 0 && pt.Y < TitleH * S) m.Result = (IntPtr)2;     // HTCLIENT → HTCAPTION
                return;
            }
            case 0x00A0 or 0x00A1 or 0x00A2 or 0x02A2:                 // non-client mouse: caption button hover / press
                if (DwmDefWindowProc(m.HWnd, m.Msg, m.WParam, m.LParam, out IntPtr r2)) { m.Result = r2; return; }
                break;
        }
        base.WndProc(ref m);
    }

    // ------------------------------------------------------------ paint surface

    void FreeSurface()
    {
        _surf?.Dispose();
        _surf = null;
        if (_dc != IntPtr.Zero) { Native.DeleteDC(_dc); _dc = IntPtr.Zero; }
        if (_hbm != IntPtr.Zero) { Native.DeleteObject(_hbm); _hbm = IntPtr.Zero; }
    }

    void EnsureSurface(int w, int h)
    {
        if (_surf != null && _sw == w && _sh == h) return;
        FreeSurface();
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        _hbm = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        _dc = Native.CreateCompatibleDC(IntPtr.Zero);
        Native.SelectObject(_dc, _hbm);
        _surf = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        _sw = w; _sh = h;
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }          // the surface covers everything

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        EnsureSurface(w, h);

        try { using var g = Graphics.FromImage(_surf!); Render(g); }
        catch (Exception ex) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ts_error.log"), ex + "\n"); } catch { } }

        IntPtr hdc = e.Graphics.GetHdc();
        try { Native.BitBlt(hdc, 0, 0, w, h, _dc, 0, 0, Native.SRCCOPY); }      // keeps the alpha channel
        finally { e.Graphics.ReleaseHdc(hdc); }
    }

    void Render(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(_mica ? Color.Transparent : _pal.Base);
        g.CompositingMode = CompositingMode.SourceOver;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ResetTransform();
        g.ScaleTransform(S, S);

        _hits.Clear();
        float W = ClientSize.Width / S, H = ClientSize.Height / S;

        _fixed = true;
        using (var sb = new SolidBrush(_dark ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(18, 0, 0, 0)))
            g.FillRectangle(sb, 0, 0, SideW, H);
        using (var sp = new Pen(_pal.Stroke)) g.DrawLine(sp, SideW, 0, SideW, H);
        Icons.DrawApp(g, new RectangleF(12, 7, 16, 16));
        Txt(g, "TaskbarStats", _fSmall, _pal.Text, new RectangleF(36, 0, 180, TitleH));
        DrawNav(g, H);
        float viewH = H - TitleH;
        if (_page != _lastPage) { _lastPage = _page; _scroll = 0; _pick = null; }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _lastBottom + 24 - viewH));
        _fixed = false;
        _contentBottom = 0;
        var saved = g.Save();
        g.SetClip(new RectangleF(SideW + 1, TitleH, W, viewH));
        g.TranslateTransform(0, TitleH - _scroll);
        float x0 = SideW + 24, cw = W - x0 - 24;
        switch (_page)
        {
            case "general": DrawGeneralPage(g, x0, cw); break;
            case "layout": DrawLayoutPage(g, x0, cw); break;
            case "sections": DrawSectionsPage(g, x0, cw); break;
            case "about": DrawAboutPage(g, x0, cw); break;
            default:
                if (ModuleFactory.KnownTypes.Contains(_page)) DrawModulePage(g, _page, x0, cw);
                break;
        }
        g.Restore(saved);
        _lastBottom = _contentBottom;
        _fixed = true;
        float maxScroll = Math.Max(0, _lastBottom + 24 - viewH);
        if (_scroll > maxScroll) { _scroll = maxScroll; Invalidate(); }
        if (maxScroll > 0)
        {
            float th = Math.Max(30, viewH * viewH / (_lastBottom + 24));
            float ty = TitleH + _scroll / maxScroll * (viewH - th);
            using var br = new SolidBrush(Color.FromArgb(_dark ? 110 : 90, _pal.Dim));
            using var path = RR(new RectangleF(W - 7, ty + 2, 4, th - 4), 2);
            g.FillPath(br, path);
        }
        DrawPicker(g, W, H);
        DrawSpaceDialog(g, W, H);
        DrawFontDialog(g, W, H);
        DrawDragOverlay(g);
    }

    // ------------------------------------------------------------------ model

    /// <summary>The layout is a single block with a top lane and a bottom lane; modules and spacers sit in the lanes.</summary>
    void EnsureExplicitBlocks()
    {
        if (_cfg.Blocks is not { Count: > 0 })
        {
            // turn the current auto-flow into explicit blocks so it can be edited
            using var t = new Theme(false);
            var layout = WidgetLayout.Build(_cfg, Program.BuildModules(_cfg), t);
            _cfg.Blocks = layout.Blocks.Select(b => new BlockConfig
            {
                Top = b.Top.Select(m => m.Id).ToList(),
                Bottom = b.Bottom.Select(m => m.Id).ToList(),
            }).ToList();
        }
        FlattenBlocks();
    }

    void FlattenBlocks()
    {
        if (_cfg.Blocks is not { Count: > 0 }) _cfg.Blocks = new() { new BlockConfig() };
        var one = _cfg.Blocks[0];
        one.Top ??= new();
        one.Bottom ??= new();
        for (int i = 1; i < _cfg.Blocks.Count; i++)
        {
            one.Top.AddRange(_cfg.Blocks[i].Top ?? new());
            one.Bottom.AddRange(_cfg.Blocks[i].Bottom ?? new());
        }
        _cfg.Blocks.RemoveRange(1, _cfg.Blocks.Count - 1);
    }

    /// <summary>Every module type has exactly one configuration (id = type); unplaced ones are disabled.</summary>
    void EnsureModules()
    {
        foreach (var type in ModuleFactory.KnownTypes)
        {
            if (_cfg.Modules.Any(m => m.Id == type)) continue;
            _cfg.Modules.Add(new ModuleConfig { Id = type, Type = type, Enabled = false, Items = ModuleFactory.DefaultItems(type) });
        }
    }

    List<string> Lane(int row) => row == 0 ? _cfg.Blocks![0].Top! : _cfg.Blocks![0].Bottom!;

    void Prune()
    {
        FlattenBlocks();
        var known = _cfg.Modules.Select(m => m.Id).ToHashSet();
        Lane(0).RemoveAll(id => !known.Contains(id));
        Lane(1).RemoveAll(id => !known.Contains(id));
        if (_cfg.Rows < 2) { Lane(0).AddRange(Lane(1)); Lane(1).Clear(); }
        var placed = Lane(0).Concat(Lane(1)).ToHashSet();
        _cfg.Modules.RemoveAll(m => m.EffectiveType == "spacer" && !placed.Contains(m.Id));
    }

    ModuleConfig Mod(string id) => _cfg.Modules.First(m => m.Id == id);

    bool IsPlaced(string id) => Lane(0).Contains(id) || Lane(1).Contains(id);

    /// <summary>Push the edited copy to the running widget (which also saves it).</summary>
    void Commit()
    {
        Prune();
        _thumbs.Clear();
        _pvLayout = null;
        _apply(_cfg.Clone());
        _thTheme?.Dispose();                                   // value font / size may have changed
        _thTheme = new Theme(!_dark);
        Invalidate();
    }

    void Unplace(string id)
    {
        Lane(0).Remove(id);
        Lane(1).Remove(id);
        var m = _cfg.Modules.FirstOrDefault(x => x.Id == id);
        if (m != null && m.EffectiveType != "spacer") m.Enabled = false;     // spacers are dropped by Prune
        Commit();
    }

    /// <summary>Show the module: append it to the top lane.</summary>
    void PlaceAtEnd(string type)
    {
        Mod(type).Enabled = true;
        Lane(0).Add(type);
        Commit();
    }

    string NewSpacerId()
    {
        string id = "spacer";
        for (int i = 2; _cfg.Modules.Any(m => m.Id == id); i++) id = "spacer" + i;
        return id;
    }

    void DropModule(ModDrag d, int row, int index)
    {
        string id;
        if (d.Id != null) id = d.Id;
        else if (d.Type == "spacer")
        {
            id = NewSpacerId();
            _cfg.Modules.Add(new ModuleConfig { Id = id, Type = "spacer" });
        }
        else
        {
            id = d.Type;
            Mod(id).Enabled = true;
        }

        var to = Lane(row);
        // a move takes it out of wherever it is, adjusting the index when it was earlier in the same lane
        for (int r = 0; r < 2; r++)
        {
            var from = Lane(r);
            int at = from.IndexOf(id);
            if (at < 0) continue;
            from.RemoveAt(at);
            if (ReferenceEquals(from, to) && at < index) index--;
        }
        to.Insert(Math.Clamp(index, 0, to.Count), id);
        Commit();
    }

    // -- items of a module

    List<string> ActiveItems(ModuleConfig m)
    {
        string type = m.EffectiveType;
        var list = new List<string>();
        foreach (var raw in m.Items is { Count: > 0 } ? m.Items : ModuleFactory.DefaultItems(type))
        {
            string n = raw.Trim();
            if (!ModuleFactory.IsSlotted(n)) n = n.ToLowerInvariant();
            if (!ModuleFactory.ValidItem(type, n)) continue;
            if (!ModuleFactory.IsSlotted(n) && list.Contains(n)) continue;
            list.Add(n);
        }
        return list.Count > 0 ? list : ModuleFactory.DefaultItems(type);
    }

    void DropItem(ModuleConfig m, ItemDrag d, int index)
    {
        var list = ActiveItems(m);
        if (d.From >= 0 && d.From < list.Count)
        {
            list.RemoveAt(d.From);
            if (d.From < index) index--;
        }
        else if (!ModuleFactory.IsSlotted(d.Name) && list.Contains(d.Name)) return;
        list.Insert(Math.Clamp(index, 0, list.Count), d.Name);
        m.Items = list;
        Commit();
    }

    void RemoveItem(ModuleConfig m, int index)
    {
        var list = ActiveItems(m);
        if (list.Count <= 1 || index < 0 || index >= list.Count) return;     // a module keeps at least one item
        list.RemoveAt(index);
        m.Items = list;
        Commit();
    }

    // -- readout blocks: "s1:Key" / "s2:Top|Bottom"

    static string[] Slots(string item)
    {
        if (item.Length >= 2 && item[1] == ':') return new[] { item[2..] };       // graph: one sensor
        var parts = item[3..].Split('|');
        var r = new string[item[1] - '0'];
        for (int i = 0; i < r.Length; i++) r[i] = i < parts.Length ? parts[i] : "";
        return r;
    }

    static string MakeReadout(string[] slots) => $"s{slots.Length}:" + string.Join("|", slots);

    static void SetSlot(List<string> items, int item, int slot, string key)
    {
        if (items[item].Length >= 2 && items[item][1] == ':') { items[item] = items[item][..2] + key; return; }
        var s = Slots(items[item]);
        s[slot] = key;
        items[item] = MakeReadout(s);
    }

    void DropSensor(ModuleConfig m, SenDrag d, int item, int slot)
    {
        var items = ActiveItems(m);
        if (item >= items.Count || !ModuleFactory.IsReadout(items[item])) return;
        string old = Slots(items[item])[slot];
        SetSlot(items, item, slot, d.Key.ToString());
        if (d.Block >= 0 && d.Block < items.Count) SetSlot(items, d.Block, d.Slot, old);     // swap when dragged from another slot
        m.Items = items;
        Commit();
    }

    void ClearSlot(ModuleConfig m, SenDrag d)
    {
        var items = ActiveItems(m);
        if (d.Block < 0 || d.Block >= items.Count) return;
        SetSlot(items, d.Block, d.Slot, "");
        m.Items = items;
        Commit();
    }

    /// <summary>Click on a sensor tile: put it into the first empty slot.</summary>
    void FillFirstEmpty(ModuleConfig m, SensorKey key)
    {
        var items = ActiveItems(m);
        for (int i = 0; i < items.Count; i++)
        {
            if (!ModuleFactory.IsReadout(items[i])) continue;
            var sl = Slots(items[i]);
            for (int k = 0; k < sl.Length; k++)
                if (sl[k] == "")
                {
                    SetSlot(items, i, k, key.ToString());
                    m.Items = items;
                    Commit();
                    return;
                }
        }
    }

    // ---------------------------------------------------------------- drawing

    static GraphicsPath RR(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Box(Graphics g, RectangleF r, float rad, Color fill, Color? stroke = null, float sw = 1f, bool dashed = false)
    {
        using var path = RR(r, rad);
        if (fill.A > 0) { using var b = new SolidBrush(fill); g.FillPath(b, path); }
        if (stroke is { } s)
        {
            using var pen = new Pen(s, sw);
            if (dashed) pen.DashStyle = DashStyle.Dash;
            g.DrawPath(pen, path);
        }
    }

    static readonly StringFormat Sf = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.EllipsisCharacter,
        LineAlignment = StringAlignment.Center,
    };

    static void Txt(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h = StringAlignment.Near)
    {
        Sf.Alignment = h;
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, r, Sf);
    }

    Hit Add(RectangleF r, Action? click = null, object? drag = null)
    {
        var h = new Hit { R = r, Click = click, Drag = drag, Fixed = _fixed };
        _hits.Add(h);
        return h;
    }

    bool Hov(RectangleF r) => _hover != null && _hover.R == r && _dragging == null;

    void CardBox(Graphics g, RectangleF r)
    {
        if (!_fixed) _contentBottom = Math.Max(_contentBottom, r.Bottom);
        Box(g, r, 8, _pal.Card, _pal.Stroke);
    }

    PointF Content(PointF p) => new(p.X, p.Y - TitleH + _scroll);
    bool Over(Hit h, PointF p) => h.R.Contains(h.Fixed ? p : Content(p));

    void CtlBox(Graphics g, RectangleF r, float rad = 6, bool hover = false) =>
        Box(g, r, rad, hover ? _pal.Hover : _pal.Control, _pal.Stroke);

    // ---------------------------------------------------------------- controls

    void Segmented(Graphics g, RectangleF r, string[] opts, int sel, Action<int> set)
    {
        Box(g, r, 6, _pal.Control, _pal.Stroke);
        float w = r.Width / opts.Length;
        for (int i = 0; i < opts.Length; i++)
        {
            int idx = i;
            var s = new RectangleF(r.X + i * w + 2, r.Y + 2, w - 4, r.Height - 4);
            bool on = i == sel;
            if (on) Box(g, s, 5, _pal.Accent);
            else if (Hov(s)) Box(g, s, 5, _pal.Hover);
            Txt(g, opts[i], _fBody, on ? _pal.AccentText : _pal.Text, s, StringAlignment.Center);
            Add(s, () => set(idx));
        }
    }

    void Stepper(Graphics g, RectangleF r, string value, Action dec, Action inc)
    {
        Box(g, r, 6, _pal.Control, _pal.Stroke);
        var a = new RectangleF(r.X, r.Y, 36, r.Height);
        var b = new RectangleF(r.Right - 36, r.Y, 36, r.Height);
        if (Hov(a)) Box(g, a, 6, _pal.Hover);
        if (Hov(b)) Box(g, b, 6, _pal.Hover);
        Txt(g, "−", _fSec, _pal.Text, a, StringAlignment.Center);
        Txt(g, "+", _fSec, _pal.Text, b, StringAlignment.Center);
        Txt(g, value, _fSemi, _pal.Text, new RectangleF(r.X + 36, r.Y, r.Width - 72, r.Height), StringAlignment.Center);
        Add(a, dec);
        Add(b, inc);
    }

    void Switch(Graphics g, float x, float y, bool on, Action toggle)
    {
        var r = new RectangleF(x, y, 42, 21);
        if (on) Box(g, r, 10.5f, _pal.Accent);
        else Box(g, r, 10.5f, Color.Transparent, _pal.Dim, 1.2f);
        float kx = on ? r.Right - 17 : r.X + 4;
        using var br = new SolidBrush(on ? _pal.AccentText : _pal.Dim);
        g.FillEllipse(br, kx, r.Y + 4, 13, 13);
        Add(r, toggle);
    }

    void Button(Graphics g, RectangleF r, string text, Action click)
    {
        CtlBox(g, r, 6, Hov(r));
        Txt(g, text, _fBody, _pal.Text, r, StringAlignment.Center);
        Add(r, click);
    }

    // ------------------------------------------------------------------ drag

    void DrawDragOverlay(Graphics g)
    {
        if (_dragging == null) return;

        var target = DropTargetAt(_dragPt);
        if (target != null)
        {
            var st = g.Save();
            if (!target.Fixed) g.TranslateTransform(0, TitleH - _scroll);
            using var pen = new Pen(_pal.Accent, 2f);
            using var path = RR(target.R, 6);
            g.DrawPath(pen, path);
            if (target.Indicator?.Invoke(target.Fixed ? _dragPt : Content(_dragPt)) is { } bar)
            {
                using var br = new SolidBrush(_pal.Accent);
                g.FillRectangle(br, bar);
            }
            g.Restore(st);
        }

        string label = _dragging switch
        {
            ModDrag d => TypeTitle(d.Type),
            ItemDrag i => ItemTitle(_page, i.Name),
            SenDrag s => SensorName(s.Key),
            _ => "",
        };
        var r = new RectangleF(_dragPt.X - 44, _dragPt.Y - 16, 104, 32);
        Box(g, r, 6, _dark ? Color.FromArgb(235, 58, 58, 62) : Color.FromArgb(240, 252, 252, 252), _pal.Accent, 1.6f);
        Txt(g, label, _fSemi, _pal.Text, r, StringAlignment.Center);
    }

    Hit? HitAt(PointF p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (Over(_hits[i], p) && (_hits[i].Click != null || _hits[i].Drag != null)) return _hits[i];
        return null;
    }

    Hit? DropTargetAt(PointF p)
    {
        if (_dragging == null) return null;
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (Over(_hits[i], p) && _hits[i].Drop != null && _hits[i].CanDrop?.Invoke(_dragging) == true) return _hits[i];
        return null;
    }

    PointF ToDip(Point p) => new(p.X / S, p.Y / S);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _downPt = ToDip(e.Location);
        _down = HitAt(_downPt);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToDip(e.Location);

        if (_dragging == null && _down?.Drag != null && e.Button == MouseButtons.Left &&
            (Math.Abs(p.X - _downPt.X) > 5 || Math.Abs(p.Y - _downPt.Y) > 5))
        {
            _dragging = _down.Drag;
            Capture = true;
        }

        if (_dragging != null)
        {
            _dragPt = p;
            Cursor = Cursors.Hand;
            Invalidate();
            return;
        }

        var h = HitAt(p);
        if (!ReferenceEquals(h, _hover))
        {
            _hover = h;
            Cursor = h != null ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging == null && _hover != null) { _hover = null; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var p = ToDip(e.Location);

        if (_dragging != null)
        {
            var payload = _dragging;
            var target = DropTargetAt(p);
            _dragging = null;
            _down = null;
            Capture = false;
            if (target != null) target.Drop!(payload, target.Fixed ? p : Content(p));
            else Invalidate();
            return;
        }

        var down = _down;
        _down = null;
        if (down != null && (ReferenceEquals(HitAt(p), down) || Over(down, p)))
            down.Click?.Invoke();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float d = -e.Delta / 120f * 56f;
        if (_fontPick) _fontScroll = Math.Max(0, _fontScroll + d);
        else if (_pick != null) _pickScroll = Math.Max(0, _pickScroll + d);
        else _scroll = Math.Max(0, _scroll + d);
        Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _fontPick) { _fontPick = false; Invalidate(); return true; }
        if (keyData == Keys.Escape && _spaceEdit != null) { _spaceEdit = null; Invalidate(); return true; }
        if (keyData == Keys.Escape && _pick != null) { _pick = null; Invalidate(); return true; }
        if (keyData == Keys.Escape && _dragging != null)
        {
            _dragging = null;
            Capture = false;
            Invalidate();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
