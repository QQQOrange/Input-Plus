using System;
using InputPlus.UI;

namespace InputPlus;

/// <summary>
/// 自检：「没松手之前，这一笔不许自己消失」（v1.7.11）。
///
/// 这条规则有三半：
///   ① 覆盖层在**跟踪中**不许被排期收起 —— 靠 `HideOverlay`/`OnOverlayGuard` 里的
///      `IsTracking` 与 `_immediateHoldVk` 两道闸；
///   ② 「立即执行」之后手指还按着时，画面留到松手才按 FadeOutExecuted 收；
///   ③ 唯一的例外：按住超过 8 秒（多半是抬手丢了）由看门狗收掉。
///
/// 前两条要真窗口才能验，这里钉的是**规则本身**（③ 的口径 + 常量），
/// 免得以后有人把 8 秒改成"多久没动"那种各算各的写法。
/// </summary>
public static partial class SelfTest
{
    private static void TestGestureHold()
    {
        CheckEqual("手势：按住上限就是 8 秒", GestureService.GestureHoldLimitMs, 8000);

        long t0 = 1_000_000;
        CheckEqual("手势：刚画完一个点不算超时", GestureService.StrokeExpired(t0, t0), false);
        CheckEqual("手势：差 1ms 到 8 秒仍不算超时",
            GestureService.StrokeExpired(t0 + 7999, t0), false);
        CheckEqual("手势：正好 8 秒仍不算超时（要「超过」）",
            GestureService.StrokeExpired(t0 + 8000, t0), false);
        CheckEqual("手势：停手超过 8 秒才算超时",
            GestureService.StrokeExpired(t0 + 8001, t0), true);

        /* 口径是"最后一个轨迹点之后"（v1.7.14），不是"按下之后"：
         * 画 1 分钟、每个点都在刷新计时 → 永远不超时。以前从按下算，
         * 画 2 秒再按住想 6 秒就被收掉 —— 用户报的"手还按着线自己没了"。 */
        CheckEqual("手势：一直有新的点就一直不超时（画一分钟也没事）",
            GestureService.StrokeExpired(t0 + 60_000, t0 + 59_900), false);
        CheckEqual("手势：点停住之后才开始计 8 秒",
            GestureService.StrokeExpired(t0 + 60_000, t0 + 51_999), true);
    }
}
