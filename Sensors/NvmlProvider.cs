using System.Runtime.InteropServices;

namespace TaskbarStats.Sensors;

/// <summary>
/// NVIDIA GPU through NVML (nvml.dll ships with the display driver). No extra driver,
/// no admin rights. Initialised lazily and shut down again when nothing is subscribed.
/// </summary>
public sealed unsafe class NvmlProvider : SensorProvider
{
    static readonly SensorKey[] Keys = { SensorKey.GpuLoad, SensorKey.GpuTemp, SensorKey.GpuMemUsedMb, SensorKey.GpuFanPct };

    [StructLayout(LayoutKind.Sequential)] struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] struct MemoryInfo { public ulong Total, Free, Used; }

    IntPtr _lib, _dev;
    bool _initialised, _failed;
    long _retryAt;

    delegate* unmanaged[Cdecl]<int> _init, _shutdown;
    delegate* unmanaged[Cdecl]<uint, IntPtr*, int> _getHandle;
    delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getTemp;
    delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _getUtil;
    delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int> _getMem;
    delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getFan;

    public override string Id => "nvidia";
    public override IReadOnlyList<SensorKey> Provides => Keys;

    /// <summary>An NVIDIA GPU was found and is being read.</summary>
    public bool Active => _initialised;

    public override bool? Available
    {
        get
        {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(sys, "nvml.dll")) ||
                   File.Exists(Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll"));
        }
    }

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (!_initialised && !TryInit()) return;

        if (wanted[(int)SensorKey.GpuTemp])
        {
            uint t;
            if (_getTemp(_dev, 0 /* NVML_TEMPERATURE_GPU */, &t) == 0) s[SensorKey.GpuTemp] = t;
        }
        if (wanted[(int)SensorKey.GpuLoad])
        {
            Utilization u;
            if (_getUtil(_dev, &u) == 0) s[SensorKey.GpuLoad] = u.Gpu;
        }
        if (wanted[(int)SensorKey.GpuFanPct] && _getFan != null)
        {
            uint f;
            s[SensorKey.GpuFanPct] = _getFan(_dev, &f) == 0 ? f : double.NaN;
        }
        if (wanted[(int)SensorKey.GpuMemUsedMb])
        {
            MemoryInfo m;
            if (_getMem(_dev, &m) == 0) s[SensorKey.GpuMemUsedMb] = m.Used / (1024.0 * 1024.0);
        }
    }

    public override void Idle()
    {
        if (_initialised) Shutdown();
    }

    bool TryInit()
    {
        if (_failed && Environment.TickCount64 < _retryAt) return false;

        try
        {
            if (_lib == IntPtr.Zero)
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string[] candidates =
                {
                    Path.Combine(sys, "nvml.dll"),
                    Path.Combine(pf, "NVIDIA Corporation", "NVSMI", "nvml.dll"),
                };
                foreach (var c in candidates)
                    if (File.Exists(c) && NativeLibrary.TryLoad(c, out _lib)) break;
                if (_lib == IntPtr.Zero) throw new DllNotFoundException("nvml.dll");

                _init = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_lib, "nvmlInit_v2");
                _shutdown = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(_lib, "nvmlShutdown");
                _getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)NativeLibrary.GetExport(_lib, "nvmlDeviceGetHandleByIndex_v2");
                _getTemp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)NativeLibrary.GetExport(_lib, "nvmlDeviceGetTemperature");
                _getUtil = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)NativeLibrary.GetExport(_lib, "nvmlDeviceGetUtilizationRates");
                _getMem = (delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int>)NativeLibrary.GetExport(_lib, "nvmlDeviceGetMemoryInfo");
                if (NativeLibrary.TryGetExport(_lib, "nvmlDeviceGetFanSpeed", out IntPtr fan))
                    _getFan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)fan;
            }

            if (_init() != 0) throw new InvalidOperationException("nvmlInit failed");
            IntPtr dev;
            if (_getHandle(0, &dev) != 0) { _shutdown(); throw new InvalidOperationException("no NVIDIA device"); }

            _dev = dev;
            _initialised = true;
            _failed = false;
            return true;
        }
        catch
        {
            _failed = true;
            _retryAt = Environment.TickCount64 + 30_000; // don't hammer a missing driver
            return false;
        }
    }

    void Shutdown()
    {
        try { _shutdown(); } catch { }
        _initialised = false;
        _dev = IntPtr.Zero;
    }

    public override void Dispose()
    {
        if (_initialised) Shutdown();
        if (_lib != IntPtr.Zero) { NativeLibrary.Free(_lib); _lib = IntPtr.Zero; }
    }
}
