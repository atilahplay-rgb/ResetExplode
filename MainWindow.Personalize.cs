using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ResetExplode;

// Menu de APARÊNCIA (botão da barra de título): modo (escuro / claro / do sistema) e cor de destaque, no estilo do Windows 11.
// A troca em si é feita pelo ThemeManager; aqui só ficam os controles do menu.
public partial class MainWindow
{
    private bool _appearInit;
    private bool _appearBusy;                 // true enquanto a página atualiza os próprios controles (evita laço de eventos)
    private string _lastManual = "#0078D7";   // última cor manual: é a que volta ao clicar em "Manual"
    private readonly List<(Border Swatch, string Hex)> _swatches = new();

    /// <summary>Qualquer mudança de aparência (clique no menu, atalho ou o Windows mudando sozinho).</summary>
    private void OnThemeChanged()
    {
        UpdateThemeButton();
        if (_appearInit) SyncAppearance();
        // O gráfico do painel é desenhado em código (usa a cor de destaque), então precisa ser redesenhado.
        if (PageDash.Visibility == Visibility.Visible) UpdateDashboard();
    }

    private void InitAppearance()
    {
        if (!_appearInit)
        {
            _appearInit = true;
            foreach (var hex in ThemeManager.WindowsAccents)
            {
                ThemeManager.TryParseHex(hex, out var c);
                var swatch = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 4, 4),
                    Background = new SolidColorBrush(c), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand, ToolTip = hex, Tag = hex,
                    Child = new TextBlock
                    {
                        Text = "\uE73E", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12,
                        Foreground = new SolidColorBrush(ThemeManager.InkFor(c)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        Visibility = Visibility.Collapsed,
                    },
                };
                swatch.MouseLeftButtonUp += Swatch_Click;
                AccentSwatches.Children.Add(swatch);
                _swatches.Add((swatch, hex));
            }
        }
        SyncAppearance();
    }

    /// <summary>Põe os controles do menu de acordo com o que o ThemeManager está usando agora.</summary>
    private void SyncAppearance()
    {
        _appearBusy = true;
        try
        {
            string mode = ThemeManager.Mode.ToString().ToLowerInvariant();
            foreach (var rb in ThemeModePanel.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == mode;

            string key = ThemeManager.AccentKey;
            bool manual = key != ThemeManager.AccentDefault && key != ThemeManager.AccentSystem;
            string kind = manual ? "manual" : key;
            foreach (var rb in AccentKindPanel.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == kind;

            if (manual) _lastManual = key;
            var ring = (Brush)FindResource("Fg");
            foreach (var (swatch, hex) in _swatches)
            {
                bool selected = manual && string.Equals(hex, key, StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = selected ? ring : Brushes.Transparent;
                swatch.Child.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ThemeManager.TryParseHex(_lastManual, out var c))
            {
                SlR.Value = c.R; SlG.Value = c.G; SlB.Value = c.B;
                HexBox.Text = ThemeManager.ToHex(c);
            }

            string eff = ThemeManager.ToHex(ThemeManager.Accent);
            AccentInfo.Text = kind switch
            {
                "default" => "Preto e branco: o visual original do app.",
                "system" => $"Seguindo a cor do Windows ({eff}); se você mudar lá, o app muda junto.",
                _ => string.Equals(eff, key, StringComparison.OrdinalIgnoreCase)
                    ? $"Cor atual: {eff}"
                    : $"Cor escolhida {key}, usada como {eff} (ajustada para ter contraste com o fundo).",
            };
        }
        finally { _appearBusy = false; }
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_appearBusy || sender is not RadioButton { Tag: string tag }) return;
        ThemeManager.SetMode(tag switch { "light" => ThemeMode.Light, "system" => ThemeMode.System, _ => ThemeMode.Dark });
    }

    private void AccentKind_Checked(object sender, RoutedEventArgs e)
    {
        if (_appearBusy || sender is not RadioButton { Tag: string tag }) return;
        ThemeManager.SetAccent(tag == "manual" ? _lastManual : tag);
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string hex }) return;
        _lastManual = hex;
        ThemeManager.SetAccent(hex);
    }

    /// <summary>Arrastar um controle RGB: aplica na hora, mas só grava o arquivo ao soltar (AccentRgb_Commit).</summary>
    private void AccentRgb_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearBusy || SlR == null || SlG == null || SlB == null) return;
        string hex = $"#{(int)SlR.Value:X2}{(int)SlG.Value:X2}{(int)SlB.Value:X2}";
        _lastManual = hex;
        ThemeManager.SetAccent(hex, persist: false);
    }

    private void AccentRgb_Commit(object sender, RoutedEventArgs e) => ThemeManager.Commit();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHex();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        if (_appearBusy) return;
        if (ThemeManager.TryParseHex(HexBox.Text, out var c))
        {
            string hex = ThemeManager.ToHex(c);
            if (string.Equals(hex, ThemeManager.AccentKey, StringComparison.OrdinalIgnoreCase)) return;
            _lastManual = hex;
            ThemeManager.SetAccent(hex);
        }
        else SyncAppearance(); // texto inválido: volta ao valor atual
    }

    private void AppearanceReset_Click(object sender, RoutedEventArgs e)
    {
        _lastManual = "#0078D7";
        ThemeManager.Reset();
    }
}
