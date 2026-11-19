using System.Windows;
using System.Windows.Input;
using InputPlus.Modules.HoldDrag;
using Loc = InputPlus.Core.Localization.Loc;

namespace InputPlus.UI.Windows;

/// <summary>
/// 单键捕获窗：按哪个键就返回哪个键，<b>不做组合键</b>。
///
/// AltDrag 的触发键就是一组虚拟键码（ini 里 Hotkeys=A4 A5），单个键就够；所以这里
/// 和「按键映射」那套 KeyCaptureWindow（组合键 + 前缀键）分开写，换来两件事：
/// · Enter / 空格 / Tab 这些在组合键捕获里会被当成"确认/取消"的键，在这里能正常选；
/// · Esc 也能被选成触发键（要取消就点按钮，键盘上不留取消键，避免和触发键抢）。
/// </summary>
public partial class TriggerKeyWindow : Window
{
    /// <summary>选中的键（KeysUtil 的 id 形式，如 K:0x0D / K:LShift）；取消时为 null。</summary>
    public string? SelectedKey { get; private set; }

    public TriggerKeyWindow(string? current)
    {
        InitializeComponent();
        CurrentText.Text = string.IsNullOrEmpty(current)
            ? Loc.T("现在是：未设置。")
            : Loc.T("现在是：") + KeysUtil.DisplayName(current!);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Keyboard.Focus(this);
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        string id = IdOf(e);
        if (id.Length == 0)
        {
            return;
        }
        e.Handled = true;
        Accept(id);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 鼠标侧键也允许当触发键（AltDrag 的 Hotkeys 只能写键盘码，这是本移植的扩展）。
        string? id = KeysUtil.FromMouseButton(e.ChangedButton switch
        {
            System.Windows.Input.MouseButton.XButton1 => System.Windows.Forms.MouseButtons.XButton1,
            System.Windows.Input.MouseButton.XButton2 => System.Windows.Forms.MouseButtons.XButton2,
            _ => System.Windows.Forms.MouseButtons.None,
        });
        if (id == null)
        {
            return;
        }
        e.Handled = true;
        Accept(id);
    }

    private void Accept(string id)
    {
        SelectedKey = id;
        KeyText.Text = KeysUtil.DisplayName(id);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>把 WPF 的按键翻译成 KeysUtil 的 id。返回空串表示这个键不记（纯修饰状态、IME 合成等）。</summary>
    private static string IdOf(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftShift:
            case Key.RightShift:
            case Key.LeftCtrl:
            case Key.RightCtrl:
            case Key.LeftAlt:
            case Key.RightAlt:
            case Key.LWin:
            case Key.RWin:
                return KeysUtil.FromWinFormsKey((System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key));
            case Key.ImeProcessed:
            case Key.DeadCharProcessed:
            case Key.None:
                return string.Empty;
            default: break;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
        {
            return string.Empty;
        }
        return "K:0x" + vk.ToString("X2");
    }
}
