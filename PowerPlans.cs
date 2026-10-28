using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Um plano de energia do Windows, como aparece em "powercfg /list".</summary>
public sealed class PowerPlanItem : INotifyPropertyChanged
{
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>false = plano padrão do Windows que ainda não existe neste PC (é criado ao selecionar).</summary>
    public bool Installed { get; init; } = true;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }

    /// <summary>Qual mini animação o botão toca ao ser escolhido.</summary>
    public string Kind => Guid.ToLowerInvariant() switch
    {
        PowerPlans.Balanced => "balance",
        PowerPlans.HighPerformance => "bolt",
        PowerPlans.Ultimate => "rocket",
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "leaf", // Economia de energia
        _ => "spark",
    };

    /// <summary>Ícone desenhado em um quadrado 16x16 (preenchido com a cor do texto do botão).</summary>
    public string IconData => Kind switch
    {
        "balance" => "M1,4 H15 V5.4 H1 Z M7.2,3 H8.8 V13 H7.2 Z M4,13 H12 V14.5 H4 Z M3,5.4 L0.3,10.2 H5.7 Z M13,5.4 L10.3,10.2 H15.7 Z",
        "bolt" => "M9.5,0.5 L2.5,9 H7.2 L6.2,15.5 L13.5,6.5 H8.8 Z",
        "rocket" => "M8,0.5 C10.6,3 11.4,7 10.3,11 L5.7,11 C4.6,7 5.4,3 8,0.5 Z M5.4,8.2 L2.6,12 L5.8,11.2 Z M10.6,8.2 L13.4,12 L10.2,11.2 Z M6.9,11.8 L8,15.5 L9.1,11.8 Z",
        "leaf" => "M2,14 C1.5,6 7,1.5 14.5,1.5 C14.5,9 10.5,14.5 2,14 Z M1,15.5 L7,9 L7.6,9.6 L1.6,16 Z",
        _ => "M8,0 L9.8,6.2 L16,8 L9.8,9.8 L8,16 L6.2,9.8 L0,8 L6.2,6.2 Z",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record PowerApplyResult(bool Ok, string Message, string? Previous);

/// <summary>
/// Troca o plano de energia do Windows. "powercfg /setactive" grava o plano ativo no registro do sistema, então ele
/// continua valendo com o app fechado e depois de reiniciar o PC; o app não desfaz nada ao sair.
/// </summary>
public static class PowerPlans
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    private static readonly (string Guid, string Name)[] BuiltIn =
    {
        (Balanced, "Equilibrado"), (HighPerformance, "Alto desempenho"), (Ultimate, "Desempenho máximo"),
    };

    private static readonly Regex LineRx = new(
        @"(?<g>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((?<n>.+?)\)\s*(?<a>\*)?\s*$", RegexOptions.Compiled);
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>Os 3 planos padrão (mesmo os que ainda não existem no PC) + qualquer outro instalado (Economia de energia, AMD Ryzen…).</summary>
    public static List<PowerPlanItem> List()
    {
        string? active = ActiveScheme();
        var installed = new List<(string Guid, string Name)>();
        foreach (var line in PowerCfg("/list").Split('\n'))
        {
            var m = LineRx.Match(line.TrimEnd('\r', ' '));
            if (m.Success) installed.Add((m.Groups["g"].Value.ToLowerInvariant(), m.Groups["n"].Value.Trim()));
        }

        var result = new List<PowerPlanItem>();
        foreach (var (guid, fallback) in BuiltIn)
        {
            var found = installed.FirstOrDefault(p => p.Guid == guid);
            bool has = found.Guid != null;
            result.Add(new PowerPlanItem
            {
                Guid = guid, Name = has ? found.Name : fallback, Installed = has,
                IsActive = string.Equals(active, guid, StringComparison.OrdinalIgnoreCase),
            });
        }
        foreach (var p in installed.Where(p => BuiltIn.All(b => b.Guid != p.Guid)))
            result.Add(new PowerPlanItem
            {
                Guid = p.Guid, Name = p.Name,
                IsActive = string.Equals(active, p.Guid, StringComparison.OrdinalIgnoreCase),
            });
        return result;
    }

    /// <summary>Ativa o plano (criando-o antes se for um dos padrões que falta) e confere se o Windows aceitou.</summary>
    public static PowerApplyResult Apply(string guid)
    {
        string? previous = ActiveScheme();
        if (!IsInstalled(guid))
        {
            // Alguns PCs vêm sem o plano listado: duplicar o modelo padrão o recria com o mesmo GUID.
            PowerCfg($"-duplicatescheme {guid} {guid}");
            if (!IsInstalled(guid))
                return new(false, "Este plano não pôde ser criado neste PC (alguns notebooks e versões do Windows não oferecem esse plano).", previous);
        }
        PowerCfg($"/setactive {guid}");
        bool ok = string.Equals(ActiveScheme(), guid, StringComparison.OrdinalIgnoreCase);
        return ok
            ? new(true, "", previous)
            : new(false, "O Windows não aceitou a mudança (pode exigir administrador).", previous);
    }

    public static void SetActive(string guid) => PowerCfg($"/setactive {guid}");

    public static string? ActiveScheme()
    {
        var m = GuidRx.Match(PowerCfg("/getactivescheme"));
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static bool IsInstalled(string guid) => PowerCfg("/list").Contains(guid, StringComparison.OrdinalIgnoreCase);

    private static string PowerCfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = OemEncoding(),
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    /// <summary>O powercfg escreve na página de código do console (OEM); sem isso os acentos dos nomes saem trocados.</summary>
    private static Encoding OemEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch { return Encoding.Default; }
    }
}
