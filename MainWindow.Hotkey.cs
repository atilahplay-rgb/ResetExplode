using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ResetExplode;

/// <summary>
/// Atalho global que liga/desliga o overlay (aba Overlay): de 1 a 3 teclas, ou seja, até 2 modificadores
/// (Ctrl, Shift, Alt, Win) + 1 tecla. Uma tecla sozinha só é aceita se não atrapalhar a digitação (F1–F24, Pause, Scroll Lock, Insert).
/// </summary>
public partial class MainWindow
{
    private const uint HkNoRepeat = 0x4000;       // MOD_NOREPEAT: não repete ao segurar
    private const uint HkDefaultMods = 0x0002 | 0x0004; // Ctrl + Shift
    private const uint HkDefaultVk = 0x4F;        // O

    private bool _hkCapturing;

    private IntPtr HotkeyHwnd => new WindowInteropHelper(this).Handle;

    /// <summary>Chamado ao abrir o app: registra o atalho salvo (ou o padrão, se o salvo for inválido ou estiver ocupado).</summary>
    private void RegisterSavedOverlayHotkey()
    {
        uint mods = _settings.OverlayHotkeyMods, vk = _settings.OverlayHotkeyVk;
        if (vk == 0 || BitCount(mods) > 2 || (mods & ~0xFu) != 0) { mods = HkDefaultMods; vk = HkDefaultVk; }

        if (!RegisterHotKey(HotkeyHwnd, HotkeyId, mods | HkNoRepeat, vk))
        {
            SetHotkeyMsg($"O atalho {HotkeyName(mods, vk)} está em uso por outro programa. Escolha outra combinação.");
        }
        RefreshHotkeyButton();
    }

    private void RefreshHotkeyButton()
    {
        if (_hkCapturing) return;
        BtnHotkey.Content = HotkeyName(_settings.OverlayHotkeyMods, _settings.OverlayHotkeyVk);
    }

    private void SetHotkeyMsg(string text)
    {
        OvHotkeyMsg.Text = text;
        OvHotkeyMsg.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BtnHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_hkCapturing) return;
        _hkCapturing = true;
        BtnHotkey.Content = "Pressione as teclas…";
        SetHotkeyMsg("Pressione a nova combinação (1 a 3 teclas, ex.: F8, Ctrl + O ou Ctrl + Shift + O). Esc cancela.");
        Keyboard.Focus(BtnHotkey);
    }

    private void BtnHotkey_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_hkCapturing) return;
        _hkCapturing = false;
        SetHotkeyMsg("");
        RefreshHotkeyButton();
    }

    private void BtnHotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_hkCapturing) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey
                : e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key;
        if (key == Key.None || key == Key.DeadCharProcessed) return;

        if (key == Key.Escape)
        {
            _hkCapturing = false;
            SetHotkeyMsg("");
            RefreshHotkeyButton();
            return;
        }

        uint mods = (uint)Keyboard.Modifiers; // ModifierKeys usa os mesmos valores do MOD_* do Windows

        // Só modificadores apertados até agora: mostra o que já tem e espera a tecla principal.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            BtnHotkey.Content = mods == 0 ? "Pressione as teclas…" : ModsName(mods) + " + …";
            return;
        }

        if (BitCount(mods) > 2)
        {
            SetHotkeyMsg("Máximo de 3 teclas: até 2 modificadores (Ctrl, Shift, Alt, Win) + 1 tecla. Tente de novo.");
            return;
        }

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        if (mods == 0 && !CanBeAlone(key))
        {
            SetHotkeyMsg("Essa tecla sozinha atrapalharia a digitação no Windows. Use uma tecla F1–F24, Pause, Scroll Lock ou Insert, ou combine com Ctrl, Shift ou Alt.");
            return;
        }
        if ((mods & 1) != 0 && key == Key.F4)
        {
            SetHotkeyMsg("Alt + F4 fecha janelas do Windows. Escolha outra combinação.");
            return;
        }

        _hkCapturing = false;
        ApplyOverlayHotkey(mods, vk);
    }

    private void BtnHotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        _hkCapturing = false;
        ApplyOverlayHotkey(HkDefaultMods, HkDefaultVk);
    }

    /// <summary>Troca o atalho; se o Windows recusar (já em uso), volta para o anterior e avisa.</summary>
    private void ApplyOverlayHotkey(uint mods, uint vk)
    {
        uint oldMods = _settings.OverlayHotkeyMods, oldVk = _settings.OverlayHotkeyVk;
        SetHotkeyMsg("");

        if (mods != oldMods || vk != oldVk)
        {
            var hwnd = HotkeyHwnd;
            UnregisterHotKey(hwnd, HotkeyId);
            if (RegisterHotKey(hwnd, HotkeyId, mods | HkNoRepeat, vk))
            {
                _settings.OverlayHotkeyMods = mods;
                _settings.OverlayHotkeyVk = vk;
                _settings.Save();
            }
            else
            {
                RegisterHotKey(hwnd, HotkeyId, oldMods | HkNoRepeat, oldVk);
                SetHotkeyMsg($"Não deu para usar {HotkeyName(mods, vk)}: já está em uso por outro programa ou reservada pelo Windows. Mantive {HotkeyName(oldMods, oldVk)}.");
            }
        }
        RefreshHotkeyButton();
    }

    private static bool CanBeAlone(Key k) =>
        (k >= Key.F1 && k <= Key.F24) || k == Key.Pause || k == Key.Scroll || k == Key.Insert;

    private static int BitCount(uint v) => System.Numerics.BitOperations.PopCount(v);

    private static string ModsName(uint mods)
    {
        var parts = new List<string>();
        if ((mods & 2) != 0) parts.Add("Ctrl");
        if ((mods & 4) != 0) parts.Add("Shift");
        if ((mods & 1) != 0) parts.Add("Alt");
        if ((mods & 8) != 0) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    private static string HotkeyName(uint mods, uint vk)
    {
        string key = KeyName(KeyInterop.KeyFromVirtualKey((int)vk));
        return mods == 0 ? key : ModsName(mods) + " + " + key;
    }

    private static string KeyName(Key k)
    {
        if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + (int)(k - Key.NumPad0);
        return k switch
        {
            Key.Return => "Enter",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            Key.Back => "Backspace",
            Key.Capital => "Caps Lock",
            Key.Scroll => "Scroll Lock",
            Key.Snapshot => "Print Screen",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            _ => k.ToString(),
        };
    }
}
