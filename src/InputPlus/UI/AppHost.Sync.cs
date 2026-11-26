using System.Windows;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Input;
using InputPlus.Core.Localization;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Update;
using InputPlus.Core.Windows;
using InputPlus.UI.Theme;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// WebDAV 同步与自动备份的调度（真正的 IO 在 SyncService 里）。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- WebDAV 同步 ----------------

    /// <summary>同步服务（界面与自动同步共用）。</summary>
    public Core.Sync.SyncService Sync { get; private set; } = null!;

    /// <summary>最近一次同步的提示，供界面显示。</summary>
    public string LastSyncMessage { get; private set; } = "";

    /// <summary>
    /// 启动同步：延后几秒再跑，等界面和钩子都就绪。
    /// 联网在后台线程，不阻塞界面。
    /// </summary>
    public void ScheduleStartupSync()
    {
        var s = Settings.Sync;
        if (!s.SyncOnStart)
        {
            CoreLog.Info("启动同步：未开启，跳过");
            return;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await RunStartupSyncAsync();
        };
        timer.Start();
    }

    private async Task RunStartupSyncAsync()
    {
        try
        {
            CoreLog.Info("启动同步：开始");
            var result = await Task.Run(() => Sync.SyncOnStartAsync()).ConfigureAwait(true);
            LastSyncMessage = result.Message;
            if (!string.IsNullOrEmpty(result.Message))
            {
                CoreLog.Info($"启动同步：{result.Message}");
                if (Settings.Global.ShowToast) Notify("同步：" + result.Message);
            }
            SaveSettings();
            SyncCompleted?.Invoke(result);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("启动同步异常：" + ex.Message);
        }
    }

    /// <summary>退出时同步（由 App.OnExit 调用）。</summary>
    public Core.Sync.SyncResult? RunExitSync()
    {
        try
        {
            if (!Settings.Sync.SyncOnExit) return null;
            CoreLog.Info("退出同步：开始");
            var r = Sync.SyncOnExit();
            CoreLog.Info($"退出同步：{r.Message}");
            SaveSettings();
            return r;
        }
        catch (Exception ex)
        {
            return Core.Sync.SyncResult.Fail(ex.Message);
        }
    }

    /// <summary>同步完成事件（界面刷新用）。</summary>
    public event Action<Core.Sync.SyncResult>? SyncCompleted;

    // ---------------- 自动备份 ----------------

    private System.Windows.Threading.DispatcherTimer? _autoBackupTimer;
    private bool _autoBackupBusy;

    /// <summary>
    /// 重建自动备份定时器。
    ///
    /// 由设置页在开关/间隔改动后调用，也在启动时调一次。
    /// 间隔很短时（最短 5 分钟）不会真的每 5 分钟写一份 ——
    /// 写之前会比对配置文件的写入时间，没变化就跳过，
    /// 否则用户开着软件不用，一天也能堆出几百份一模一样的备份。
    /// </summary>
    public void RestartAutoBackupTimer()
    {
        _autoBackupTimer?.Stop();
        _autoBackupTimer = null;

        var s = Settings.Sync;
        if (!s.AutoBackup)
        {
            CoreLog.Info("自动备份：未开启");
            return;
        }
        if (string.IsNullOrWhiteSpace(s.BackupPassword))
        {
            CoreLog.Warn("自动备份：没设备份密码，无法加密，已跳过");
            return;
        }

        int minutes = Math.Clamp(s.AutoBackupMinutes, 5, 1440);
        _autoBackupTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(minutes),
        };
        _autoBackupTimer.Tick += async (_, _) => await RunAutoBackupAsync().ConfigureAwait(true);
        _autoBackupTimer.Start();
        CoreLog.Info($"自动备份：每 {minutes} 分钟一次");
    }

    private async Task RunAutoBackupAsync()
    {
        if (_autoBackupBusy) return;
        _autoBackupBusy = true;
        try
        {
            var r = await Task.Run(() => Sync.AutoBackupAsync()).ConfigureAwait(true);
            LastSyncMessage = r.Message;
            if (r.Ok)
            {
                CoreLog.Info("自动备份：" + r.Message);
                SaveSettings();
                SyncCompleted?.Invoke(r);
            }
            else
            {
                CoreLog.Warn("自动备份：" + r.Message);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("自动备份异常：" + ex.Message);
        }
        finally
        {
            _autoBackupBusy = false;
        }
    }

    /// <summary>动作表重建后触发（界面需要刷新功能下拉）。</summary>
    public event Action? ActionsChanged;

    /// <summary>
    /// 重建动作表。
    ///
    /// 必须能重建：宏动作（macro:&lt;id&gt;）是在 <c>ActionCatalog.Create</c> 时
    /// 按当时的宏列表注册的。动作表只在启动时建一次的话，
    /// **用户新建的宏永远不会出现在"功能"下拉里** ——
    /// 这正是用户反馈的"按键绑不了宏"。
    ///
    /// 所以任何会改变动作集合的操作（增删宏、重新加载配置）之后都要调它。
    /// </summary>
    public void RebuildActions()
    {
        Actions = ActionCatalog.Create(Context);
        ActionsChanged?.Invoke();
    }

    /// <summary>手动触发一次同步并通知界面。</summary>
    public async Task<Core.Sync.SyncResult> RunSyncNowAsync(bool upload)
    {
        var r = upload
            ? await Sync.UploadAsync().ConfigureAwait(true)
            : await Sync.DownloadAsync().ConfigureAwait(true);
        LastSyncMessage = r.Message;
        SaveSettings();
        SyncCompleted?.Invoke(r);
        return r;
    }

    /// <summary>
    /// 退出前先停输入：停掉宿主、暂停引擎并补发抬起。
    /// 退出同步最长 8 秒，期间窗口和托盘已经消失；如果钩子还活着，
    /// 用户按快捷键会触发看不见的动作，强杀时还可能留下粘住的修饰键。
    /// </summary>
    public void StopForExit()
    {
        try
        {
            _hookHostReady = false;
            _hookHostRelay = false;
            Core.App.InjectionBridge.Detach();
            Engine.HookDelegatedToHost = false;
            try { _hostPipeCts?.Cancel(); } catch { }
            try { _hookHost?.Kill(); } catch { }
            _hookHost = null;
            Engine.Enabled = false;
            Engine.Suspended = true;
            Core.Input.InputSafety.ReleaseAll();
        }
        catch (Exception ex) { CoreLog.Swallow(ex, "退出前停止输入"); }
    }

    /// <summary>
    /// 退出看门狗的等待时长。
    ///
    /// 必须比 <see cref="App"/> 的 `OnExit` 里最慢的一段长：退出同步有 8 秒硬上限
    /// （见 App.OnExit 的注释），再留几秒给设置落盘与日志刷盘。正常情况下 OnExit
    /// 几百毫秒就完了，看门狗根本不会醒 —— 它只在"CLR 收尾卡死"时才动手。
    /// </summary>
    private const int ExitWatchdogMs = 12000;

    public void ExitApp(bool restart = false)
    {
        CoreLog.Info("[退出] 收到退出请求（restart=" + restart + "）");

        /* 整条链必须自己兜住：托盘菜单的回调跑在 WinForms 的消息线程上，
         * 这里抛出去的异常会被那边的消息循环吞掉 —— 用户看到的就是
         * "图标没了、进程还在"（2026-11-26 的现场：pid 37644 一直挂到第二天，
         * 无窗口、0 CPU、单线程等待，crash-context 每次都判上次不干净退出）。 */
        try
        {
            Exiting = true;
            try { StopForExit(); } catch (Exception ex) { CoreLog.Swallow(ex, "退出前停止输入"); }
            try { SaveSettings(); } catch (Exception ex) { CoreLog.Warn("[退出] 保存设置失败：" + ex.Message); }
            try { UsageStats.Flush(); } catch (Exception ex) { CoreLog.Warn("[退出] 统计落盘失败：" + ex.Message); }
            try { _tray?.Dispose(); } catch (Exception ex) { CoreLog.Swallow(ex, "退出时拆托盘"); }
            _tray = null;
            try { _selectionWatcher?.Dispose(); _selectionWatcher = null; } catch { }   /* 预期内：清理动作，失败无副作用 */

            if (restart)
            {
                /* 重启自己（权限切换必须重启才生效）。
                 * 用 explorer 启动而不是当前进程 —— 否则子进程会继承
                 * 当前进程的完整性级别，达不到"降权重启"的目的。 */
                try
                {
                    var exe = Environment.ProcessPath ?? AppPaths.AppDir.TrimEnd('\\') + "\\InputPlus.exe";
                    // 先释放互斥体，避免“退出同步”尚未结束时新实例等到超时后退出。
                    try { SingleInstance.Release(); } catch { }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    { UseShellExecute = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory });
                }
                catch (Exception ex) { CoreLog.Warn("重启失败：" + ex.Message); }
            }

            /* 看门狗：从这里开始，12 秒内进程还没自己结束就强制 Exit。
             *
             * 为什么必须有：退出路径上有一串 native 收尾（UIA/COM、Rust 索引 dll、
             * WinForms NotifyIcon），任何一处等待都能让 CLR 永远退不完 —— 那时候
             * 界面和托盘早就没了，用户只能去任务管理器里杀。有了它，"点了退出就一定退"
             * 不再依赖每一处收尾都写得正确。 */
            ArmExitWatchdog();

            CoreLog.Info("[退出] 调用 Application.Shutdown");
            var app = Application.Current;
            if (app == null)
            {
                /* 没有 WPF Application（例如别的地方先拆了）——没有 Dispatcher 可投递，直接结束。 */
                CoreLog.Warn("[退出] Application.Current 为空，直接结束进程");
                CoreLog.Flush();
                Environment.Exit(0);
            }

            if (app.Dispatcher.CheckAccess())
            {
                app.Shutdown();
            }
            else
            {
                /* 托盘菜单是 WinForms 的，回调线程不保证是 WPF 的界面线程。
                 * 直接调 Shutdown 会抛 InvalidOperationException（跨线程访问 DispatcherObject），
                 * 而那正是"图标消失、进程不退"的经典成因 —— 投递过去，让界面线程自己关。 */
                CoreLog.Info("[退出] 不在界面线程上，投递 Shutdown");
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CoreLog.Info("[退出] 界面线程执行 Shutdown");
                    app.Shutdown();
                }));
            }
        }
        catch (Exception ex)
        {
            /* 走到这里说明连"正常退出"都组织不起来了：不留悬念，直接结束。
             * 看门狗此时可能已经武装，但没必要等它。 */
            CoreLog.Error("[退出] 退出流程异常，强制结束进程", ex);
            CoreLog.Flush();
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 起一个后台线程，<see cref="ExitWatchdogMs"/> 之后进程还在就强制退出。
    ///
    /// 故意做成"只管开、不管关"：它醒来说明退出没走完，那时候任何"取消看门狗"
    /// 的代码本身也可能已经卡住了，靠不住。进程正常退出时线程跟着消失，代价为零。
    /// </summary>
    private static void ArmExitWatchdog()
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(ExitWatchdogMs);
            try { CoreLog.Warn("[退出] 看门狗：" + ExitWatchdogMs + "ms 内没退干净，强制结束进程"); } catch { }
            try { CoreLog.Flush(); } catch { }
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "InputPlus.ExitWatchdog",
        };
        thread.Start();
    }

}
