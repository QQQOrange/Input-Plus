using System;
using InputPlus.Core.Qbar;
using InputPlus.UI.Views;

namespace InputPlus;

/// <summary>
/// 自检：文件索引那两个按钮（v1.7.13）。
///
/// 现场：点「重建索引」几百毫秒后就弹「文件索引已就绪：N 个文件」，而实际重建刚开始；
/// 点「刷新状态」屏幕上什么都不动，像没实现。
/// 根因是"拿 `Ready` 当重建完成的判据" —— 那只是"索引里有文档"这个缓存事实。
/// 这里钉死新的对账规则：**按 generation 判完成**，且宽限期内不许判"没在跑"。
/// </summary>
public static partial class SelfTest
{
    private static void TestQbarIndexButtons()
    {
        /* generation 0 / 负数永远不算完成 —— RequestRebuild 失败时返回的就是 0，
         * 不这么判的话"没发起重建"会被当成"已经建好了"。 */
        CheckEqual("索引：generation 0 不算建完", QbarIndex.IsRebuildDone(0), false);
        CheckEqual("索引：负 generation 不算建完", QbarIndex.IsRebuildDone(-3), false);
        CheckEqual("索引：没发生过的 generation 不算建完", QbarIndex.IsRebuildDone(987_654), false);

        /* 宽限期：Worker 刚被唤起的那几毫秒 Building/Starting 都还是 0，
         * 这时候判"没在跑"就等于"点了没反应"。 */
        long t0 = 5_000_000;
        CheckEqual("索引：请求刚发出时算「还在跑」（宽限内）",
            QbarIndex.RebuildStillRunning(987_654, t0, t0), true);
        CheckEqual("索引：差 1ms 到宽限仍算「还在跑」",
            QbarIndex.RebuildStillRunning(987_654, t0 + 2_999, t0), true);
        CheckEqual("索引：过了宽限、又没在建 = 请求没生效，别再干等",
            QbarIndex.RebuildStillRunning(987_654, t0 + 3_001, t0), false);

        /* 按钮自己的等待上限：10 分钟。太短会在机械盘上误报"还在建"，
         * 无限等又会让用户以为卡死（状态行会一直转）。 */
        CheckEqual("索引：重建按钮最多等 10 分钟", QbarSettingsView.RebuildWaitMs, 600_000);
        Check("索引：等待上限必须是正数", QbarSettingsView.RebuildWaitMs > 0);
    }
}
