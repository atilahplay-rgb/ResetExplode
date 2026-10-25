using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ResetExplode;

public enum ThemeMode { Dark, Light, System }

/// <summary>
/// Aparência do app: modo (escuro / claro / o do Windows) + cor de destaque (padrão preto e branco, cor do Windows ou manual).
/// Tudo é feito substituindo os pincéis da paleta (Theme.xaml). O XAML os referencia com DynamicResource,
/// então a troca vale na hora, sem recarregar janelas. Overlay e aviso de dispositivo ficam sempre escuros (aparecem sobre jogos).
/// </summary>
public static class ThemeManager
{
    public const string AccentDefault = "default"; // preto e branco, como era antes
    public const string AccentSystem = "system";   // acompanha a cor de destaque do Windows

    /// <summary>As 48 cores de destaque do Windows (mesma grade de Configurações › Personalização › Cores).</summary>
    public static readonly string[] WindowsAccents =
    {
        "#FFB900", "#FF8C00", "#F7630C", "#CA5010", "#DA3B01", "#EF6950", "#D13438", "#FF4343",
        "#E74856", "#E81123", "#EA005E", "#C30052", "#E3008C", "#BF0077", "#C239B3", "#9A0089",
        "#0078D7", "#0063B1", "#8E8CD8", "#6B69D6", "#8764B8", "#744DA9", "#B146C2", "#881798",
        "#0099BC", "#2D7D9A", "#00B7C3", "#038387", "#00B294", "#018574", "#00CC6A", "#10893E",
        "#7A7574", "#5D5A58", "#68768A", "#515C6B", "#567C73", "#486860", "#498205", "#107C10",
        "#767676", "#4C4A48", "#69797E", "#4A5459", "#647C64", "#525E54", "#847545", "#7E735F",
    };

    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;
    /// <summary>"default", "system" ou "#RRGGBB".</summary>
    public static string AccentKey { get; private set; } = AccentDefault;
    /// <summary>Modo efetivo agora (já resolvido quando o modo é "do sistema").</summary>
    public static bool IsLight { get; private set; }
    /// <summary>Cor de destaque efetiva agora (já ajustada para ter contraste com o fundo).</summary>
    public static Color Accent { get; private set; } = Color.FromRgb(0xF5, 0xF5, 0xF5);

    /// <summary>Disparado depois de qualquer mudança de aparência (inclusive quando o Windows muda e o app o acompanha).</summary>
    public static event Action? Changed;

    private static readonly (object Key, string Dark, string Light)[] Palette =
    {
        ("Bg", "#000000", "#FFFFFF"),
        ("Panel", "#080808", "#F2F2F2"),
        ("Card", "#0E0E0E", "#FFFFFF"),
        ("Hover", "#181818", "#E4E4E4"),
        ("Line", "#1F1F1F", "#CFCFCF"),
        ("LineStrong", "#3A3A3A", "#8A8A8A"),
        ("Fg", "#F5F5F5", "#111111"),
        ("Muted", "#8C8C8C", "#3F3F3F"),
        ("Faint", "#4A4A4A", "#5C5C5C"),
        ("Ink", "#000000", "#FFFFFF"),
        ("ThumbBrush", "#2E2E2E", "#CFCFCF"),
        ("ThumbHover", "#6A6A6A", "#A5A5A5"),
        ("ThumbDrag", "#9A9A9A", "#7A7A7A"),
        (SystemColors.ControlBrushKey, "#000000", "#FFFFFF"),
        (SystemColors.HighlightBrushKey, "#181818", "#E4E4E4"),
        (SystemColors.InactiveSelectionHighlightBrushKey, "#181818", "#E4E4E4"),
    };

    private static AppSettings? _settings;
    private static DispatcherTimer? _watch;
    private static (bool Light, Color Accent) _lastSystem;

    /// <summary>Lê a escolha salva, aplica e passa a acompanhar o Windows (se o modo ou a cor forem "do sistema").</summary>
    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        Mode = settings.ThemeMode switch
        {
            "light" => ThemeMode.Light,
            "system" => ThemeMode.System,
            "dark" => ThemeMode.Dark,
            _ => settings.LightTheme ? ThemeMode.Light : ThemeMode.Dark, // arquivo de uma versão antiga: só existia LightTheme
        };
        AccentKey = NormalizeAccent(settings.AccentColor);
        Reapply(raise: false);

        // O Windows não avisa o app de forma simples; ler dois valores do registro a cada 2 s custa nada.
        _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watch.Tick += (_, _) =>
        {
            if (Mode != ThemeMode.System && AccentKey != AccentSystem) return;
            if (ReadSystem() != _lastSystem) Reapply(raise: true);
        };
        _watch.Start();
    }

    public static void SetMode(ThemeMode mode)
    {
        Mode = mode;
        Reapply(raise: true);
        Commit();
    }

    /// <param name="key">"default", "system" ou "#RRGGBB".</param>
    /// <param name="persist">false enquanto o usuário arrasta um controle; chame <see cref="Commit"/> ao soltar.</param>
    public static void SetAccent(string key, bool persist = true)
    {
        AccentKey = NormalizeAccent(key);
        Reapply(raise: true);
        if (persist) Commit();
    }

    /// <summary>Volta ao visual original: modo escuro, preto e branco.</summary>
    public static void Reset()
    {
        Mode = ThemeMode.Dark;
        AccentKey = AccentDefault;
        Reapply(raise: true);
        Commit();
    }

    /// <summary>Grava a escolha atual em settings.json.</summary>
    public static void Commit()
    {
        if (_settings == null) return;
        _settings.ThemeMode = Mode.ToString().ToLowerInvariant();
        _settings.AccentColor = AccentKey;
        _settings.LightTheme = IsLight; // mantém o campo antigo coerente (versões antigas do app ainda o leem)
        try { _settings.Save(); } catch { }
    }

    // ------------------------------------------------------------------ aplicação

    private static void Reapply(bool raise)
    {
        _lastSystem = ReadSystem();
        IsLight = Mode switch { ThemeMode.Light => true, ThemeMode.Dark => false, _ => _lastSystem.Light };

        var res = Application.Current.Resources;
        foreach (var (key, dark, light) in Palette)
            res[key] = Frozen(ParseOrBlack(IsLight ? light : dark));

        var bg = ParseOrBlack(IsLight ? "#FFFFFF" : "#000000");
        Color accent, accentInk;
        if (AccentKey == AccentDefault)
        {
            // Preto e branco: o destaque é o próprio texto (visual de antes).
            accent = ParseOrBlack(IsLight ? "#111111" : "#F5F5F5");
            accentInk = bg;
        }
        else
        {
            var raw = AccentKey == AccentSystem ? _lastSystem.Accent
                : TryParseHex(AccentKey, out var c) ? c : _lastSystem.Accent;
            accent = EnsureContrast(raw, bg, 3.0);
            accentInk = InkFor(accent);
        }
        Accent = accent;
        res["Accent"] = Frozen(accent);
        res["AccentInk"] = Frozen(accentInk);
        res["AccentSoft"] = Frozen(Color.FromArgb(0x33, accent.R, accent.G, accent.B));

        if (raise) Changed?.Invoke();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b; // o XAML usa DynamicResource: trocar o pincel atualiza todos os controles na hora
    }

    // ------------------------------------------------------------------ Windows

    private static (bool Light, Color Accent) ReadSystem() => (ReadSystemLight(), ReadSystemAccent());

    private static bool ReadSystemLight()
    {
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return v is not int i || i != 0;
        }
        catch { return false; }
    }

    private static Color ReadSystemAccent()
    {
        try
        {
            // DWM\AccentColor é gravado como 0xAABBGGRR; ColorizationColor (0xAARRGGBB) serve de reserva.
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "ColorizationColor", null) is int argb)
                return Color.FromRgb((byte)((argb >> 16) & 0xFF), (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD7); // azul padrão do Windows
    }

    // ------------------------------------------------------------------ cores

    public static string NormalizeAccent(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return AccentDefault;
        key = key.Trim();
        if (key.Equals(AccentDefault, StringComparison.OrdinalIgnoreCase)) return AccentDefault;
        if (key.Equals(AccentSystem, StringComparison.OrdinalIgnoreCase)) return AccentSystem;
        return TryParseHex(key, out var c) ? ToHex(c) : AccentDefault;
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => new string(ch, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        color = Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseOrBlack(string hex) => TryParseHex(hex, out var c) ? c : Colors.Black;

    /// <summary>Preto ou branco, o que ler melhor por cima da cor (texto de botões e ícone de seleção).</summary>
    public static Color InkFor(Color onColor)
        => Ratio(Colors.White, onColor) >= Ratio(Colors.Black, onColor) ? Colors.White : Colors.Black;

    private static double Luminance(Color c)
    {
        static double F(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double Ratio(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        if (la < lb) (la, lb) = (lb, la);
        return (la + 0.05) / (lb + 0.05);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Clareia (no escuro) ou escurece (no claro) a cor até ela aparecer bem sobre o fundo, como o Windows faz.</summary>
    private static Color EnsureContrast(Color c, Color bg, double min)
    {
        var target = Luminance(bg) < 0.5 ? Colors.White : Colors.Black;
        for (int i = 0; i < 24 && Ratio(c, bg) < min; i++) c = Blend(c, target, 0.08);
        return c;
    }
}
