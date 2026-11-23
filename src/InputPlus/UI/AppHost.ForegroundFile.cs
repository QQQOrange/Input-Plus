using System;
using System.Threading.Tasks;
using System.Windows;
using InputPlus.Core;
using InputPlus.Core.Interop;
using InputPlus.Core.Localization;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;

namespace InputPlus.UI;

/// <summary>
/// 附加菜单顶部的两个入口（v1.5.1）：**收藏当前文件** 与 **打开文件所在位置**。
///
/// 分工：
///   · 识别与"打开"都在后台（<see cref="ForegroundFiles"/> / <see cref="WindowService"/>）；
///   · 只有落库、弹对话框、提示这些必须碰界面的事切回界面线程；
///   · Core 那边（ActionContext）只发"用户点了"这一个信号，不留任何界面依赖。
///
/// 为什么失败时**不静默**：用户明明白白点了一下按钮。什么都不发生的话，
/// 他会以为是功能坏了；所以识别失败一律给一句人话，并给出下一步
/// （收藏 → 手动选文件；打开位置 → 打开这个程序所在的目录）。
/// </summary>
public sealed partial class AppHost
{
    /// <summary>
    /// 「收藏当前文件」：识别 → 落库 → 提示。
    ///
    /// 连续点两下只会跑一次识别（<see cref="ForegroundFiles.GetOrRefreshAsync"/> 里
    /// 800ms 缓存 + 单飞），第二下直接命中同一份结果。
    /// </summary>
    public void BeginFavoriteForegroundFile()
    {
        /* 桌面 / 资源管理器 / 任务栏：这不是"某个程序打开了文件"。
         * 星标那条路不受影响（它收藏的是文件夹/桌面本身），这里只挡"收藏当前文件"。 */
        if (!AnchorCanHoldFile())
        {
            Notify(Loc.T("这里没有正在打开的文件"));
            return;
        }

        /* 识别**同步发起**：GetOrRefreshAsync 在锁里就把"菜单弹出前的锚点"读走了，
         * 扫描随后在后台跑。丢进 Task.Run 再调的话，锚点可能已经被
         * OnMiddleMenuClosed 清掉 —— 那时问出来的前台是我们自己的菜单窗口，
         * 认到的永远是 InputPlus.exe。 */
        Task<ForegroundFileResult> pending;
        try { pending = ForegroundFiles.GetOrRefreshAsync(); }
        catch (Exception ex)
        {
            CoreLog.Error("收藏当前文件：无法发起识别", ex);
            Notify(Loc.T("没找到它打开的文件，请手动选一个"));
            return;
        }

        _ = Task.Run(async () =>
        {
            ForegroundFileResult result;
            try
            {
                result = await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CoreLog.Error("收藏当前文件：识别失败", ex);
                _ = _dispatcher.BeginInvoke(new Action(() => Notify(Loc.T("没找到它打开的文件，请手动选一个"))));
                return;
            }

            if (result.File is { } file)
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    try { FavoriteForegroundFile(file); }
                    catch (Exception ex)
                    {
                        CoreLog.Error("收藏当前文件：落库失败", ex);
                        NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("收藏当前文件"), ex.Message, DateTime.Now)));
                    }
                }));
                return;
            }

            /* 识别不到：先给一句人话，紧接着**直接弹文件对话框**。
             * 不做"再点一次试试"这种空提示 —— 用户已经点过一次了。 */
            _ = _dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Notify(Loc.T("没找到它打开的文件，请手动选一个"));
                    PickFileAndFavorite();
                }
                catch (Exception ex)
                {
                    CoreLog.Error("收藏当前文件：手动选择失败", ex);
                    NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("收藏当前文件"), ex.Message, DateTime.Now)));
                }
            }));
        });
    }

    /// <summary>
    /// 「打开文件所在位置」：识别 → 资源管理器里打开父目录并选中（设置里可改成只开目录）。
    ///
    /// 5 秒没返回就给「网络位置无响应」—— 网络路径离线时 explorer 会同步阻塞几秒，
    /// 那几秒里界面绝不能跟着停。
    /// </summary>
    public void BeginRevealForegroundFile()
    {
        /* 同上：桌面上点这一条什么也不该发生（菜单里它本来就是灰的）。 */
        if (!AnchorCanHoldFile())
        {
            FavoriteTrace.Write($"reveal: 拒绝（锚点不是可能开着文件的窗口）anchor=0x{ActionAnchor().ToInt64():X} " +
                                $"proc={ForegroundWatcher.ProcessNameOf(ActionAnchor())} class={WindowService.ClassNameOf(ActionAnchor())}");
            Notify(Loc.T("这里没有正在打开的文件"));
            return;
        }

        bool folderOnly = FileLocationMode.IsOpenFolderOnly(Settings.OpenFileLocationMode);

        /* 同上：锚点必须在调用线程上就读走。 */
        Task<ForegroundFileResult> pending;
        try { pending = ForegroundFiles.GetOrRefreshAsync(); }
        catch (Exception ex)
        {
            CoreLog.Error("打开位置：无法发起识别", ex);
            pending = Task.FromResult(ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, ex.Message));
        }

        _ = Task.Run(async () =>
        {
            ForegroundFileResult result;
            try
            {
                result = await pending.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CoreLog.Error("打开位置：识别失败", ex);
                result = ForegroundFileResult.Fail(ForegroundFileOutcome.NoCandidate, ex.Message);
            }

            if (result.File is not { } file)
            {
                /* 识别不到 = **这里没有正在打开的文件**。
                 *
                 * v1.5 改：不再弹「选择要定位的文件」。以前那条兜底在 Electron / 浏览器里
                 * 是常态（它们的句柄里根本没有用户文件），用户点了一下「打开文件所在位置」，
                 * 得到的却是一个让他自己找文件的对话框 —— 而他刚才明明在看一个东西。
                 * 现在菜单里那一条本来就灰着（见 MiddleMenuPolicy.Build / MarkForegroundFile），
                 * 正常点不到；万一点到了（热键/旧引用/文件刚好被挪走），只给一句话，绝不弹框。 */
                FavoriteTrace.Write($"reveal: 识别失败 outcome={result.Outcome} why={result.Why} → 只提示，不弹选择框");
                _ = _dispatcher.BeginInvoke(new Action(() => Notify(Loc.T("这里没有正在打开的文件"))));
                return;
            }

            FavoriteTrace.Write($"reveal: 文件={file.Path} 来源={file.ProcessName} 缓存={file.FromCache}");

            Task<WindowService.RevealOutcome> reveal = Task.Run(
                () => WindowService.RevealInForeground(file.Path, folderOnly));

            Task finished = await Task.WhenAny(reveal, Task.Delay(5000)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, reveal))
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                    Notify(Loc.T("网络位置无响应，稍后再试"))));
                CoreLog.Warn($"[打开位置] 5 秒没返回：{file.Path}");
                return;
            }

            WindowService.RevealOutcome outcome;
            try { outcome = await reveal.ConfigureAwait(false); }
            catch (Exception ex)
            {
                CoreLog.Error("打开位置：失败", ex);
                _ = _dispatcher.BeginInvoke(new Action(() =>
                    NotifyTranslated(UiText.FailLine(UiText.FailedAt(Loc.T("打开文件所在位置"), ex.Message, DateTime.Now)))));
                return;
            }

            /* 成功的常规情形**不弹提示**：资源管理器已经跳到用户面前了，那就是反馈。
             * 只有"退了一步"和"彻底不行"才需要说话。 */
            if (!outcome.Launched)
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                {
                    Notify(outcome.Reachable
                        ? Loc.T("没能在资源管理器里定位它，可以直接打开上级目录")
                        : Loc.T("所在位置无法访问"));
                    if (outcome.Reachable && outcome.Folder.Length > 0)
                    {
                        Task.Run(() => WindowService.RevealInForeground(outcome.Folder, openFolderOnly: true));
                    }
                }));
                return;
            }

            if (file.FromCache)
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                    Notify(Loc.T("这是 WPS 云文档的本地缓存副本，原文件不在本机"))));
            }

            FavoriteTrace.Write($"reveal: 通道={outcome.Channel} 结果={(outcome.Launched ? "OK" : "失败 " + outcome.Why)} 目录={outcome.Folder} 上溯={outcome.WentUp}");

            if (outcome.WentUp)
            {
                _ = _dispatcher.BeginInvoke(new Action(() =>
                    Notify(Loc.T("文件已不在了，已打开上级目录"))));
            }
        });
    }

    /// <summary>
    /// 这次动作该对着哪个窗口：菜单锚点优先（菜单弹出前那个窗口），
    /// 没有锚点（比如把动作绑到键上、根本没弹菜单）就用当前前台。
    /// </summary>
    private static IntPtr ActionAnchor()
    {
        IntPtr anchor = ForegroundFiles.AnchorHwnd;
        return anchor != IntPtr.Zero ? anchor : NativeMethods.GetForegroundWindow();
    }

    /// <summary>
    /// 锚点窗口是不是"可能开着文件"的那类窗口（v1.5.4）。
    ///
    /// 桌面 / 资源管理器 / 任务栏一律不算 —— 菜单条目那边已经置灰了，
    /// 这里是**第二道门**：动作也可能来自热键、旧菜单引用、或锚点被换掉的情况。
    /// 之前缺这道门，桌面上点中键 → 菜单用了上一个程序（欧路词典）的类名 →
    /// 条目是亮的 → 点下去扫别人、扫不到 → 弹出"选择要定位的文件"，
    /// 默认目录还是那个程序的安装目录。用户看到的完全是莫名其妙的一幕。
    /// </summary>
    private static bool AnchorCanHoldFile()
    {
        IntPtr hwnd = ActionAnchor();
        if (hwnd == IntPtr.Zero) return false;

        string proc = ForegroundWatcher.ProcessNameOf(hwnd);
        string cls = WindowService.ClassNameOf(hwnd) ?? "";
        return ForegroundFileProbe.CanActOnForegroundFile(proc, cls);
    }

    /// <summary>
    /// 手动选择文件时的初始目录（v1.5.4）。
    ///
    /// 不设的话对话框会用 shell 记的"上一次目录" —— 真机上第一次弹出来的默认位置是
    /// `D:\software\eudic`（另一个程序的安装目录），用户完全不知道那是什么。
    /// 这里固定在"上次挑过的地方 → 桌面"，把未知状态挤掉。
    /// </summary>
    private static string PickStartDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_lastPickDir) && Directory.Exists(_lastPickDir)) return _lastPickDir;
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (desktop.Length > 0 && Directory.Exists(desktop)) return desktop;
        }
        catch { }
        return "";
    }

    private static string _lastPickDir = "";
    /// <summary>
    /// 手动选择文件兜底（复用系统文件对话框，不新建窗口）。
    /// 选出来的文件当作"来自 manual"，其余流程与自动识别完全一样。
    /// </summary>
    private void PickFileAndFavorite()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.T("选择要收藏的文件"),
            CheckFileExists = true,
            Multiselect = false,
            Filter = Loc.T("所有文件") + "|*.*",
            InitialDirectory = PickStartDirectory(),
            RestoreDirectory = true,
        };

        Window? owner = Application.Current?.MainWindow;
        bool? ok = owner != null && owner.IsVisible ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true || string.IsNullOrWhiteSpace(dialog.FileName)) return;

        try { _lastPickDir = Path.GetDirectoryName(dialog.FileName) ?? ""; } catch { }
        FavoriteForegroundFile(new ForegroundFile(dialog.FileName, "manual", IntPtr.Zero, 0, DateTime.Now));
    }
}
