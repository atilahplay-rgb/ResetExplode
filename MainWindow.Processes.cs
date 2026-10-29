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

    private void InitProcesses()
    {
        if (_procView == null)
        {
            _procView = new ListCollectionView(_procRows);
            _procView.IsLiveSorting = true;
            foreach (var p in new[] { nameof(ProcRow.Title), nameof(ProcRow.Cpu), nameof(ProcRow.MemoryBytes), nameof(ProcRow.DiskBps) })
                _procView.LiveSortingProperties.Add(p);
            _procView.SortDescriptions.Add(new SortDescription(nameof(ProcRow.MemoryBytes), ListSortDirection.Descending));
            _procView.Filter = o => MatchesProcFilter((ProcRow)o);
            ProcGrid.ItemsSource = _procView;

            _procTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                (_, _) => { if (PageProcesses.IsVisible) RefreshProcesses(); }, Dispatcher);
        }
        _procTimer!.Start();
        RefreshProcesses();
    }

    private bool MatchesProcFilter(ProcRow r)
    {
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
            double totalCpu = 0; int pidCount = 0;
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
                totalCpu += s.Cpu; pidCount += s.Pids.Count;
            }
            foreach (var gone in _procMap.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _procRows.Remove(_procMap[gone]);
                _procMap.Remove(gone);
            }

            string susp = _pm.SuspendedCount > 0 ? $" · {_pm.SuspendedCount} suspensos" : "";
            ProcSummary.Text = $"{samples.Count} PROGRAMAS · {pidCount} PROCESSOS · CPU {Math.Min(100, totalCpu):N0} %{susp}";
            UpdateProcButtons();
        }
        catch (Exception ex) { ProcResult.Text = $"Não foi possível ler os processos: {ex.Message}"; }
        finally { _procBusy = false; }
    }

    private static void UpdateRow(ProcRow row, ProcessManager.Sample s)
    {
        row.Pids = s.Pids; row.Count = s.Pids.Count;
        row.Cpu = s.Cpu; row.MemoryBytes = s.Mem; row.DiskBps = s.Disk;
        row.State = s.State; row.Priority = s.Priority;
    }

    private List<ProcRow> SelectedProcs() => ProcGrid.SelectedItems.Cast<ProcRow>().ToList();

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
    private void ProcSearch_Changed(object sender, TextChangedEventArgs e) => _procView?.Refresh();
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
}
