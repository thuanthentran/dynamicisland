using System.Runtime;

namespace DynamicIsland.Platform;

/// <summary>
/// Returns idle memory to the OS while the island rests small. Pages come back on demand, so this only costs a few page
/// faults the next time the large island opens.
/// </summary>
internal static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
    }
}
