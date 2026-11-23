using System;
using InputPlus.Core.Interop;

namespace InputPlus.Core.Windows;

/// <summary>一条"NT 设备路径 → DOS 盘符"的映射。</summary>
/// <param name="Device">形如 <c>\Device\HarddiskVolume3</c>。</param>
/// <param name="Dos">形如 <c>C:</c>。</param>
internal readonly record struct DeviceMapping(string Device, string Dos);

/// <summary>
/// NT 设备路径 → 盘符（v1.5.1，「收藏当前文件」用）。
///
/// 句柄名字查出来是 <c>\Device\HarddiskVolume3\Users\me\a.pdf</c> 这种，
/// 用户要的是 <c>C:\Users\me\a.pdf</c>。映射只能靠 <c>QueryDosDevice</c> 问系统，
/// 而"问一遍 26 个盘符"在一次收藏里做一次就够了 —— 所以：
///   · <see cref="ToDosPath"/> 第一次调用时枚举一次并缓存（盘符映射在进程生命周期内基本不变，
///     插拔 U 盘最坏也就是新盘符查不到、落回原样交给上层判"不可达"）；
///   · 排序按**设备名长度倒序**：不排的话 <c>\Device\HarddiskVolume1</c> 会把
///     <c>\Device\HarddiskVolume10</c> 的前缀吃掉，第十块盘的文件全变成第一块盘的路径。
///   · <see cref="Translate"/> 是纯函数（映射从外面喂），自检可以直接断言，不用真挂一块盘。
/// </summary>
internal static class DosDeviceMap
{
    private static readonly object Gate = new();

    private static DeviceMapping[]? _cached;

    /// <summary>NT 设备路径 → DOS 路径；认不出来时返回空串（调用方据此判"不是本地盘上的东西"）。</summary>
    internal static string ToDosPath(string? ntPath) => Translate(ntPath, EnsureMap());

    /// <summary>
    /// 纯翻译：映射请按"设备名长度倒序"传进来（<see cref="EnsureMap"/> 已经排好）。
    /// 网络路径（<c>\Device\Mup\...</c>）单独处理 —— 它没有盘符，正确形式是 <c>\\server\share</c>。
    /// </summary>
    internal static string Translate(string? ntPath, IReadOnlyList<DeviceMapping> mappings)
    {
        string path = (ntPath ?? "").Trim();
        if (path.Length == 0) return "";

        /* 网络：\Device\Mup\srv\share\x.pdf → \\srv\share\x.pdf
         * LanmanRedirector 是同一个东西的老名字，企业环境里还会见到。 */
        if (StartsWith(path, @"\Device\Mup\")) return @"\\" + path[12..];
        if (StartsWith(path, @"\Device\LanmanRedirector\")) return @"\\" + path[24..];

        if (mappings != null)
        {
            foreach (DeviceMapping m in mappings)
            {
                if (m.Device.Length == 0) continue;
                if (!StartsWith(path, m.Device)) continue;
                return m.Dos + path[m.Device.Length..];
            }
        }
        return "";
    }

    /// <summary>仅供自检：把缓存清掉（下一次 <see cref="ToDosPath"/> 重新枚举）。</summary>
    internal static void ResetForTest()
    {
        lock (Gate) _cached = null;
    }

    private static DeviceMapping[] EnsureMap()
    {
        DeviceMapping[]? cached = _cached;
        if (cached != null) return cached;

        lock (Gate)
        {
            if (_cached != null) return _cached;

            var list = new List<DeviceMapping>(26);
            try
            {
                uint mask = NativeMethods.GetLogicalDrives();
                var buffer = new System.Text.StringBuilder(1024);

                for (int i = 0; i < 26; i++)
                {
                    if ((mask & (1u << i)) == 0) continue;   // 这个盘符没挂东西：QueryDosDevice 会失败，不问

                    string letter = ((char)('A' + i)).ToString() + ":";
                    buffer.Clear();
                    int written = NativeMethods.QueryDosDevice(letter, buffer, buffer.Capacity);
                    if (written <= 0) continue;

                    /* 一个盘符可能对应多个设备名（subst、卷影、网络重定向），用 \0 分隔；
                     * 取第一个 \Device\ 开头的就够。 */
                    foreach (string name in buffer.ToString().Split('\0'))
                    {
                        string device = name.Trim();
                        if (device.Length == 0) continue;
                        if (!device.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new DeviceMapping(device, letter));
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                CoreLog.Swallow(ex, "收藏：枚举盘符映射");
            }

            /* 长度倒序：HarddiskVolume10 必须排在 HarddiskVolume1 前面，否则前缀会被吃错。 */
            list.Sort((a, b) => b.Device.Length.CompareTo(a.Device.Length));
            _cached = list.ToArray();
            if (_cached.Length == 0) CoreLog.Warn("[收藏] 一个盘符映射都没拿到：NT 路径将无法转成本地路径");
            return _cached;
        }
    }

    private static bool StartsWith(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
}
