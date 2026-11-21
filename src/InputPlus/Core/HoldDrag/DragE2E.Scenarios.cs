using System;
using System.Text;
using InputPlus.Modules.HoldDrag;
using HdNative = InputPlus.Modules.HoldDrag.Native;

namespace InputPlus.Core;

/// <summary>
/// &#12300;按住拖拽&#12301;的额外端到端场景：右键拖拽调整大小、短按不许误触发、左上角那条边。
///
/// 为什么要有这个文件：原来的 --drag-e2e 只跑「左键拖拽移动窗口」一条路径。而这条功能
/// 一共两条路径（左键移动 / 右键调整横竖大小）加四个必须成立的负向行为（短按左键不移动、
/// 短按右键不缩放且不误弹菜单）。返工最容易坏的正是没被测到的那几条 —— 于是有了这些场景。
///
/// 判定原则和主文件一致：绝不用"别的窗口挡住靶子"当失败证据，量不到位就判「跳过」。
/// </summary>
internal static partial class DragE2E
{
    private const uint MouseLeftDown = 0x0002u;
    private const uint MouseLeftUp = 0x0004u;
    private const uint MouseRightDown = 0x0008u;
    private const uint MouseRightUp = 0x0010u;

    private enum Expect
    {
        /// <summary>窗口矩形一点都不能变（短按）。</summary>
        NoChange,
        /// <summary>只有右边和下边长大，左上角不动。</summary>
        GrowBottomRight,
        /// <summary>只有左边和上边往外长，右下角不动。</summary>
        GrowTopLeft,
    }

    /// <summary>跑完全部额外场景，返回 0 = 全过，1 = 有用例失败，2 = 取不到窗口矩形，3 = 环境不可用。</summary>
    private static int RunExtraScenarios(StringBuilder sb, nint hwnd, AppConfig cfg, AltDragSink sink, int hold)
    {
        // 吸附（AutoSnap）会让几何结果取决于当时桌面上还有哪些窗口 —— 回归用例要的是确定性，
        // 这里临时关掉；带吸附的那条路径由场景 1（主流程）按出厂配置跑过。
        int snap = cfg.AutoSnap;
        cfg.AutoSnap = 0;
        int worst = 0;
        try
        {
            worst = Worse(worst, Scenario(sb, hwnd, cfg, sink, hold,
                "场景2 短按左键（按住触发键点一下，不应移动窗口）",
                r => (r.Left + 180, r.Top + 12), MouseLeftDown, MouseLeftUp, 0, 0, 1, Expect.NoChange));

            worst = Worse(worst, Scenario(sb, hwnd, cfg, sink, hold,
                "场景3 右键拖拽右下角（横向 +80 / 纵向 +48，窗口应变大）",
                r => (r.Right - 14, r.Bottom - 14), MouseRightDown, MouseRightUp, 80, 48, 8, Expect.GrowBottomRight));

            worst = Worse(worst, Scenario(sb, hwnd, cfg, sink, hold,
                "场景4 右键拖拽左上角（横向 -60 / 纵向 -40，左上角应往外长）",
                r => (r.Left + 14, r.Top + 14), MouseRightDown, MouseRightUp, -60, -40, 6, Expect.GrowTopLeft));

            worst = Worse(worst, Scenario(sb, hwnd, cfg, sink, hold,
                "场景5 短按右键（按住触发键点一下，不应改变尺寸）",
                r => (r.Right - 14, r.Bottom - 14), MouseRightDown, MouseRightUp, 0, 0, 1, Expect.NoChange));
        }
        catch (Exception ex)
        {
            sb.AppendLine("额外场景异常：" + ex.Message);
            sb.AppendLine(ex.ToString());
            worst = 2;
        }
        finally
        {
            cfg.AutoSnap = snap;
        }
        if (Injections.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("额外场景注入异常：" + Injections);
        }
        return worst;
    }

    /// <summary>0 是"全过"，比它大的才算问题；跳过（3）不能盖掉一个已经出现的失败。</summary>
    private static int Worse(int a, int b)
    {
        if (b == 0) return a;
        if (a == 2 || b == 2) return 2;
        if (a == 1 || b == 1) return 1;
        return a == 0 ? b : a;
    }

    private static int Scenario(
        StringBuilder sb, nint hwnd, AppConfig cfg, AltDragSink sink, int hold,
        string label, Func<HdNative.RECT, (int X, int Y)> pick,
        uint downFlag, uint upFlag, int dx, int dy, int steps, Expect expect)
    {
        // 两次按下之间必须离开系统的双击时间窗：紧挨着的第二下会被判成双击
        // （移动→最大化、缩放→半屏），量到的就不再是拖拽几何了。
        Pump(700);
        sb.AppendLine();
        sb.AppendLine("=== " + label + " ===");

        if (!HdNative.GetWindowRect(hwnd, out var before))
        {
            sb.AppendLine("  失败 —— 取不到窗口矩形");
            return 2;
        }
        var (x, y) = pick(before);
        HdNative.SetCursorPos(x, y);
        Pump(60);
        nint under = HdNative.WindowFromPoint(new HdNative.POINT { X = x, Y = y });
        if (under != nint.Zero)
        {
            under = HdNative.GetAncestor(under, HdNative.GA_ROOT);
        }
        if (under != hwnd)
        {
            sb.AppendLine("  跳过 —— 光标下不是靶子窗口（0x" + under.ToString("X") + "），环境不可用");
            return 3;
        }

        sb.AppendLine("  按下点=(" + x + "," + y + ")  矩形前=" + Rect(before.Left, before.Top, before.Right, before.Bottom));
        Key(VkReturn, true);
        Pump(hold + 140);
        sb.AppendLine("  触发键按住=" + sink.TriggerHeld + "（期望 True）");

        HdNative.SetCursorPos(x, y);
        Pump(40);
        Mouse(downFlag);
        Pump(40);
        for (int i = 1; i <= steps; i++)
        {
            HdNative.SetCursorPos(x + dx * i / steps, y + dy * i / steps);
            Pump(16);
        }
        Pump(80);
        Mouse(upFlag);
        Pump(80);
        Key(VkReturn, false);
        Pump(220);

        if (!HdNative.GetWindowRect(hwnd, out var after))
        {
            sb.AppendLine("  失败 —— 取不到拖动后的窗口矩形");
            return 2;
        }
        int dl = after.Left - before.Left;
        int dt = after.Top - before.Top;
        int dr = after.Right - before.Right;
        int db = after.Bottom - before.Bottom;
        sb.AppendLine("  矩形后=" + Rect(after.Left, after.Top, after.Right, after.Bottom) +
                      "  边增量 左=" + dl + " 上=" + dt + " 右=" + dr + " 下=" + db);

        bool ok;
        switch (expect)
        {
            case Expect.NoChange:
                ok = Math.Abs(dl) <= 2 && Math.Abs(dt) <= 2 && Math.Abs(dr) <= 2 && Math.Abs(db) <= 2;
                sb.AppendLine("  判定：" + (ok ? "通过 —— 短按没有动窗口" : "失败 —— 短按把窗口动了（会误触发拖拽）"));
                break;
            case Expect.GrowBottomRight:
                ok = dr >= dx * 6 / 10 && db >= dy * 6 / 10 && Math.Abs(dl) <= 2 && Math.Abs(dt) <= 2;
                sb.AppendLine("  判定：" + (ok ? "通过 —— 右下角跟着光标长大了，左上角没动"
                                              : "失败 —— 右键拖拽没有调整大小（或整窗被搬走、左上角跟着跑了）"));
                break;
            case Expect.GrowTopLeft:
                ok = dl <= dx * 6 / 10 && dt <= dy * 6 / 10 && Math.Abs(dr) <= 2 && Math.Abs(db) <= 2;
                sb.AppendLine("  判定：" + (ok ? "通过 —— 左上角跟着光标往外长了，右下角没动"
                                              : "失败 —— 拖左上角没有按「改大小」处理（或整窗被搬走）"));
                break;
            default:
                ok = false;
                break;
        }
        return ok ? 0 : 1;
    }
}
