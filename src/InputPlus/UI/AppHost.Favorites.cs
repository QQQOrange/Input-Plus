using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InputPlus.Core;
using InputPlus.Core.Icons;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 「收藏到 Qbar」（v1.5.0）：附加菜单右上角那颗星标的后半程。
///
/// 前半程在 <see cref="GestureService"/> 里 —— 中键菜单弹出时把"指针底下是什么"
/// 抓成一个 <see cref="FavoriteTarget"/> 快照；星标被点中之后经过这里：
///   ① 弹 <see cref="FavoriteCaptureWindow"/>（先出窗、字段随后异步补）；
///   ② 用户点「收藏」→ 写进 <see cref="QbarOptions.Pinned"/>；
///   ③ 落盘 + 一条提示。**没有第二条存储路径** —— 固定项就是 Qbar 设置里那张表，
///      设置页、备份、同步、中键菜单全都跟着它走。
/// </summary>
public sealed partial class AppHost
{
    /// <summary>附加菜单的星标被点中：弹收藏对话框。</summary>
    public void BeginFavoriteCapture(FavoriteTarget target)
    {
        if (target == null) return;

        /* ⚠ 这一段必须在**调用线程**上先做完，不能搬进下面的 BeginInvoke：
         * OnMiddleMenuClosed 在 return 之后会 synchronously 清掉菜单锚点，而 BeginInvoke 是排队执行的 ——
         * 等界面回调真正跑起来时，锚点已经是 0，重建上下文只能失败，
         * 于是对话框写着"没认出这次是在哪里点的收藏"（v1.5.1 在 WPS 里的真实症状）。 */
        IntPtr anchor = ForegroundFiles.AnchorHwnd;

        Task<ForegroundFileResult>? openFileTask = null;
        if (target.Source is FavoriteSource.Application or FavoriteSource.Unknown or FavoriteSource.Browser)
        {
            try { openFileTask = ForegroundFiles.GetOrRefreshAsync(); }
            catch (Exception ex) { CoreLog.Swallow(ex, "收藏：发起识别"); }
        }

        /* 上下文本身也可能是空快照（Capture 依赖弹菜单那一刻的前台/指针）。先记一条，
         * 再用锚点重建 —— 重建要的句柄已经在上面抓好了。 */
        CoreLog.Info($"[收藏] 上下文：source={target.Source} proc={target.ProcessName} " +
                     $"class={target.ClassName}/{target.RootClass} hwnd=0x{target.Hwnd.ToInt64():X} " +
                     $"anchor=0x{anchor.ToInt64():X} exe={target.ExePath}");
        FavoriteTrace.Write($"star: source={target.Source} proc={target.ProcessName} " +
                            $"class={target.ClassName}/{target.RootClass} anchor=0x{anchor.ToInt64():X}");

        target = FavoriteTarget.EnsureUsable(target, anchor);

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var win = new FavoriteCaptureWindow(target, TakenKeywords());
                Window? owner = Application.Current?.MainWindow;
                if (owner != null && owner.IsVisible && !ReferenceEquals(owner, win)) win.Owner = owner;

                ForegroundFile? openFile = null;
                if (openFileTask != null)
                {
                    _ = openFileTask.ContinueWith(t =>
                    {
                        if (!t.IsCompletedSuccessfully) return;
                        if (t.Result.File is not { } file) return;
                        _ = _dispatcher.BeginInvoke(new Action(() =>
                        {
                            openFile = file;
                            try { win.ProvideOpenFile(file.Path, file.ProcessName, file.FromCache); }
                            catch (Exception ex) { CoreLog.Swallow(ex, "收藏：补当前文件"); }
                        }));
                    }, TaskScheduler.Default);
                }

                bool ok = win.ShowDialog() == true;
                if (!ok) return;

                /* 用户走的是"顺手收那个文件"这条 —— 和菜单那条入口完全同一条落地路径。 */
                if (win.FileRequested)
                {
                    if (openFile != null) FavoriteForegroundFile(openFile);
                    return;
                }

                if (win.Result == null) return;
                AddPinnedEntry(win.Result, win.ShowInMiddleMenu);
            }
            catch (Exception ex)
            {
                CoreLog.Error("收藏到 Qbar 失败", ex);
                NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("收藏到 Qbar"), ex.Message, DateTime.Now)));
            }
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }
    /// <summary>
    /// 已经在用的关键字：四张表（固定项 / 搜索 / 程序 / 网址）里出现过的都算。
    ///
    /// 为什么要连别的表一起看：关键字是 Qbar 的**全局前缀**，固定项用一个
    /// 已经被 <c>bd</c> 占掉的名字，用户按 bd + 空格时命中的会是搜索引擎 ——
    /// 收藏看起来"成功了"，实际按不出来。
    /// </summary>
    public IReadOnlyList<string> TakenKeywords()
    {
        var list = new List<string>();
        try
        {
            foreach (var e in Settings.Qbar?.Pinned ?? new List<QEntry>())
                if (e != null) list.Add(e.Key ?? "");
            foreach (var e in Settings.Search ?? new List<QEntry>())
                if (e != null) list.Add(e.Key ?? "");
            foreach (var e in Settings.Run ?? new List<QEntry>())
                if (e != null) list.Add(e.Key ?? "");
            foreach (var e in Settings.Web ?? new List<QEntry>())
                if (e != null) list.Add(e.Key ?? "");
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：收集已用关键字"); }

        return list.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
    }

    /// <summary>
    /// 把草稿写进 Qbar 固定项。
    ///
    /// 插在**最前面**：固定项在附加菜单里按顺序显示，刚收藏的那一条最可能是下一次要点的。
    /// </summary>
    public void AddPinnedEntry(FavoriteDraft draft, bool showInMiddleMenu)
    {
        if (draft == null) return;

        QEntry entry = draft.ToEntry(showInMiddleMenu);
        if (string.IsNullOrWhiteSpace(entry.Value))
        {
            Notify(Loc.T("目标路径是空的，没有收藏"));
            return;
        }

        /* 同一条（目标 + 关键字）已经在了就不重复加：用户连点两下星标只该有一条。 */
        var existing = Settings.Qbar.Pinned.FirstOrDefault(p =>
            p != null
            && string.Equals((p.Value ?? "").Trim(), entry.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals((p.Key ?? "").Trim(), entry.Key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Comment = entry.Comment;
            existing.IconPath = entry.IconPath;
            if (showInMiddleMenu) existing.ShowInMiddleMenu = true;
            SaveSettings();
            Notify(Loc.T("这条已经收藏过了，备注已更新"));
            return;
        }

        Settings.Qbar.Pinned.Insert(0, entry);
        SaveSettings();

        /* 网址顺手预热 favicon：下次开附加菜单/Qbar 时图标直接就在，
         * 不会先出现首字母色块再闪一下。 */
        try
        {
            if (entry.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                IconCache.LoadWebIconAsync(entry.Value, _ => { }, force: true);
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：预热网页图标"); }

        string name = string.IsNullOrWhiteSpace(entry.Comment) ? entry.Key : entry.Comment;
        Notify(Loc.T("已收藏到 Qbar：") + name);
        CoreLog.Info($"[收藏] 固定项 +1：{entry.Key} → {entry.Value}");
    }

    // ──── v1.5.1：附加菜单顶部的「收藏当前文件 / 打开文件所在位置」 ────

    /// <summary>下一次进设置页时要选中的固定项关键字（收藏完"光标定位到那一条"）。</summary>
    public string? PendingPinnedKey { get; set; }

    /// <summary>取走"待选中的固定项"（取一次就清空，和别名页那套一致）。</summary>
    public string? TakePendingPinnedKey()
    {
        string? key = PendingPinnedKey;
        PendingPinnedKey = null;
        return key;
    }

    /// <summary>
    /// 这个文件已经在固定项里了吗。按**归一化路径**比（小写 + 去尾斜杠 + 剥 <c>\\?\</c>）——
    /// 从 WPS 收一次、从记事本再收一次，两次拿到的路径可能一个大写盘符一个带长路径前缀，
    /// 按字符串直接比就会变成两条。
    /// </summary>
    public bool IsPinnedFile(string? path) => FindPinnedFile(path) != null;

    /// <summary>按归一化路径找固定项（找不到返回 null）。</summary>
    public QEntry? FindPinnedFile(string? path)
    {
        string key = RevealPath.CompareKey(path);
        if (key.Length == 0) return null;

        try
        {
            foreach (QEntry entry in Settings.Qbar?.Pinned ?? new List<QEntry>())
            {
                if (entry == null) continue;
                if (string.Equals(RevealPath.CompareKey(entry.Value), key, StringComparison.Ordinal))
                    return entry;
            }
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "收藏：查固定项"); }
        return null;
    }

    /// <summary>
    /// 「收藏当前文件」落库的那一步：**没有对话框、没有二次确认**（用户要的是"点一下就好"）。
    ///
    /// 返回 true = 新增了一条；false = 已经收藏过 —— 此时 <see cref="QEntry.UseCount"/> 加一、
    /// 待选中的关键字指向那一条，提示也换成「已在收藏中」。
    ///
    /// 写设置失败要**回滚干净**：留半条记录比没收藏更糟 —— 用户会看到一条打不开的固定项，
    /// 还以为是自己删错了。
    /// </summary>
    public bool FavoriteForegroundFile(ForegroundFile? file)
    {
        if (file == null) return false;

        string path = RevealPath.Clean(file.Path);
        if (path.Length == 0)
        {
            Notify(Loc.T("没找到要收藏的路径"));
            return false;
        }

        var pinned = Settings.Qbar.Pinned;
        /* 幂等与新增都在 PinnedFiles 里（纯函数，自检直接喂它两次验"只有一条"）。
         * 这里只负责：拿"已经在用的关键字"、写盘、失败回滚、提示、定位。 */
        PinnedUpsertResult result = PinnedFiles.Upsert(pinned, file, TakenKeywords());

        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            /* 写设置失败要回滚干净：留半条记录比没收藏更糟 —— 用户会看到一条打不开的
             * 固定项，还以为是自己删错了。只有"这次新增的"才需要撤掉。 */
            if (result.Added) pinned.Remove(result.Entry);
            CoreLog.Error("收藏当前文件：写设置失败，已回滚", ex);
            NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("收藏当前文件"), ex.Message, DateTime.Now)));
            return false;
        }

        PendingPinnedKey = result.Entry.Key;
        string name = string.IsNullOrWhiteSpace(result.Entry.Comment) ? result.Entry.Key : result.Entry.Comment;

        if (!result.Added)
        {
            Notify(Loc.T("已在收藏中：") + name);
            CoreLog.Info($"[收藏] 已存在，次数 +1（{result.Entry.UseCount}）：{path}");
            return false;
        }

        Notify(Loc.T("已收藏：") + name);
        CoreLog.Info($"[收藏] 当前文件 → 固定项 {result.Entry.Key}：{path}（来源 {file.ProcessName}）");
        return true;
    }
}