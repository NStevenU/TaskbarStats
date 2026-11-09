using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using TaskbarStats.Sensors;

namespace TaskbarStats.Ui;

/// <summary>One line of a "top processes" list (all processes of one program added together).</summary>
internal sealed class ProcRow
{
    public string Name = "";
    public string? Path;
    public double Cpu;                 // % of the whole CPU
    public long Mem;                   // bytes (working set)
    public double Read, Write;         // bytes per second
    public double Gpu;                 // % of the busiest engine
}

/// <summary>
/// Per-process numbers, sampled only while a popup is open. One pass opens each process once for its CPU
/// time, I/O counters and image path; GPU engine load comes from the Windows "GPU Engine" counters.
/// </summary>
internal sealed class ProcSampler
{
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr h, out IoCounters io);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder sb, ref int size);

    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    sealed class Prev { public long Cpu; public ulong Read, Write; }

    readonly Dictionary<int, Prev> _prev = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _lastMs = -1;
    ManagementObjectSearcher? _gpuQuery;
    bool _gpuFailed;

    public volatile ProcRow[] Rows = Array.Empty<ProcRow>();
    public bool WantGpu;
    public volatile bool Busy;

    public void Sample()
    {
        if (Busy) return;
        Busy = true;
        try { Run(); } catch { /* keep the previous rows */ }
        Busy = false;
    }

    void Run()
    {
        long now = _clock.ElapsedMilliseconds;
        double dt = _lastMs < 0 ? 0 : (now - _lastMs) / 1000.0;
        _lastMs = now;
        int logical = Math.Max(1, Environment.ProcessorCount);

        var gpuByPid = WantGpu ? ReadGpu() : null;
        var seen = new HashSet<int>();
        var byName = new Dictionary<string, ProcRow>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(1024);

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid = p.Id;
                if (pid == 0) continue;
                string name = p.ProcessName;
                long ws = 0;
                try { ws = p.WorkingSet64; } catch { }

                double cpu = 0, rd = 0, wr = 0;
                string? path = null;
                IntPtr h = OpenProcess(0x1400, false, pid);               // QUERY_INFORMATION | QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero) h = OpenProcess(0x1000, false, pid);
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        bool isNew = !_prev.TryGetValue(pid, out var pv);
                        if (isNew) _prev[pid] = pv = new Prev();
                        if (GetProcessTimes(h, out _, out _, out long k, out long u))
                        {
                            long total = k + u;
                            if (!isNew && dt > 0) cpu = (total - pv!.Cpu) / 1e7 / dt / logical * 100.0;
                            pv!.Cpu = total;
                        }
                        if (GetProcessIoCounters(h, out var io))
                        {
                            if (!isNew && dt > 0)
                            {
                                rd = Math.Max(0, (double)(io.ReadBytes - pv!.Read) / dt);
                                wr = Math.Max(0, (double)(io.WriteBytes - pv.Write) / dt);
                            }
                            pv!.Read = io.ReadBytes;
                            pv.Write = io.WriteBytes;
                        }
                        sb.Clear();
                        int size = sb.Capacity;
                        if (QueryFullProcessImageNameW(h, 0, sb, ref size)) path = sb.ToString();
                    }
                    finally { CloseHandle(h); }
                }
                seen.Add(pid);

                if (!byName.TryGetValue(name, out var row)) byName[name] = row = new ProcRow { Name = name, Path = path };
                row.Path ??= path;
                row.Cpu += Math.Max(0, cpu);
                row.Mem += ws;
                row.Read += rd;
                row.Write += wr;
                if (gpuByPid != null && gpuByPid.TryGetValue(pid, out double gv)) row.Gpu += gv;
            }
        }

        foreach (int dead in _prev.Keys.Where(k => !seen.Contains(k)).ToList()) _prev.Remove(dead);
        Rows = byName.Values.ToArray();
    }

    static readonly Regex PidRx = new(@"pid_(\d+)_.*engtype_(\w+)", RegexOptions.Compiled);

    Dictionary<int, double>? ReadGpu()
    {
        if (_gpuFailed) return null;
        try
        {
            _gpuQuery ??= new ManagementObjectSearcher(
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine WHERE UtilizationPercentage > 0");
            var perEngine = new Dictionary<(int, string), double>();
            foreach (ManagementBaseObject o in _gpuQuery.Get())
            {
                var m = PidRx.Match(o["Name"]?.ToString() ?? "");
                if (!m.Success) continue;
                var key = (int.Parse(m.Groups[1].Value), m.Groups[2].Value);
                perEngine[key] = perEngine.GetValueOrDefault(key) + Convert.ToDouble(o["UtilizationPercentage"]);
            }
            var res = new Dictionary<int, double>();
            foreach (var ((pid, _), v) in perEngine) res[pid] = Math.Max(res.GetValueOrDefault(pid), v);   // like Task Manager: busiest engine
            return res;
        }
        catch { _gpuFailed = true; return null; }
    }
}

/// <summary>Process icons, cached by path (a few KB each).</summary>
internal static class ProcIcons
{
    static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var b)) return b;
            Bitmap? bmp = null;
            try
            {
                using var ic = Icon.ExtractAssociatedIcon(path);
                if (ic != null)
                {
                    using var full = ic.ToBitmap();
                    bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                    using var g = Graphics.FromImage(bmp);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, 32, 32);
                }
            }
            catch { bmp = null; }
            Cache[path] = bmp;
            return bmp;
        }
    }

    public static void Clear()
    {
        lock (Cache)
        {
            foreach (var b in Cache.Values) b?.Dispose();
            Cache.Clear();
        }
    }
}

/// <summary>One sensor as LibreHardwareMonitor reports it (live while a popup is open).</summary>
internal sealed record LiveSensor(string Id, string Name, string Hardware, string HwType, string Kind, double Value);

/// <summary>A private LibreHardwareMonitor instance that lives only while a popup needs it.</summary>
internal sealed class LiveLhm : IDisposable
{
    readonly bool _cpu, _mobo, _gpu, _mem, _storage;
    Computer? _pc;
    bool _opening, _busy, _dead;

    public volatile IReadOnlyList<LiveSensor> Sensors = Array.Empty<LiveSensor>();
    public volatile bool Ready;

    public LiveLhm(bool cpu = false, bool mobo = false, bool gpu = false, bool mem = false, bool storage = false)
    { _cpu = cpu; _mobo = mobo; _gpu = gpu; _mem = mem; _storage = storage; }

    public void Update()
    {
        if (_busy || _dead) return;
        _busy = true;
        try
        {
            if (_pc == null && !_opening)
            {
                _opening = true;
                var pc = new Computer { IsCpuEnabled = _cpu, IsMotherboardEnabled = _mobo, IsGpuEnabled = _gpu, IsMemoryEnabled = _mem, IsStorageEnabled = _storage };
                pc.Open();
                if (_dead) { pc.Close(); return; }
                _pc = pc;
            }
            if (_pc == null) return;

            var list = new List<LiveSensor>();
            void Walk(IHardware h, string top, string type)
            {
                h.Update();
                foreach (var s in h.Sensors)
                    if (s.Value is { } v && !float.IsNaN(v))
                    {
                        string id = s.Identifier.ToString();
                        list.Add(new LiveSensor(id, s.Name, top, type, CustomSensors.KindOf(id), v));
                    }
                foreach (var sub in h.SubHardware) Walk(sub, top, type);
            }
            foreach (var h in _pc.Hardware) Walk(h, h.Name, h.HardwareType.ToString());
            Sensors = list;
            Ready = true;
        }
        catch { Ready = true; }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        _dead = true;
        // closing waits for a running update to finish on its own thread
        Task.Run(() =>
        {
            for (int i = 0; i < 50 && _busy; i++) Thread.Sleep(100);
            try { _pc?.Close(); } catch { }
            _pc = null;
        });
    }
}

/// <summary>Plain Windows queries the popups share: memory, frequency, drives, adapters.</summary>
internal static class SysInfo
{
    [StructLayout(LayoutKind.Sequential)]
    struct PerfInfo
    {
        public uint Cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(out PerfInfo info, uint size);

    public sealed record Mem(double TotalGb, double UsedGb, double FreeGb, double CacheGb, double CommitGb, double CommitLimitGb);

    public static Mem ReadMemory()
    {
        if (!GetPerformanceInfo(out var p, (uint)Marshal.SizeOf<PerfInfo>())) return new Mem(0, 0, 0, 0, 0, 0);
        double ps = (double)p.PageSize / (1024.0 * 1024 * 1024);
        double total = (double)p.PhysicalTotal * ps, avail = (double)p.PhysicalAvailable * ps;
        return new Mem(total, total - avail, avail, (double)p.SystemCache * ps, (double)p.CommitTotal * ps, (double)p.CommitLimit * ps);
    }

    /// <summary>Page file in use / allocated, in GB (one WMI query, call from a worker).</summary>
    public static (double used, double size)? ReadSwap()
    {
        try
        {
            double used = 0, size = 0;
            using var q = new ManagementObjectSearcher("SELECT CurrentUsage, AllocatedBaseSize FROM Win32_PageFileUsage");
            foreach (ManagementBaseObject o in q.Get())
            {
                used += Convert.ToDouble(o["CurrentUsage"]) / 1024.0;
                size += Convert.ToDouble(o["AllocatedBaseSize"]) / 1024.0;
            }
            return size > 0 ? (used, size) : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ frequency

    public sealed record Freq(double All, double? Efficiency, double? Performance);

    static ManagementObjectSearcher? _freqQuery;

    /// <summary>Current clock per core group from the "Processor Information" counters (no admin rights needed).</summary>
    public static Freq? ReadFrequency()
    {
        try
        {
            _freqQuery ??= new ManagementObjectSearcher(
                "SELECT Name, ProcessorFrequency, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation");
            var perLogical = new double[CpuTopology.Logical];
            var have = new bool[CpuTopology.Logical];
            foreach (ManagementBaseObject o in _freqQuery.Get())
            {
                string name = o["Name"]?.ToString() ?? "";
                int c = name.IndexOf(',');
                if (c < 0 || name.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(name[(c + 1)..], out int idx) || idx < 0 || idx >= perLogical.Length) continue;
                double nominal = Convert.ToDouble(o["ProcessorFrequency"]), perf = Convert.ToDouble(o["PercentProcessorPerformance"]);
                perLogical[idx] = nominal * perf / 100.0;
                have[idx] = true;
            }

            var all = new List<double>(); var e = new List<double>(); var pCores = new List<double>();
            for (int core = 0; core < CpuTopology.CoreThreads.Length; core++)
            {
                var th = CpuTopology.CoreThreads[core].Where(t => t < have.Length && have[t]).Select(t => perLogical[t]).ToList();
                if (th.Count == 0) continue;
                double f = th.Average();
                all.Add(f);
                (CpuTopology.CoreIsEfficiency[core] ? e : pCores).Add(f);
            }
            if (all.Count == 0) return null;
            bool hybrid = e.Count > 0 && pCores.Count > 0;
            return new Freq(all.Average(), hybrid ? e.Average() : null, hybrid ? pCores.Average() : null);
        }
        catch (Exception ex) { try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "ts_error.log"), "freq: " + ex + Environment.NewLine); } catch { } return null; }
    }

    // ------------------------------------------------------------------ drives / adapters

    public sealed record Drive(string Name, string Label, double TotalGb, double FreeGb);

    public static List<Drive> ReadDrives()
    {
        var list = new List<Drive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new Drive(d.Name.TrimEnd('\\'), d.VolumeLabel, d.TotalSize / 1073741824.0, d.AvailableFreeSpace / 1073741824.0));
            }
            catch { }
        }
        return list;
    }

    public sealed record Adapter(string Name, string Kind, string? Ip, long SpeedMbps, long Rx, long Tx);

    /// <summary>Connected adapters with their local (internal) IPv4 address and byte counters.</summary>
    public static List<Adapter> ReadAdapters()
    {
        var list = new List<Adapter>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string desc = n.Description;
                string all = n.Name + " " + desc;
                if (new[] { "WFP", "QoS", "LightWeight", "Filter", "Scheduler", "Npcap", "Miniport" }.Any(k => all.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                    || desc.Contains("VMware", StringComparison.OrdinalIgnoreCase) || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) continue;

                string? ip = n.GetIPProperties().UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
                var st = n.GetIPv4Statistics();
                string kind = n.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    _ => n.NetworkInterfaceType.ToString(),
                };
                list.Add(new Adapter(n.Name, kind, ip, n.Speed / 1_000_000, st.BytesReceived, st.BytesSent));
            }
        }
        catch { }
        return list;
    }

    public static string Uptime()
    {
        var t = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : $"{t.Hours}h {t.Minutes}m";
    }
}
