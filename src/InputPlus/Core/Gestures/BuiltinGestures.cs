using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using InputPlus.Core.Settings;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 1.0 起内置的手势库：把一套调好的鼠标手势直接带在软件里，
/// 新装（或把手势表清空）的用户开机就能用，不必先去 WGestures 里导一份。
///
/// 数据在 Assets/gestures-builtin.json（嵌入资源），生成时已经脱敏：
/// 本机用户名、个人脚本路径一律换成 %LOCALAPPDATA% / %ProgramFiles% 这类
/// 环境变量占位（运行期由 <see cref="Core.Actions.ActionContext.RunProgram"/>
/// 展开），依赖本机脚本的那一条没有进库。
///
/// 为什么用 JSON 资源而不是一堆 C# 对象初始化：手势带轨迹点数组，
/// 用对象写出来是几千行噪声；JSON 还能直接和导出的配置对照，改起来也不容易错。
/// </summary>
internal static class BuiltinGestures
{
    /// <summary>嵌入资源的清单名（默认由 csproj 的 LogicalName 规则拼出来）。</summary>
    private const string ResourceName = "InputPlus.Assets.gestures-builtin.json";

    /// <summary>库的版本：以后换一套手势就 +1，配合 <see cref="GestureOptions.BuiltinsVersion"/> 只铺一次。</summary>
    public const int Version = 1;

    private sealed class Pack
    {
        public int Version { get; set; }
        public List<MouseGesture>? Gestures { get; set; }
    }

    /// <summary>
    /// 读出内置手势（每次都是新对象，调用方可以随便改）。
    /// 资源缺失/格式坏了返回空表 —— 内置手势是"锦上添花"，读不出来也绝不能拦住启动。
    /// </summary>
    public static List<MouseGesture> Create()
    {
        var list = new List<MouseGesture>();
        try
        {
            using Stream? stream = typeof(BuiltinGestures).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                CoreLog.Warn("[手势] 找不到内置手势资源 " + ResourceName + "，跳过");
                return list;
            }

            var pack = JsonSerializer.Deserialize<Pack>(stream, SettingsStore.JsonOptions);
            if (pack?.Gestures == null || pack.Gestures.Count == 0) return list;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in pack.Gestures)
            {
                if (g == null || string.IsNullOrWhiteSpace(g.Name)) continue;
                if (string.IsNullOrWhiteSpace(g.Id) || !seen.Add(g.Id)) g.Id = MouseGesture.NewId();
                seen.Add(g.Id);
                g.Enabled = true;
                list.Add(g);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[手势] 读内置手势库失败：" + ex.Message);
            list.Clear();
        }
        return list;
    }
}
