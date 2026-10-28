using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Um plano de energia do Windows, como aparece em "powercfg /list".</summary>
public sealed class PowerPlanItem : INotifyPropertyChanged
{
    /// <summary>GUID do plano no Windows (muda se o plano precisou ser criado com outro GUID).</summary>
    public string Guid { get; set; } = "";
    public string Name { get; init; } = "";
    /// <summary>false = plano padrão do Windows que ainda não existe neste PC (é criado ao selecionar).</summary>
    public bool Installed { get; init; } = true;
    /// <summary>Qual mini animação/ícone o botão usa (fixo, não muda com o GUID).</summary>
    public string Kind { get; init; } = "spark";

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive))); }
    }

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

/// <param name="ActiveGuid">GUID do plano que ficou ativo (pode ser diferente do pedido se o Windows criou o plano com outro GUID).</param>
public sealed record PowerApplyResult(bool Ok, string Message, string? Previous, string? ActiveGuid = null);

/// <summary>
/// Troca o plano de energia do Windows. "powercfg /setactive" grava o plano ativo no registro do sistema, então ele
/// continua valendo com o app fechado e depois de reiniciar o PC; o app não desfaz nada ao sair.
/// </summary>
public static class PowerPlans
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    private const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    private static readonly (string Guid, string Name, string Kind)[] BuiltIn =
    {
        (Balanced, "Equilibrado", "balance"), (HighPerformance, "Alto desempenho", "bolt"), (Ultimate, "Desempenho máximo", "rocket"),
    };

    private static readonly Regex LineRx = new(
        @"(?<g>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s+\((?<n>.+?)\)\s*(?<a>\*)?\s*$", RegexOptions.Compiled);
    private static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", RegexOptions.Compiled);

    /// <summary>Um plano criado sem GUID fixo ainda é o "Desempenho máximo": reconhece pelo nome (o powercfg pode trocar os acentos).</summary>
    private static bool IsUltimateName(string name) =>
        name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Desempenho M", StringComparison.OrdinalIgnoreCase);

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

        bool IsActive(string g) => string.Equals(active, g, StringComparison.OrdinalIgnoreCase);
        var result = new List<PowerPlanItem>();
        var used = new HashSet<string>();
        foreach (var (guid, fallback, kind) in BuiltIn)
        {
            var found = installed.FirstOrDefault(p => p.Guid == guid);
            if (found.Guid == null && guid == Ultimate)
                found = installed.FirstOrDefault(p => IsUltimateName(p.Name)); // criado antes com outro GUID
            bool has = found.Guid != null;
            string useGuid = has ? found.Guid : guid;
            used.Add(useGuid);
            result.Add(new PowerPlanItem
            {
                Guid = useGuid, Name = has ? found.Name : fallback, Installed = has, Kind = kind, IsActive = IsActive(useGuid),
            });
        }
        foreach (var p in installed.Where(p => !used.Contains(p.Guid)))
            result.Add(new PowerPlanItem
            {
                Guid = p.Guid, Name = p.Name, Kind = p.Guid == PowerSaver ? "leaf" : "spark", IsActive = IsActive(p.Guid),
            });
        return result;
    }

    /// <summary>Ativa o plano (criando-o antes se for um dos padrões que falta) e confere se o Windows aceitou.</summary>
    public static PowerApplyResult Apply(string guid)
    {
        string? previous = ActiveScheme();
        string target = guid;
        if (!IsInstalled(target))
        {
            // 1) recria o modelo padrão com o mesmo GUID
            string log = PowerCfg($"-duplicatescheme {guid} {guid}");
            if (!IsInstalled(guid))
            {
                // 2) deixa o Windows escolher o GUID do plano novo (a forma mais comum de criar o "Desempenho máximo")
                string log2 = PowerCfg($"-duplicatescheme {guid}");
                var m = GuidRx.Match(log2);
                if (m.Success && IsInstalled(m.Value)) target = m.Value.ToLowerInvariant();
                else return new(false, "Este plano não pôde ser criado neste PC. " + Reason(log2.Trim().Length > 0 ? log2 : log), previous);
            }
        }
        PowerCfg($"/setactive {target}");
        bool ok = string.Equals(ActiveScheme(), target, StringComparison.OrdinalIgnoreCase);
        return ok
            ? new(true, "", previous, target)
            : new(false, "O Windows não aceitou a mudança (pode exigir administrador).", previous);
    }

    private static string Reason(string output)
    {
        string t = Regex.Replace(output, @"\s+", " ").Trim();
        if (t.Length == 0) return "O Windows não deu nenhuma explicação (alguns notebooks e versões do Windows não oferecem esse plano).";
        if (t.Length > 220) t = t[..220] + "…";
        return "O Windows respondeu: " + t;
    }

    public static void SetActive(string guid) => PowerCfg($"/setactive {guid}");

    public static string? ActiveScheme()
    {
        var m = GuidRx.Match(PowerCfg("/getactivescheme"));
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    private static bool IsInstalled(string guid) => PowerCfg("/list").Contains(guid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Roda o powercfg e devolve o que ele escreveu (saída normal + mensagens de erro).</summary>
    private static string PowerCfg(string args)
    {
        var enc = OemEncoding();
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = enc, StandardErrorEncoding = enc,
        };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output + err.GetAwaiter().GetResult();
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
