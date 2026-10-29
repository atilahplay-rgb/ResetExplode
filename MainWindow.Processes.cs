using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Aba RESET FERRAMENTAS · Processos: tabela ao vivo no estilo do Gerenciador de Tarefas do Windows 11.</summary>
public partial class MainWindow
{
    private readonly ProcessManager _pm = new();
    private readonly ObservableCollection<ProcRow> _procRows = new();
    private readonly Dictionary<string, ProcRow> _procMap = new(StringComparer.OrdinalIgnoreCase);
    private ListCollectionView? _procView;
    private DispatcherTimer? _procTimer;
    private bool _procBusy;
    private readonly ProcRow _hdrApps = new() { Name = "Aplicativos", Key = "#apps", Title = "Aplicativos", IsHeader = true, Locked = true, FixedSection = 0 };
    private readonly ProcRow _hdrBg = new() { Name = "Segundo plano", Key = "#bg", Title = "Processos em segundo plano", IsHeader = true, Locked = true, FixedSection = 1 };

    /// <summary>Atualiza os números dos títulos "Aplicativos (N)" e "Processos em segundo plano (N)" conforme a busca.</summary>
    private void UpdateProcHeaders()
    {
        int apps = 0, bg = 0;
        foreach (var r in _procRows)
        {
            if (r.IsHeader || !MatchesProcFilter(r)) continue;
            if (r.IsApp) apps++; else bg++;
        }
        _hdrApps.Title = $"Aplicativos ({apps})";
        _hdrBg.Title = $"Processos em segundo plano ({bg})";
    }
    private int _procSpeed = 1; // 0 alta · 1 normal · 2 baixa · 3 pausada (igual ao Gerenciador de Tarefas)
    private static readonly TimeSpan[] ProcIntervals =
        { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1) };
    private static readonly string[] ProcSpeedNames = { "ALTA", "NORMAL", "BAIXA", "PAUSADA" };

    private void BtnProcSpeed_Click(object sender, RoutedEventArgs e)
    {
        _procSpeed = (_procSpeed + 1) % 4;
        BtnProcSpeed.Content = $"VELOCIDADE: {ProcSpeedNames[_procSpeed]}";
        if (_procTimer == null) return;
        _procTimer.Stop();
        _procTimer.Interval = ProcIntervals[_procSpeed];
        if (_procSpeed != 3) _procTimer.Start();
    }

    private void InitProcesses()
    {
        if (_procView == null)
        {
            _procView = new ListCollectionView(_procRows);
            _procView.IsLiveSorting = true;
            foreach (var p in new[] { nameof(ProcRow.Title), nameof(ProcRow.Cpu), nameof(ProcRow.MemoryBytes), nameof(ProcRow.DiskBps),
                                      nameof(ProcRow.NetBps), nameof(ProcRow.SectionOrder), nameof(ProcRow.RowRank) })
                _procView.LiveSortingProperties.Add(p);
            ApplyProcSort(nameof(ProcRow.MemoryBytes), ListSortDirection.Descending);
            _procView.Filter = o => MatchesProcFilter((ProcRow)o);
            _procRows.Add(_hdrApps);
            _procRows.Add(_hdrBg);
            ProcGrid.ItemsSource = _procView;

            _procTimer = new DispatcherTimer(ProcIntervals[_procSpeed], DispatcherPriority.Normal,
                (_, _) => { if (PageProcesses.IsVisible) RefreshProcesses(); }, Dispatcher);
        }
        _pm.StartNet();
        if (_procSpeed != 3) _procTimer!.Start();
        RefreshProcesses();
    }

    private bool MatchesProcFilter(ProcRow r)
    {
        if (r.IsHeader) return true;
        string q = ProcSearch.Text.Trim();
        if (q.Length == 0) return true;
        return r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Pids.Any(pid => pid.ToString() == q);
    }

    private async void RefreshProcesses()
    {
        if (_procBusy) return;
        _procBusy = true;
        try
        {
            _pm.ShowSystem = ProcShowSystem.IsChecked == true;
            var samples = await Task.Run(() => _pm.Read());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalCpu = 0; long totalDisk = 0, totalNet = 0; int pidCount = 0;
            foreach (var s in samples)
            {
                seen.Add(s.Key);
                if (!_procMap.TryGetValue(s.Key, out var row))
                {
                    row = new ProcRow { Name = s.Name, Key = s.Key };
                    _procMap[s.Key] = row;
                    row.Title = s.Title; row.Locked = s.Locked; row.Critical = s.Critical;
                    UpdateRow(row, s);
                    _procRows.Add(row);
                }
                else UpdateRow(row, s);
                totalCpu += s.Cpu; totalDisk += s.Disk; totalNet += s.Net; pidCount += s.Pids.Count;
            }
            foreach (var gone in _procMap.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _procRows.Remove(_procMap[gone]);
                _procMap.Remove(gone);
            }

            string susp = _pm.SuspendedCount > 0 ? $" · {_pm.SuspendedCount} suspensos" : "";
            ProcSummary.Text = $"{samples.Count} PROGRAMAS · {pidCount} PROCESSOS · CPU {Math.Min(100, totalCpu):N0} %{susp}";
            UpdateProcHeaders();
            UpdateProcChips(totalCpu, totalDisk, totalNet);
            UpdateProcButtons();
        }
        catch (Exception ex) { ProcResult.Text = $"Não foi possível ler os processos: {ex.Message}"; }
        finally { _procBusy = false; }
    }

    private static void UpdateRow(ProcRow row, ProcessManager.Sample s)
    {
        row.Pids = s.Pids; row.Count = s.Pids.Count;
        row.Cpu = s.Cpu; row.MemoryBytes = s.Mem; row.DiskBps = s.Disk; row.NetBps = s.Net;
        row.State = s.State; row.Priority = s.Priority; row.IsApp = s.AnyWindow;

        // Mapa de calor das células, como no Gerenciador: quanto mais consumo, mais forte a cor de destaque.
        row.CpuHeat = Heat(s.Cpu >= 20 ? 3 : s.Cpu >= 5 ? 2 : s.Cpu >= 0.5 ? 1 : 0);
        double mb = s.Mem / 1048576.0;
        row.MemHeat = Heat(mb >= 1500 ? 3 : mb >= 500 ? 2 : mb >= 100 ? 1 : 0);
        double dmb = s.Disk / 1048576.0;
        row.DiskHeat = Heat(dmb >= 10 ? 3 : dmb >= 1 ? 2 : dmb >= 0.1 ? 1 : 0);
        double mbps = s.Net * 8 / 1e6;
        row.NetHeat = Heat(mbps >= 10 ? 3 : mbps >= 1 ? 2 : mbps >= 0.1 ? 1 : 0);
    }

    private static readonly Dictionary<(System.Windows.Media.Color, int), System.Windows.Media.Brush> HeatCache = new();

    private static System.Windows.Media.Brush Heat(int level)
    {
        if (level <= 0) return System.Windows.Media.Brushes.Transparent;
        var accent = (Application.Current.TryFindResource("Accent") as System.Windows.Media.SolidColorBrush)?.Color
                     ?? System.Windows.Media.Colors.White;
        if (!HeatCache.TryGetValue((accent, level), out var brush))
        {
            byte alpha = level == 1 ? (byte)0x1C : level == 2 ? (byte)0x3A : (byte)0x66;
            var b = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, accent.R, accent.G, accent.B));
            b.Freeze();
            HeatCache[(accent, level)] = brush = b;
        }
        return brush;
    }

    private List<ProcRow> SelectedProcs() => ProcGrid.SelectedItems.Cast<ProcRow>().Where(r => !r.IsHeader).ToList();

    private void UpdateProcButtons()
    {
        var sel = SelectedProcs();
        bool any = sel.Count > 0;
        BtnProcKill.IsEnabled = any && sel.Any(r => !r.Locked);
        BtnProcEco.IsEnabled = any && sel.Any(r => !r.Locked);
        BtnProcSuspend.IsEnabled = any && sel.Any(r => !r.Locked && !r.Critical);
        BtnProcPriority.IsEnabled = any && sel.Any(r => !r.Locked);

        bool allEco = any && sel.Where(r => !r.Locked).All(r => r.State == "Eficiência");
        BtnProcEco.Content = allEco ? "SAIR DA EFICIÊNCIA" : "MODO DE EFICIÊNCIA";
        bool allSusp = any && sel.Where(r => !r.Locked && !r.Critical).All(r => r.State == "Suspenso");
        BtnProcSuspend.Content = allSusp ? "RETOMAR" : "SUSPENDER";
    }

    private void ProcGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProcButtons();
    private void BtnProcRefresh_Click(object sender, RoutedEventArgs e) { ProcResult.Text = ""; RefreshProcesses(); }
    private void ProcSearch_Changed(object sender, TextChangedEventArgs e) { _procView?.Refresh(); UpdateProcHeaders(); }
    private void ProcShowSystem_Changed(object sender, RoutedEventArgs e) { if (_procView != null) RefreshProcesses(); }

    private void ProcGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && BtnProcKill.IsEnabled) { BtnProcKill_Click(sender, e); e.Handled = true; }
    }

    private void BtnProcKill_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedProcs().Where(r => !r.Locked).ToList();
        if (sel.Count == 0) return;

        string names = string.Join("\n", sel.Take(10).Select(r => $"  • {r.Title}{(r.Count > 1 ? $" ({r.Count} processos)" : "")}"))
                       + (sel.Count > 10 ? $"\n  … e mais {sel.Count - 10}" : "");
        bool sys = sel.Any(r => r.Critical);
        string warn = sys
            ? "\n\nATENÇÃO: há processos do Windows na seleção. Finalizar o que o sistema usa pode fechar a sessão, travar a tela ou até reiniciar o PC."
            : "\n\nO que não estiver salvo nesses programas será perdido.";
        var r1 = MessageBox.Show($"Finalizar {sel.Count} {(sel.Count == 1 ? "programa" : "programas")}?\n\n{names}\n\nOs processos filhos também serão encerrados.{warn}",
                                 "Processos", MessageBoxButton.YesNo, sys ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (r1 != MessageBoxResult.Yes) return;

        var res = _pm.Kill(sel);
        ProcResult.Text = res.Failed.Count == 0
            ? $"{res.Killed} processos encerrados."
            : $"{res.Killed} encerrados. Não foi possível: {string.Join(", ", res.Failed)}" +
              (SensorMonitor.IsElevated ? "." : " (abra o app como administrador para encerrar processos protegidos).");
        RefreshProcesses();
    }

    private void BtnProcEco_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedProcs().Where(r => !r.Locked).ToList();
        if (sel.Count == 0) return;
        bool turnOn = !sel.All(r => r.State == "Eficiência");
        var (ok, fail) = _pm.SetEfficiency(sel, turnOn);
        ProcResult.Text = turnOn
            ? $"Modo de eficiência ligado em {ok} processos: prioridade baixa e economia de energia, sem fechar nada."
            : $"Modo de eficiência desligado em {ok} processos.";
        if (fail > 0) ProcResult.Text += $" {fail} não permitiram a mudança.";
        RefreshProcesses();
    }

    private void BtnProcSuspend_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedProcs().Where(r => !r.Locked && !r.Critical).ToList();
        if (sel.Count == 0) return;
        bool suspend = !sel.All(r => r.State == "Suspenso");
        var (ok, fail) = _pm.SetSuspended(sel, suspend);
        ProcResult.Text = suspend
            ? $"{ok} processos suspensos: não usam CPU, mas continuam na memória. Use RETOMAR para voltar (o app também retoma tudo ao fechar)."
            : $"{ok} processos retomados.";
        if (fail > 0) ProcResult.Text += $" {fail} falharam.";
        RefreshProcesses();
    }

    private void BtnProcPriority_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedProcs().Where(r => !r.Locked).ToList();
        if (sel.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = BtnProcPriority, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, level) in new (string, ProcessPriorityClass)[]
                 {
                     ("Baixa", ProcessPriorityClass.Idle), ("Abaixo do normal", ProcessPriorityClass.BelowNormal),
                     ("Normal", ProcessPriorityClass.Normal), ("Acima do normal", ProcessPriorityClass.AboveNormal),
                     ("Alta", ProcessPriorityClass.High),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) =>
            {
                var (ok, fail) = _pm.SetPriority(sel, level);
                ProcResult.Text = $"Prioridade \"{label}\" aplicada em {ok} processos." + (fail > 0 ? $" {fail} não permitiram." : "");
                RefreshProcesses();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ----- Ordenar por (CPU, memória, disco, rede) com o total ao vivo no próprio filtro -----

    private bool _procChipSync;

    private static void SetHdr(TextBlock? t, string text) { if (t != null) t.Text = text; }

    private void UpdateProcChips(double cpu, long disk, long net)
    {
        var (total, avail) = Optimizer.MemoryStatus();
        double memPct = total > 0 ? (total - avail) * 100.0 / total : 0;
        SetHdr(HdrCpu, $"{Math.Min(100, cpu):N0}%");
        SetHdr(HdrMem, $"{memPct:N0}%");
        SetHdr(HdrDisk, $"{disk / 1048576.0:N1} MB/s");
        SetHdr(HdrNet, _pm.NetAvailable ? $"{net * 8 / 1e6:N1} Mbps" : "—");
        ChipSortCpu.Content = $"CPU · {Math.Min(100, cpu):N0} %";
        ChipSortMem.Content = $"Memória · {memPct:N0} %";
        ChipSortDisk.Content = $"Disco · {disk / 1048576.0:N1} MB/s";
        if (_pm.NetAvailable) { ChipSortNet.Content = $"Rede · {net * 8 / 1e6:N1} Mbps"; ChipSortNet.ToolTip = null; }
        else
        {
            ChipSortNet.Content = "Rede · —";
            ChipSortNet.ToolTip = "A leitura de rede por processo precisa do app como administrador" +
                                  (_pm.NetError is { Length: > 0 } err ? $" ({err})" : "") + ".";
        }
    }

    private void ProcSortChip_Checked(object sender, RoutedEventArgs e)
    {
        if (_procView == null || _procChipSync || sender is not RadioButton { Tag: string prop }) return;
        ApplyProcSort(prop, prop == nameof(ProcRow.Title) ? ListSortDirection.Ascending : ListSortDirection.Descending);
    }

    /// <summary>Aplica a ordenação mantendo "Aplicativos" sempre antes de "Processos em segundo plano".</summary>
    private void ApplyProcSort(string prop, ListSortDirection dir)
    {
        if (_procView == null) return;
        _procView.SortDescriptions.Clear();
        _procView.SortDescriptions.Add(new SortDescription(nameof(ProcRow.SectionOrder), ListSortDirection.Ascending));
        _procView.SortDescriptions.Add(new SortDescription(nameof(ProcRow.RowRank), ListSortDirection.Ascending)); // o título fica no topo da seção
        _procView.SortDescriptions.Add(new SortDescription(prop, dir));
        foreach (var col in ProcGrid.Columns)
            col.SortDirection = col.SortMemberPath == prop ? dir : null;
    }

    /// <summary>Clicar no cabeçalho também ordena (alternando crescente/decrescente) e o filtro acompanha a coluna.</summary>
    private void ProcGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // a ordenação padrão do DataGrid desfaria a separação em seções
        string prop = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(prop)) return;
        var dir = e.Column.SortDirection is { } cur
            ? (cur == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
            : (prop is nameof(ProcRow.Title) or nameof(ProcRow.State) or nameof(ProcRow.Priority)
                ? ListSortDirection.Ascending : ListSortDirection.Descending);
        ApplyProcSort(prop, dir);

        _procChipSync = true;
        foreach (var chip in new[] { ChipSortName, ChipSortCpu, ChipSortMem, ChipSortDisk, ChipSortNet })
            chip.IsChecked = (string)chip.Tag == prop;
        _procChipSync = false;
    }
}
