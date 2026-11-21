using System;
using System.Text;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;
using HdNative = InputPlus.Modules.HoldDrag.Native;

namespace InputPlus.Core.HoldDrag;

/// <summary>
/// `--holddrag-config`：把「按住拖拽」的**运行期真实配置**打出来，并顺带做一遍九宫格选边演算。
///
/// 为什么需要它：用户报「右键缩放有时候只能上下或左右，不能同时」（m05425）。选边只由两个
/// 参数决定 —— CenterFraction（中间带占宽/高的百分比）与 ResizeCenter（抓到正中间干什么），
/// 而这两个参数**设置界面没有暴露**，只存在加密的 settings.json 的 HoldDragJson 里。
/// 光读代码读不出用户手上是哪一组值，于是只能在同一次运行里把「配置 + 演算结果」一起打出来。
///
/// 演算用的是与真拖拽完全相同的两个函数（<see cref="AltDragCore.SetEdgeAndOffset"/> +
/// <see cref="AltDragCore.ApplyResizeCenter"/>），所以表里出现「单轴」就是真拖拽会出现的单轴。
/// </summary>
internal static class HoldDragConfigCli
{
    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        AppSettings settings = SettingsStore.Load();
        AppConfig cfg = AppConfig.Parse(settings.HoldDragJson);
        cfg.Normalize();

        sb.AppendLine("按住拖拽配置（" + AppPaths.DataDir + "）");
        sb.AppendLine("  总开关 HoldDragEnabled = " + settings.HoldDragEnabled);
        sb.AppendLine("  HoldDragJson 长度 = " + (settings.HoldDragJson ?? "").Length +
                      (string.IsNullOrWhiteSpace(settings.HoldDragJson) ? "（没存过，全部走出厂值）" : ""));
        sb.AppendLine();
        sb.AppendLine("  触发键        = " + cfg.TriggerKey + "（" + KeysUtil.DisplayName(cfg.TriggerKey) + "）");
        sb.AppendLine("  长按阈值      = " + cfg.TriggerHoldMs + " ms");
        sb.AppendLine("  设备范围      = " + AppConfig.DeviceLabel(cfg.Device));
        sb.AppendLine("  吞掉触发键    = " + cfg.SwallowTrigger);
        sb.AppendLine("  左/右/中键    = " + cfg.ActionLeft + " / " + cfg.ActionRight + " / " + cfg.ActionMiddle);
        sb.AppendLine("  X1/X2        = " + cfg.ActionX1 + " / " + cfg.ActionX2);
        sb.AppendLine("  抽帧率        = 移动 " + cfg.MoveRate + " / 缩放 " + cfg.ResizeRate);
        sb.AppendLine("  CenterFraction= " + cfg.CenterFraction + " %（中间带；出厂 " + AppConfig.DefaultCenterFraction + "）");
        sb.AppendLine("  SidesFraction = " + cfg.SidesFraction + "（已不参与选边，只做 ini 往返）");
        sb.AppendLine("  ResizeCenter  = " + cfg.ResizeCenter + " " + ResizeCenterLabel(cfg.ResizeCenter));
        /* 这两个是"右键只能单轴"那条 bug 的补丁开关：关掉 ResizeBothAxes 就退回纯九宫格，
         * 阈值以内的位移不计入该轴 —— 排障时必须能一眼看到它们当前是多少。 */
        sb.AppendLine("  同时两轴      = " + (cfg.ResizeBothAxes ? "True（一轴在中间带时按手势收养）" : "False（纯九宫格，一轴必被锁死）"));
        sb.AppendLine("  收养阈值      = " + cfg.ResizeAxesThreshold + " px（越过它才把中间带那一轴跟着改）");
        sb.AppendLine("  AutoSnap      = " + cfg.AutoSnap + "   SnapThreshold = " + cfg.SnapThreshold + " px");

        int sideS = (100 - cfg.CenterFraction) / 2;
        sb.AppendLine();
        sb.AppendLine("  边带宽度 = 宽/高的 " + sideS + "%，中间带 = " + Math.Max(0, 100 - 2 * sideS) + "%");
        if (cfg.ResizeCenter == 3)
        {
            sb.AppendLine("  ⚠ ResizeCenter=3：抓到正中间只会按最近的一条边缩放 —— 这一档天然是单轴。");
        }
        if (sideS * 2 >= 100)
        {
            sb.AppendLine("  ⚠ CenterFraction 太小（或为 0）：中间带消失，任何一点都会命中边带。");
        }

        sb.AppendLine();
        sb.AppendLine("九宫格演算（窗口 400x300 @ 100,100）");
        sb.AppendLine("  抓点           EdgeX/EdgeY       结果");
        HdNative.RECT wnd = new HdNative.RECT { Left = 100, Top = 100, Right = 500, Bottom = 400 };
        (string Label, int X, int Y)[] probes =
        {
            ("左上角", 104, 104),
            ("上边中", 300, 104),
            ("右上角", 496, 104),
            ("左边中", 104, 250),
            ("正中间", 300, 250),
            ("右边中", 496, 250),
            ("左下角", 104, 396),
            ("下边中", 300, 396),
            ("右下角", 496, 396),
        };
        foreach ((string label, int px, int py) in probes)
        {
            var alt = new AltState();
            var pt = new HdNative.POINT(px, py);
            AltDragCore.SetEdgeAndOffset(alt, wnd, pt, cfg);
            bool move = false;
            AltDragCore.ApplyResizeCenter(alt, wnd, pt, cfg, out move);
            string verdict = move
                ? "换成移动"
                : alt.EdgeX != DragEdge.Center && alt.EdgeY != DragEdgeY.Center
                    ? "宽高同时变"
                    : alt.EdgeX == DragEdge.Center && alt.EdgeY == DragEdgeY.Center
                        ? "四向扩张（宽高同时变）"
                        : "单轴（" + (alt.EdgeX == DragEdge.Center ? "只变高" : "只变宽") + "）";
            sb.AppendLine("  " + Pad(label, 12) + " " + Pad(alt.EdgeX + "/" + alt.EdgeY, 18) + " " + verdict);
        }

        sb.AppendLine();
        sb.AppendLine("提示：想改这几个值，可在设置 → 按住拖拽里改（界面没暴露的项存进 HoldDragJson，需手改 JSON）。");
        string text = sb.ToString();
        Console.Out.Write(text);
        try
        {
            string file = System.IO.Path.Combine(AppPaths.DataDir, "holddrag-config.txt");
            System.IO.File.WriteAllText(file, text, Encoding.UTF8);
        }
        catch
        {
            // 写不出文件不影响结论：标准输出已经有了。
        }
        return 0;
    }

    private static string ResizeCenterLabel(int value) => value switch
    {
        0 => "（固定改右下）",
        1 => "（四向扩张，出厂值）",
        2 => "（转成移动）",
        3 => "（按最近的一条边 —— 单轴）",
        _ => "（未知，按四向扩张处理）",
    };

    private static string Pad(string text, int width)
    {
        int len = 0;
        foreach (char c in text)
        {
            len += c > 0x7F ? 2 : 1;
        }
        var sb = new StringBuilder(text);
        for (int i = len; i < width; i++)
        {
            sb.Append(' ');
        }
        return sb.ToString();
    }
}
