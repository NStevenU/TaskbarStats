using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Security.Principal;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>What each module's click popup can show; also drives the Sections page of the settings.</summary>
internal static class PopupSections
{
    public static (string Id, string Title, string Desc)[] For(string type) => type switch
    {
        "cpu" => new[]
        {
            ("cores", "Core usage", "One bar per core"),
            ("history", "History graph", "Total usage over the last minutes"),
            ("freq", "Frequency", "All cores, efficiency and performance cores"),
            ("procs", "Top processes", "Programs using the most CPU"),
            ("uptime", "Uptime", "Time since the PC started"),
        },
        "gpu" => new[]
        {
            ("summary", "Details", "Temperature, VRAM, clocks, fan and power"),
            ("history", "History graph", "GPU usage over the last minutes"),
            ("procs", "Top processes", "Programs using the GPU"),
        },
        "mem" => new[]
        {
            ("details", "Details", "Used, free, cache, commit and swap"),
            ("history", "History graph", "Memory use over the last minutes"),
            ("procs", "Top processes", "Programs using the most memory"),
        },
        "disk" => new[]
        {
            ("drives", "Drives", "Used space of every drive"),
            ("history", "Read / write graph", "Disk speed over the last minutes"),
            ("procs", "Top processes", "Programs with the most file I/O (includes network files)"),
            ("temp", "Drive temperature", "From LibreHardwareMonitor, may need administrator"),
        },
        "net" => new[]
        {
            ("ip", "Local IP", "The address of this PC on your network"),
            ("history", "Up / down graph", "Network speed over the last minutes"),
            ("adapters", "Adapters", "Every connected adapter with its speed"),
        },
        "sensors" => new[] { ("list", "All sensors", "Every sensor LibreHardwareMonitor can read, live") },
        _ => Array.Empty<(string, string, string)>(),
    };
}

/// <summary>
/// The window that opens when a module is clicked. Acrylic backdrop, rounded corners, drawn by hand into a
/// 32-bit surface so the untouched pixels stay see-through. Everything it collects runs only while it is open.
/// </summary>
internal sealed class DetailPopup : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    const float W = 360f, Pad = 14f, CW = W - 2 * Pad, HistLen = 60;

    public string Type { get; }
    readonly SensorHub _hub;
    readonly AppConfig _cfg;
    readonly bool _light;
    readonly int _anchorX, _anchorY;
    readonly System.Windows.Forms.Timer _timer = new(), _watch = new();
    readonly SensorKey[] _keys;
    bool _wasActive;
    float S => DeviceDpi / 96f;

    // palette
    readonly Color _fg, _dim, _card, _track;
    static readonly Color Blue = Color.FromArgb(132, 188, 255), Peach = Color.FromArgb(255, 205, 150), Coral = Color.FromArgb(255, 132, 148);

    // fonts (dip pixels)
    readonly Font _fTitle, _fText, _fBold, _fBig, _fSmall;

    // data
    readonly Dictionary<SensorKey, List<float>> _hist = new();
    readonly ProcSampler _procs = new();
    LiveLhm? _live;
    volatile SysInfo.Freq? _freq;
    volatile bool _swapDone;
    (double used, double size)? _swap;
    volatile bool _freqBusy;
    List<SysInfo.Adapter> _adapters = new();
    readonly Dictionary<string, (long rx, long tx, long ms)> _adPrev = new();
    readonly Dictionary<string, (double down, double up)> _adRate = new();
    List<SysInfo.Drive> _drives = new();
    bool _admin;

    // surface
    Bitmap? _surf;
    IntPtr _dc, _hbm;
    int _sw, _sh;
    float _y, _scroll, _contentH;
    int _bottomY;

    public DetailPopup(string type, SensorHub hub, AppConfig cfg, bool light, int anchorX, int anchorY)
    {
        Type = type;
        _hub = hub;
        _cfg = cfg;
        _light = light;
        _anchorX = anchorX;
        _anchorY = anchorY;
        _bottomY = anchorY;

        _fg = light ? Color.FromArgb(28, 28, 28) : Color.FromArgb(245, 245, 245);
        _dim = light ? Color.FromArgb(255, 96, 96, 100) : Color.FromArgb(255, 172, 172, 178);
        _card = light ? Color.FromArgb(128, 255, 255, 255) : Color.FromArgb(24, 255, 255, 255);
        _track = Color.FromArgb(light ? 34 : 40, _fg);

        string fam = Pick("Segoe UI Variable Text", "Segoe UI"), semi = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
        _fTitle = new Font(fam, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fSmall = new Font(fam, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fText = new Font(fam, 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBold = new Font(semi, 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fBig = new Font(semi, 26f, FontStyle.Regular, GraphicsUnit.Pixel);

        _keys = type switch
        {
            "cpu" => new[] { SensorKey.CpuTotal, SensorKey.CpuCores, SensorKey.CpuTemp },
            "gpu" => new[] { SensorKey.GpuLoad, SensorKey.GpuTemp, SensorKey.GpuMemUsedMb, SensorKey.GpuFanPct },
            "mem" => new[] { SensorKey.MemUsedPct, SensorKey.MemUsedMb },
            "disk" => new[] { SensorKey.DiskUsedPct, SensorKey.DiskActivePct, SensorKey.DiskReadKBs, SensorKey.DiskWriteKBs },
            "net" => new[] { SensorKey.NetDownKBs, SensorKey.NetUpKBs },
            _ => Array.Empty<SensorKey>(),
        };

        try { _admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); } catch { }

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false;
        BackColor = Color.Black;

        _hub.Subscribe(_keys);
        _hub.Update();

        BeginCollect();
        _timer.Interval = Math.Max(1000, _cfg.IntervalMs);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Activated += (_, _) => _wasActive = true;
        Deactivate += (_, _) => { if (_wasActive) Close(); };
        // the click that opened us may not hand us the foreground; poll so clicking any other window always closes
        _watch.Interval = 120;
        _watch.Tick += (_, _) =>
        {
            bool mine = Native.GetForegroundWindow() == Handle;
            if (mine) _wasActive = true;
            else if (_wasActive) Close();
        };
        _watch.Start();
    }

    static string Pick(string want, string fallback)
    {
        try { using var f = new FontFamily(want); return want; } catch { return fallback; }
    }

    bool On(string section) => !_cfg.DisabledSections.Contains(Type + "." + section);

    // ------------------------------------------------------------------ lifetime

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = _light ? 0 : 1, round = 2, acrylic = 3;
        DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        DwmSetWindowAttribute(Handle, 33, ref round, 4);
        DwmSetWindowAttribute(Handle, 38, ref acrylic, 4);
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(Handle, ref m);
        Relayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.SetForegroundWindow(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        _watch.Stop();
        _watch.Dispose();
        _hub.Unsubscribe(_keys);
        _live?.Dispose();
        FreeSurface();
        _fTitle.Dispose(); _fText.Dispose(); _fBold.Dispose(); _fBig.Dispose(); _fSmall.Dispose();
        ProcIcons.Clear();
        base.OnFormClosed(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        float max = Math.Max(0, _contentH - ClientSize.Height / S);
        _scroll = Math.Clamp(_scroll - e.Delta / 120f * 48f, 0, max);
        Invalidate();
    }

    // ------------------------------------------------------------------ data

    void BeginCollect()
    {
        switch (Type)
        {
            case "cpu": StartFreq(); break;
            case "gpu": _live = new LiveLhm(gpu: true); _procs.WantGpu = true; break;
            case "mem": Task.Run(() => { _swap = SysInfo.ReadSwap(); _swapDone = true; }); break;
            case "disk": _live = new LiveLhm(storage: true); _drives = SysInfo.ReadDrives(); break;
            case "net": break;
            case "sensors": _live = new LiveLhm(cpu: true, mobo: true, gpu: true, mem: true, storage: true); break;
        }
        CollectNow();
    }

    void StartFreq()
    {
        if (_freqBusy) return;
        _freqBusy = true;
        Task.Run(() => { _freq = SysInfo.ReadFrequency(); _freqBusy = false; });
    }

    void CollectNow()
    {
        bool procs = Type is "cpu" or "gpu" or "mem" or "disk" && On("procs");
        if (procs) Task.Run(_procs.Sample);
        if (_live != null) Task.Run(_live.Update);
    }

    void Tick()
    {
        var s = _hub.Current;
        foreach (var k in _keys)
        {
            if (k == SensorKey.CpuCores) continue;
            if (!_hist.TryGetValue(k, out var l)) _hist[k] = l = new List<float>();
            l.Add(double.IsNaN(s[k]) ? 0f : (float)s[k]);
            if (l.Count > HistLen) l.RemoveAt(0);
        }
        if (Type == "cpu") StartFreq();
        if (Type == "net") UpdateAdapters();
        if (Type == "disk") _drives = SysInfo.ReadDrives();
        CollectNow();
        Relayout();
        Invalidate();
    }

    void UpdateAdapters()
    {
        _adapters = SysInfo.ReadAdapters();
        long now = Environment.TickCount64;
        foreach (var a in _adapters)
        {
            if (_adPrev.TryGetValue(a.Name, out var p) && now > p.ms)
            {
                double dt = (now - p.ms) / 1000.0;
                _adRate[a.Name] = (Math.Max(0, (a.Rx - p.rx) / 1024.0 / dt), Math.Max(0, (a.Tx - p.tx) / 1024.0 / dt));
            }
            _adPrev[a.Name] = (a.Rx, a.Tx, now);
        }
    }

    // ------------------------------------------------------------------ geometry

    /// <summary>Measure the content and fit the window above the taskbar.</summary>
    void Relayout()
    {
        if (!IsHandleCreated) return;
        using var bmp = new Bitmap(4, 4);
        using var g = Graphics.FromImage(bmp);
        _contentH = Frame(g, false);

        var wa = Screen.FromPoint(new Point(_anchorX, _anchorY)).WorkingArea;
        float s = S;
        int w = (int)Math.Round(W * s);
        int maxH = (int)(wa.Height * 0.86);
        int h = Math.Min((int)Math.Ceiling(_contentH * s), maxH);
        int x = Math.Clamp(_anchorX - w / 2, wa.Left + 4, wa.Right - w - 4);
        int y = _bottomY - (int)(8 * s) - h;
        if (Bounds != new Rectangle(x, y, w, h)) SetBounds(x, y, w, h);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _contentH - h / s));
    }

    // ------------------------------------------------------------------ painting

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

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        EnsureSurface(w, h);
        try
        {
            using var g = Graphics.FromImage(_surf!);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(S, S);
            g.TranslateTransform(0, -_scroll);
            Frame(g, true);
            if (_contentH * S > h)                                   // thin scrollbar when the list is longer than the window
            {
                g.ResetTransform();
                g.TranslateTransform(0, 0);
                float th = h * (h / (_contentH * S)), ty = _scroll * S * (h / (_contentH * S));
                using var b = new SolidBrush(Color.FromArgb(90, _fg));
                g.FillRectangle(b, w - 5 * S, ty + 2, 3 * S, Math.Max(24, th - 4));
            }
        }
        catch (Exception ex) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ts_error.log"), ex + "\n"); } catch { } }

        IntPtr hdc = e.Graphics.GetHdc();
        try { Native.BitBlt(hdc, 0, 0, w, h, _dc, 0, 0, Native.SRCCOPY); }
        finally { e.Graphics.ReleaseHdc(hdc); }

        if (Environment.GetEnvironmentVariable("TS_POPUPRECT") is { Length: > 0 } f)
            try { File.WriteAllText(f, $"{Left},{Top},{Width},{Height}"); } catch { }
    }

    // ------------------------------------------------------------------ drawing primitives (dip)

    Graphics _g = null!;
    bool _draw;
    static readonly StringFormat Left_ = MakeFmt(StringAlignment.Near), Right_ = MakeFmt(StringAlignment.Far), Mid_ = MakeFmt(StringAlignment.Center);
    static StringFormat MakeFmt(StringAlignment a) => new(StringFormat.GenericTypographic)
    { Alignment = a, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };

    void Txt(string s, Font f, Color c, float x, float y, float w, float h, bool right = false, bool mid = false)
    {
        if (!_draw) return;
        using var b = new SolidBrush(c);
        _g.DrawString(s, f, b, new RectangleF(x, y, w, h), mid ? Mid_ : right ? Right_ : Left_);
    }

    GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    void Fill(RectangleF r, float rad, Color c)
    {
        if (!_draw) return;
        using var p = Round(r, rad);
        using var b = new SolidBrush(c);
        _g.FillPath(b, p);
    }

    RectangleF Card(float h)
    {
        var r = new RectangleF(Pad, _y, CW, h);
        Fill(r, 10, _card);
        if (_draw)
        {
            using var p = Round(r, 10);
            using var pen = new Pen(Color.FromArgb(_light ? 40 : 22, _fg), 1f);
            _g.DrawPath(pen, p);
        }
        _y += h + 8;
        return r;
    }

    void Title(string text, string? right = null)
    {
        Txt(text, _fTitle, _dim, Pad + 2, _y, CW - 4, 18);
        if (right != null) Txt(right, _fTitle, _dim, Pad + 2, _y, CW - 4, 18, true);
        _y += 20;
    }

    static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>Pastel scale: low = blue, middle = peach, high = coral.</summary>
    static Color Heat(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5f ? Lerp(Blue, Peach, t * 2) : Lerp(Peach, Coral, (t - 0.5f) * 2);
    }

    void Header(string name, string big, string? line1, string? line2)
    {
        Txt(name, _fTitle, _dim, Pad + 2, _y, 200, 16);
        Txt(big, _fBig, _fg, Pad + 2, _y + 14, 200, 34);
        if (line1 != null) Txt(line1, _fBold, _fg, Pad, _y + 14, CW, 20, true);
        if (line2 != null) Txt(line2, _fText, _dim, Pad, _y + 33, CW, 20, true);
        _y += 58;
    }

    void Rows(params (string k, string v)[] rows)
    {
        float h = rows.Length * 24 + 8;
        var r = Card(h);
        float y = r.Y + 4;
        foreach (var (k, v) in rows)
        {
            Txt(k, _fText, _dim, r.X + 12, y, r.Width * 0.5f, 24);
            Txt(v, _fBold, _fg, r.X + r.Width * 0.4f, y, r.Width * 0.6f - 12, 24, true);
            y += 24;
        }
    }

    /// <summary>Horizontal meter with a pastel gradient fill.</summary>
    void Meter(RectangleF r, double frac)
    {
        if (!_draw) return;
        frac = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
        Fill(r, r.Height / 2, _track);
        if (frac <= 0.001) return;
        var fr = new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * frac)), r.Height);
        using var p = Round(fr, r.Height / 2);
        using var b = new LinearGradientBrush(new RectangleF(r.X - 0.5f, r.Y, r.Width + 1, r.Height), Blue, Coral, LinearGradientMode.Horizontal)
        { InterpolationColors = new ColorBlend(3) { Colors = new[] { Blue, Peach, Coral }, Positions = new[] { 0f, 0.5f, 1f } } };
        _g.FillPath(b, p);
    }

    /// <summary>Area chart of the last minutes with the pastel gradient (tall = coral).</summary>
    void Chart(string title, string? right, IList<float> data, float max, float h = 62)
    {
        Title(title, right);
        var r = Card(h);
        if (!_draw) return;
        var inner = new RectangleF(r.X + 8, r.Y + 8, r.Width - 16, r.Height - 16);
        if (data.Count < 2) return;
        var pts = new List<PointF>();
        float step = inner.Width / (HistLen - 1);
        float x0 = inner.Right - step * (data.Count - 1);
        for (int i = 0; i < data.Count; i++)
        {
            float v = Math.Clamp(data[i] / Math.Max(max, 0.0001f), 0, 1);
            pts.Add(new PointF(x0 + i * step, inner.Bottom - v * inner.Height));
        }
        var blend = new ColorBlend(3) { Colors = new[] { Coral, Peach, Blue }, Positions = new[] { 0f, 0.5f, 1f } };
        using var line = new LinearGradientBrush(new RectangleF(inner.X, inner.Y - 0.5f, inner.Width, inner.Height + 1), Coral, Blue, LinearGradientMode.Vertical) { InterpolationColors = blend };
        var faded = new ColorBlend(3)
        {
            Colors = new[] { Color.FromArgb(150, Coral), Color.FromArgb(105, Peach), Color.FromArgb(34, Blue) },
            Positions = new[] { 0f, 0.5f, 1f },
        };
        using var fill = new LinearGradientBrush(new RectangleF(inner.X, inner.Y - 0.5f, inner.Width, inner.Height + 1), Coral, Blue, LinearGradientMode.Vertical) { InterpolationColors = faded };

        using var path = new GraphicsPath();
        path.AddCurve(pts.ToArray(), 0.25f);
        var area = (GraphicsPath)path.Clone();
        area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, inner.Bottom);
        area.AddLine(pts[^1].X, inner.Bottom, pts[0].X, inner.Bottom);
        area.CloseFigure();
        var save = _g.Save();
        _g.SetClip(inner, CombineMode.Intersect);
        _g.FillPath(fill, area);
        using var pen = new Pen(line, 1.8f) { LineJoin = LineJoin.Round };
        _g.DrawPath(pen, path);
        _g.Restore(save);
        area.Dispose();
    }

    /// <summary>One bar per core, pastel by load.</summary>
    void CoreBars(float[] v)
    {
        var r = Card(88);
        if (!_draw || v.Length == 0) return;
        float gap = 4f, inner = r.Width - 24;
        float bw = Math.Clamp((inner - gap * (v.Length - 1)) / v.Length, 4, 22);
        float total = bw * v.Length + gap * (v.Length - 1);
        float x = r.X + (r.Width - total) / 2, top = r.Y + 12, bh = 52;
        for (int i = 0; i < v.Length; i++)
        {
            var track = new RectangleF(x, top, bw, bh);
            Fill(track, 3f, _track);
            float t = Math.Clamp(v[i] / 100f, 0, 1), fh = Math.Max(2f, bh * t);
            if (t > 0.01f)
            {
                var f = new RectangleF(x, top + bh - fh, bw, fh);
                using var p = Round(f, Math.Min(3f, fh / 2));
                using var b = new LinearGradientBrush(new RectangleF(x, top + bh - fh - 0.5f, bw, fh + 1), Heat(t), Lerp(Blue, Heat(t), 0.35f), LinearGradientMode.Vertical);
                _g.FillPath(b, p);
            }
            if (bw >= 8) Txt((i + 1).ToString(), _fSmall, _dim, x - 3, top + bh + 3, bw + 6, 14, false, true);
            x += bw + gap;
        }
        if (_draw)
        {
            using var gp = new Pen(Color.FromArgb(_light ? 50 : 40, _fg), 1f) { DashStyle = DashStyle.Dot };
            foreach (float q in new[] { 0.25f, 0.5f, 0.75f }) _g.DrawLine(gp, r.X + 6, top + bh * q, r.Right - 6, top + bh * q);
        }
        // centred labels use the Left format; nudge by drawing again is unnecessary at this size
    }

    void Procs(string title, Func<ProcRow, double> sel, Func<ProcRow, string> fmt, bool activeOnly = false)
    {
        int n = Math.Clamp(_cfg.ProcessCount, 1, 15);
        var rows = _procs.Rows.Where(p => !activeOnly || sel(p) > 0.05).OrderByDescending(sel).Take(n).ToArray();
        Title(title);
        var r = Card((activeOnly ? Math.Max(1, rows.Length) : n) * 28 + 8);
        if (!_draw) return;
        float y = r.Y + 4;
        for (int i = 0; i < n; i++, y += 28)
        {
            if (i >= rows.Length) break;
            var p = rows[i];
            var ico = ProcIcons.Get(p.Path);
            var ir = new RectangleF(r.X + 12, y + 6, 16, 16);
            if (ico != null) { _g.InterpolationMode = InterpolationMode.HighQualityBicubic; _g.DrawImage(ico, ir); }
            else Fill(ir, 4, _track);
            Txt(p.Name, _fText, _fg, r.X + 36, y, r.Width - 36 - 96, 28);
            Txt(fmt(p), _fBold, _fg, r.Right - 100, y, 88, 28, true);
        }
        if (rows.Length == 0) Txt(activeOnly && _procs.Rows.Length > 0 ? "No activity" : "Reading…", _fText, _dim, r.X + 12, r.Y + 4, r.Width - 24, 28);
    }

    // ------------------------------------------------------------------ formatting

    static string Bytes(double b) =>
        b >= 1073741824 ? $"{b / 1073741824:0.0} GB" : b >= 1048576 ? $"{b / 1048576:0} MB" : $"{b / 1024:0} KB";
    static string Rate(double bps) =>
        bps >= 1048576 ? $"{bps / 1048576:0.0} MB/s" : bps >= 1024 ? $"{bps / 1024:0} KB/s" : $"{bps:0} B/s";
    static string KbRate(double kbs) => double.IsNaN(kbs) ? "--" : Rate(kbs * 1024);
    static string Ghz(double mhz) => mhz >= 1000 ? $"{mhz / 1000:0.00} GHz" : $"{mhz:0} MHz";
    static string Pct(double v) => double.IsNaN(v) ? "--" : $"{(int)Math.Round(v)}%";

    // ------------------------------------------------------------------ frames

    float Frame(Graphics g, bool draw)
    {
        _g = g;
        _draw = draw;
        _y = 14;
        switch (Type)
        {
            case "cpu": Cpu(); break;
            case "gpu": Gpu(); break;
            case "mem": Mem(); break;
            case "disk": Disk(); break;
            case "net": Net(); break;
            case "sensors": Sensors(); break;
        }
        return _y + 6;
    }

    float[] H(SensorKey k) => _hist.TryGetValue(k, out var l) ? l.ToArray() : Array.Empty<float>();
    double V(SensorKey k) => _hub.Current[k];

    void Cpu()
    {
        var s = _hub.Current;
        string? temp = double.IsNaN(s[SensorKey.CpuTemp]) ? null : "Temperature  " + Fmt_.Temp(s[SensorKey.CpuTemp]);
        var f = _freq;
        Header("CPU", Pct(s[SensorKey.CpuTotal]), temp, f != null ? Ghz(f.All) : null);

        if (On("cores"))
        {
            Title("Cores", $"{(s.PhysCores.Length > 0 ? s.PhysCores.Length : s.Cores.Length)} cores");
            CoreBars(s.PhysCores.Length > 0 ? s.PhysCores : s.Cores);
        }
        if (On("history")) Chart("History", Pct(s[SensorKey.CpuTotal]), H(SensorKey.CpuTotal), 100);
        if (On("freq"))
        {
            Title("Frequency");
            if (f == null) Rows(("All cores", "--"));
            else
            {
                var rows = new List<(string, string)> { ("All cores", Ghz(f.All)) };
                if (f.Efficiency is { } e) rows.Add(("Efficiency cores", Ghz(e)));
                if (f.Performance is { } p) rows.Add(("Performance cores", Ghz(p)));
                Rows(rows.ToArray());
            }
        }
        if (On("procs")) Procs("Top processes", p => p.Cpu, p => $"{p.Cpu:0.0}%");
        if (On("uptime")) { Title("System"); Rows(("Uptime", SysInfo.Uptime())); }
    }

    void Gpu()
    {
        var s = _hub.Current;
        var live = _live?.Sensors ?? Array.Empty<LiveSensor>();
        LiveSensor? Find(string kind, params string[] names) =>
            live.FirstOrDefault(x => x.Kind == kind && names.Any(n => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase)));

        var core = Find("clock", "Core"); var memClk = Find("clock", "Memory");
        var power = live.FirstOrDefault(x => x.Kind == "power");
        var total = Find("smalldata", "Memory Total"); var used = Find("smalldata", "Memory Used");
        var fanRpm = live.FirstOrDefault(x => x.Kind == "fan");
        string hwName = live.FirstOrDefault()?.Hardware ?? "";

        Header(hwName.Length > 0 ? "GPU · " + hwName : "GPU", Pct(s[SensorKey.GpuLoad]),
            double.IsNaN(s[SensorKey.GpuTemp]) ? null : "Temperature  " + Fmt_.Temp(s[SensorKey.GpuTemp]), power != null ? $"{power.Value:0.#} W" : null);

        if (On("summary"))
        {
            Title("Details");
            var rows = new List<(string, string)>();
            double usedMb = !double.IsNaN(s[SensorKey.GpuMemUsedMb]) ? s[SensorKey.GpuMemUsedMb] : used?.Value ?? double.NaN;
            if (!double.IsNaN(usedMb))
                rows.Add(("VRAM", total != null ? $"{usedMb / 1024:0.0} / {total.Value / 1024:0.0} GB" : $"{usedMb / 1024:0.0} GB"));
            if (core != null) rows.Add(("Core clock", Ghz(core.Value)));
            if (memClk != null) rows.Add(("Memory clock", Ghz(memClk.Value)));
            if (fanRpm != null) rows.Add(("Fan", Fmt_.Rpm(fanRpm.Value)));
            else if (!double.IsNaN(s[SensorKey.GpuFanPct])) rows.Add(("Fan", Pct(s[SensorKey.GpuFanPct])));
            if (power != null) rows.Add(("Power", $"{power.Value:0.#} W"));
            if (rows.Count == 0) rows.Add(("Details", _live is { Ready: false } ? "Reading…" : "Not available"));
            Rows(rows.ToArray());
        }
        if (On("history")) Chart("History", Pct(s[SensorKey.GpuLoad]), H(SensorKey.GpuLoad), 100);
        if (On("procs")) Procs("Top processes", p => p.Gpu, p => $"{p.Gpu:0.#}%", true);
    }

    void Mem()
    {
        var s = _hub.Current;
        var m = SysInfo.ReadMemory();
        Header("Memory", Pct(s[SensorKey.MemUsedPct]), $"{m.UsedGb:0.0} / {m.TotalGb:0.0} GB", null);
        if (On("details"))
        {
            Title("Details");
            var rows = new List<(string, string)>
            {
                ("Used", $"{m.UsedGb:0.0} GB"), ("Free", $"{m.FreeGb:0.0} GB"), ("Cache", $"{m.CacheGb:0.0} GB"),
                ("Commit", $"{m.CommitGb:0.0} / {m.CommitLimitGb:0.0} GB"),
            };
            if (_swap is { } sw) rows.Add(("Swap (page file)", $"{sw.used:0.0} / {sw.size:0.0} GB"));
            else if (!_swapDone) rows.Add(("Swap (page file)", "Reading…"));
            Rows(rows.ToArray());
        }
        if (On("history")) Chart("History", Pct(s[SensorKey.MemUsedPct]), H(SensorKey.MemUsedPct), 100);
        if (On("procs")) Procs("Top processes", p => p.Mem, p => Bytes(p.Mem));
    }

    void Disk()
    {
        var s = _hub.Current;
        Header("Disks", Pct(s[SensorKey.DiskActivePct]), "Read  " + KbRate(s[SensorKey.DiskReadKBs]), "Write  " + KbRate(s[SensorKey.DiskWriteKBs]));
        if (On("drives"))
        {
            Title("Drives");
            var r = Card(Math.Max(1, _drives.Count) * 44 + 8);
            float y = r.Y + 6;
            foreach (var d in _drives)
            {
                double used = d.TotalGb - d.FreeGb;
                Txt(d.Name + (d.Label.Length > 0 ? "  " + d.Label : ""), _fText, _fg, r.X + 12, y, r.Width * 0.55f, 20);
                Txt($"{d.FreeGb:0} GB free of {d.TotalGb:0}", _fSmall, _dim, r.X + r.Width * 0.4f, y, r.Width * 0.6f - 12, 20, true);
                Meter(new RectangleF(r.X + 12, y + 24, r.Width - 24, 7), used / Math.Max(1, d.TotalGb));
                y += 44;
            }
        }
        if (On("history"))
        {
            Chart("Read", KbRate(s[SensorKey.DiskReadKBs]), H(SensorKey.DiskReadKBs), AutoMax(H(SensorKey.DiskReadKBs), 512), 52);
            Chart("Write", KbRate(s[SensorKey.DiskWriteKBs]), H(SensorKey.DiskWriteKBs), AutoMax(H(SensorKey.DiskWriteKBs), 512), 52);
        }
        if (On("procs")) Procs("Top processes (file I/O)", p => p.Read + p.Write, p => Rate(p.Read + p.Write), true);
        if (On("temp"))
        {
            Title("Drive temperature");
            var t = (_live?.Sensors ?? Array.Empty<LiveSensor>()).Where(x => x.Kind == "temperature")
                .GroupBy(x => x.Hardware).Select(g => (g.Key, Fmt_.Temp(g.First().Value))).ToArray();
            if (t.Length == 0) Rows((_live is { Ready: true } ? (_admin ? "No sensor" : "Needs administrator") : "Reading…", "--"));
            else Rows(t);
        }
    }

    static float AutoMax(float[] d, float min) => Math.Max(min, d.Length == 0 ? 0 : d.Max() * 1.15f);

    void Net()
    {
        var s = _hub.Current;
        Header("Network", "↓ " + Fmt_.Speed(s[SensorKey.NetDownKBs]), "Upload  " + KbRate(s[SensorKey.NetUpKBs]), "Download  " + KbRate(s[SensorKey.NetDownKBs]));
        if (On("ip"))
        {
            Title("Local IP");
            var main = _adapters.FirstOrDefault(a => a.Ip != null);
            Rows((main?.Kind ?? "Adapter", main?.Ip ?? "--"));
        }
        if (On("history"))
        {
            Chart("Download", KbRate(s[SensorKey.NetDownKBs]), H(SensorKey.NetDownKBs), AutoMax(H(SensorKey.NetDownKBs), 256), 56);
            Chart("Upload", KbRate(s[SensorKey.NetUpKBs]), H(SensorKey.NetUpKBs), AutoMax(H(SensorKey.NetUpKBs), 256), 56);
        }
        if (On("adapters"))
        {
            Title("Adapters");
            int n = Math.Max(1, _adapters.Count);
            var r = Card(n * 46 + 8);
            float y = r.Y + 4;
            if (_adapters.Count == 0) Txt("No connected adapter", _fText, _dim, r.X + 12, y, r.Width - 24, 46);
            foreach (var a in _adapters)
            {
                _adRate.TryGetValue(a.Name, out var rt);
                Txt(a.Name, _fText, _fg, r.X + 12, y + 2, r.Width * 0.55f, 20);
                Txt(a.Speed(), _fSmall, _dim, r.X + r.Width * 0.5f, y + 2, r.Width * 0.5f - 12, 20, true);
                Txt($"{a.Kind}  {a.Ip ?? ""}", _fSmall, _dim, r.X + 12, y + 22, r.Width * 0.5f, 18);
                Txt($"↓ {Rate(rt.down * 1024)}   ↑ {Rate(rt.up * 1024)}", _fSmall, _fg, r.X + r.Width * 0.3f, y + 22, r.Width * 0.7f - 12, 18, true);
                y += 46;
            }
        }
    }

    void Sensors()
    {
        var all = _live?.Sensors ?? Array.Empty<LiveSensor>();
        Header("Sensors", all.Count > 0 ? $"{all.Count}" : "…", null, null);
        if (!On("list")) return;
        if (_live is { Ready: false } || all.Count == 0)
        {
            var r = Card(40);
            Txt(_live is { Ready: false } ? "Reading sensors…" : "No sensors found", _fText, _dim, r.X + 12, r.Y, r.Width - 24, 40);
            return;
        }
        string[] hwOrder = { "Cpu", "Motherboard", "SuperIO", "GpuNvidia", "GpuAmd", "GpuIntel", "Memory", "Storage", "EmbeddedController" };
        string[] kindOrder = { "temperature", "fan", "control", "load", "clock", "power", "voltage", "current", "data", "smalldata", "factor", "level", "throughput", "energy", "noise" };
        foreach (var grp in all.GroupBy(x => x.Hardware).OrderBy(g => Array.IndexOf(hwOrder, g.First().HwType) is var i && i < 0 ? 99 : i))
        {
            Title(grp.Key);
            var list = grp.OrderBy(x => Array.IndexOf(kindOrder, x.Kind) is var i && i < 0 ? 99 : i).ThenBy(x => x.Name).ToList();
            int kinds = list.Select(x => x.Kind).Distinct().Count();
            var r = Card(list.Count * 24 + kinds * 18 + 12);
            float y = r.Y + 6;
            string last = "";
            foreach (var x in list)
            {
                if (x.Kind != last)
                {
                    last = x.Kind;
                    Txt(KindTitle(x.Kind), _fSmall, _dim, r.X + 12, y, r.Width - 24, 18);
                    y += 18;
                }
                Txt(x.Name, _fText, _fg, r.X + 12, y, r.Width * 0.62f, 24);
                Txt(ValueText(x), _fBold, _fg, r.X + r.Width * 0.5f, y, r.Width * 0.5f - 12, 24, true);
                y += 24;
            }
        }
        if (!_admin)
        {
            var r = Card(40);
            Txt("Run as administrator to see CPU and motherboard sensors.", _fSmall, _dim, r.X + 12, r.Y, r.Width - 24, 40);
        }
    }

    static string ValueText(LiveSensor x) =>
        x.Kind == "temperature" && x.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)
            ? (TaskbarStats.Ui.Fmt.Fahrenheit ? $"{x.Value * 9 / 5:0}°F" : $"{x.Value:0}°C")      // a difference, not a temperature
            : CustomSensors.Format(x.Kind, x.Value);

    static string KindTitle(string kind) => kind switch
    {
        "temperature" => "Temperatures", "fan" => "Fans", "control" => "Fan controls", "load" => "Load", "clock" => "Clocks",
        "power" => "Power", "voltage" => "Voltages", "current" => "Current", "data" or "smalldata" => "Data", "factor" => "Factors",
        "level" => "Levels", "throughput" => "Throughput", "energy" => "Energy", "noise" => "Noise", _ => kind,
    };
}

/// <summary>Shorthand for the formatting helpers (the popup has its own Fmt method for string formats).</summary>
internal static class Fmt_
{
    public static string Temp(double v) => TaskbarStats.Ui.Fmt.Temp(v);
    public static string Rpm(double v) => TaskbarStats.Ui.Fmt.Rpm(v);
    public static string Speed(double kbs) => TaskbarStats.Ui.Fmt.Speed(kbs);
}

internal static class AdapterExt
{
    public static string Speed(this SysInfo.Adapter a) =>
        a.SpeedMbps >= 1000 ? $"{a.SpeedMbps / 1000.0:0.#} Gbps" : a.SpeedMbps > 0 ? $"{a.SpeedMbps} Mbps" : "";
}
