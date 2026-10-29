using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>Uma linha da tabela de processos: todos os processos de mesmo nome agrupados (como "Aplicativos" do Gerenciador de Tarefas).</summary>
public sealed class ProcRow : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Key { get; init; } = "";
    public bool Locked { get; set; }      // nunca pode ser finalizado
    public bool Critical { get; set; }    // do sistema: pede confirmação reforçada
    public List<int> Pids { get; set; } = new();

    private string _title = "";
    private int _count;
    private double _cpu;
    private long _mem, _disk;
    private string _state = "";
    private string _priority = "";

    public string Title { get => _title; set => Set(ref _title, value, nameof(Title)); }
    public int Count { get => _count; set { if (Set(ref _count, value, nameof(Count))) Raise(nameof(CountText)); } }
    public double Cpu { get => _cpu; set { if (Set(ref _cpu, value, nameof(Cpu))) Raise(nameof(CpuText)); } }
    public long MemoryBytes { get => _mem; set { if (Set(ref _mem, value, nameof(MemoryBytes))) Raise(nameof(MemoryText)); } }
    public long DiskBps { get => _disk; set { if (Set(ref _disk, value, nameof(DiskBps))) Raise(nameof(DiskText)); } }
    public string State { get => _state; set => Set(ref _state, value, nameof(State)); }
    public string Priority { get => _priority; set => Set(ref _priority, value, nameof(Priority)); }

    public string CountText => Count > 1 ? $"{Count}" : "";
    public string CpuText => Cpu < 0.05 ? "0 %" : $"{Cpu:N1} %";
    public string MemoryText => MemoryBytes >= 1_073_741_824 ? $"{MemoryBytes / 1073741824.0:N2} GB" : $"{MemoryBytes / 1048576.0:N0} MB";
    public string DiskText => DiskBps < 1024 ? "0 MB/s" : DiskBps < 10_485_760 ? $"{DiskBps / 1048576.0:N2} MB/s" : $"{DiskBps / 1048576.0:N1} MB/s";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Raise(name); return true;
    }
}

public sealed record KillResult(int Killed, List<string> Failed);

/// <summary>Lista processos com CPU/memória/disco ao vivo e age sobre eles: finalizar (com árvore), modo de eficiência, prioridade, suspender.</summary>
public sealed class ProcessManager
{
    /// <summary>Encerrar qualquer um destes derruba o Windows (tela azul) ou a sessão: bloqueados sempre.</summary>
    private static readonly HashSet<string> Locked = new(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "System", "Registry", "Memory Compression", "MemCompression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "ResetExplode",
    };

    /// <summary>Do sistema/segurança: dá para finalizar, mas só com confirmação reforçada. Fora da lista padrão.</summary>
    private static readonly HashSet<string> SystemLike = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "RuntimeBroker", "StartMenuExperienceHost",
        "ShellExperienceHost", "SearchHost", "SearchIndexer", "TextInputHost", "SecurityHealthService", "SecurityHealthSystray",
        "MsMpEng", "NisSrv", "SgrmBroker", "audiodg", "conhost", "dllhost", "spoolsv", "WmiPrvSE", "LockApp", "ApplicationFrameHost",
        "SystemSettings", "smartscreen", "WUDFHost", "dasHost", "nvcontainer", "NVDisplay.Container", "atiesrxx", "atieclxx",
        "amdfendrsr", "PawnIO",
    };

    private readonly int _cores = Environment.ProcessorCount;
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly Dictionary<int, (TimeSpan Cpu, ulong Io)> _prev = new();
    private readonly HashSet<int> _suspended = new();
    private readonly Dictionary<string, string> _titleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTime _prevTime = DateTime.MinValue;

    public bool ShowSystem { get; set; }

    public sealed record Sample(string Name, string Key, List<int> Pids, string Title, double Cpu, long Mem, long Disk,
                                bool Locked, bool Critical, bool AnyWindow, string State, string Priority);

    /// <summary>Lê todos os processos e calcula CPU e disco pela diferença desde a leitura anterior. Rodar fora da thread da janela.</summary>
    public List<Sample> Read()
    {
        var now = DateTime.UtcNow;
        double secs = _prevTime == DateTime.MinValue ? 0 : (now - _prevTime).TotalSeconds;
        var nextPrev = new Dictionary<int, (TimeSpan, ulong)>();
        var groups = new Dictionary<string, List<(Process P, double Cpu, long Disk)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string name = p.ProcessName;
                bool locked = Locked.Contains(name);
                bool sysLike = SystemLike.Contains(name);
                if (!ShowSystem && (locked || sysLike || p.SessionId != _session)) { p.Dispose(); continue; }

                TimeSpan cpuTime = TimeSpan.Zero; ulong io = 0;
                try { cpuTime = p.TotalProcessorTime; } catch { }
                try { io = ReadIo(p); } catch { }
                nextPrev[p.Id] = (cpuTime, io);

                double cpu = 0; long disk = 0;
                if (secs > 0 && _prev.TryGetValue(p.Id, out var old))
                {
                    cpu = Math.Max(0, (cpuTime - old.Cpu).TotalSeconds / secs / _cores * 100);
                    disk = io >= old.Io ? (long)((io - old.Io) / secs) : 0;
                }
                if (!groups.TryGetValue(name, out var list)) groups[name] = list = new();
                list.Add((p, cpu, disk));
            }
            catch { p.Dispose(); }
        }

        _prev.Clear();
        foreach (var kv in nextPrev) _prev[kv.Key] = kv.Value;
        _prevTime = now;
        HashSet<int> susp;
        lock (_gate) { _suspended.RemoveWhere(pid => !nextPrev.ContainsKey(pid)); susp = new HashSet<int>(_suspended); }

        var result = new List<Sample>(groups.Count);
        foreach (var (name, procs) in groups)
        {
            long mem = 0; double cpu = 0; long disk = 0; bool window = false;
            var pids = new List<int>(procs.Count);
            foreach (var (p, c, d) in procs)
            {
                pids.Add(p.Id); cpu += c; disk += d;
                try { mem += p.WorkingSet64; } catch { }
                if (!window) try { window = p.MainWindowHandle != IntPtr.Zero; } catch { }
            }

            string title = name;
            if (!_titleCache.TryGetValue(name, out var cached))
            {
                try { title = procs[0].P.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : name; } catch { }
                _titleCache[name] = title;
            }
            else title = cached;

            // Estado: suspenso (todos) / eficiência (todos) / normal.
            bool allSusp = pids.All(susp.Contains);
            bool anyEco = false, allEco = true;
            string prio = "";
            foreach (var (p, _, _) in procs)
            {
                bool eco = IsEfficiency(p.Id);
                anyEco |= eco; allEco &= eco;
            }
            try { prio = PriorityName(procs[0].P.PriorityClass); } catch { }
            string state = allSusp ? "Suspenso" : allEco && anyEco ? "Eficiência" : anyEco ? "Eficiência (parte)" : "";

            result.Add(new Sample(name, name.ToLowerInvariant(), pids, title, cpu, mem, disk,
                Locked.Contains(name), SystemLike.Contains(name), window, state, prio));
            foreach (var (p, _, _) in procs) p.Dispose();
        }
        return result;
    }

    // ---------- ações ----------

    /// <summary>Encerra todos os processos do grupo, incluindo os filhos (árvore).</summary>
    public KillResult Kill(IEnumerable<ProcRow> rows)
    {
        int killed = 0;
        var failed = new List<string>();
        foreach (var row in rows)
        {
            if (row.Locked) { failed.Add($"{row.Title} (protegido)"); continue; }
            bool ok = true;
            foreach (int pid in row.Pids.ToList())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    bool wasSusp; lock (_gate) wasSusp = _suspended.Remove(pid);
                    if (wasSusp) NtResume(pid);
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (ArgumentException) { }  // já fechou
                catch (Exception) { ok = false; }
            }
            if (!ok) failed.Add(row.Title);
        }
        return new KillResult(killed, failed);
    }

    /// <summary>Modo de eficiência do Windows 11: prioridade ociosa + EcoQoS (baixo consumo de energia). Desligar volta ao normal.</summary>
    public (int Ok, int Fail) SetEfficiency(IEnumerable<ProcRow> rows, bool on)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            if (row.Locked) { fail++; continue; }
            foreach (int pid in row.Pids)
            {
                IntPtr h = OpenProcess(PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) { fail++; continue; }
                try
                {
                    var st = new PROCESS_POWER_THROTTLING_STATE
                    {
                        Version = 1, ControlMask = 1 /* EXECUTION_SPEED */, StateMask = on ? 1u : 0u
                    };
                    bool a = SetProcessInformation(h, 4 /* ProcessPowerThrottling */, ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>());
                    bool b = SetPriorityClass(h, on ? IDLE_PRIORITY_CLASS : NORMAL_PRIORITY_CLASS);
                    if (a || b) ok++; else fail++;
                }
                finally { CloseHandle(h); }
            }
        }
        return (ok, fail);
    }

    public (int Ok, int Fail) SetPriority(IEnumerable<ProcRow> rows, ProcessPriorityClass level)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            if (row.Locked) { fail++; continue; }
            foreach (int pid in row.Pids)
            {
                try { using var p = Process.GetProcessById(pid); p.PriorityClass = level; ok++; }
                catch { fail++; }
            }
        }
        return (ok, fail);
    }

    /// <summary>Congela (ou retoma) o processo sem fechar: não usa CPU, mas continua na memória.</summary>
    public (int Ok, int Fail) SetSuspended(IEnumerable<ProcRow> rows, bool suspend)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            if (row.Locked || row.Critical) { fail++; continue; }
            foreach (int pid in row.Pids)
            {
                bool done = suspend ? NtSuspend(pid) : NtResume(pid);
                if (done) { ok++; lock (_gate) { if (suspend) _suspended.Add(pid); else _suspended.Remove(pid); } }
                else fail++;
            }
        }
        return (ok, fail);
    }

    /// <summary>Retoma tudo que o app congelou (chamado ao fechar o app, para nada ficar travado para sempre).</summary>
    public void ResumeAll()
    {
        int[] all; lock (_gate) { all = _suspended.ToArray(); _suspended.Clear(); }
        foreach (int pid in all) NtResume(pid);
    }

    public int SuspendedCount { get { lock (_gate) return _suspended.Count; } }

    // ---------- helpers ----------

    private static string PriorityName(ProcessPriorityClass c) => c switch
    {
        ProcessPriorityClass.Idle => "Baixa",
        ProcessPriorityClass.BelowNormal => "Abaixo do normal",
        ProcessPriorityClass.Normal => "Normal",
        ProcessPriorityClass.AboveNormal => "Acima do normal",
        ProcessPriorityClass.High => "Alta",
        ProcessPriorityClass.RealTime => "Tempo real",
        _ => "",
    };

    private static ulong ReadIo(Process p)
    {
        if (!GetProcessIoCounters(p.Handle, out var io)) return 0;
        return io.ReadTransferCount + io.WriteTransferCount;
    }

    private static bool IsEfficiency(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var st = new PROCESS_POWER_THROTTLING_STATE { Version = 1 };
            if (!GetProcessInformation(h, 4, ref st, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>())) return false;
            return (st.StateMask & 1) != 0 && (st.ControlMask & 1) != 0;
        }
        finally { CloseHandle(h); }
    }

    private static bool NtSuspend(int pid) => NtAction(pid, true);
    private static bool NtResume(int pid) => NtAction(pid, false);
    private static bool NtAction(int pid, bool suspend)
    {
        IntPtr h = OpenProcess(PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return (suspend ? NtSuspendProcess(h) : NtResumeProcess(h)) == 0; }
        finally { CloseHandle(h); }
    }

    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SUSPEND_RESUME = 0x0800;
    private const uint IDLE_PRIORITY_CLASS = 0x40;
    private const uint NORMAL_PRIORITY_CLASS = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);
    [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS counters);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr h, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);
    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr h);
}
