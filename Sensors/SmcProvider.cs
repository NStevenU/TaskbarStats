using System.Runtime.InteropServices;

namespace TaskbarStats.Sensors;

/// <summary>
/// Apple SMC of a Mac running Windows (Boot Camp), read through Apple's own MacHALDriver. The device is opened
/// for reading only and nothing is ever written. It fills CPU temperature, GPU temperature and fan speed only
/// where no other provider has produced a value. On a PC without the driver it stays silent.
/// </summary>
public sealed unsafe class SmcProvider : SensorProvider
{
    static readonly SensorKey[] Keys = { SensorKey.CpuTemp, SensorKey.GpuTemp, SensorKey.FanRpm };

    // IOCTL codes of MacHALDriver: key info, key value, key name by index (we only need the first two)
    const uint ReadInfo = 0x9C402460, ReadValue = 0x9C402454;

    static readonly string[] CpuTempKeys = { "TC0P", "TC0D", "TC0E", "TC0F", "TCXC" };
    static readonly string[] GpuTempKeys = { "TG0P", "TG0D", "TGDD", "TG0H" };

    IntPtr _h = new(-1);
    long _retryAt;
    long _probeAt;
    bool _probe;
    string? _cpuKey, _gpuKey;
    int _fans = -1;

    public override string Id => "smc";

    public override IReadOnlyList<SensorKey> Provides => Keys;

    /// <summary>Is the Apple driver present? Probed at most every few seconds.</summary>
    public override bool? Available
    {
        get
        {
            long now = Environment.TickCount64;
            if (now < _probeAt) return _probe;
            _probeAt = now + 5000;
            IntPtr h = CreateFileW(@"\\.\MacHALDriver", 0x80000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            _probe = h != new IntPtr(-1);
            if (_probe) CloseHandle(h);
            return _probe;
        }
    }

    public override void Update(Snapshot s, bool[] wanted)
    {
        if (_h == new IntPtr(-1))
        {
            if (Environment.TickCount64 < _retryAt) return;
            _h = CreateFileW(@"\\.\MacHALDriver", 0x80000000 /* GENERIC_READ */, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_h == new IntPtr(-1)) { _retryAt = Environment.TickCount64 + 60_000; return; }
            _cpuKey = _gpuKey = null;
            _fans = -1;
        }

        if (wanted[(int)SensorKey.CpuTemp] && double.IsNaN(s[SensorKey.CpuTemp]))
        {
            _cpuKey ??= FirstReadable(CpuTempKeys);
            if (_cpuKey != null && Read(_cpuKey) is { } t) s[SensorKey.CpuTemp] = t;
        }
        if (wanted[(int)SensorKey.GpuTemp] && double.IsNaN(s[SensorKey.GpuTemp]))
        {
            _gpuKey ??= FirstReadable(GpuTempKeys);
            if (_gpuKey != null && Read(_gpuKey) is { } t) s[SensorKey.GpuTemp] = t;
        }
        if (wanted[(int)SensorKey.FanRpm] && double.IsNaN(s[SensorKey.FanRpm]))
        {
            if (_fans < 0) _fans = Read("FNum") is { } n ? (int)n : 0;
            double best = double.NaN;
            for (int i = 0; i < _fans && i < 4; i++)
                if (Read($"F{i}Ac") is { } v && v > 0 && (double.IsNaN(best) || v > best)) best = v;
            if (!double.IsNaN(best)) s[SensorKey.FanRpm] = best;
        }
    }

    string? FirstReadable(string[] keys)
    {
        foreach (var k in keys)
            if (Read(k) is { } v && v > 0 && v < 150) return k;
        return null;
    }

    /// <summary>One key as a number, or null when it does not exist or has a type we do not read.</summary>
    double? Read(string key)
    {
        byte* name = stackalloc byte[5];
        for (int i = 0; i < 4; i++) name[i] = (byte)key[i];
        name[4] = 0;

        byte* info = stackalloc byte[12];
        if (!DeviceIoControl(_h, ReadInfo, name, 5, info, 12, out int got, IntPtr.Zero) || got != 12) return null;
        string type = new string(new[] { (char)info[4], (char)info[5], (char)info[6], (char)info[7] });

        byte* buf = stackalloc byte[32];
        new Span<byte>(buf, 32).Clear();
        if (!DeviceIoControl(_h, ReadValue, name, 5, buf, 32, out got, IntPtr.Zero)) return null;

        switch (type)
        {
            case "sp78": return (short)((buf[0] << 8) | buf[1]) / 256.0;          // signed 7.8 fixed point, big endian
            case "fpe2": return ((buf[0] << 8) | buf[1]) / 4.0;                    // unsigned 14.2 fixed point, big endian
            case "flt ": return *(float*)buf;                                      // little endian float
            case "ui8 ": return buf[0];
            case "ui16": return (buf[0] << 8) | buf[1];
            case "ui32": return (uint)((buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3]);
            default: return null;
        }
    }

    public override void Idle() => Close();

    void Close()
    {
        if (_h != new IntPtr(-1)) { CloseHandle(_h); _h = new IntPtr(-1); }
    }

    public override void Dispose() => Close();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, void* inBuf, int inLen, void* outBuf, int outLen,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
