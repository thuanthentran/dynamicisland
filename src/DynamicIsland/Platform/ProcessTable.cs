using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicIsland.Platform;

internal readonly record struct ProcessEntry(uint Id, uint ParentId, string Name);

/// <summary>Toolhelp snapshot of running processes: id, parent id and executable name without extension.</summary>
internal static class ProcessTable
{
    private const uint SnapshotProcesses = 0x00000002;

    public static Dictionary<uint, ProcessEntry> Snapshot()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var result = new Dictionary<uint, ProcessEntry>();
        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        if (!Process32First(snapshot, ref entry))
            return result;
        do
        {
            result[entry.th32ProcessID] = new ProcessEntry(
                entry.th32ProcessID, entry.th32ParentProcessID, Path.GetFileNameWithoutExtension(entry.szExeFile));
        } while (Process32Next(snapshot, ref entry));
        return result;
    }

    /// <summary>Walks up through parents with the same executable name, e.g. from a browser's audio process to its root.</summary>
    public static uint RootOfSameName(uint processId, IReadOnlyDictionary<uint, ProcessEntry> processes)
    {
        var visited = new HashSet<uint>();
        while (visited.Add(processId) &&
               processes.TryGetValue(processId, out var process) &&
               processes.TryGetValue(process.ParentId, out var parent) &&
               string.Equals(parent.Name, process.Name, StringComparison.OrdinalIgnoreCase))
            processId = parent.Id;
        return processId;
    }

#pragma warning disable IDE1006 // Win32 field names
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
#pragma warning restore IDE1006

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref PROCESSENTRY32 entry);
}
