using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TaskbarStats.Ui;

public sealed class Theme : IDisposable
{
    public bool Light { get; }
    public Color Fg { get; }
    public Color FgDim { get; }
    public Color Warn { get; }
    public Color Outline { get; }
    public Color LabelColor { get; }

    /// <summary>Fixed-size fonts, in dip (the renderer applies the DPI scale as a transform).</summary>
    public Font Value { get; }
    public Font ValueLarge { get; }

    readonly FontFamily _family;
    FontFamily? _valFamily;
    readonly FontStyle _labelStyle;
    readonly Dictionary<int, Font> _labelFonts = new();

    public Theme(bool light)
    {
        Light = light;
        if (light)
        {
            Fg = Color.FromArgb(255, 28, 28, 28);
            FgDim = Color.FromArgb(255, 90, 90, 90);
            Warn = Color.FromArgb(255, 200, 50, 30);
            Outline = Color.FromArgb(190, 28, 28, 28);
            LabelColor = Color.FromArgb(255, 70, 70, 70);
        }
        else
        {
            Fg = Color.FromArgb(255, 240, 240, 240);
            FgDim = Color.FromArgb(255, 170, 170, 170);
            Warn = Color.FromArgb(255, 255, 110, 80);
            Outline = Color.FromArgb(190, 240, 240, 240);
            LabelColor = Color.FromArgb(255, 205, 205, 205);
        }

        _family = PrivateLabelFamily() ?? LabelFamily();
        _labelStyle = ReferenceEquals(_family, s_private) ? FontStyle.Regular : FontStyle.Bold;
        FontFamily fam;
        try { fam = _valFamily = new FontFamily(s_text); }
        catch (ArgumentException) { fam = _valFamily = new FontFamily("Segoe UI"); }
        var style = s_bold && fam.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold
                  : fam.IsStyleAvailable(FontStyle.Regular) ? FontStyle.Regular : FontStyle.Bold;
        Value = new Font(fam, s_size, style, GraphicsUnit.Pixel);
        ValueLarge = new Font(fam, s_size * 1.25f, style, GraphicsUnit.Pixel);
    }

    /// <summary>Stacked-letter label font, sized to the room one letter gets.</summary>
    public Font LabelFont(float px)
    {
        int key = (int)Math.Round(px * 4);
        if (!_labelFonts.TryGetValue(key, out var f))
            _labelFonts[key] = f = new Font(_family, key / 4f, _labelStyle, GraphicsUnit.Pixel);
        return f;
    }

    static string s_text = "Segoe UI";
    static bool s_bold;
    static float s_size = 10f;

    /// <summary>The value text font chosen in the settings; applies to themes created afterwards.</summary>
    public static void Configure(TaskbarStats.AppConfig c)
    {
        s_text = string.IsNullOrWhiteSpace(c.TextFont) ? "Segoe UI" : c.TextFont;
        s_bold = c.TextBold;
        s_size = Math.Clamp(c.TextSize, 7f, 16f);
    }

    static PrivateFontCollection? s_fonts;
    static FontFamily? s_private;

    /// <summary>The label font shipped inside the exe (Xanmono), or null when it cannot be loaded.</summary>
    static FontFamily? PrivateLabelFamily()
    {
        if (s_private != null) return s_private;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("font.label.ttf");
            if (s == null) return null;
            var data = new byte[s.Length];
            s.ReadExactly(data);
            IntPtr mem = Marshal.AllocCoTaskMem(data.Length);          // GDI+ keeps using this memory: never freed
            Marshal.Copy(data, 0, mem, data.Length);
            s_fonts = new PrivateFontCollection();
            s_fonts.AddMemoryFont(mem, data.Length);
            return s_private = s_fonts.Families.Length > 0 ? s_fonts.Families[0] : null;
        }
        catch { return null; }
    }

    /// <summary>The optical size made for tiny text, so the stacked letters keep one weight.</summary>
    static FontFamily LabelFamily()
    {
        try { return new FontFamily("Segoe UI Variable Small"); }
        catch (ArgumentException) { return new FontFamily("Segoe UI"); }
    }

    static readonly Graphics s_measure = Graphics.FromImage(new Bitmap(1, 1));

    /// <summary>Width in dip of a string in the given font.</summary>
    public static float TextWidth(string s, Font f) =>
        s_measure.MeasureString(s, f, PointF.Empty, StringFormat.GenericTypographic).Width + 1f;

    /// <summary>Does the taskbar currently use the light theme?</summary>
    public static bool SystemTaskbarIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        Value.Dispose();
        ValueLarge.Dispose();
        _valFamily?.Dispose();
        foreach (var f in _labelFonts.Values) f.Dispose();
        _labelFonts.Clear();
    }
}
