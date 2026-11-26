using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 附加菜单多级下钻用的目录枚举（v1.6.7）。
///
/// ── 和"分页列文件"那条老路的区别 ───────────────────────────────────────
/// v1.6.6 及以前，附加菜单悬停文件夹用的是"把里面的东西分页列出来"：
/// 文件也在里面、一次一页、最多两层。现在这一级是**导航**：
/// 只列直接子文件夹，而且每一条都得知道"它自己有没有子文件夹"——
/// 界面靠这个决定要不要画 ▸ 箭头（叶子不能有箭头，也不能悬停出空面板）。
///
/// ── 五条硬约束（对应用户需求里"数据与状态"那一节）─────────────────────
/// ① **绝不在 UI 线程做 IO**：调用方一律 <c>Task.Run</c> 包起来，本类只提供同步 API；
/// ② 只读**一层**：不递归，递归是界面一层一层问出来的（懒加载）；
/// ③ 会话内缓存：同一个目录只枚举一次，重试按钮传 <c>fresh: true</c> 绕过；
/// ④ 任何异常都变成"带错误种类的空结果"，绝不因为一个没权限的目录把菜单打崩；
/// ⑤ 重解析点（junction / 符号链接）与隐藏/系统项**直接跳过** ——
///    这是防"无限下钻"的第一道闸（第二道是窗口里的祖先链判重）。
///
/// ── 长路径 ────────────────────────────────────────────────────────────
/// 超过 240 字符时枚举改用 <c>\\?\</c> 形式（System.IO 认这个）；
/// 交给界面/explorer 的路径一律是**剥掉前缀**的干净形式，见 <see cref="RevealPath.Clean"/>。
/// </summary>
internal static class FolderTree
{
    /// <summary>一个目录最多列这么多条子文件夹（再多也点不完）。</summary>
    internal const int MaxChildren = 400;

    /// <summary>超过这个长度就用 <c>\\?\</c> 形式去枚举。</summary>
    private const int LongPathThreshold = 240;

    /// <summary>
    /// 探测"子文件夹下面还有没有子文件夹"的总预算（毫秒）。
    ///
    /// 为什么需要：网络路径断掉时，每一条子目录的探测都可能卡到 TCP 超时
    /// （几十秒），几百条就是永远转圈。超预算的条目按"未知"处理（照样画箭头），
    /// 用户真点开时会看到明确的错误与重试，而不是菜单卡死。
    /// </summary>
    private const int ProbeBudgetMs = 1500;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        // 重解析点一律不列：junction / 符号链接是"无限下钻"最主要的来源，
        // 隐藏/系统项（AppData、$RECYCLE.BIN…）列出来也只会让人误点。
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    /// <summary>枚举失败的种类（界面据此决定提示文案，不把异常消息直接摊给用户）。</summary>
    internal enum FailureKind
    {
        None = 0,
        /// <summary>没有读取权限（含被 ACL 挡住）。</summary>
        Denied = 1,
        /// <summary>目录已经不存在了（删掉 / U 盘拔了）。</summary>
        Missing = 2,
        /// <summary>网络位置无响应或读取超时。</summary>
        Unreachable = 3,
        /// <summary>其它 IO / 路径错误。</summary>
        Other = 4,
    }

    /// <summary>
    /// 一条子文件夹。
    /// <paramref name="HasSubfolders"/> 三态：true 有、false 没有（叶子）、
    /// <c>null</c> 不知道（没权限 / 超预算）—— 未知时界面**照样给箭头**，
    /// 让用户点开看到真实的错误，而不是假装它是叶子。
    /// </summary>
    internal sealed record Item(string Path, string Name, bool? HasSubfolders);

    /// <summary>一次枚举的结果。</summary>
    internal sealed record Listing(
        IReadOnlyList<Item> Items,
        FailureKind Failure,
        string Detail,
        bool FromCache,
        bool Truncated)
    {
        internal bool Ok => Failure == FailureKind.None;

        internal static Listing Failed(FailureKind kind, string detail) =>
            new(Array.Empty<Item>(), kind, detail ?? "", false, false);
    }

    /// <summary>会话内缓存（进程生命周期）。键是清洗过的小写路径。</summary>
    private static readonly ConcurrentDictionary<string, Listing> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 列出一个目录的**直接子文件夹**（已排序、已过滤隐藏/系统/重解析点）。
    ///
    /// <paramref name="fresh"/> = true 时绕过缓存重新枚举（错误行上的"重试"用）。
    /// **同步**：调用方负责放到后台线程（<see cref="System.Threading.Tasks.Task.Run(Action)"/>）。
    /// </summary>
    internal static Listing List(string? folder, bool fresh = false)
    {
        string clean = RevealPath.Clean(folder);
        if (clean.Length == 0) return Listing.Failed(FailureKind.Other, "路径为空");

        string key = clean.ToLowerInvariant();
        if (!fresh && Cache.TryGetValue(key, out Listing? hit))
            return hit with { FromCache = true };

        Listing listing;
        try { listing = Enumerate(clean); }
        catch (Exception ex)
        {
            // 兜底：Enumerate 内部已经分门别类，走到这里说明是意料之外的错
            CoreLog.Swallow(ex, "目录树：枚举 " + clean);
            listing = Listing.Failed(FailureKind.Other, ex.Message);
        }

        Cache[key] = listing;
        return listing;
    }

    /// <summary>清空会话缓存（自检用；也让"换了个网络环境"能靠重启菜单复原）。</summary>
    internal static void ClearCache() => Cache.Clear();

    /// <summary>
    /// 单独问一个目录"有没有直接子文件夹"（三态）。
    ///
    /// 给一级菜单的固定项用：那一条在进菜单时还不知道自己是文件还是文件夹，
    /// 由窗口在**后台线程**探一次，回来才决定要不要画 ▸。
    /// </summary>
    internal static bool? ProbeHasSubfolders(string? folder)
    {
        string clean = RevealPath.Clean(folder);
        if (clean.Length == 0) return null;
        return HasChildFolder(clean);
    }

    /// <summary>仅供自检：缓存里现在有多少个目录。</summary>
    internal static int CachedCountForTest => Cache.Count;

    /// <summary>
    /// 仅供自检：枚举选项里**确实**跳过了重解析点。
    ///
    /// 这条是结构断言 —— 防"无限下钻"最要紧的一行就是
    /// <c>AttributesToSkip |= ReparsePoint</c>，它要是被人顺手改掉，
    /// 只有在用户机器上撞到 junction 才会暴露（自检里造 junction 有删错目录的风险，
    /// 所以不去真造）。
    /// </summary>
    internal static bool SkipsReparsePointsForTest =>
        (Options.AttributesToSkip & FileAttributes.ReparsePoint) != 0;

    // ── 枚举本体 ────────────────────────────────────────────────────

    private static Listing Enumerate(string folder)
    {
        string probe = ForIo(folder);
        var items = new List<Item>(32);
        var watch = Stopwatch.StartNew();
        bool truncated = false;
        bool unknownHasSub = false;

        try
        {
            using var enumerator = Directory.EnumerateDirectories(probe, "*", Options).GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (items.Count >= MaxChildren) { truncated = true; break; }

                string raw = enumerator.Current;
                string display = RevealPath.Clean(raw);
                string name = NameOf(display);
                if (name.Length == 0) continue;

                bool? has;
                if (watch.ElapsedMilliseconds > ProbeBudgetMs)
                {
                    /* 预算用完：剩下的条目按"未知"处理。
                     * 未知 = 照样画箭头，点开才知道有没有 —— 比"卡在这里等网络超时"好。 */
                    has = null;
                    unknownHasSub = true;
                }
                else
                {
                    has = HasChildFolder(display);
                }

                items.Add(new Item(display, name, has));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Listing.Failed(FailureKind.Denied, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Listing.Failed(FailureKind.Missing, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            /* 网络断开、驱动器卸载、被占用… 全归"不可达"：
             * 界面提示"网络位置不可达"，比原样摊一句英文 IO 异常有用。 */
            return Listing.Failed(FailureKind.Unreachable, ex.Message);
        }
        catch (Exception ex)
        {
            return Listing.Failed(FailureKind.Other, ex.Message);
        }

        items.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        if (unknownHasSub) CoreLog.Warn($"[目录树] 探测超预算，部分条目按未知处理：{folder}");
        return new Listing(items, FailureKind.None, "", false, truncated);
    }

    /// <summary>
    /// 这个目录里还有没有子文件夹。三态：true/false，探不出来就是 <c>null</c>。
    ///
    /// 用 <c>EnumerateDirectories(...).MoveNext()</c> 而不是 <c>GetDirectories().Length</c>：
    /// 后者会把几千条全读出来，只为回答"是不是空"。
    /// </summary>
    private static bool? HasChildFolder(string dir)
    {
        try
        {
            using var enumerator = Directory.EnumerateDirectories(ForIo(dir), "*", Options).GetEnumerator();
            return enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (Exception) { return null; }
    }

    /// <summary>System.IO 用的形式：必要时补 <c>\\?\</c> / <c>\\?\UNC\</c> 前缀。</summary>
    internal static string ForIo(string? path)
    {
        string p = RevealPath.Clean(path);
        if (p.Length == 0) return "";

        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            if (p.Length < LongPathThreshold) return p;
            return @"\\?\UNC\" + p[2..];
        }

        if (p.Length < LongPathThreshold) return p;

        /* 相对路径补前缀只会得到废话：先尽量转绝对，转不了就原样交出去。 */
        try
        {
            if (!Path.IsPathFullyQualified(p)) p = Path.GetFullPath(p);
        }
        catch { return p; }

        return p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
    }

    /// <summary>路径的最后一段（目录名）；根目录取 "C:\" 这种原样。</summary>
    internal static string NameOf(string path)
    {
        string p = RevealPath.Clean(path);
        if (p.Length == 0) return "";
        string trimmed = p.TrimEnd('\\');
        if (trimmed.Length == 0) return p;
        if (trimmed.Length == 2 && trimmed[1] == ':') return trimmed + @"\";
        int cut = trimmed.LastIndexOf('\\');
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}
