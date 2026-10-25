using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Páginas Energia (bateria e fonte) e Rede (diagnóstico de lag).</summary>
public partial class MainWindow
{
    // ================= Energia =================

    private bool _energyBusy;

    private async void RefreshEnergy()
    {
        if (_energyBusy) return;
        _energyBusy = true;
        BtnEnergyRefresh.IsEnabled = false;
        EnergyStatus.Text = "Lendo bateria e energia…";
        try
        {
            var sensors = _monitor.Rows.ToList();
            var cards = await Task.Run(() => BatteryInfo.Read(sensors));
            EnergyCards.ItemsSource = cards;
            bool hasBattery = cards.Count > 1;
            EnergyStatus.Text = (hasBattery ? "Bateria detectada" : "Nenhuma bateria (PC de mesa)") + $" · lido às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { EnergyStatus.Text = "Falhou: " + ex.Message; }
        finally
        {
            _energyBusy = false;
            BtnEnergyRefresh.IsEnabled = true;
        }
    }

    private void BtnEnergyRefresh_Click(object sender, RoutedEventArgs e) => RefreshEnergy();

    // ================= Rede =================

    private CancellationTokenSource? _netCts;
    private bool _netRunning;
    private List<DiskHealthCard>? _lastNet;

    private async void BtnNetTest_Click(object sender, RoutedEventArgs e)
    {
        if (_netRunning) { _netCts?.Cancel(); return; }
        if (AnyBenchRunning || TestRunning) { NetStatus.Text = "Termine o outro teste antes."; return; }

        _netRunning = true;
        _netCts = new CancellationTokenSource();
        var ct = _netCts.Token;
        BtnNetTest.Content = "CANCELAR";
        NetCards.ItemsSource = null;
        NetProgress.ScaleX = 0;
        try
        {
            var cards = await NetworkTest.Run((text, frac) => Dispatcher.BeginInvoke(new Action(() =>
            {
                NetStatus.Text = text;
                NetProgress.ScaleX = Math.Clamp(frac, 0, 1);
            })), ct);
            _lastNet = cards;
            NetCards.ItemsSource = cards;
            NetStatus.Text = $"Concluído às {DateTime.Now:HH:mm:ss}. O resultado entra no relatório exportado.";
            NetProgress.ScaleX = 1;
        }
        catch (OperationCanceledException) { NetStatus.Text = "Teste cancelado."; NetProgress.ScaleX = 0; }
        catch (Exception ex) { NetStatus.Text = "Falhou: " + ex.Message; NetProgress.ScaleX = 0; }
        finally
        {
            _netRunning = false;
            _netCts?.Dispose();
            _netCts = null;
            BtnNetTest.Content = "TESTAR";
        }
    }

    // ================= Tema (modo + cor de destaque) =================

    private DateTime _themePopupClosedAt = DateTime.MinValue;

    /// <summary>Botão da barra de título: abre/fecha o menu de aparência (modo e cor de destaque).</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemePopup.IsOpen) { ThemePopup.IsOpen = false; return; }
        // Clicar no botão com o menu aberto fecha o menu (clique fora) e, logo em seguida, chega aqui: não reabrir.
        if ((DateTime.UtcNow - _themePopupClosedAt).TotalMilliseconds < 250) return;
        InitAppearance();
        ThemePopup.IsOpen = true;
    }

    private void ThemePopup_Closed(object? sender, EventArgs e) => _themePopupClosedAt = DateTime.UtcNow;

    private void ThemePopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) ThemePopup.IsOpen = false;
    }

    /// <summary>Mostra o ícone do modo atual (sol = claro, lua = escuro) e troca o logo.</summary>
    private void UpdateThemeButton()
    {
        bool light = ThemeManager.IsLight;
        BtnTheme.Content = light ? "\uE706" : "\uE708";
        BtnTheme.ToolTip = "Aparência (modo e cor de destaque)";
        TitleLogo.Source = new System.Windows.Media.Imaging.BitmapImage(
            new Uri(light ? "pack://application:,,,/Assets/logo-dark.png" : "pack://application:,,,/Assets/logo.png"));
    }
}
