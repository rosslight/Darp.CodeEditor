using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Darp.CodeEditor.Probe;

/// <summary>
/// Memory of a process and all its descendants. Linux: PSS (shared pages split between the processes that map them,
/// so the sum does not double count) and RSS. Windows: private bytes and working set.
/// </summary>
internal static partial class ProcessTreeMemory
{
    public static void Report(int rootPid, Action<string> report)
    {
        Dictionary<int, int> parents = OperatingSystem.IsWindows() ? WindowsParents() : LinuxParents();
        var tree = new List<int> { rootPid };
        for (int i = 0; i < tree.Count; i++)
            tree.AddRange(parents.Where(p => p.Value == tree[i]).Select(p => p.Key));

        long totalPrimary = 0;
        long totalSecondary = 0;
        string primary = OperatingSystem.IsWindows() ? "private" : "PSS";
        string secondary = OperatingSystem.IsWindows() ? "working set" : "RSS";
        foreach (int pid in tree)
        {
            (string name, long a, long b) = OperatingSystem.IsWindows() ? WindowsMemory(pid) : LinuxMemory(pid);
            totalPrimary += a;
            totalSecondary += b;
            report($"  {pid, 8} {name, -28} {primary} {a / 1024.0, 8:F1} MB   {secondary} {b / 1024.0, 8:F1} MB");
        }
        report(
            $"total over {tree.Count} processes: {primary} {totalPrimary / 1024.0:F1} MB, {secondary} {totalSecondary / 1024.0:F1} MB"
        );
    }

    private static Dictionary<int, int> LinuxParents()
    {
        var parents = new Dictionary<int, int>();
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int pid))
                continue;
            try
            {
                // Fields after the command name, which is in parentheses and may contain spaces: state ppid ...
                string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                parents[pid] = int.Parse(fields[1]);
            }
            catch (IOException) { }
        }
        return parents;
    }

    /// <returns>Name, PSS and RSS in kB.</returns>
    private static (string Name, long Pss, long Rss) LinuxMemory(int pid)
    {
        try
        {
            string name = File.ReadAllText($"/proc/{pid}/comm").Trim();
            long pss = 0;
            long rss = 0;
            foreach (string line in File.ReadLines($"/proc/{pid}/smaps_rollup"))
            {
                if (line.StartsWith("Pss:", StringComparison.Ordinal))
                    pss = ParseKb(line);
                else if (line.StartsWith("Rss:", StringComparison.Ordinal))
                    rss = ParseKb(line);
            }
            return (name, pss, rss);
        }
        catch (IOException)
        {
            return ("(exited)", 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return ("(no access)", 0, 0);
        }

        static long ParseKb(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    [SupportedOSPlatform("windows")]
    private static (string Name, long Private, long WorkingSet) WindowsMemory(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return (process.ProcessName, process.PrivateMemorySize64 / 1024, process.WorkingSet64 / 1024);
        }
        catch (ArgumentException)
        {
            return ("(exited)", 0, 0);
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe Dictionary<int, int> WindowsParents()
    {
        var parents = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
