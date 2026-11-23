using System.Windows;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Input;
using InputPlus.Core.Localization;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Content;
using InputPlus.Core.Settings;
using InputPlus.Core.Update;
using InputPlus.Core.Windows;
using InputPlus.UI.Theme;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 动作分发：把层里的动作调用到各服务，并做失败提示去重。
/// </summary>
public sealed partial class AppHost
{
    // ---------------- 动作 ----------------
    /// <summary>
    /// 本次动作调用内是否已经弹过提示。
    /// 动作自己通常会解释原因（"没能打开文件夹：xxx"），
    /// 这时不该再补一条，否则同一个失败会弹两次。
    /// </summary>
    private bool _notifiedInCall;

    /// <summary>
    /// 上一次"注入被完整性级别挡掉"的提示时间（TickCount64 毫秒）。
    /// 连按手势会连着失败几十次，20 秒内只提醒一次。
    /// </summary>
    private long _lastInjectBarrierAt;

    /// <summary>
    /// 「这次调用里的成功提示可以省略」的嵌套计数。
    ///
    /// 手势是个例外：松手的时候名字已经浮在屏幕上了（「关闭」），动作自己再补一条
    /// 「已发送 Ctrl+W（未绑定应用，发给当前窗口）」就是把同一件事说第二遍 ——
    /// 用户明确说过这种提示没必要。所以手势执行期间用 <see cref="QuietSuccess"/>
    /// 圈一段，段内的**成功**提示全部省掉；失败（「✗ …」）照旧弹，那才是要看的东西。
    /// </summary>
    private int _quietSuccess;

    /// <summary>
    /// 进入「不弹成功提示」的作用域，Dispose 时退出。可嵌套。
    /// </summary>
    public IDisposable QuietSuccess()
    {
        System.Threading.Interlocked.Increment(ref _quietSuccess);
        return new QuietScope(this);
    }

    private sealed class QuietScope : IDisposable
    {
        private AppHost? _host;
        public QuietScope(AppHost host) => _host = host;
        public void Dispose()
        {
            var host = _host;
            _host = null;
            if (host != null) System.Threading.Interlocked.Decrement(ref host._quietSuccess);
        }
    }

    /// <summary>
    /// 这条提示在静默期里要不要吞掉。
    ///
    /// 只吞成功提示：失败文案统一以「✗ 」开头（同 <c>UiText.FailLine</c>），
    /// 那是唯一"用户非看不可"的一类，静默期也放行。
    /// </summary>
    internal static bool ShouldSuppressToast(int quietDepth, string message)
        => quietDepth > 0 && !(message ?? "").TrimStart().StartsWith('✗');

    public bool InvokeAction(string action, string arg)
    {
        _notifiedInCall = false;

        bool ok;
        try
        {
            ok = Actions.Invoke(action, arg);
        }
        catch (Exception ex)
        {
            /* 动作里抛异常以前会一路冒到动作队列线程（最坏情况把队列打死），
             * 现在在这里收口：记日志 + 按失败通知，主流程继续。 */
            CoreLog.Error($"动作执行异常：{action}（{ex.GetType().Name}）", ex);
            NotifyActionFailed(action, ex.Message);
            return false;
        }

        /* 动作跑完了，看一眼刚才那批注入有没有被 UIPI 挡掉。
         * 放在这里而不是每个动作各自判断：手势、热键、按键绑定、Qbar 全都走这条出口。 */
        ReportInjectBarrier();

        if (ok)
        {
            UsageStats.Record(action);
            CoreLog.Info($"动作：{action}{(string.IsNullOrEmpty(arg) ? "" : " " + arg)}");
            return true;
        }

        // 未注册（未知 id）或执行了但没完成，都算失败
        CoreLog.Warn($"动作未执行：{action}（未注册，或执行了但没完成）");
        NotifyActionFailed(action, null);
        return false;
    }

    /// <summary>
    /// 注入被完整性级别挡掉时给一条界面提示。
    ///
    /// 典型场景：InputPlus 以普通权限运行，用户对「以管理员身份运行」的窗口
    /// 发键（手势 / 热键动作 / 宏 / Qbar 动作）—— SendInput 被 UIPI 拒绝
    /// （错误 5）。以前只有日志里一行 Warn，用户看到的就是"按了没反应"。
    /// 提示只在真的挡了的时候出现，20 秒内不重复。
    /// </summary>
    private void ReportInjectBarrier()
    {
        var barrier = InputSender.LastInjectBarrier;
        if (barrier == Core.App.InjectionBarrier.None) return;

        long now = Environment.TickCount64;
        if (now - _lastInjectBarrierAt < 20_000) return;
        _lastInjectBarrierAt = now;

        string text = Core.App.ElevationState.BarrierText(barrier);
        if (string.IsNullOrWhiteSpace(text)) return;
        CoreLog.Warn("[注入] " + text);
        NotifyTranslated("✗ " + text);
    }

    /// <summary>
    /// 动作失败通知。
    ///
    /// 三条原则：
    ///   ① **与成功提示同一条出口**（同一个 Toast、同一个开关 `ShowToast`），
    ///      否则用户会觉得"失败时界面没反应"；
    ///   ② 文案统一为 `没能<动作名>：<原因> · <时间>`，一眼能看出是失败；
    ///   ③ **不重复**：动作自己通常已经解释过原因（如"没能打开文件夹：不存在"），
    ///      本次调用内已经弹过就不再补一条，避免同一个失败弹两次。
    /// </summary>
    private void NotifyActionFailed(string action, string? reason)
    {
        if (_notifiedInCall) return;

        string title = ActionCatalog.TitleOf(action) ?? action;

        /* 前缀沿用产品里既有的失败标记「✗ 」（与设置页状态行同一套约定），
         * 这样不用改 Toast 样式也能一眼看出"这条是失败"。 */
        NotifyTranslated(UiText.FailLine(UiText.FailedAt(title, reason, DateTime.Now)));
    }

    /// <summary>诊断开关：把钩子收到的每个按键写进日志。</summary>
    public void EnableKeyTrace() => Engine.EnableKeyTrace();

    /// <summary>
    /// 弹一条 Toast 提示。
    ///
    /// 这里是**显示出口**，翻译放在这一层：全仓几十处调用点
    /// （`Notify("已恢复默认设置")`）都照旧写中文，不必逐处包 Loc.T。
    /// 查不到译文就原样显示，所以带参数的拼串（"已同步 N 项"）最坏也只是保持中文。
    /// </summary>
    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        if (ShouldSuppressToast(_quietSuccess, message))
        {
            CoreLog.Info($"[提示] 手势已提示过名称，这条省略：{message}");
            return;
        }
        _notifiedInCall = true;
        /* 直接交给 ToastWindow：它内部会调度到界面线程，且只显示最新一条。
         * 连按音量键时，前一条还没画出来就被后一条顶掉，不会积压。 */
        ToastWindow.Show(Loc.T(message));
    }

    /// <summary>
    /// 已经翻译好的提示文本直接上屏。
    ///
    /// 失败通知是"格式化后的整句"（含动作名、原因、时间），
    /// 再过一遍 <see cref="Loc.T"/> 当键查表没有意义，也会让译文对不上。
    /// </summary>
    private void NotifyTranslated(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !Settings.Global.ShowToast) return;
        if (ShouldSuppressToast(_quietSuccess, message))
        {
            CoreLog.Info($"[提示] 手势已提示过名称，这条省略：{message}");
            return;
        }
        _notifiedInCall = true;
        ToastWindow.Show(message);
    }

    /// <summary>正文索引是否正在显示右下角进度（用来区分"收尾事件"和启动时就绪）。</summary>
    private bool _contentProgressShown;

    /// <summary>
    /// 正文索引进度 → 右下角常驻进度条；建完关掉它并弹一条"已完成"。
    ///
    /// 为什么不在 ContentIndex 里直接开窗口：那一层是 Core，不该知道 UI 存在；
    /// 界面这一侧订阅事件，Core 只负责发进度。
    /// </summary>
    private void OnContentIndexProgress(ContentIndex.ProgressInfo info)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnContentIndexProgress(info)));
            return;
        }

        if (info.Building)
        {
            _contentProgressShown = true;
            IndexProgressToast.Show(info);
            return;
        }

        /* 没显示过进度（例如启动时就绪）就不该弹"已完成" —— 那不是这次的事。 */
        if (!_contentProgressShown) return;
        _contentProgressShown = false;
        IndexProgressToast.Hide();

        if (info.Phase == "就绪")
        {
            Notify(Loc.F("正文索引已完成：{0} 条 · {1}",
                ContentIndex.FormatCount(info.Docs), ContentIndex.FormatSize(ContentIndex.Bytes)));
        }
        else if (info.Phase is "已中断" or "失败")
        {
            Notify(ContentIndex.Status);
        }
    }

    public void ToggleEnabled() => SetEnabled(!Engine.Enabled);

    public void SetEnabled(bool enabled)
    {
        if (Engine.Enabled == enabled) return;
        Engine.Enabled = enabled;

        /* 钩子在宿主进程里时，宿主也有一份 Enabled 状态；只改主进程字段
         * 会出现“界面显示已暂停、实际热键仍触发”。 */
        if (_hookHostReady)
            SendHookHostCommand(enabled ? "ENABLED 1" : "ENABLED 0");

        /* 暂停 = **全部功能一起停**，不只是热键。
         *
         * 手势、平滑滚动、按住拖拽各自装着独立的钩子（手势一条鼠标钩子、
         * 平滑滚动复用它、按住拖拽是一整套键盘+鼠标钩子）。只停引擎的话，
         * 用户点了暂停，画手势 / 滚轮缓动 / 按住 Enter 拖窗口照样生效 ——
         * 那就不叫暂停。这三面旗子都是"不改用户设置"的临时状态，
         * 恢复时原样装回来。 */
        try { Gestures.Suspended = !enabled; } catch (Exception ex) { CoreLog.Swallow(ex, "暂停鼠标手势"); }
        try { Scroll.Suspended = !enabled; } catch (Exception ex) { CoreLog.Swallow(ex, "暂停平滑滚动"); }
        try { HoldDrag.Suspended = !enabled; } catch (Exception ex) { CoreLog.Swallow(ex, "暂停按住拖拽"); }

        /* 暂停前把可能"按着没抬起来"的键补一次抬起：
         * 暂停后引擎不再管按键，粘住就没人修了。 */
        if (!enabled)
        {
            Core.Input.InputSafety.ReleaseAll();

            /* 正在播放的宏/手势回放也停掉：那是自动化按键，暂停之后继续打字更吓人。 */
            try { Context.MacroPlayer.Cancel(); } catch (Exception ex) { CoreLog.Swallow(ex, "暂停宏播放"); }
        }

        _tray?.SetEnabled(enabled);
        EnabledChanged?.Invoke(enabled);
        Notify(enabled ? AppInfo.ShortName + " 已启用" : AppInfo.ShortName + " 已暂停");
    }

}
