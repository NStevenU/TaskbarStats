using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace TaskbarStats.Ui;

/// <summary>
/// The icons of the settings window. The SVGs are embedded; their path data is parsed once into GDI+ paths
/// and every icon is fitted to the same box by its drawn extent and filled with one colour, so mixed source
/// grids (48, 24, 16, 14.93 units) and fill colours come out uniform.
/// </summary>
internal static class Icons
{
    static readonly Dictionary<string, (GraphicsPath Path, RectangleF Bounds)?> Cache = new();

    public static void Draw(Graphics g, string name, RectangleF box, Color color)
    {
        if (Get(name) is not { } icon) return;
        var b = icon.Bounds;
        float k = Math.Min(box.Width / b.Width, box.Height / b.Height);
        float tx = box.X + (box.Width - b.Width * k) / 2 - b.X * k;
        float ty = box.Y + (box.Height - b.Height * k) / 2 - b.Y * k;

        var state = g.Save();
        g.TranslateTransform(tx, ty);
        g.ScaleTransform(k, k);
        using var br = new SolidBrush(color);
        g.FillPath(br, icon.Path);
        g.Restore(state);
    }

    static (GraphicsPath Path, RectangleF Bounds)? Get(string name)
    {
        if (Cache.TryGetValue(name, out var c)) return c;
        (GraphicsPath Path, RectangleF Bounds)? result = null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"icons.{name}.svg");
            if (s != null)
            {
                string svg = new StreamReader(s).ReadToEnd();
                var gp = new GraphicsPath(FillMode.Winding);
                foreach (Match m in Regex.Matches(svg, "\\sd=\"([^\"]+)\""))
                    Parse(gp, m.Groups[1].Value);
                if (gp.PointCount > 0)
                {
                    var bounds = gp.GetBounds();
                    if (bounds.Width > 0 && bounds.Height > 0) result = (gp, bounds);
                }
            }
        }
        catch { /* a broken icon is simply not drawn */ }
        Cache[name] = result;
        return result;
    }

    static Bitmap? _app;

    /// <summary>The program image (embedded app.png), or null when missing.</summary>
    public static Bitmap? AppImage()
    {
        if (_app != null) return _app;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png");
            if (s != null) { using var src = new Bitmap(s); _app = new Bitmap(src); }
        }
        catch { }
        return _app;
    }

    /// <summary>Draws the program image smoothly into a box.</summary>
    public static void DrawApp(Graphics g, RectangleF box)
    {
        var img = AppImage();
        if (img == null) { Draw(g, "cpu", box, Color.Gray); return; }
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(img, box);
        g.InterpolationMode = old;
    }

    /// <summary>The window icon, made from the program image.</summary>
    public static Icon MakeAppIcon()
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp)) DrawApp(g, new RectangleF(0, 0, 32, 32));
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    // ---------------------------------------------------------- SVG path data

    sealed class Scanner
    {
        readonly string _s;
        public int P;
        public Scanner(string s) => _s = s;

        public bool More()
        {
            while (P < _s.Length && (char.IsWhiteSpace(_s[P]) || _s[P] == ',')) P++;
            return P < _s.Length;
        }

        public char Peek => _s[P];

        public float Num()
        {
            More();
            int st = P;
            if (P < _s.Length && (_s[P] == '-' || _s[P] == '+')) P++;
            while (P < _s.Length && char.IsAsciiDigit(_s[P])) P++;
            if (P < _s.Length && _s[P] == '.')
            {
                P++;
                while (P < _s.Length && char.IsAsciiDigit(_s[P])) P++;
            }
            if (P < _s.Length && (_s[P] == 'e' || _s[P] == 'E'))
            {
                P++;
                if (P < _s.Length && (_s[P] == '-' || _s[P] == '+')) P++;
                while (P < _s.Length && char.IsAsciiDigit(_s[P])) P++;
            }
            return P > st ? float.Parse(_s.AsSpan(st, P - st), NumberStyles.Float, CultureInfo.InvariantCulture) : 0;
        }

        public bool Flag() { More(); return P < _s.Length && _s[P++] == '1'; }
    }

    static void Parse(GraphicsPath gp, string d)
    {
        var sc = new Scanner(d);
        char cmd = 'M';
        PointF cur = default, start = default, lastCtl = default;
        char prev = ' ';

        while (sc.More())
        {
            char c = sc.Peek;
            if (char.IsLetter(c)) { cmd = c; sc.P++; }
            else if (cmd == 'M') cmd = 'L';
            else if (cmd == 'm') cmd = 'l';
            else if (cmd is 'Z' or 'z') { sc.P++; continue; }

            bool rel = char.IsLower(cmd);
            float ox = rel ? cur.X : 0, oy = rel ? cur.Y : 0;
            char u = char.ToUpperInvariant(cmd);

            switch (u)
            {
                case 'M':
                {
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    gp.StartFigure();
                    cur = start = p;
                    break;
                }
                case 'L':
                {
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    gp.AddLine(cur, p);
                    cur = p;
                    break;
                }
                case 'H':
                {
                    var p = new PointF(sc.Num() + ox, cur.Y);
                    gp.AddLine(cur, p);
                    cur = p;
                    break;
                }
                case 'V':
                {
                    var p = new PointF(cur.X, sc.Num() + oy);
                    gp.AddLine(cur, p);
                    cur = p;
                    break;
                }
                case 'C':
                {
                    var c1 = new PointF(sc.Num() + ox, sc.Num() + oy);
                    var c2 = new PointF(sc.Num() + ox, sc.Num() + oy);
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    gp.AddBezier(cur, c1, c2, p);
                    lastCtl = c2; cur = p;
                    break;
                }
                case 'S':
                {
                    var c1 = prev is 'C' or 'S' ? new PointF(2 * cur.X - lastCtl.X, 2 * cur.Y - lastCtl.Y) : cur;
                    var c2 = new PointF(sc.Num() + ox, sc.Num() + oy);
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    gp.AddBezier(cur, c1, c2, p);
                    lastCtl = c2; cur = p;
                    break;
                }
                case 'Q':
                {
                    var q = new PointF(sc.Num() + ox, sc.Num() + oy);
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    Quad(gp, cur, q, p);
                    lastCtl = q; cur = p;
                    break;
                }
                case 'T':
                {
                    var q = prev is 'Q' or 'T' ? new PointF(2 * cur.X - lastCtl.X, 2 * cur.Y - lastCtl.Y) : cur;
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    Quad(gp, cur, q, p);
                    lastCtl = q; cur = p;
                    break;
                }
                case 'A':
                {
                    float rx = sc.Num(), ry = sc.Num(), rot = sc.Num();
                    bool large = sc.Flag(), sweep = sc.Flag();
                    var p = new PointF(sc.Num() + ox, sc.Num() + oy);
                    Arc(gp, cur, rx, ry, rot, large, sweep, p);
                    cur = p;
                    break;
                }
                case 'Z':
                    gp.CloseFigure();
                    cur = start;
                    break;
                default:
                    return;                                    // unknown command: stop rather than loop
            }
            prev = u;
        }
    }

    static void Quad(GraphicsPath gp, PointF p0, PointF q, PointF p1)
    {
        var c1 = new PointF(p0.X + 2f / 3f * (q.X - p0.X), p0.Y + 2f / 3f * (q.Y - p0.Y));
        var c2 = new PointF(p1.X + 2f / 3f * (q.X - p1.X), p1.Y + 2f / 3f * (q.Y - p1.Y));
        gp.AddBezier(p0, c1, c2, p1);
    }

    /// <summary>SVG elliptical arc (endpoint form) as cubic Béziers.</summary>
    static void Arc(GraphicsPath gp, PointF p0, float rxf, float ryf, float rotDeg, bool large, bool sweep, PointF p1)
    {
        double rx = Math.Abs(rxf), ry = Math.Abs(ryf);
        if (p0 == p1) return;
        if (rx == 0 || ry == 0) { gp.AddLine(p0, p1); return; }

        double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;

        double lam = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
        if (lam > 1) { double sq = Math.Sqrt(lam); rx *= sq; ry *= sq; }

        double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        double co = Math.Sqrt(Math.Max(0, num / den));
        if (large == sweep) co = -co;
        double cxp = co * rx * y1 / ry, cyp = -co * ry * x1 / rx;
        double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2.0;
        double cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2.0;

        double th1 = Math.Atan2((y1 - cyp) / ry, (x1 - cxp) / rx);
        double th2 = Math.Atan2((-y1 - cyp) / ry, (-x1 - cxp) / rx);
        double dth = th2 - th1;
        if (sweep && dth < 0) dth += 2 * Math.PI;
        if (!sweep && dth > 0) dth -= 2 * Math.PI;

        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(dth) / (Math.PI / 2) - 1e-9));
        double step = dth / n, t = 4.0 / 3.0 * Math.Tan(step / 4);

        PointF Map(double u, double v) =>
            new((float)(cx + cos * rx * u - sin * ry * v), (float)(cy + sin * rx * u + cos * ry * v));

        PointF prev = p0;
        for (int k = 0; k < n; k++)
        {
            double a1 = th1 + k * step, a2 = a1 + step;
            var c1 = Map(Math.Cos(a1) - t * Math.Sin(a1), Math.Sin(a1) + t * Math.Cos(a1));
            var c2 = Map(Math.Cos(a2) + t * Math.Sin(a2), Math.Sin(a2) - t * Math.Cos(a2));
            var e = k == n - 1 ? p1 : Map(Math.Cos(a2), Math.Sin(a2));
            gp.AddBezier(prev, c1, c2, e);
            prev = e;
        }
    }
}
