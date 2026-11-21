using System.Globalization;
using System.Text;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.App;

/// <summary>
/// 注入下沉通道：提升进程把"要按的键"打包成一行文本，交给普通权限的
/// 钩子宿主（--hookhost）去真正调用 SendInput。
///
/// 为什么不让提升进程自己发：
///   * 钩子在宿主里。注入端和钩子端**不在同一完整性级别**时，宿主看不到
///     这些注入事件，回声抑制（dwExtraInfo = CLP!）就失效了 —— 表现是
///     "自己发的键把自己又触发一遍"；
///   * 最小权限：管理员进程不该去碰别的程序的输入队列。
///
/// 协议（一行一条命令，走 hookhost 的控制管道）：
///   INJECT|k:&lt;vk&gt;:&lt;scan&gt;:&lt;flags&gt;:&lt;tag&gt;|m:&lt;dx&gt;:&lt;dy&gt;:&lt;data&gt;:&lt;flags&gt;:&lt;tag&gt;
/// 一条命令可以带多个事件，宿主**整批**交给一次 SendInput —— 按下+抬起
/// 不会被别的注入插到中间。
/// </summary>
internal static class InjectionBridge
{
    /// <summary>命令前缀（宿主按它识别）。</summary>
    internal const string Prefix = "INJECT";

    /// <summary>
    /// 主进程侧：把一行命令交给宿主控制管道。为 null 表示"下沉通道没接上"。
    /// 由 AppHost 在宿主 READY 之后接上，在宿主退出/重启时断开。
    /// </summary>
    internal static Func<string, bool>? SendLine { get; set; }

    /// <summary>下沉通道是否在线。</summary>
    internal static bool IsLive
    {
        get { return SendLine != null; }
    }

    /// <summary>本次会话下沉了多少次（日志/自检用）。</summary>
    internal static int ForwardedCount { get; private set; }

    /// <summary>下沉失败（通道断了）次数。</summary>
    internal static int ForwardFailedCount { get; private set; }

    /// <summary>断开下沉通道（宿主退出、重启、关闭时调用）。</summary>
    internal static void Detach()
    {
        SendLine = null;
    }

    /// <summary>仅供自检：重置计数与通道。</summary>
    internal static void ResetForTest()
    {
        SendLine = null;
        ForwardedCount = 0;
        ForwardFailedCount = 0;
    }

    /// <summary>把一批输入事件编码成一行命令（纯函数，自检会做往返比对）。</summary>
    internal static string Encode(INPUT[] inputs)
    {
        var sb = new StringBuilder(Prefix);
        if (inputs != null)
        {
            foreach (var input in inputs)
            {
                if (input.type == NativeMethods.INPUT_KEYBOARD)
                {
                    sb.Append("|k:")
                      .Append(input.U.ki.wVk.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.ki.wScan.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.ki.dwFlags.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.ki.dwExtraInfo.ToInt64().ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("|m:")
                      .Append(input.U.mi.dx.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.mi.dy.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.mi.mouseData.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.mi.dwFlags.ToString(CultureInfo.InvariantCulture)).Append(':')
                      .Append(input.U.mi.dwExtraInfo.ToInt64().ToString(CultureInfo.InvariantCulture));
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>解码一行命令。格式不认识就返回空数组（宿主据此忽略并记日志）。</summary>
    internal static INPUT[] Decode(string? line)
    {
        if (string.IsNullOrEmpty(line)) return Array.Empty<INPUT>();

        string[] parts = line.Split('|');
        if (parts.Length < 2 || !parts[0].StartsWith(Prefix, StringComparison.Ordinal)) return Array.Empty<INPUT>();

        var list = new List<INPUT>(parts.Length - 1);
        for (int i = 1; i < parts.Length; i++)
        {
            string[] f = parts[i].Split(':');
            try
            {
                if (f.Length == 5 && f[0] == "k")
                {
                    var input = new INPUT { type = NativeMethods.INPUT_KEYBOARD };
                    input.U.ki.wVk = (ushort)uint.Parse(f[1], CultureInfo.InvariantCulture);
                    input.U.ki.wScan = (ushort)uint.Parse(f[2], CultureInfo.InvariantCulture);
                    input.U.ki.dwFlags = uint.Parse(f[3], CultureInfo.InvariantCulture);
                    input.U.ki.dwExtraInfo = new IntPtr(long.Parse(f[4], CultureInfo.InvariantCulture));
                    list.Add(input);
                }
                else if (f.Length == 6 && f[0] == "m")
                {
                    var input = new INPUT { type = NativeMethods.INPUT_MOUSE };
                    input.U.mi.dx = int.Parse(f[1], CultureInfo.InvariantCulture);
                    input.U.mi.dy = int.Parse(f[2], CultureInfo.InvariantCulture);
                    input.U.mi.mouseData = uint.Parse(f[3], CultureInfo.InvariantCulture);
                    input.U.mi.dwFlags = uint.Parse(f[4], CultureInfo.InvariantCulture);
                    input.U.mi.dwExtraInfo = new IntPtr(long.Parse(f[5], CultureInfo.InvariantCulture));
                    list.Add(input);
                }
            }
            catch (FormatException) { /* 单条坏了就丢掉这一条，别让整批失效 */ }
            catch (OverflowException) { }
        }
        return list.ToArray();
    }

    /// <summary>宿主侧：解码并真的把输入发出去。返回实际提交的事件数。</summary>
    internal static int ApplyFromWire(string? line)
    {
        INPUT[] inputs = Decode(line);
        if (inputs.Length == 0) return 0;
        return InputSender.SendFromHost(inputs) ? inputs.Length : 0;
    }

    /// <summary>主进程侧：尝试把这一批输入下沉给宿主。</summary>
    internal static bool TryForward(INPUT[] inputs)
    {
        var forward = SendLine;
        if (forward == null) return false;

        bool ok;
        try { ok = forward(Encode(inputs)); }
        catch { ok = false; }

        if (ok)
        {
            ForwardedCount++;
        }
        else
        {
            ForwardFailedCount++;
        }
        return ok;
    }
}
