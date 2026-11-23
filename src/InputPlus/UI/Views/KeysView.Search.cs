using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    /// 弹层因为"搜索框还没排好版"被推迟了几次。
    ///
    /// 为什么要计数而不是无限重试：推迟是靠 Dispatcher.BeginInvoke 再排一帧，
    /// 万一搜索框始终没进视觉树（宿主被折叠、窗口关掉了），无上限地排下去
    /// 就是一条自我唤醒的死循环，UI 线程会被它占满。
    /// </summary>
    private int _bindingPopupRetries;

    private const int MaxPopupRetries = 3;

    /// <summary>
    /// 上一次真正弹开时那批候选的指纹（条数 + 每条的组合键与标题）。
    ///
    /// 用来判断"内容变了没有"：Popup 只在**打开那一刻**放一次位置，
    /// 之后条目从 8 条缩到 2 条，它不会自己挪回搜索框下面，
    /// 被夹到屏幕边的位置也可能随内容变高而重新出界。
    /// </summary>
    private string _bindingPopupSignature = "";

    /// <summary>
    /// 一条候选。<see cref="ActionId"/> 非空表示这是"功能"而不是"已绑定的键"：
    /// 前者点中直接应用到当前键，后者点中跳到那条绑定。
    /// </summary>
    private sealed record BindingSearchHit(string Combo, string Key, string Description, string? ActionId = null)
    {
        public bool IsAction => ActionId != null;
    }

    private void OnBindingSearchChanged(object sender, RoutedEventArgs e)
    {
        /* 用户改了关键词（或换了分类）：推迟预算重新给满（上一次那几帧的失败跟这次输入无关）。 */
        _bindingPopupRetries = 0;
        RefreshBindingSearch();
    }

    /// <summary>搜索框里按回车 = 选第一条候选（下拉已按"键名前缀命中优先"排过序）。</summary>
    private void OnBindingSearchSubmitted(object sender, RoutedEventArgs e)
    {
        if (_bindingHits.Count > 0) ActivateHit(_bindingHits[0]);
    }

    /// <summary>搜索框里按 Esc 且框已空：收起候选弹层。</summary>
    private void OnBindingSearchCancelled(object sender, RoutedEventArgs e)
        => CloseBindingSearch(clear: false);

    /// <summary>
    /// 只负责「↓ 把焦点送进候选列表」这一件事。
    ///
    /// Esc / 回车 由统一搜索框自己发事件（SearchCancelled / SearchSubmitted）——
    /// 那两个键的语义在控件里已经统一了（先清空、再退出；回车提交），
    /// 页面这边再拦一次只会和控件打架（比如 Esc 只关弹层、框里的字却不清）。
    /// </summary>
    private void OnBindingSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down) return;
        if (_bindingHits.Count == 0 || !BindingSearchPopup.IsOpen) return;

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

    /// <summary>
    /// 这条绑定属不属于搜索框里选中的分类。
    ///
    /// 分类身份用动作表的 <see cref="InputPlus.Core.Actions.ActionInfo.Category"/> 原文
    /// （和「分类」下拉的 Tag 完全同一个字符串），所以两边不会各说各话。
    /// 认不出动作的老绑定（动作已被删掉）只在「全部」下出现 —— 否则用户选了「窗口」
    /// 就再也搜不到那条，只能靠翻键盘找，比不支持分类还难用。
    /// </summary>
    private bool BindingInCategory(InputPlus.Core.Settings.KeyBinding? binding, string category)
    {
        if (category.Length == 0) return true;
        if (binding == null) return false;

        string? id = binding.Action;
        if (string.IsNullOrEmpty(id)) return false;

        return string.Equals(_host?.Actions.Get(id)?.Category, category, StringComparison.Ordinal);
    }

    /// <summary>
    /// 填搜索框的分类下拉：「全部」+ 动作表里出现过的分类。
    ///
    /// 为什么分类来自动作表而不是另写一份常量：加一个宏/一个新动作分类，
    /// 这里自动多一项，不会出现"新分类在分类下拉里有、在搜索框里没有"。
    /// </summary>
    private void BuildSearchCategories()
    {
        if (_host == null) return;

        var items = new List<UI.Controls.SearchCategory> { new(Loc.T("全部"), "") };

        var categories = _host.Actions.Items
            .Where(i => !i.Hidden)
            .Select(i => i.Category)
            .Distinct()
            .ToList();

        foreach (string category in categories)
            items.Add(new UI.Controls.SearchCategory(Loc.T(category), category));

        BindingSearchBox.SetCategories(items.ToArray());
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
        string query = (BindingSearchBox.SearchText ?? "").Trim();
        string category = BindingSearchBox.SelectedTag ?? "";

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
            if (!BindingInCategory(binding, category)) continue;

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
           功能那几条没有 combo（点中不是跳转，而是应用到当前键）。
           选了分类时这里也只在那个分类里挑 —— 否则"先筛分类再搜"等于没筛。 */
        if (_bindingHits.Count < MaxHits && _host != null)
        {
            var more = _host.Actions.Items
                .Where(i => !i.Hidden)
                .Where(i => category.Length == 0
                            || string.Equals(i.Category, category, StringComparison.Ordinal))
                .Where(i => MatchesAction(i, query))
                .OrderBy(i => i.Category)
                .ThenBy(i => i.Title)
                .Take(MaxHits - _bindingHits.Count)
                .Select(i => new BindingSearchHit("", i.Title,
                    Loc.F("功能 · {0} · 回车应用到当前键", i.DisplayCategory), i.Id));
            _bindingHits.AddRange(more);
        }

        BindingSearchList.ItemsSource = _bindingHits;

        if (_bindingHits.Count == 0)
        {
            BindingSearchPopup.IsOpen = false;
            return;
        }

        /* 打开之前必须确认搜索框已经**排过版** —— 这是"结果弹到屏幕左上角"的根因。
         *
         * Popup 只在打开那一刻按 PlacementTarget 算一次位置，之后再改什么都不管。
         * 如果那一刻搜索框还没进视觉树（ActualHeight=0、PointToScreen 还是 (0,0)），
         * Custom 回调拿到的是 0 尺寸目标，第一个候选 (0, 0+4) 相对屏幕原点就落成
         * 左上角那一条，并且用户接着往下敲也不会自己回到正确位置。
         *
         * 所以宁可晚一帧：等布局真的出来了再开。重试有上限，见 _bindingPopupRetries。
         * 实在开不了就干脆不弹（下一次按键会重新触发），也绝不在错误位置画一个框。 */
        if (!BindingSearchTargetReady())
        {
            if (_bindingPopupRetries < MaxPopupRetries)
            {
                _bindingPopupRetries++;
                Core.CoreLog.Info($"[绑定搜索] 搜索框还没排好版（{DescribeBindingTarget()}），推迟一帧再弹");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    /* 这一帧里用户可能已经清空输入 / 关掉了搜索：
                     * 那时 _bindingHits 已经为空，RefreshBindingSearch 会直接不弹。 */
                    if (_bindingHits.Count > 0) RefreshBindingSearch();
                }), DispatcherPriority.Loaded);
            }
            else
            {
                Core.CoreLog.Warn($"[绑定搜索] 搜索框始终没有排好版（{DescribeBindingTarget()}），这次不弹");
            }

            BindingSearchPopup.IsOpen = false;
            return;
        }

        _bindingPopupRetries = 0;

        /* 内容变了就重放一次：WPF 不会为**已经打开**的 Popup 重跑放置回调，
         * 条目从 8 条缩到 2 条之后它还停在"翻到上方"那个位置。
         * 关一下再开（同一帧内），放置逻辑就会按新的弹层尺寸重算；
         * 键盘焦点在搜索框里，这一下不抢焦点、也不闪。 */
        string signature = _bindingHits.Count + "|"
                         + string.Join(",", _bindingHits.Select(h => h.Combo + "\u0001" + h.Key));
        if (BindingSearchPopup.IsOpen && signature != _bindingPopupSignature)
        {
            BindingSearchPopup.IsOpen = false;
        }

        BindingSearchPopup.IsOpen = true;
        _bindingPopupSignature = signature;
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

        /* 目标尺寸为 0 说明还没排版。正常路径在打开前就被 BindingSearchTargetReady()
         * 挡掉了，这里再兜一层：真拿 0 去算，结果就是屏幕左上角那一条。 */
        double targetH = target.Height > 1 ? target.Height : Math.Max(BindingSearchBox.ActualHeight, 0);
        double targetW = target.Width > 1 ? target.Width : Math.Max(BindingSearchBox.ActualWidth, 0);
        double popupW = Math.Max(popup.Width, 1);
        double popupH = Math.Max(popup.Height, 1);

        var candidates = new List<CustomPopupPlacement>
        {
            // 正路：贴搜索框左边缘、往下 4px
            new(new Point(0, targetH + gap), PopupPrimaryAxis.Horizontal),
            // 下面放不下：整块翻到框上方
            new(new Point(0, -popupH - gap), PopupPrimaryAxis.Horizontal),
            // 窗口贴右边：改成右边缘对齐
            new(new Point(targetW - popupW, targetH + gap), PopupPrimaryAxis.Horizontal),
            new(new Point(targetW - popupW, -popupH - gap), PopupPrimaryAxis.Horizontal),
        };

        return ClampPopupCandidates(candidates, popupW, popupH).ToArray();
    }

    /// <summary>
    /// 搜索框是否已经排好版 —— 弹层定位的唯一前提。
    /// 三条都得满足：进过视觉树（IsLoaded）、量到了真实尺寸、挂在一个 PresentationSource 上
    /// （最后一条才是 PointToScreen 能有意义的前提，光 IsLoaded 还不够）。
    /// </summary>
    private bool BindingSearchTargetReady()
        => BindingSearchBox.IsLoaded
           && BindingSearchBox.IsVisible
           && BindingSearchBox.ActualWidth > 1
           && BindingSearchBox.ActualHeight > 1
           && PresentationSource.FromVisual(BindingSearchBox) != null;

    /// <summary>把搜索框的就绪状态写成一行，进日志/诊断用。</summary>
    private string DescribeBindingTarget()
        => $"IsLoaded={BindingSearchBox.IsLoaded} IsVisible={BindingSearchBox.IsVisible} "
         + $"尺寸={BindingSearchBox.ActualWidth:0.#}x{BindingSearchBox.ActualHeight:0.#} "
         + $"源={(PresentationSource.FromVisual(BindingSearchBox) == null ? "null" : "ok")}";

    /// <summary>
    /// 把候选偏移夹进搜索框所在显示器的工作区。
    ///
    /// Custom 放位的候选点是**相对目标**的偏移，WPF 照给的点摆，不会帮你避让屏幕边缘：
    /// 设置页拉满屏、结果又有很多条时，"往下 4px"会让弹层下半截直接出屏。
    /// 四个候选本身覆盖了"下 / 上 / 右对齐"，但窗口又贴顶又贴底时四个都放不下，
    /// 夹一下至少保证它落在能看见的地方；被夹过的候选排到没被夹的后面，
    /// 这样"该在的位置"永远是优先项。
    /// </summary>
    private List<CustomPopupPlacement> ClampPopupCandidates(
        List<CustomPopupPlacement> candidates, double popupW, double popupH)
    {
        if (!TryGetSearchBoxScreenDip(out Point origin, out Rect work))
        {
            // 量不到工作区（句柄还没建好）：保持原样，靠上面的就绪判断兜底
            return candidates;
        }

        var exact = new List<CustomPopupPlacement>();
        var nudged = new List<CustomPopupPlacement>();

        foreach (var candidate in candidates)
        {
            double left = origin.X + candidate.Point.X;
            double top = origin.Y + candidate.Point.Y;

            double maxLeft = Math.Max(work.Left, work.Right - popupW);
            double maxTop = Math.Max(work.Top, work.Bottom - popupH);
            double fitLeft = Math.Clamp(left, work.Left, maxLeft);
            double fitTop = Math.Clamp(top, work.Top, maxTop);

            var adjusted = new CustomPopupPlacement(
                new Point(fitLeft - origin.X, fitTop - origin.Y), PopupPrimaryAxis.Horizontal);

            bool moved = Math.Abs(adjusted.Point.X - candidate.Point.X) >= 0.5
                      || Math.Abs(adjusted.Point.Y - candidate.Point.Y) >= 0.5;
            (moved ? nudged : exact).Add(adjusted);
        }

        exact.AddRange(nudged);
        return exact;
    }

    /// <summary>
    /// 搜索框左上角的屏幕坐标（DIP）+ 它所在显示器的工作区（DIP）。
    /// PointToScreen 给物理像素，用该视觉自己的 CompositionTarget 转回 DIP ——
    /// 200% 缩放的主屏和 100% 的副屏混用时，这一步不做就会差一倍。
    /// </summary>
    private bool TryGetSearchBoxScreenDip(out Point origin, out Rect work)
    {
        origin = default;
        work = Rect.Empty;

        PresentationSource? source = PresentationSource.FromVisual(BindingSearchBox);
        if (source?.CompositionTarget == null) return false;

        try
        {
            Point screenPx = BindingSearchBox.PointToScreen(new Point(0, 0));
            origin = source.CompositionTarget.TransformFromDevice.Transform(screenPx);
        }
        catch
        {
            return false;
        }

        Window? owner = Window.GetWindow(BindingSearchBox);
        if (owner != null
            && Core.Windows.ScreenPlacement.TryGetWorkAreaDip(owner, out Rect area)
            && !area.IsEmpty)
        {
            work = area;
        }
        else
        {
            // 兜底：主屏工作区（老系统、窗口句柄还没建立时只有这条路）
            work = SystemParameters.WorkArea;
        }

        return true;
    }

    private void CloseBindingSearch(bool clear)
    {
        BindingSearchPopup.IsOpen = false;
        _bindingPopupSignature = "";
        _bindingHits = new List<BindingSearchHit>();
        BindingSearchList.ItemsSource = null;

        if (clear && (BindingSearchBox.SearchText?.Length ?? 0) > 0) BindingSearchBox.ClearSearch();
    }

    /* ── 渲染 / 自检钩子 ────────────────────────────────────────────────
     * 结果弹层是独立的顶层视觉，不进主窗口截图 —— 也就是说"弹层到底弹在哪儿"
     * 这件事从来没被任何截图验证过。下面几个方法让 --render 能把弹层单独拍下来，
     * 并且把它的位置量成一行文本，位置错了才看得出来。
     */

    /// <summary>渲染用：填上关键词、打开弹层，返回弹层里的内容元素（没有结果时返回 null）。</summary>
    internal FrameworkElement? OpenBindingSearchForRender(string query)
    {
        BindingSearchBox.SearchText = query ?? "";
        RefreshBindingSearch();
        BindingSearchPopup.UpdateLayout();
        return BindingSearchPopup.IsOpen ? BindingSearchPopup.Child as FrameworkElement : null;
    }

    /// <summary>
    /// 渲染用：拿"目标尺寸 0、弹层 310x320"这个**退化输入**走一遍放置逻辑。
    ///
    /// 这就是用户报的"结果弹到屏幕左上角"的现场数学：搜索框还没排版时
    /// Custom 回调拿到的目标尺寸就是 0x0，旧实现老老实实返回 (0, 0+4)，
    /// 相对屏幕原点正好是左上角。现在这一步已经不靠目标尺寸了 ——
    /// 会退回用搜索框自己的实际尺寸，并且夹进工作区。
    /// 输出落进 keys-binding-search-placement.txt，出图时顺手回归。
    /// </summary>
    internal string DescribeDegeneratePlacement()
    {
        CustomPopupPlacement[] candidates =
            PlaceBindingSearchPopup(new Size(310, 320), new Size(0, 0), new Point());

        if (candidates.Length == 0) return "退化场景：一个候选都没有（异常）";

        Point first = candidates[0].Point;
        if (!TryGetSearchBoxScreenDip(out Point origin, out Rect work))
        {
            return $"退化场景：首个候选=({first.X:0.#},{first.Y:0.#}) 量不到工作区";
        }

        var rect = new Rect(origin.X + first.X, origin.Y + first.Y, 310, 320);
        string verdict = work.Contains(rect) ? "落进工作区" : $"越界（{rect} 不在 {work}）";
        return $"退化场景：首个候选=({first.X:0.#},{first.Y:0.#}) 绝对位置=({rect.X:0},{rect.Y:0}) {verdict}";
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
        /* 判读新增两项：
         *   就绪 —— 搜索框排过版没有。no 说明这次弹层的位置不可信（也正是
         *          "弹到屏幕左上角"那个 bug 的现场），必须先修布局时机再看坐标。
         *   弹层工作区 —— 弹层整块落在当前显示器工作区里没有。外 = 用户会看到半截框。 */
        string ready = BindingSearchTargetReady() ? "yes" : "no";
        string inside = "量不到";
        if (!double.IsNaN(childAbs.X)
            && BindingSearchPopup.Child is FrameworkElement childEl
            && TryGetSearchBoxScreenDip(out _, out Rect work)
            && PresentationSource.FromVisual(BindingSearchBox)?.CompositionTarget is CompositionTarget ct)
        {
            Point popupDip = ct.TransformFromDevice.Transform(childAbs);
            var popupRect = new Rect(popupDip.X, popupDip.Y, childEl.RenderSize.Width, childEl.RenderSize.Height);
            inside = work.Contains(popupRect) ? "内" : $"外（工作区 {work}）";
        }

        return $"PlacementTarget={(BindingSearchPopup.PlacementTarget == null ? "null（绑定没生效）" : "ok")} "
             + $"框={target.RenderSize.Width:0.#}x{target.RenderSize.Height:0.#} "
             + $"{where} Placement={BindingSearchPopup.Placement} 命中={_bindingHits.Count}\n"
             + $"框屏幕=({targetOrigin.X:0},{targetOrigin.Y:0}) 弹层屏幕=({childAbs.X:0},{childAbs.Y:0}) "
             + $"DPI={scale:0.##} 工作区={SystemParameters.WorkArea}\n"
             + $"就绪={ready} 弹层工作区={inside} 目标状态={DescribeBindingTarget()}";
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
        TextBox box = BindingSearchBox.InputBox;

        /* 插入符坐标是相对输入框内容区的；图标的右边缘在搜索框坐标系里。
         * 两边都换算到外层搜索框再比，才是"光标到底有没有排在放大镜后面"。 */
        Rect caret = box.GetRectFromCharacterIndex(0, false);
        string caretText = "caretX=量不到";
        if (!caret.IsEmpty)
        {
            Point point = box.TranslatePoint(new Point(caret.Left, 0), BindingSearchBox);
            caretText = $"caretX={point.X:0.#}";
        }

        string iconText = "图标=没找到";
        if (BindingSearchBox.FindName("SearchGlyph") is TextBlock glyph)
        {
            Point origin = glyph.TranslatePoint(new Point(0, 0), BindingSearchBox);
            iconText = $"图标x={origin.X:0.#}..{origin.X + glyph.ActualWidth:0.#}";
        }

        string probes = "";
        q(0); q(1); q(2);
        void q(int i)
        {
            Rect rc = box.GetRectFromCharacterIndex(Math.Min(i, box.Text.Length), false);
            probes += $" i{i}={(rc.IsEmpty ? "空" : rc.Left.ToString("0.#"))}";
        }

        return $"Padding.Left={box.Padding.Left:0.#} 文本长度={box.Text.Length} {iconText} {caretText}{probes}";
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
