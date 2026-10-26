using System.Collections.ObjectModel;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Encapsula o LibreHardwareMonitorLib e mantém a lista de sensores atualizada.</summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly Dictionary<string, SensorRow> _index = new();
    private volatile bool _open;

    public ObservableCollection<SensorRow> Rows { get; } = new();
    public bool IsOpen => _open;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsControllerEnabled = true
        };
    }

    /// <summary>Detecta o hardware (leva de 0,5 a alguns segundos) fora da thread da janela. Até terminar, Refresh não faz nada.</summary>
    public Task OpenAsync() => Task.Run(() => { _computer.Open(); _open = true; });

    /// <summary>Leitura crua de um sensor, copiada fora da thread da janela.</summary>
    private readonly record struct Snap(string Id, string Hardware, SensorType Type, string Name, HardwareType HwType,
                                        float? Value, float? Min, float? Max);

    private Task? _inflight; // leitura em andamento (só mexe na thread da janela)

    /// <summary>
    /// Atualiza os sensores sem travar a janela: hw.Update() (que fala com driver, SMBus, disco, rede...) roda em outra
    /// thread; só a cópia dos valores para a lista acontece na thread da janela. Chame sempre da thread da janela.
    /// Se já houver uma leitura em andamento, quem chamar espera por ela em vez de iniciar outra.
    /// </summary>
    public Task RefreshAsync()
    {
        if (!_open) return Task.CompletedTask;
        if (_inflight != null) return _inflight;
        var t = RunRefresh();
        if (!t.IsCompleted) _inflight = t;
        return t;
    }

    private async Task RunRefresh()
    {
        try
        {
            var snap = await Task.Run(Collect);
            if (_open) Apply(snap);
        }
        catch { /* uma leitura que falha não pode derrubar o app; tenta de novo no próximo ciclo */ }
        finally { _inflight = null; }
    }

    /// <summary>Versão síncrona (trava a janela durante a leitura); prefira RefreshAsync.</summary>
    public void Refresh()
    {
        if (!_open) return;
        Apply(Collect());
    }

    private int _collecting; // 1 enquanto hw.Update() roda em outra thread (Dispose espera isso acabar)

    private List<Snap> Collect()
    {
        Volatile.Write(ref _collecting, 1);
        try
        {
            var list = new List<Snap>(256);
            foreach (var hw in _computer.Hardware)
                Walk(hw, list);
            return list;
        }
        finally { Volatile.Write(ref _collecting, 0); }
    }

    private static void Walk(IHardware hw, List<Snap> into)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
            into.Add(new Snap(s.Identifier.ToString(), hw.Name, s.SensorType, s.Name, hw.HardwareType, s.Value, s.Min, s.Max));
        foreach (var sub in hw.SubHardware)
            Walk(sub, into);
    }

    private void Apply(List<Snap> snap)
    {
        foreach (var s in snap)
        {
            if (!_index.TryGetValue(s.Id, out var row))
            {
                row = new SensorRow
                {
                    Id = s.Id,
                    Hardware = s.Hardware,
                    Category = SensorRow.CategoryName(s.Type),
                    Name = s.Name,
                    Type = s.Type,
                    HardwareType = s.HwType
                };
                _index[s.Id] = row;
                Rows.Add(row);
            }
            row.Update(s.Value, s.Min, s.Max);
        }
    }

    /// <summary>Temperatura principal da CPU: Package (Intel) / Tctl-Tdie (AMD), senão a maior entre os núcleos.</summary>
    public float? CpuTemperature
    {
        get
        {
            var temps = Rows.Where(r => r.HardwareType == HardwareType.Cpu
                                     && r.Type == SensorType.Temperature
                                     && r.RawValue is > 0).ToList();
            var main = temps.FirstOrDefault(r => r.Name.Contains("Package") || r.Name.Contains("Tctl") || r.Name.Contains("Tdie"));
            return main?.RawValue ?? temps.Max(r => r.RawValue);
        }
    }

    /// <summary>Carga total da CPU em %.</summary>
    public float? CpuLoad => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                   && r.Type == SensorType.Load
                                                   && r.Name == "CPU Total")?.RawValue;

    /// <summary>Consumo do pacote da CPU em W.</summary>
    public float? CpuPower => Rows.FirstOrDefault(r => r.HardwareType == HardwareType.Cpu
                                                    && r.Type == SensorType.Power
                                                    && r.Name.Contains("Package"))?.RawValue;

    /// <summary>
    /// Driver PawnIO (necessário para temperatura/consumo da CPU via MSR/SMN).
    /// A biblioteca procura exatamente esta chave de desinstalação.
    /// </summary>
    public static bool IsPawnIOInstalled
    {
        get
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,
                                                                     Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            return key != null;
        }
    }

    public static bool IsElevated =>
        new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public enum CpuSensorProblem { None, NotElevated, PawnIOMissing, ReadFailed }

    /// <summary>Por que a temperatura/consumo da CPU não aparecem (o motivo mais provável primeiro).</summary>
    public CpuSensorProblem DiagnoseCpuSensors()
    {
        if (CpuTemperature != null) return CpuSensorProblem.None;
        if (!IsElevated) return CpuSensorProblem.NotElevated;
        if (!IsPawnIOInstalled) return CpuSensorProblem.PawnIOMissing;
        return CpuSensorProblem.ReadFailed;
    }

    public static string Explain(CpuSensorProblem p) => p switch
    {
        CpuSensorProblem.NotElevated =>
            "O RESET EXPLODE não está rodando como administrador, e o Windows só libera os sensores internos da CPU " +
            "nesse modo. Feche e abra de novo aceitando o pedido de administrador.",
        CpuSensorProblem.PawnIOMissing =>
            "Falta o driver PawnIO, que lê a temperatura e o consumo da CPU. Ele é gratuito, assinado e usado também " +
            "pelo LibreHardwareMonitor e pelo FanControl. Depois de instalar, feche e abra o RESET EXPLODE.",
        CpuSensorProblem.ReadFailed =>
            "O PawnIO está instalado, mas a leitura falhou. Se acabou de instalar, reinicie o PC. Se continuar, " +
            "confira se o antivírus não bloqueou o driver ou se a CPU é muito antiga/recente para a biblioteca de sensores.",
        _ => ""
    };

    public void Dispose()
    {
        if (!_open) return;
        _open = false;
        // espera (pouco) uma leitura em andamento terminar antes de fechar os drivers
        for (int i = 0; i < 100 && Volatile.Read(ref _collecting) == 1; i++) Thread.Sleep(10);
        _computer.Close();
    }
}
