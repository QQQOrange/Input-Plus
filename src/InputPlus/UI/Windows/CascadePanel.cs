using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace InputPlus.UI.Windows;

/// <summary>
/// 多级菜单的"自由拼接"面板（v1.6.7）。
///
/// ── 为什么不用 StackPanel / Grid ────────────────────────────────
/// 每一级卡片都要**纵向对齐到父级的某一行**，行号不同、卡片高度不同，
/// 横向还要紧贴（0 间隙）。StackPanel 只能顶对齐，Grid 的行高会被最高的那块撑开；
/// 只有"我自己给每个孩子一个位置"能同时满足这两条。
///
/// 位置由窗口算好（<see cref="SetRect"/>），本面板只负责两件事：
///   · Measure：用孩子自己的 DesiredSize 加上各自的位置，算出整块的包围盒；
///   · Arrange：按位置摆 —— 于是窗口的 SizeToContent 仍然是"内容多大窗口多大"。
///
/// 位置是**含 Margin 的外框**：卡片自带 14 DIP 的投影留白，
/// 想让它"看起来贴在上一级右沿"就得把留白一起算进去，这一步在窗口里做。
/// </summary>
public sealed class CascadePanel : Panel
{
    private readonly Dictionary<UIElement, Rect> _rects = new();

    /// <summary>给一个孩子指定外框（含 Margin 的坐标系）。</summary>
    internal void SetRect(UIElement child, Rect rect)
    {
        if (_rects.TryGetValue(child, out Rect old) && old == rect) return;
        _rects[child] = rect;
        InvalidateMeasure();
    }

    internal Rect GetRect(UIElement child) =>
        _rects.TryGetValue(child, out Rect r) ? r : new Rect(0, 0, 0, 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        Prune();

        double width = 0, height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Rect rect = GetRect(child);
            width = Math.Max(width, rect.X + child.DesiredSize.Width);
            height = Math.Max(height, rect.Y + child.DesiredSize.Height);
        }

        return new Size(Math.Ceiling(width), Math.Ceiling(height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (child == null) continue;
            Rect rect = GetRect(child);
            /* 尺寸以 DesiredSize 为准：卡片宽度是钉死的 160，
             * 高度由内容决定；rect 只负责位置。 */
            double w = Math.Max(rect.Width, child.DesiredSize.Width);
            double h = Math.Max(rect.Height, child.DesiredSize.Height);
            child.Arrange(new Rect(rect.X, rect.Y, w, h));
        }
        return finalSize;
    }

    /// <summary>卡片被移除后把它的位置记录也删掉，免得字典无限长大。</summary>
    private void Prune()
    {
        if (_rects.Count <= InternalChildren.Count) return;
        var live = new HashSet<UIElement>();
        foreach (UIElement child in InternalChildren) if (child != null) live.Add(child);
        var stale = new List<UIElement>();
        foreach (UIElement key in _rects.Keys) if (!live.Contains(key)) stale.Add(key);
        foreach (UIElement key in stale) _rects.Remove(key);
    }
}
