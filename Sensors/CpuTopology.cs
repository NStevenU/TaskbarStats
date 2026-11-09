using System.Runtime.InteropServices;

namespace TaskbarStats.Sensors;

/// <summary>
/// Logical-processor to physical-core mapping, so the CPU graph can hide Hyper-Threading siblings
/// (one bar per core) and tell performance cores from efficiency cores on hybrid CPUs.
/// </summary>
public static unsafe class CpuTopology
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, byte* buffer, ref uint length);

    const int RelationProcessorCore = 0;

    public static readonly int Logical = Math.Min(Environment.ProcessorCount, 64);

    /// <summary>For each physical core, the logical processors that belong to it.</summary>
    public static readonly int[][] CoreThreads;

    /// <summary>For each physical core: true on the efficiency cores of a hybrid CPU.</summary>
    public static readonly bool[] CoreIsEfficiency;

    public static int Physical => CoreThreads.Length;

    static CpuTopology()
    {
        var threads = new List<int[]>();
        var eff = new List<byte>();

        try
        {
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, null, ref len);
            if (len > 0)
            {
                var buf = new byte[len];
                fixed (byte* p = buf)
                {
                    if (GetLogicalProcessorInformationEx(RelationProcessorCore, p, ref len))
                    {
                        for (uint off = 0; off + 8 <= len;)
                        {
                            int relationship = *(int*)(p + off);
                            uint size = *(uint*)(p + off + 4);
                            if (size == 0) break;

                            if (relationship == RelationProcessorCore)
                            {
                                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: 8-byte header, then PROCESSOR_RELATIONSHIP
                                // { Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GROUP_AFFINITY[] }
                                byte efficiencyClass = p[off + 9];
                                ulong mask = *(ulong*)(p + off + 32);   // first group only (<= 64 logical processors)
                                var list = new List<int>();
                                for (int bit = 0; bit < 64; bit++)
                                    if ((mask >> bit & 1) != 0 && bit < Logical) list.Add(bit);
                                if (list.Count > 0)
                                {
                                    threads.Add(list.ToArray());
                                    eff.Add(efficiencyClass);
                                }
                            }
                            off += size;
                        }
                    }
                }
            }
        }
        catch { /* fall through to the flat mapping below */ }

        if (threads.Count == 0)
        {
            for (int i = 0; i < Logical; i++) { threads.Add(new[] { i }); eff.Add(0); }
        }

        CoreThreads = threads.ToArray();

        // On a hybrid CPU efficiency cores report a lower EfficiencyClass than performance cores.
        byte max = eff.Max();
        byte min = eff.Min();
        CoreIsEfficiency = new bool[eff.Count];
        if (max != min)
            for (int i = 0; i < eff.Count; i++) CoreIsEfficiency[i] = eff[i] < max;
    }
}
