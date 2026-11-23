using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using InputPlus.Core.Qbar.Content;

namespace InputPlus.UI.Windows;

/// <summary>
/// 右下角的**常驻**进度条：正文索引建立期间一直显示，抽完才消失。
///
/// 为什么不用 <see cref="ToastWindow"/>：那个是"1.8 秒自动消失"的轻提示，
/// 而建索引可能跑几分钟 —— 用户要的是"看得见还在动"，不是一条一闪而过的消息。
/// 两套窗口各管一件事：Toast 管事件，这个管进度。
///
/// 与 Toast 一样：不抢焦点（ShowActivated=false）、点击穿透（IsHitTestVisible=false），
/// 压在右下角不挡任务栏。
/// </summary>
internal static class IndexProgressToast
{
    private static Window? _window;
    private static TextBlock? _title;
    private static TextBlock? _detail;
    private static ProgressBar? _bar;
    private static bool _visible;

    internal static void Show(ContentIndex.ProgressInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Show(info)));
            return;
        }

        EnsureWindow();
        if (_window == null) return;

        _title!.Text = info.Phase switch
        {
            "扫描" => "正在扫描文件…",
            "抽取" => "正在建立正文索引",
            _ => "正文索引",
        };
        _detail!.Text = ContentIndex.Status
                        + (info.Visited > 0 ? " · 已扫描 " + ContentIndex.FormatCount(info.Visited) + " 项" : "");

        if (info.Percent < 0)
        {
            _bar!.IsIndeterminate = true;
        }
        else
        {
            _bar!.IsIndeterminate = false;
            _bar.Value = Math.Clamp(info.Percent, 0, 100);
        }

        if (!_visible)
        {
            _window.Show();
            _visible = true;
        }
        Position();
    }

    internal static void Hide()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || _window == null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(Hide));
            return;
        }

        _window.Hide();
        _visible = false;
    }

    private static void EnsureWindow()
    {
        if (_window != null) return;

        var card = new Border
        {
            Background = Res("Brush.OverlayCardAlt", Color.FromArgb(0xF2, 0x16, 0x1A, 0x23)),
            CornerRadius = new CornerRadius(InputPlus.UI.ThemeMetrics.RadiusCard),
            Padding = new Thickness(16, 11, 16, 11),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.35 },
        };

        var panel = new StackPanel { Width = 300 };
        _title = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("Brush.TextPrimary", Colors.White),
            TextWrapping = TextWrapping.Wrap,
        };
        _detail = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.82,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Res("Brush.TextSecondary", Colors.White),
            TextWrapping = TextWrapping.Wrap,
        };
        _bar = new ProgressBar
        {
            Height = 4,
            Margin = new Thickness(0, 9, 0, 0),
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
        };

        panel.Children.Add(_title);
        panel.Children.Add(_detail);
        panel.Children.Add(_bar);
        card.Child = panel;

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            IsHitTestVisible = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = card,
        };
        _window.SourceInitialized += (_, _) => Core.Windows.OverlayWindowStyle.Apply(_window, nameof(IndexProgressToast));
        _window.Loaded += (_, _) => Position();
    }

    private static void Position()
    {
        if (_window == null || !_visible) return;
        _window.UpdateLayout();
        var fg = Core.Interop.NativeMethods.GetForegroundWindow();
        if (Core.Windows.ScreenPlacement.Place(_window, Core.Windows.ScreenPlacement.Anchor.BottomRight,
                marginX: 10, marginY: 10, target: fg))
            return;

        var wa = SystemParameters.WorkArea;
        _window.Left = wa.Right - _window.ActualWidth - 10;
        _window.Top = wa.Bottom - _window.ActualHeight - 10;
    }

    private static Brush Res(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
