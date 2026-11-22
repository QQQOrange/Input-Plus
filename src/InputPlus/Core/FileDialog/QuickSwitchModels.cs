using System.Collections.Generic;

namespace InputPlus.Core.FileDialog;

/// <summary>QuickSwitch 右侧菜单/浮层发回给 Service 的动作。</summary>
public enum QuickSwitchAction
{
    OpenSettings,
    RevealExplorerFolder,
    HideSession,
    HideProcess,
    HideGlobal,
    CloseBar,
    AddFavorite,
    ClearHistory,
    ShowHideMenu,
}

/// <summary>浮层里的一行（收藏/历史/菜单共用）。</summary>
public sealed class QuickSwitchRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = "\uE8B7";
    public string? Path { get; init; }

    /// <summary>取图标用的真实文件/目录路径（Path 可能是导航目标父目录）。</summary>
    public string? IconPath { get; init; }

    public QuickSwitchAction? Action { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Expandable { get; init; }
    public System.Windows.Visibility SubtitleVisibility
        => string.IsNullOrEmpty(Subtitle) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public double Opacity => Enabled ? 1.0 : 0.55;

    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? Icon => _icon;
    public System.Windows.Visibility IconVisibility => _icon == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public System.Windows.Visibility GlyphVisibility => _icon == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public void SetIcon(System.Windows.Media.ImageSource? icon)
    {
        _icon = icon;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IconVisibility)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GlyphVisibility)));
    }
}
