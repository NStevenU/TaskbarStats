using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>
/// One transparent, always-on-top, click-able layered window laid over the taskbar.
/// Redraws only when a displayed value changed; stops polling while hidden.
/// </summary>
internal sealed class WidgetWindow : IDisposable
{
    const string ClassName = "TaskbarStatsWidget";
    const uint TimerId = 1;

    static WidgetWindow? s_instance;
    static readonly WndProc s_proc = StaticProc;

    AppConfig _cfg;
    readonly SensorHub _hub;
    WidgetLayout _layout;

    Theme _theme;
    IntPtr _hwnd;
    uint _taskbarCreatedMsg;

    int _dpi = 96;
    float _scale = 1f;
    int _pxW, _pxH;

    IntPtr _memDc, _dib, _oldBmp;
    Bitmap? _bmp;
    Graphics? _g;

    RECT _lastTaskbar;
    bool _visible, _forceDraw = true;

    public WidgetWindow(AppConfig cfg, SensorHub hub, IReadOnlyDictionary<string, IWidgetModule> mods)
    {
        _cfg = cfg;
        _hub = hub;
        _theme = MakeTheme();
        _layout = WidgetLayout.Build(cfg, mods, _theme);
        foreach (var m in _layout.Modules) _hub.Subscribe(m.Needs);
    }

    /// <summary>Swap in a new configuration without restarting: rebuild modules, layout, theme and timer.</summary>
    public void ApplyConfig(AppConfig cfg)
    {
        foreach (var m in _layout.Modules) _hub.Unsubscribe(m.Needs);
        _cfg = cfg;
        _hub.SetDisabled(cfg.DisabledComponents);
        Fmt.Configure(cfg);
        _theme.Dispose();
        _theme = MakeTheme();
        _layout = WidgetLayout.Build(cfg, Program.BuildModules(cfg), _theme);
        foreach (var m in _layout.Modules) _hub.Subscribe(m.Needs);
        Native.SetTimer(_hwnd, (UIntPtr)TimerId, (uint)cfg.IntervalMs, IntPtr.Zero);
        _lastTaskbar = default;
        _forceDraw = true;
        Tick();
    }

    Theme MakeTheme() => new(_cfg.Theme switch
    {
        "light" => true,
        "dark" => false,
        _ => Theme.SystemTaskbarIsLight(),
    });

    public IntPtr Handle => _hwnd;

    public void Create()
    {
        s_instance = this;
        IntPtr inst = Native.GetModuleHandleW(null);

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
            hInstance = inst,
            hCursor = Native.LoadCursorW(IntPtr.Zero, (IntPtr)Native.IDC_ARROW),
            lpszClassName = ClassName,
        };
        if (Native.RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            ClassName, "TaskbarStats", Native.WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");
        Native.SetTimer(_hwnd, (UIntPtr)TimerId, (uint)_cfg.IntervalMs, IntPtr.Zero);
        Tick();
    }

    public int RunMessageLoop()
    {
        // WinForms loop: also drives the settings window and the popup; WM_QUIT from WM_DESTROY ends it
        Application.Run();
        return 0;
    }

    static IntPtr StaticProc(IntPtr h, uint m, IntPtr w, IntPtr l) =>
        s_instance != null ? s_instance.Proc(h, m, w, l) : Native.DefWindowProcW(h, m, w, l);

    IntPtr Proc(IntPtr h, uint m, IntPtr w, IntPtr l)
    {
        if (m == _taskbarCreatedMsg && m != 0)
        {
            // explorer restarted: everything about the taskbar may have changed
            _lastTaskbar = default;
            _forceDraw = true;
            return IntPtr.Zero;
        }

        switch (m)
        {
            case Native.WM_TIMER:
                Tick();
                return IntPtr.Zero;

            case Native.WM_LBUTTONUP:
            {
                long lp = (long)l;
                float cx = (short)(lp & 0xFFFF) / _scale, cy = (short)((lp >> 16) & 0xFFFF) / _scale;
                var hit = _layout.HitTest(cx, cy, _theme);
                string? type = hit == null ? null : _cfg.Modules.FirstOrDefault(m => m.Id == hit.Id)?.EffectiveType;
                if (type != null) TogglePopup(type);
                return IntPtr.Zero;
            }

            case Native.WM_RBUTTONUP:
                ShowMenu();
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                _lastTaskbar = default;
                _forceDraw = true;
                return IntPtr.Zero;

            case Native.WM_SETTINGCHANGE:
                if (l != IntPtr.Zero && Marshal.PtrToStringUni(l) == "ImmersiveColorSet" && _cfg.Theme == "auto")
                {
                    _theme.Dispose();
                    _theme = MakeTheme();
                    _forceDraw = true;
                }
                break;

            case Native.WM_DESTROY:
                Native.KillTimer(h, (UIntPtr)TimerId);
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProcW(h, m, w, l);
    }

    // ------------------------------------------------------------------ tick

    void Tick()
    {
        // Nothing to look at while a full-screen app/game owns the screen: no polling, no drawing.
        if (IsFullscreenBusy())
        {
            if (_visible)
            {
                Native.ShowWindow(_hwnd, Native.SW_HIDE);
                _visible = false;
            }
            return;
        }

        bool layoutChanged = false;

        uint dpi = Native.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = Native.GetDpiForSystem();
        if (dpi != _dpi)
        {
            _dpi = (int)dpi;
            _scale = dpi / 96f;
            layoutChanged = true;
        }

        if (!TryGetTaskbar(out var tb)) return;
        if (tb.Left != _lastTaskbar.Left || tb.Top != _lastTaskbar.Top ||
            tb.Right != _lastTaskbar.Right || tb.Bottom != _lastTaskbar.Bottom)
        {
            _lastTaskbar = tb;
            layoutChanged = true;
        }

        _hub.Update();

        bool changed = false;
        foreach (var m in _layout.Modules) changed |= m.Prepare(_hub.Current);

        if (changed || layoutChanged || _forceDraw || !_visible)
        {
            Render();
            Present(tb);
            _forceDraw = false;
        }

        // The taskbar is itself topmost and can climb over us when it is clicked.
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    static bool IsFullscreenBusy()
    {
        // 2 = busy (full-screen app), 3 = D3D full-screen, 4 = presentation mode
        return Native.SHQueryUserNotificationState(out int st) == 0 && st is 2 or 3 or 4;
    }

    /// <summary>
    /// Physical-pixel rectangle of the primary taskbar. GetWindowRect on Shell_TrayWnd is used because
    /// ABM_GETTASKBARPOS returned coordinates scaled a second time by the DPI factor on a per-monitor
    /// DPI-aware process (observed: 1.5x too large at 150%).
    /// </summary>
    static bool TryGetTaskbar(out RECT rc)
    {
        IntPtr tray = Native.FindWindowW("Shell_TrayWnd", null);
        if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out rc))
            return rc.Width > 0 && rc.Height > 0;
        rc = default;
        return false;
    }

    // ---------------------------------------------------------------- drawing

    void EnsureSurface(int w, int h)
    {
        if (_bmp != null && w == _pxW && h == _pxH) return;
        FreeSurface();

        var bi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,           // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _oldBmp = Native.SelectObject(_memDc, _dib);

        // GDI+ draws straight into the DIB (premultiplied alpha), so no copy is needed to present.
        _bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
        _g = Graphics.FromImage(_bmp);
        _pxW = w;
        _pxH = h;
    }

    void FreeSurface()
    {
        _g?.Dispose(); _g = null;
        _bmp?.Dispose(); _bmp = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBmp != IntPtr.Zero) Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteDC(_memDc);
        }
        if (_dib != IntPtr.Zero) Native.DeleteObject(_dib);
        _memDc = _dib = _oldBmp = IntPtr.Zero;
    }

    void Render()
    {
        int w = (int)Math.Ceiling(_layout.TotalWidth(_theme) * _scale);
        int h = (int)Math.Ceiling(_layout.Height * _scale);
        EnsureSurface(w, h);

        var g = _g!;
        WidgetLayout.Configure(g, _scale);

        // alpha=1 (not 0) so the whole widget is clickable, not just the glyphs
        g.CompositingMode = CompositingMode.SourceCopy;
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.CompositingMode = CompositingMode.SourceOver;

        _layout.Draw(g, _theme);
        g.Flush(FlushIntention.Sync);
    }

    void Present(RECT tb)
    {
        int Px(int dip) => (int)Math.Round(dip * _scale);

        int x = _cfg.Anchor switch
        {
            "right" => tb.Right - _pxW - Px(_cfg.OffsetX),
            "center" => tb.Left + (tb.Width - _pxW) / 2 + Px(_cfg.OffsetX),
            _ => tb.Left + Px(_cfg.OffsetX),
        };
        int y = tb.Top + (tb.Height - _pxH) / 2 + Px(_cfg.OffsetY);

        var pos = new POINT { X = x, Y = y };
        var size = new SIZE { CX = _pxW, CY = _pxH };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0 /* AC_SRC_OVER */, SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };

        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref pos, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);

        if (!_visible)
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            _visible = true;
        }
    }

    // ------------------------------------------------------------------- menu

    void ShowMenu()
    {
        Native.GetCursorPos(out var pt);
        IntPtr menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)3, "Settings...");
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)1, "Open config folder");
        Native.AppendMenuW(menu, Native.MF_SEPARATOR, UIntPtr.Zero, null);
        Native.AppendMenuW(menu, Native.MF_STRING, (UIntPtr)2, "Exit");

        Native.SetForegroundWindow(_hwnd);
        int cmd = Native.TrackPopupMenu(menu,
            Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN | Native.TPM_RETURNCMD,
            pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        Native.DestroyMenu(menu);

        switch (cmd)
        {
            case 1:
                Directory.CreateDirectory(AppConfig.Dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppConfig.Dir}\"") { UseShellExecute = true });
                break;
            case 2:
                Native.DestroyWindow(_hwnd);
                break;
            case 3:
                ShowSettings();
                break;
        }
    }

    // ------------------------------------------------------------ settings / popup

    SettingsForm? _settings;
    DetailPopup? _popup;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr h, IntPtr min, IntPtr max);

    /// <summary>Give the pages of the closed window back to the OS so the idle footprint stays small.</summary>
    static void TrimMemory()
    {
        GC.Collect();
        SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
    }

    public void Open(string what)
    {
        if (what == "settings") ShowSettings();
        else if (what.StartsWith("popup")) TogglePopup(what.Length > 6 ? what[6..] : "cpu");
    }

    void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        _settings = new SettingsForm(_cfg.Clone(), _hub, cfg =>
        {
            cfg.Save();
            ApplyConfig(cfg);
        });
        _settings.FormClosed += (_, _) => { _settings = null; TrimMemory(); };
        _settings.Show();
    }

    void TogglePopup(string type)
    {
        if (_popup != null)
        {
            bool same = _popup.Type == type;
            _popup.Close();
            if (same) return;
        }
        if (!TryGetTaskbar(out var tb)) return;
        Native.GetWindowRect(_hwnd, out var me);
        Native.GetCursorPos(out var cur);
        int ax = cur.X > me.Left && cur.X < me.Right ? cur.X : me.Left;
        _popup = new DetailPopup(type, _hub, _cfg, _theme.Light, ax, tb.Top);
        _popup.FormClosed += (_, _) => { _popup = null; TrimMemory(); };
        _popup.Show();
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && s_instance == this) s_instance = null;
        FreeSurface();
        _theme.Dispose();
    }
}
