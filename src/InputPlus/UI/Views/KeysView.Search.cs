using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using InputPlus.Core.Input;

namespace InputPlus.UI.Views;

/// <summary>
/// 「编辑绑定」标题行里的搜索框：在当前层已绑定的键里找一条，点中就跳过去。
///
/// 为什么要有它：这个位置原来放的是一个只写着 "Q" 的键帽 —— 12px 的字母 Q
/// 远看像个放大镜，白底圆角又像个空搜索框，用户直接把它当成了"比正常搜索框
/// 大一号的输入框"。与其解释那不是搜索框，不如在这里真的放一个：
/// 找键（CapsLock / F5）、找功能（微信 / 复制 / 打开 B 站）都能用，
/// 选中就跳到那条绑定的编辑面板，省得在 108 键键盘上一个个找。
///
/// 只搜当前层：切了层还列出别的层的键，点进去会改错东西。
///
/// 已绑定的键之后，还会列**全部功能**里匹配的项：分类行原来并排放着第二个
/// 搜索框专门干这件事，两个框搜同一批词却给出不同结果，用户不知道该用哪个。
/// 合成一个之后，找"绑在哪个键上"和找"要绑什么功能"是同一个入口。
/// </summary>
public partial class KeysView
{
    /// <summary>搜索结果最多几条：再多就得滚，回车取第一条也会变得没把握。</summary>
    private const int MaxHits = 8;

    /// <summary>搜索框当前列出的候选；回车取第一条。</summary>
    private List<BindingSearchHit> _bindingHits = new();

    /// <summary>
    /// 一条候选。<see cref="ActionId"/> 非空表示这是"功能"而不是"已绑定的键"：
    /// 前者点中直接应用到当前键，后者点中跳到那条绑定。
    /// </summary>
    private sealed record BindingSearchHit(string Combo, string Key, string Description, string? ActionId = null)
    {
        public bool IsAction => ActionId != null;
    }

    private void OnBindingSearchChanged(object sender, TextChangedEventArgs e) => RefreshBindingSearch();

    private void OnBindingSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseBindingSearch(clear: true);
                e.Handled = true;
                break;

            case Key.Enter:
                /* 回车 = 选第一条。
                   下拉已经按"键名前缀命中优先"排过序，所以敲 cap 回车就是 CapsLock。 */
                if (_bindingHits.Count > 0)
                {
                    ActivateHit(_bindingHits[0]);
                    e.Handled = true;
                }
                break;

            case Key.Down:
                if (_bindingHits.Count > 0 && BindingSearchPopup.IsOpen)
                {
                    /* 把焦点交给第一颗候选按钮：之后 Tab / 方向键在候选之间走，
                       回车由按钮自己触发，跟鼠标点一样。 */
                    BindingSearchList.UpdateLayout();
                    if (BindingSearchList.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter cp
                        && FindFocusable(cp) is { } target)
                    {
                        target.Focus();
                        e.Handled = true;
                    }
                }
                break;
        }
    }

    private void OnBindingSearchPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BindingSearchHit hit })
            ActivateHit(hit);
    }

    private void ActivateHit(BindingSearchHit hit)
    {
        if (hit.IsAction) ApplyActionHit(hit.ActionId!);
        else JumpToBinding(hit.Combo);
    }

    /// <summary>
    /// 把一条功能应用到当前键。
    ///
    /// 顺序与点搜索结果时一致：先切分类（四个下拉都同步），再切编辑模式，
    /// 最后才填功能 —— 反过来会先按旧分类铺面板，留下"文本框空着但动作已换"的中间态。
    /// </summary>
    private void ApplyActionHit(string actionId)
    {
        if (_host == null) return;

        _suppressEvents = true;
        try
        {
            var info = _host.Actions.Get(actionId);
            if (info != null)
            {
                foreach (var box in new[] { CategoryBox, CategoryBoxWinBind, CategoryBoxWeb, CategoryBoxType })
                    SelectTag(box, info.Category);
            }

            var mode = ModeOf(info?.Category);
            ApplyEditorMode(mode);

            if (mode == EditorMode.Normal)
            {
                BuildActionBox();
                foreach (ComboBoxItem it in ActionBox.Items)
                {
                    if (it.Tag is string tag && tag.Equals(actionId, StringComparison.OrdinalIgnoreCase))
                    {
                        ActionBox.SelectedItem = it;
                        break;
                    }
                }
                ApplySelectedAction();
            }
            else
            {
                EnsureActionForCategory(actionId);
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        CloseBindingSearch(clear: true);
        ScrollEditorIntoView();
    }

    /// <summary>按输入内容重建候选列表；空输入直接收起下拉。</summary>
    private void RefreshBindingSearch()
    {
        string query = BindingSearchBox.Text.Trim();

        if (query.Length == 0)
        {
            CloseBindingSearch(clear: false);
            return;
        }

        var hits = new List<BindingSearchHit>();

        foreach (var binding in CurrentLayerKeys)
        {
            if (binding == null || !binding.IsBound) continue;
            if (string.IsNullOrWhiteSpace(binding.Combo)) continue;

            string keyText = DescribeCombo(binding.Combo);

            string description;
            try { description = DescribeActionOf(binding); }
            catch { description = binding.Action ?? ""; }

            /* 一起搜：键名、内部 combo id、功能说明、备注、参数（网址 / 程序路径）。
               用户记得住"微信"却记不住绑在哪个键上，就是靠这几样找回来的。 */
            string haystack = keyText + "\n" + binding.Combo + "\n" + description
                              + "\n" + (binding.Note ?? "") + "\n" + (binding.Arg ?? "");

            if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

            hits.Add(new BindingSearchHit(binding.Combo, keyText, description));
        }

        /* 键名以输入开头的排前面（敲 cap 先给 CapsLock，而不是"某条备注里带 cap"的），
           同组内按键名排，保证每次输入顺序稳定。 */
        _bindingHits = hits
            .OrderByDescending(h => h.Key.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxHits)
            .ToList();

        /* 已绑定的键排在前面；还有空位就把全部功能里匹配的补在后面。
           功能那几条没有 combo（点中不是跳转，而是应用到当前键）。 */
        if (_bindingHits.Count < MaxHits && _host != null)
        {
            var more = _host.Actions.Items
                .Where(i => !i.Hidden && MatchesAction(i, query))
                .OrderBy(i => i.Category)
                .ThenBy(i => i.Title)
                .Take(MaxHits - _bindingHits.Count)
                .Select(i => new BindingSearchHit("", i.Title,
                    Loc.F("功能 · {0} · 回车应用到当前键", i.DisplayCategory), i.Id));
            _bindingHits.AddRange(more);
        }

        BindingSearchList.ItemsSource = _bindingHits;
        BindingSearchPopup.IsOpen = _bindingHits.Count > 0;
    }

    /// <summary>收起下拉。clear=true 时连输入一起清掉（跳转、Esc 之后）。</summary>
    /// <summary>
    /// 搜索结果弹层的自定义放置：优先"贴搜索框左边缘、往下 4px"，
    /// 下方空间不够就翻到上方（左边缘对齐）；窗口太靠右时再退化成右对齐。
    /// 坐标是相对 PlacementTarget 的 DIP，WPF 会按屏幕工作区从前往后挑第一个放得下的。
    /// </summary>
    private CustomPopupPlacement[] PlaceBindingSearchPopup(Size popup, Size target, Point offset)
    {
        const double gap = 4;

        return new[]
        {
            new CustomPopupPlacement(new Point(0, target.Height + gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0, -popup.Height - gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + gap), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - gap), PopupPrimaryAxis.Horizontal),
        };
    }

    private void CloseBindingSearch(bool clear)
    {
        BindingSearchPopup.IsOpen = false;
        _bindingHits = new List<BindingSearchHit>();
        BindingSearchList.ItemsSource = null;

        if (clear && BindingSearchBox.Text.Length > 0) BindingSearchBox.Text = "";
    }

    /* ── 渲染 / 自检钩子 ────────────────────────────────────────────────
     * 结果弹层是独立的顶层视觉，不进主窗口截图 —— 也就是说"弹层到底弹在哪儿"
     * 这件事从来没被任何截图验证过。下面几个方法让 --render 能把弹层单独拍下来，
     * 并且把它的位置量成一行文本，位置错了才看得出来。
     */

    /// <summary>渲染用：填上关键词、打开弹层，返回弹层里的内容元素（没有结果时返回 null）。</summary>
    internal FrameworkElement? OpenBindingSearchForRender(string query)
    {
        BindingSearchBox.Text = query ?? "";
        RefreshBindingSearch();
        BindingSearchPopup.UpdateLayout();
        return BindingSearchPopup.IsOpen ? BindingSearchPopup.Child as FrameworkElement : null;
    }

    /// <summary>
    /// 渲染用：量出弹层相对搜索框的偏移。
    ///
    /// 判读方法：dx 应该接近 0（左边缘对齐搜索框），dy 应该接近"框高 + 4"；
    /// 下面放不下时 dy 会变成负值（翻到框上方）。dx/dy 出现几万这种数字，
    /// 说明 PlacementTarget 没生效、弹层飞到了屏幕角落。
    /// </summary>
    internal string DescribeBindingSearchPlacement()
    {
        if (!BindingSearchPopup.IsOpen) return "弹层未打开";

        var target = BindingSearchPopup.PlacementTarget ?? BindingSearchBox;
        Point targetOrigin = target.PointToScreen(new Point(0, 0));
        double scale = VisualTreeHelper.GetDpi(BindingSearchBox).DpiScaleX;
        string where = "弹层内容没量到";
        if (BindingSearchPopup.Child is FrameworkElement child && child.IsVisible)
        {
            Point childOrigin = child.PointToScreen(new Point(0, 0));
            where = $"dx={(childOrigin.X - targetOrigin.X) / scale:0.#} "
                  + $"dy={(childOrigin.Y - targetOrigin.Y) / scale:0.#} "
                  + $"弹层={child.RenderSize.Width:0.#}x{child.RenderSize.Height:0.#}";
        }

        Point childAbs = BindingSearchPopup.Child is FrameworkElement c2 && c2.IsVisible
            ? c2.PointToScreen(new Point(0, 0))
            : new Point(double.NaN, double.NaN);
        return $"PlacementTarget={(BindingSearchPopup.PlacementTarget == null ? "null（绑定没生效）" : "ok")} "
             + $"框={target.RenderSize.Width:0.#}x{target.RenderSize.Height:0.#} "
             + $"{where} Placement={BindingSearchPopup.Placement} 命中={_bindingHits.Count}\n"
             + $"框屏幕=({targetOrigin.X:0},{targetOrigin.Y:0}) 弹层屏幕=({childAbs.X:0},{childAbs.Y:0}) "
             + $"DPI={scale:0.##} 工作区={SystemParameters.WorkArea}";
    }

    /// <summary>
    /// 渲染用：量"光标到底画在哪儿"。
    ///
    /// 用户报的是"闪动的光标落在占位文字后面、没跟在放大镜后面"，
    /// 但截图里没有光标（RenderTargetBitmap 不画 caret），只能算：
    /// GetRectFromCharacterIndex(0) 就是第 0 个字符之前那个插入点的位置 = 空框时光标的位置。
    /// 再把它和放大镜图标的右边缘比一比，就知道有没有真的排在图标后面。
    /// </summary>
    internal string DescribeBindingSearchField()
    {
        Rect caret = BindingSearchBox.GetRectFromCharacterIndex(0, false);
        string caretText = caret.IsEmpty ? "caretX=量不到" : $"caretX={caret.Left:0.#}";

        string iconText = "图标=没找到";
        if (BindingSearchBox.Parent is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is TextBlock tb && tb.Text == "\uE721")
                {
                    Point origin = tb.TranslatePoint(new Point(0, 0), BindingSearchBox);
                    iconText = $"图标x={origin.X:0.#}..{origin.X + tb.ActualWidth:0.#}";
                    break;
                }
            }
        }

        var host = BindingSearchBox.Template?.FindName("PART_ContentHost", BindingSearchBox) as FrameworkElement;
        string hostText = host == null
            ? "content host=没找到"
            : $"host={host.GetType().Name} pad={host.Margin} "
              + $"actual={host.ActualWidth:0.#} x={(host.TranslatePoint(new Point(0, 0), BindingSearchBox)).X:0.#}";
        string viewText = "view=没量到";
        if (host is System.Windows.Controls.ScrollViewer sv && sv.Content is FrameworkElement view)
        {
            Point vp = view.TranslatePoint(new Point(0, 0), BindingSearchBox);
            viewText = $"view={view.GetType().Name} margin={view.Margin} x={vp.X:0.#} w={view.ActualWidth:0.#} "
                     + $"svpad={sv.Padding} hoff={sv.HorizontalOffset:0.#} "
                     + $"extent={sv.ExtentWidth:0.#} viewport={sv.ViewportWidth:0.#} "
                     + $"boxHa={BindingSearchBox.HorizontalContentAlignment} "
                     + $"align={BindingSearchBox.TextAlignment}";
        }
        string probes = "";
        q(0); q(1); q(2);
        void q(int i)
        {
            Rect rc = BindingSearchBox.GetRectFromCharacterIndex(Math.Min(i, BindingSearchBox.Text.Length), false);
            probes += $" i{i}={(rc.IsEmpty ? "空" : rc.Left.ToString("0.#"))}";
        }
        return $"Padding.Left={BindingSearchBox.Padding.Left:0.#} 文本长度={BindingSearchBox.Text.Length} {iconText} {caretText}{probes} {hostText} {viewText}";
    }

    /// <summary>渲染用：收尾（清空输入、收起弹层）。</summary>
    internal void CloseBindingSearchForRender() => CloseBindingSearch(clear: true);

    /// <summary>跳到某个键：先切到它所在的子层，再选中它，最后把编辑区滚进视野。</summary>
    private void JumpToBinding(string combo)
    {
        CloseBindingSearch(clear: true);

        /* 不能把整个 combo 直接丢给 SelectKey：SelectKey 收的是"物理键名"，
           它会用当前子层再拼一次 combo。caps_ctrl_k 传进去会被拼成
           caps_ctrl_ctrl_k，选中一条不存在的东西。 */
        if (ParseComboAny(combo) is not { } parsed) return;

        SelectSubLayer(parsed.SubLayer);
        SelectKey(parsed.Key);
        ScrollEditorIntoView();
    }

    /// <summary>
    /// 把内部 combo id 拆成「修饰键 / 子层键 / 物理键」。
    ///
    /// 与 <see cref="ComboFor"/> 互逆：caps_ctrl_shift_k → Ctrl+Shift 的子层 + k；
    /// caps_tab_q → Tab 子层 + q；press_caps → 直接按 capslock。
    /// 键名里不含下划线，所以"最后一段是键名、前面全是前缀"这个切法是无损的。
    /// </summary>
    private static (KeyModifiers Mods, string SubLayer, string Key)? ParseComboAny(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        if (combo.Equals("press_caps", StringComparison.OrdinalIgnoreCase))
            return (KeyModifiers.None, "", "capslock");
        if (!combo.StartsWith("caps_", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = combo["caps_".Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var mods = KeyModifiers.None;
        int i = 0;
        for (; i < parts.Length - 1; i++)          // 最后一段一定是键名，不能当修饰键吃
        {
            var bit = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "lctrl" or "rctrl" => KeyModifiers.Ctrl,
                "shift" or "lshift" or "rshift" => KeyModifiers.Shift,
                "lalt" or "ralt" or "alt" => KeyModifiers.Alt,
                "win" or "lwin" or "rwin" => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
            if (bit == KeyModifiers.None) break;
            mods |= bit;
        }

        string key = parts[^1];
        string sub = string.Join('_', parts[i..^1]);

        /* 没有显式子层键、但有修饰键：修饰键本身就是子层（Ctrl / Ctrl+Shift）。
           有显式子层键时（tab / capslock）修饰位由它表达，LayerKeyOf 的结果会重复。 */
        if (sub.Length == 0 && mods != KeyModifiers.None) sub = LayerKeyOf(mods);

        return key.Length == 0 ? null : (mods, sub, key);
    }

    /// <summary>combo id → 给人看的写法：Ctrl + K / Tab + Q。</summary>
    private static string DescribeCombo(string combo)
    {
        if (ParseComboAny(combo) is not { } parsed) return combo;

        var parts = new List<string>();
        if (parsed.Mods.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (parsed.Mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (parsed.Mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (parsed.Mods.HasFlag(KeyModifiers.Win)) parts.Add("Win");

        /* 非修饰键子层（Tab / CapsLock）单独写出来，否则 caps_tab_q 只显示 Q，
           用户会以为绑在直接按的 Q 上。 */
        if (parsed.SubLayer.Length > 0
            && !parsed.SubLayer.Equals(LayerKeyOf(parsed.Mods), StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(Core.Input.KeyName.Display(parsed.SubLayer));
        }

        parts.Add(Core.Input.KeyName.Display(parsed.Key));
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// 把编辑区滚到视野里。
    ///
    /// 算法与 <see cref="ScrollToEditorForRender"/> 一致：取 EditorCard 相对
    /// PageScroll 的位置再换算成滚动偏移。键盘在编辑区上方，选中键之后
    /// 视口往往还停在键盘上，不滚一下用户看不见改的是哪条。
    /// </summary>
    private void ScrollEditorIntoView()
    {
        UpdateLayout();
        try
        {
            double top = EditorCard.TransformToAncestor(PageScroll).Transform(new Point(0, 0)).Y
                         + PageScroll.VerticalOffset;
            PageScroll.ScrollToVerticalOffset(Math.Max(0, top - 12));
        }
        catch
        {
            /* 预期内：还没参与布局时滚不动，忽略即可 */
        }
    }

    /// <summary>在模板生成的可视化树里找第一个能接键盘焦点的元素。</summary>
    private static UIElement? FindFocusable(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Focusable: true } element) return element;
            if (FindFocusable(child) is { } nested) return nested;
        }
        return null;
    }
}
