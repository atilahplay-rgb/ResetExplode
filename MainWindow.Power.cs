using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ResetExplode;

/// <summary>Seletor de plano de energia da aba RESET OTIMIZA, com uma mini animação em cada botão ao ser escolhido.</summary>
public partial class MainWindow
{
    private bool _powerBusy;
    private List<PowerPlanItem> _powerItems = new();

    private async void RefreshPowerPlans()
    {
        try
        {
            _powerItems = await Task.Run(PowerPlans.List);
            PowerPlanList.ItemsSource = _powerItems;
        }
        catch (Exception ex) { PowerPlanStatus.Text = $"Não foi possível ler os planos: {ex.Message}"; }
    }

    private async void PowerPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: PowerPlanItem plan } chip || _powerBusy) return;
        PlayPlanAnimation(chip, plan.Kind);   // na hora, sem esperar o Windows
        if (plan.IsActive) return;            // já é o plano ativo

        _powerBusy = true;
        PowerPlanList.IsHitTestVisible = false;
        PowerPlanStatus.Text = "";
        bool ok = false;
        try
        {
            var r = await Task.Run(() => PowerPlans.Apply(plan.Guid));
            ok = r.Ok;
            if (r.Ok)
            {
                // guarda o plano de antes só na primeira alteração, para "Restaurar originais"
                if (!_settings.TweakBackup.ContainsKey("power")) _settings.TweakBackup["power"] = r.Previous;
                _settings.Save();
                foreach (var p in _powerItems) p.IsActive = p.Guid == plan.Guid; // atualiza no lugar, sem recriar os botões
            }
            else PowerPlanStatus.Text = r.Message; // só erros aparecem
        }
        catch (Exception ex) { PowerPlanStatus.Text = $"Falhou: {ex.Message}"; }
        finally { _powerBusy = false; PowerPlanList.IsHitTestVisible = true; }
        if (!ok) RefreshPowerPlans(); // volta os botões ao estado real
    }

    // ---------------- mini animações ----------------

    private static readonly IEasingFunction EaseIn = new CubicEase { EasingMode = EasingMode.EaseIn };
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static DoubleAnimationUsingKeyFrames Seq(params (int Ms, double V, IEasingFunction? E)[] frames)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (ms, v, ease) in frames)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), ease));
        return a;
    }

    private static void PlayPlanAnimation(RadioButton chip, string kind)
    {
        if (chip.Content is not StackPanel { Children.Count: > 0 } sp || sp.Children[0] is not Grid icon) return;
        if (icon.RenderTransform is not TransformGroup { Children.Count: 3 } g) return;
        var scale = (ScaleTransform)g.Children[0];
        var rot = (RotateTransform)g.Children[1];
        var move = (TranslateTransform)g.Children[2];

        void Scale(DoubleAnimationUsingKeyFrames a)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        switch (kind)
        {
            case "rocket": // treme, sobe e some; volta de baixo
                move.BeginAnimation(TranslateTransform.YProperty, Seq((0, 0, null), (120, 2, null), (550, -26, EaseIn), (551, 14, null), (1000, 0, EaseOut)));
                icon.BeginAnimation(UIElement.OpacityProperty, Seq((0, 1, null), (350, 1, null), (550, 0, null), (700, 0, null), (1000, 1, null)));
                rot.BeginAnimation(RotateTransform.AngleProperty, Seq((0, 0, null), (60, -8, null), (120, 8, null), (180, -6, null), (240, 5, null), (300, 0, null)));
                break;
            case "bolt": // estoura e pisca
                Scale(Seq((0, 1, null), (120, 1.6, EaseOut), (300, 0.9, null), (450, 1.15, null), (600, 1, null)));
                icon.BeginAnimation(UIElement.OpacityProperty, Seq((0, 1, null), (80, 0.3, null), (160, 1, null), (240, 0.4, null), (320, 1, null)));
                break;
            case "balance": // balança oscilando até se equilibrar
                rot.BeginAnimation(RotateTransform.AngleProperty, Seq((0, 0, null), (150, -22, EaseOut), (350, 18, null), (520, -12, null), (680, 7, null), (800, -3, null), (900, 0, null)));
                break;
            case "leaf": // folha balançando e caindo de leve
                rot.BeginAnimation(RotateTransform.AngleProperty, Seq((0, 0, null), (200, 30, null), (450, -20, null), (700, 10, null), (900, 0, null)));
                move.BeginAnimation(TranslateTransform.YProperty, Seq((0, 0, null), (450, 5, null), (900, 0, null)));
                break;
            default: // brilho girando
                rot.BeginAnimation(RotateTransform.AngleProperty, Seq((0, 0, null), (500, 180, EaseOut)));
                Scale(Seq((0, 1, null), (180, 1.7, EaseOut), (500, 1, null)));
                break;
        }
    }
}
