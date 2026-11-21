using System;
using InputPlus.Core.App;
using InputPlus.Core.Input;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus;

/// <summary>
/// 自检：管理员模式下的"注入走哪条路"。
///
/// 全部是纯函数或纯数据结构，**不会启动任何进程** —— 自检跑在无人值守的
/// --selftest 里，一旦弹 UAC 就会把整轮卡死。真实的进程启动在
/// Core/App/HookHostLauncher.cs，这里只验判定逻辑。
/// </summary>
public static partial class SelfTest
{
    private static void TestElevationRoute()
    {
        /* 1. 唯一的规则：提升就下沉、没提升就自己发。
         * 用 ForceElevatedForTest 钉住 Route 这条链路（它平时读真实令牌）。 */
        ElevationState.ForceElevatedForTest(true);
        bool routeElevated = ElevationState.Route == InjectionRoute.HookHost;
        ElevationState.ForceElevatedForTest(false);
        bool routeNormal = ElevationState.Route == InjectionRoute.InProcess;
        ElevationState.ResetCacheForTest();

        Check("注入路由：提升下沉给钩子宿主、普通权限本进程发送",
            routeElevated && routeNormal
            && ElevationState.RouteFor(true) == InjectionRoute.HookHost
            && ElevationState.RouteFor(false) == InjectionRoute.InProcess,
            $"提升={routeElevated}, 普通={routeNormal}");

        /* 2. 两个"必须压过下沉"的例外。*/
        bool resolved =
               ElevationState.ResolveRoute(true, false, true) == InjectionRoute.HookHost      // 正常下沉
            && ElevationState.ResolveRoute(true, false, false) == InjectionRoute.InProcess    // 中继不在 → 自己发
            && ElevationState.ResolveRoute(true, true, true) == InjectionRoute.InProcess      // 目标更高 → 自己发（高→高允许）
            && ElevationState.ResolveRoute(false, false, true) == InjectionRoute.InProcess;   // 本来就没提升

        Check("注入路由：目标提升或中继缺失时压过下沉", resolved);

        /* 3. 边界的定义：只有"普通权限 → 提升窗口"才注定失败。
         * 提升的我们往普通窗口发是高 → 低，UIPI 不管。 */
        bool barrier =
               ElevationState.BarrierFor(false, true) == InjectionBarrier.ReceiverElevated
            && ElevationState.BarrierFor(true, true) == InjectionBarrier.None
            && ElevationState.BarrierFor(true, false) == InjectionBarrier.None
            && ElevationState.BarrierFor(false, false) == InjectionBarrier.None;

        Check("注入边界：只有低权限发往提升窗口才算注定被拦",
            barrier && ElevationState.BarrierText(InjectionBarrier.ReceiverElevated).Length > 10);

        /* 4. 下沉用的线格式必须无损：键盘和鼠标两种事件都要能原样回来。
         * dwExtraInfo 里的 CLP! 标记尤其重要 —— 丢了之后宿主发的键会被自家
         * 钩子当成物理按键，形成回环。 */
        var keyboard = new INPUT { type = INPUT_KEYBOARD };
        keyboard.U.ki.wVk = 0x41;
        keyboard.U.ki.wScan = 0x1E;
        keyboard.U.ki.dwFlags = 0x0002;
        keyboard.U.ki.dwExtraInfo = InputSender.InjectionTag;

        var mouse = new INPUT { type = INPUT_MOUSE };
        mouse.U.mi.dx = -12;
        mouse.U.mi.dy = 34;
        mouse.U.mi.mouseData = 0;
        mouse.U.mi.dwFlags = 0x0001;
        mouse.U.mi.dwExtraInfo = InputSender.InjectionTag;

        string wire = InjectionBridge.Encode(new[] { keyboard, mouse });
        INPUT[] back = InjectionBridge.Decode(wire);

        bool roundTrip = back.Length == 2
            && back[0].type == INPUT_KEYBOARD
            && back[0].U.ki.wVk == 0x41 && back[0].U.ki.wScan == 0x1E
            && back[0].U.ki.dwFlags == 0x0002
            && back[0].U.ki.dwExtraInfo == InputSender.InjectionTag
            && back[1].type == INPUT_MOUSE
            && back[1].U.mi.dx == -12 && back[1].U.mi.dy == 34
            && back[1].U.mi.dwFlags == 0x0001
            && back[1].U.mi.dwExtraInfo == InputSender.InjectionTag;

        /* 不认识的命令行必须解出空数组，而不是抛异常把宿主打死。 */
        bool rejectsJunk = InjectionBridge.Decode("").Length == 0
            && InjectionBridge.Decode("HELLO").Length == 0
            && InjectionBridge.Decode("INJECT|k:1:2").Length == 0
            && InjectionBridge.Decode("INJECT|k:x:0:0:0").Length == 0;

        Check("注入下沉：线格式编码/解码无损，垃圾行解出空数组",
            roundTrip && rejectsJunk, wire);

        /* 5. 通道没接上时不能"假装发成功"：那样调用方会以为键已经出去了，
         * 用户看到的是"动作执行了但什么都没发生"。 */
        InjectionBridge.ResetForTest();

        /* 5a. 压根没接通道：返回 false，但**不算**一次"送失败"（连尝试都没有）。 */
        bool noChannel = !InjectionBridge.TryForward(new[] { keyboard });
        int idleFailures = InjectionBridge.ForwardFailedCount;

        /* 5b. 通道在、但送不出去：必须计入失败，并且不能假装成功。 */
        InjectionBridge.SendLine = _ => false;
        bool brokenChannel = !InjectionBridge.TryForward(new[] { keyboard });
        int failedCount = InjectionBridge.ForwardFailedCount;

        /* 5c. 通道正常：成功计数 +1，且整行命令确实是 INJECT 开头。 */
        string? wireSent = null;
        InjectionBridge.SendLine = line => { wireSent = line; return true; };
        bool okChannel = InjectionBridge.TryForward(new[] { keyboard });
        int forwarded = InjectionBridge.ForwardedCount;
        InjectionBridge.ResetForTest();

        Check("注入下沉：通道缺失/断开/正常时都如实回报",
            noChannel && idleFailures == 0
            && brokenChannel && failedCount == 1
            && okChannel && forwarded == 1
            && wireSent != null && wireSent.StartsWith(InjectionBridge.Prefix, StringComparison.Ordinal),
            $"无通道={noChannel}, 断开后失败={failedCount}, 成功={forwarded}, 线={wireSent}");

        /* 6. 钩子宿主的命令行拼装（explorer/ShellExecute 那条路只能给一整行）。 */
        string argLine = HookHostLauncher.BuildArgumentLine(
            new[] { "--hookhost", "clp-hook-1234", "1234", "C:\\Program Files\\a b" });

        Check("钩子宿主命令行：含空格的参数会加引号",
            argLine == "--hookhost clp-hook-1234 1234 \"C:\\Program Files\\a b\"", argLine);

        /* 7. 一次性提示的语义：未提升永远不弹、提升只弹一次。
         * 标记落在数据目录（自检是 INPUTPLUS_DATA 的临时目录），验完清掉。 */
        ElevationNotice.ResetForTest();
        bool quietWhenNormal = !ElevationNotice.ConsumeOnce(false) && !ElevationNotice.HasShown();
        bool firstTime = ElevationNotice.ConsumeOnce(true);
        bool secondTime = !ElevationNotice.ConsumeOnce(true);
        bool hasText = ElevationNotice.Text.Contains("管理员") && ElevationNotice.Text.Contains("普通权限");
        ElevationNotice.ResetForTest();

        Check("管理员提示：只提示一次，普通权限不提示",
            quietWhenNormal && firstTime && secondTime && hasText,
            $"普通时静默={quietWhenNormal}, 首次={firstTime}, 再次={!secondTime}");
    }
}
