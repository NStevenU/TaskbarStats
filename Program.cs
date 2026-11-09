using System.Drawing;
using System.Drawing.Imaging;
using TaskbarStats.Sensors;
using TaskbarStats.Ui;

namespace TaskbarStats;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);     // before any window exists; replaces the manifest entry

        int di = Array.IndexOf(args, "--dump");     // diagnostic: what LibreHardwareMonitor sees (needs admin)
        if (di >= 0 && di + 1 < args.Length) return Dump(args[di + 1]);

        int ai = Array.IndexOf(args, "--autostart");   // used by the installer: --autostart on | off
        if (ai >= 0 && ai + 1 < args.Length) return Ui.Autostart.Set(args[ai + 1] == "on") ? 0 : 1;

        int ci = Array.IndexOf(args, "--config");
        var cfg = AppConfig.Load(ci >= 0 && ci + 1 < args.Length ? args[ci + 1] : null);
        Fmt.Configure(cfg);

        // "Run as administrator" setting: relaunch elevated (no prompt when the startup task exists)
        if (ci < 0 && cfg.RunAsAdmin && !args.Contains("--snapshot") && !Elevation.IsAdmin() && Elevation.Relaunch(args)) return 0;

        using var hub = new SensorHub();
        hub.Add(new CpuMemProvider());
        var nvidia = new NvmlProvider();
        hub.Add(nvidia);
        hub.Add(new LhmGpuProvider(nvidia));
        hub.Add(new DiskProvider());
        hub.Add(new NetProvider());
        hub.Add(new LhmCpuProvider());
        hub.Add(new LhmCustomProvider());
        var smc = new SmcProvider();
        hub.Add(smc);                        // last: it only fills what the others could not
        // First time Apple's SMC driver is seen (Boot Camp Mac): switch the component on, once. Turning it off later sticks.
        if (ci < 0 && !cfg.SmcAutoChecked && smc.Available == true)
        {
            cfg.DisabledComponents.RemoveAll(c => c == "smc");
            cfg.SmcAutoChecked = true;
            cfg.Save();
        }
        hub.SetDisabled(cfg.DisabledComponents);

        var mods = BuildModules(cfg);

        // Headless render to a PNG, used to look at the widget without touching the taskbar.
        int snap = Array.IndexOf(args, "--snapshot");
        if (snap >= 0 && snap + 1 < args.Length)
            return Snapshot(args[snap + 1], args.Contains("--light"), ParseScale(args), cfg, hub, mods);

        Application.EnableVisualStyles();
        using var single = new Mutex(true, ci >= 0 ? @"Local\TaskbarStats.SingleInstance.Test" : @"Local\TaskbarStats.SingleInstance", out bool first);
        if (!first) return 0;

        using var window = new WidgetWindow(cfg, hub, mods);
        window.Create();
        int oi = Array.IndexOf(args, "--open");      // debugging aid: open settings/popup at start
        if (oi >= 0 && oi + 1 < args.Length) window.Open(args[oi + 1]);
        return window.RunMessageLoop();
    }

    /// <summary>All enabled modules by id; the layout decides which of them are shown and where.</summary>
    internal static Dictionary<string, IWidgetModule> BuildModules(AppConfig cfg)
    {
        var dict = new Dictionary<string, IWidgetModule>(StringComparer.Ordinal);
        foreach (var m in cfg.Modules)
        {
            if (!m.Enabled || string.IsNullOrWhiteSpace(m.Id) || dict.ContainsKey(m.Id)) continue;
            var module = ModuleFactory.Create(m);
            if (module != null) dict[m.Id] = module;
        }
        return dict;
    }

    static int Dump(string file)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            sb.AppendLine("admin=" + new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator));
            var pc = new LibreHardwareMonitor.Hardware.Computer
            { IsCpuEnabled = true, IsMotherboardEnabled = true, IsGpuEnabled = true };
            pc.Open();
            void Walk(LibreHardwareMonitor.Hardware.IHardware h, string ind)
            {
                h.Update();
                sb.AppendLine($"{ind}[{h.HardwareType}] {h.Name}");
                foreach (var s in h.Sensors) sb.AppendLine($"{ind}  {s.Identifier} {s.Name} = {s.Value}");
                foreach (var sh in h.SubHardware) Walk(sh, ind + "  ");
            }
            foreach (var h in pc.Hardware) Walk(h, "");
            pc.Close();
        }
        catch (Exception e) { sb.AppendLine(e.ToString()); }
        File.WriteAllText(file, sb.ToString());
        return 0;
    }

    static float ParseScale(string[] args)
    {
        int i = Array.IndexOf(args, "--scale");
        return i >= 0 && i + 1 < args.Length &&
               float.TryParse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 1.5f;
    }

    static int Snapshot(string path, bool light, float scale, AppConfig cfg, SensorHub hub,
                        IReadOnlyDictionary<string, IWidgetModule> mods)
    {
        using var theme = new Theme(light);
        var layout = WidgetLayout.Build(cfg, mods, theme);
        foreach (var m in layout.Modules) hub.Subscribe(m.Needs);

        hub.Update();
        Thread.Sleep(700);            // CPU usage needs two samples
        hub.Update();
        foreach (var m in layout.Modules) m.Prepare(hub.Current);

        int w = (int)Math.Ceiling(layout.TotalWidth(theme) * scale);
        int h = (int)Math.Ceiling(layout.Height * scale);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
            WidgetLayout.Configure(g, scale);
            layout.Draw(g, theme);
        }
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine($"{path}  {w}x{h}px  scale {scale}");
        return 0;
    }
}
