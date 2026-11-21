using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace InputPlus.Core.Icons;

/// <summary>
/// Windows Shell 图标解析器：Qbar 结果行、启动程序表、设置页的文件图标统一走这里。
///
/// 为什么不能自己解码：旧实现把「不是 exe/dll/快捷方式」的文件一律丢给
/// <c>BitmapImage</c> 解码。PDF、Office 文档、未知扩展名、无扩展名的文件
/// 根本不是图片，解码必然抛异常 → 返回 null → 结果行退化成字形图标。
/// 资源管理器显示的是「该扩展名关联程序的图标」（PDF 就是系统默认处理器），
/// 只有 Shell 知道这个对应关系，所以我们只问 Shell。
///
/// 取图顺序（每一条都对应一种真实场景）：
///   ① <b>IShellItemImageFactory::GetImage</b>：能拿到关联程序图标（PDF=系统默认
///      阅读器）、UWP/包应用图标、.exe 内嵌图标，而且支持任意像素尺寸；
///   ② 快捷方式（.lnk/.url/.appref-ms/.pif）改走
///      <c>SHGetFileInfo(SHGFI_ICON|SHGFI_LINKOVERLAY|SHGFI_LARGEICON)</c>，
///      保证小箭头不丢（GetImage 不合成叠加图标）；
///   ③ 回退：<c>SHGetFileInfo(SHGFI_SYSICONINDEX)</c> + <c>SHGetImageList</c>
///      (JUMBO 256 / EXTRALARGE 48 / LARGE 32 / SMALL 16) + <c>IImageList::GetIcon</c>。
///
/// 线程模型：全部解析跑在**一条后台 STA 线程**上（Shell 的 COM 对象要求 STA），
/// 调用方可选同步等待（`RequestSync`，给设置页那种「一行一个图标、
/// 没有异步刷新机制」的老调用点用）或异步回调（`Request`，Qbar 每敲一个字
/// 都会重建结果行，绝不能阻塞 UI）。同 key 请求合并；关联变更后旧请求的结果直接丢弃。
/// </summary>
public static class ShellIcons
{
    /* Qbar 结果行的图标格是 28 DIP（UI/Windows/QbarWindow.xaml 的 IconTile），
     * 这里给个默认值，避免调用方各写一遍魔法数字。 */
    public const int DefaultDip = 28;

    /// <summary>取图档位。向上取档，绝不把 16px 放大到 42px 去糊。</summary>
    private static readonly int[] Buckets = new int[] { 16, 24, 32, 48, 64, 96, 256 };

    /// <summary>档位表（只读视图，自检用来断言档位）。</summary>
    public static int[] PixelBuckets => (int[])Buckets.Clone();

    private const int NetworkTimeoutMs = 400;
    private const int LocalSyncTimeoutMs = 2500;
    private const int NegativeTtlMs = 60_000;
    private const int StampTtlMs = 4_000;
    private const int CacheCap = 512;

    // ---------------------------------------------------------------- 缓存

    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> Negative = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> AssocStamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Stamp> FileStamps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> PathHits = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> Logged = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, Completion> Pending = new(StringComparer.Ordinal);

    /// <summary>缓存代号：关联变更 / Invalidate 时 +1，旧代号的结果一律丢弃。</summary>
    private static long _epoch;

    private struct Stamp
    {
        public long Ticks;
        public long At;
    }

    // ---------------------------------------------------------------- 公共 API

    /// <summary>
    /// 把 DIP 尺寸 × DPI 换算成取图档位。
    /// 28 DIP 在 100%/125%/150%/200% 下分别落到 32/48/48/64 像素，
    /// 与资源管理器「大图标」在同样缩放下的取样一致。
    /// </summary>
    public static int PickPixels(int dip, double dpiScale)
    {
        double scale = dpiScale > 0 && !double.IsNaN(dpiScale) ? dpiScale : 1.0;
        int want = (int)Math.Ceiling(Math.Max(1, dip) * scale);
        for (int i = 0; i < Buckets.Length; i++)
        {
            if (want <= Buckets[i]) return Buckets[i];
        }
        return Buckets[Buckets.Length - 1];
    }

    /// <summary>系统 DPI 缩放（窗口级 DPI 请调用方用 VisualTreeHelper.GetDpi 自己取）。</summary>
    public static double SystemDpiScale()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi > 0) return dpi / 96.0;
        }
        catch (EntryPointNotFoundException) { }   // Win7（net7 线）：没有这个 API
        catch (DllNotFoundException) { }

        try
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                int dpi = GetDeviceCaps(dc, LOGPIXELSX);
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }
        return 1.0;
    }

    /// <summary>
    /// 同步取**已经解析过**的图标。永不阻塞、永不碰磁盘（文件时间戳走 4 秒 TTL 的缓存，
    /// 网络路径不探测）—— 给 Qbar 结果行的第一帧用。
    /// </summary>
    public static ImageSource? TryGetCached(string? path, int pixelSize, double dpiScale)
    {
        string norm = NormalizePath(path);
        if (norm.Length == 0) return null;
        string key = BuildKey(norm, ClampPixel(pixelSize), NormDpi(dpiScale));
        return Cache.TryGetValue(key, out ImageSource? image) ? image : null;
    }

    /// <summary>
    /// 异步解析（同 key 合并）。回调在**后台线程**触发，UI 调用方自己 Dispatcher 切回去；
    /// 命中缓存或已判定失败时，回调会立刻排到线程池，不会同步回调进调用栈。
    /// </summary>
    public static void Request(string? path, int pixelSize, double dpiScale, Action<ImageSource?>? done)
    {
        string norm = NormalizePath(path);
        if (norm.Length == 0)
        {
            Post(done, null);
            return;
        }

        int px = ClampPixel(pixelSize);
        double dpi = NormDpi(dpiScale);
        string key = BuildKey(norm, px, dpi);
        long epoch = Volatile.Read(ref _epoch);

        if (Cache.TryGetValue(key, out ImageSource? cached))
        {
            Post(done, cached);
            return;
        }
        if (IsNegativeFresh(key))
        {
            Post(done, null);
            return;
        }

        Completion completion = Attach(key, norm, px, epoch);
        if (done != null)
        {
            completion.Tcs.Task.ContinueWith(
                t => { try { done(t.Status == TaskStatus.RanToCompletion ? t.Result : null); } catch { } },
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 同步解析：给「一行一个图标、没有异步刷新」的老调用点（设置页、按键页）用。
    /// 超时返回 null 并把请求留在队列里跑完 —— 下次重绘/重开页面就能命中缓存。
    /// 注意：**不要在 UI 线程上对网络路径调用它**（内部会直接放弃等待）。
    /// </summary>
    public static ImageSource? RequestSync(string? path, int pixelSize, double dpiScale, int timeoutMs = LocalSyncTimeoutMs)
    {
        string norm = NormalizePath(path);
        if (norm.Length == 0) return null;

        int px = ClampPixel(pixelSize);
        double dpi = NormDpi(dpiScale);
        string key = BuildKey(norm, px, dpi);
        long epoch = Volatile.Read(ref _epoch);

        if (Cache.TryGetValue(key, out ImageSource? cached)) return cached;
        if (IsNegativeFresh(key)) return null;

        /* 在解析线程上等自己 = 必然超时，直接放弃（正常调用路径不会走到）。 */
        if (Thread.CurrentThread == Worker) return null;

        Completion completion = Attach(key, norm, px, epoch);

        int budget = IsNetworkPath(norm) ? Math.Min(timeoutMs, NetworkTimeoutMs) : timeoutMs;
        try
        {
            if (!completion.Tcs.Task.Wait(budget))
            {
                WarnOnce("timeout:" + key, "图标解析超时（" + norm + "），先显示占位图标");
                return null;
            }
            return completion.Tcs.Task.Result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 整体失效：关联方式变了（默认程序被改）、用户改了图标、主题切换。
    /// 由 SystemEvents.UserPreferenceChanged 自动触发，也可以手工调。
    /// </summary>
    public static void Invalidate(string? reason = null)
    {
        Interlocked.Increment(ref _epoch);
        Cache.Clear();
        Negative.Clear();
        AssocStamps.Clear();
        FileStamps.Clear();
        PathHits.Clear();
        if (!string.IsNullOrEmpty(reason))
        {
            CoreLog.Info("Shell 图标缓存已失效：" + reason);
        }
    }

    /// <summary>
    /// 规范化路径：展开环境变量、去引号、去尾随空格、超长路径补 \\?\ 前缀。
    /// 缓存 key 用的就是这个形态（同一个文件的不同写法必须命中同一个 key）。
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        string text = path.Trim().Trim('"');
        try { text = Environment.ExpandEnvironmentVariables(text); } catch { }

        /* Windows 的文件名不允许尾随空格，用户从聊天窗口粘过来时常常带一个；
         * 不去掉的话 "a.pdf " 与 "a.pdf" 会是两个缓存 key。 */
        text = text.TrimEnd(' ');

        /* 超过 MAX_PATH 的路径必须写成 \\?\ 形式，否则 Shell 的
         * SHCreateItemFromParsingName 会直接失败（长路径是继权限不足之后最常见的
         * 「文件明明在，却说找不到」）。UNC 路径的前缀是 \\?\UNC\。 */
        try
        {
            if (text.Length >= 260 &&
                Path.IsPathRooted(text) &&
                !text.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                text = text.StartsWith(@"\\", StringComparison.Ordinal)
                    ? @"\\?\UNC\" + text.Substring(2)
                    : @"\\?\" + text;
            }
        }
        catch { }
        return text;
    }

    /// <summary>
    /// 这个值能不能交给 Shell 解析成「某个文件/文件夹/命名空间项」。
    /// 纯粹的搜索词、网址、窗口句柄都不算 —— 它们没有文件图标可拿。
    /// </summary>
    public static bool IsShellTarget(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim().Trim('"');
        if (text.Length == 0) return false;
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("::", StringComparison.Ordinal)) return true;
        if (text.Contains("://", StringComparison.Ordinal)) return false;
        try { return Path.IsPathRooted(text); }
        catch { return false; }
    }

    /// <summary>图片类扩展名（这些仍然用 WPF 解码原图，不交给 Shell）。</summary>
    public static bool IsImageFile(string? path)
    {
        string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        switch (ext)
        {
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp":
            case ".gif":
            case ".tif":
            case ".tiff":
            case ".ico":
            case ".webp":
            case ".svg":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 把「裸程序名」解析成磁盘路径（启动程序表里很多只写了 notepad.exe）。
    /// 解析结果带 5 分钟级别的进程内缓存，避免每次按键都扫一遍 PATH。
    /// </summary>
    public static string? ResolveFilesystemPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = NormalizePath(value);
        if (text.Length == 0) return null;
        if (text.Contains("://", StringComparison.Ordinal)) return null;
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            if (File.Exists(text) || Directory.Exists(text)) return text;

            /* 带目录的相对路径不猜：相对谁没有意义。 */
            if (text.IndexOf('\\') >= 0 || text.IndexOf('/') >= 0) return null;

            if (PathHits.TryGetValue(text, out string? hit))
            {
                return hit.Length == 0 ? null : hit;
            }

            string? resolved = null;
            string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), text);
                    if (File.Exists(candidate)) { resolved = candidate; break; }
                }
                catch { }
            }

            /* 找没找到都记住：没找到的裸名字反复扫 PATH 是纯浪费。 */
            PathHits[text] = resolved ?? "";
            return resolved;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "解析程序路径");
            return null;
        }
    }

    /// <summary>是不是网络路径（UNC 或映射的网络盘）。不碰网络，只问 shlwapi。</summary>
    public static bool IsNetworkPath(string? path)
    {
        string text = path ?? "";
        if (text.Length < 2) return false;
        if (text.StartsWith(@"\\", StringComparison.Ordinal) &&
            !text.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return true;
        }
        try { return PathIsNetworkPath(text); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 缓存 key

    /// <summary>
    /// key = 规范化路径 + 像素档位 + DPI + 文件时间戳 + 关联版本戳。
    /// 时间戳用 <see cref="StampOf"/>（4 秒 TTL、网络路径不探测），
    /// 关联版本戳用 <see cref="AssocStamp"/>，两者都在这里拼进 key。
    /// </summary>
    private static string BuildKey(string normPath, int pixelSize, double dpiScale)
    {
        string ext = Path.GetExtension(normPath) ?? "";
        string assoc = AssocStamp(ext);
        long stamp = StampOf(normPath);
        return string.Concat(
            normPath, "\u0001",
            pixelSize.ToString(CultureInfo.InvariantCulture), "\u0001",
            dpiScale.ToString("0.###", CultureInfo.InvariantCulture), "\u0001",
            stamp.ToString(CultureInfo.InvariantCulture), "\u0001",
            assoc);
    }

    /// <summary>
    /// 文件时间戳（LastWriteTimeUtc.Ticks）。给缓存 key 用：
    /// 同名文件被替换（下载覆盖、Office 另存）后图标可能真的变了。
    /// 网络路径不在这里探测 —— 那会把 UI 线程卡在 SMB 超时上。
    /// </summary>
    private static long StampOf(string normPath)
    {
        if (IsNetworkPath(normPath)) return 0;

        long now = Environment.TickCount64;
        if (FileStamps.TryGetValue(normPath, out Stamp stamp) && now - stamp.At < StampTtlMs)
        {
            return stamp.Ticks;
        }

        long ticks = 0;
        try
        {
            ticks = Directory.Exists(normPath)
                ? Directory.GetLastWriteTimeUtc(normPath).Ticks
                : File.GetLastWriteTimeUtc(normPath).Ticks;
        }
        catch { ticks = 0; }

        FileStamps[normPath] = new Stamp { Ticks = ticks, At = now };
        return ticks;
    }

    /// <summary>
    /// 关联版本戳：默认程序 / DefaultIcon 变了，同一路径的图标就该换。
    /// 覆盖三处（都在注册表里，都是几十微秒级的读，按扩展名记忆化）：
    ///   · HKCU\...\Explorer\FileExts\&lt;ext&gt;\UserChoice → ProgId
    ///   · HKCR\&lt;ProgId&gt;\DefaultIcon 的默认值
    ///   · HKCU\...\Explorer\FileExts 键的 LastWriteTime
    /// </summary>
    private static string AssocStamp(string ext)
    {
        if (string.IsNullOrEmpty(ext)) return "";
        if (AssocStamps.TryGetValue(ext, out string? cached)) return cached;

        string progId = "";
        string defIcon = "";
        long feTicks = 0;
        try
        {
            using (RegistryKey? userChoice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + ext + @"\UserChoice"))
            {
                progId = userChoice?.GetValue("ProgId") as string ?? "";
            }
            if (progId.Length == 0)
            {
                using RegistryKey? extKey = Registry.ClassesRoot.OpenSubKey(ext);
                progId = extKey?.GetValue(null) as string ?? "";
            }
            if (progId.Length > 0)
            {
                using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey(progId + @"\DefaultIcon");
                defIcon = iconKey?.GetValue(null) as string ?? "";
            }
            if (defIcon.Length == 0)
            {
                using RegistryKey? iconKey = Registry.ClassesRoot.OpenSubKey(ext + @"\DefaultIcon");
                defIcon = iconKey?.GetValue(null) as string ?? "";
            }
            using (RegistryKey? fe = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts"))
            {
                if (fe != null) feTicks = RegistryLastWriteTicks(fe);
            }
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "读取文件关联版本戳");
        }

        string stamp = progId + "|" + defIcon + "|" + feTicks.ToString(CultureInfo.InvariantCulture);
        AssocStamps[ext] = stamp;
        return stamp;
    }

    /// <summary>
    /// 注册表键的 LastWriteTime（托管 API 没暴露，用 RegQueryInfoKey 的最后两个参数）。
    /// 返回 FILETIME 的 64 位原值，只当版本戳用，不解释成时间。
    /// </summary>
    private static long RegistryLastWriteTicks(RegistryKey key)
    {
        try
        {
            uint classLen = 0;
            int rc = RegQueryInfoKey(
                key.Handle.DangerousGetHandle(), IntPtr.Zero, ref classLen, IntPtr.Zero,
                out _, out _, out _, out _, out _, out _, out _,
                out long lastWrite);
            return rc == 0 ? lastWrite : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsNegativeFresh(string key)
    {
        if (!Negative.TryGetValue(key, out long at)) return false;
        if (Environment.TickCount64 - at < NegativeTtlMs) return true;
        Negative.TryRemove(key, out _);
        return false;
    }

    // ---------------------------------------------------------------- 队列 / 线程

    private sealed class WorkItem
    {
        public string Key = "";
        public string Path = "";
        public int PixelSize;
        public long Epoch;
    }

    private sealed class Completion
    {
        public readonly TaskCompletionSource<ImageSource?> Tcs =
            new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly BlockingCollection<WorkItem> Queue = new BlockingCollection<WorkItem>();
    private static readonly object WorkerGate = new object();
    private static Thread? _worker;

    private static Thread? Worker => _worker;

    /// <summary>把请求挂到 key 上；同 key 已在飞就复用，只排一次队。</summary>
    private static Completion Attach(string key, string normPath, int pixelSize, long epoch)
    {
        var mine = new Completion();
        Completion completion = Pending.GetOrAdd(key, mine);
        if (ReferenceEquals(completion, mine))
        {
            EnsureWorker();
            Queue.Add(new WorkItem
            {
                Key = key,
                Path = normPath,
                PixelSize = pixelSize,
                Epoch = epoch,
            });
        }
        return completion;
    }

    private static void EnsureWorker()
    {
        if (_worker != null) return;
        lock (WorkerGate)
        {
            if (_worker != null) return;
            var thread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "ShellIcons",
            };
            /* Shell 的图标接口（IShellItemImageFactory、SHGetImageList）都要求 STA。 */
            thread.SetApartmentState(ApartmentState.STA);
            _worker = thread;
            thread.Start();
        }
    }

    private static void WorkerLoop()
    {
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        try
        {
            foreach (WorkItem item in Queue.GetConsumingEnumerable())
            {
                ImageSource? image = null;
                try
                {
                    image = Resolve(item);
                }
                catch (Exception ex)
                {
                    CoreLog.Swallow(ex, "解析 Shell 图标");
                }

                Finish(item, image);
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            CoUninitialize();
        }
    }

    private static void Finish(WorkItem item, ImageSource? image)
    {
        /* 关联变更（Invalidate）之后回来的旧结果不能进缓存，也不能交给界面 ——
         * 用户刚把 PDF 默认程序改掉，屏幕上是旧图标的话比没有图标还糟。 */
        if (item.Epoch != Volatile.Read(ref _epoch))
        {
            image = null;
        }

        if (image != null)
        {
            if (Cache.Count > CacheCap) Cache.Clear();
            Cache[item.Key] = image;
            Negative.TryRemove(item.Key, out _);
        }
        else
        {
            Negative[item.Key] = Environment.TickCount64;
        }

        /* 先落缓存、再唤醒等待者：RequestSync 醒来时读到的就是刚算好的那张图。 */
        if (Pending.TryRemove(item.Key, out Completion? completion))
        {
            completion.Tcs.TrySetResult(image);
        }
    }

    // ---------------------------------------------------------------- 解析

    private static ImageSource? Resolve(WorkItem item)
    {
        if (!IsNetworkPath(item.Path))
        {
            return ResolveCore(item.Path, item.PixelSize);
        }

        /* 网络路径（\\server\share）：SMB 掉线时 File.Exists 能卡几十秒。
         * 解析线程是队列的唯一消费者，卡住它等于整个 Qbar 没图标。
         * 所以网络路径改由一条短命 STA 线程去探，超时就放弃，结果照旧进缓存。 */
        if (Interlocked.Increment(ref _networkProbes) > 2)
        {
            Interlocked.Decrement(ref _networkProbes);
            WarnOnce("busy:" + item.Path, "网络路径图标排队过多，跳过（" + item.Path + "）");
            return null;
        }

        ImageSource? result = null;
        try
        {
            var probe = new Thread(() =>
            {
                CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
                try
                {
                    result = ResolveCore(item.Path, item.PixelSize);
                }
                catch (Exception ex)
                {
                    CoreLog.Swallow(ex, "解析网络路径图标");
                }
                finally
                {
                    CoUninitialize();
                }
            })
            {
                IsBackground = true,
                Name = "ShellIcons.Net",
            };
            probe.SetApartmentState(ApartmentState.STA);
            probe.Start();
            if (!probe.Join(NetworkTimeoutMs))
            {
                WarnOnce("nettimeout:" + item.Path, "网络路径图标超时，先显示占位（" + item.Path + "）");
                return null;
            }
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _networkProbes);
        }
    }

    private static int _networkProbes;

    private static ImageSource? ResolveCore(string path, int pixelSize)
    {
        if (!Exists(path))
        {
            WarnOnce("missing:" + path, "取图标：文件不存在或不可访问（" + path + "）");
            return null;
        }

        /* 快捷方式走 SHGFI_LINKOVERLAY，Shell 直接把小箭头合成进图标（取舍见
         * ShortcutIcon 的注释）。取不到时退回下面的大图路径，宁可没箭头也别没图标。 */
        if (IsShortcut(path))
        {
            ImageSource? withOverlay = ShortcutIcon(path, pixelSize);
            if (withOverlay != null) return withOverlay;
        }

        ImageSource? image = ShellItemImage(path, pixelSize);
        if (image != null) return image;

        image = ImageListIcon(path, pixelSize);
        if (image != null) return image;

        WarnOnce("shellfail:" + path, "Shell 未返回图标（" + path + "）");
        return null;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsShortcut(string path)
    {
        return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".url", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".pif", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 主路径：IShellItemImageFactory::GetImage(SIIGBF_ICONONLY|SIIGBF_BIGGERSIZEOK)。
    /// 这是资源管理器缩略图/图标走的同一条路，PDF 拿到的就是系统默认处理器图标，
    /// UWP 应用拿到的是包图标，.exe 拿到的是内嵌图标。
    /// </summary>
    private static ImageSource? ShellItemImage(string path, int pixelSize)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory);
            if (hr != 0 || factory == null) return null;

            /* SIIGBF_BIGGERSIZEOK：允许 Shell 返回比请求更大的图（我们从大图缩小，
             * 质量比让小图放大好得多）；SIIGBF_ICONONLY：只要图标，不要缩略图 ——
             * 结果行上缩略图会把一行文字的节奏搞乱。 */
            hr = factory.GetImage(
                new SIZE(pixelSize, pixelSize),
                SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK | SIIGBF_RESIZETOFIT,
                out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero) return null;

            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "GetImage 取图标");
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// 快捷方式（.lnk/.url/.appref-ms/.pif）：用 Shell 合成好的「带小箭头」图标。
    ///
    /// 取舍（写在这里，免得后来人以为是偷懒）：另外两条路都实测不可靠 ——
    /// ① SHGetFileInfo 带 SHGFI_OVERLAYINDEX 读 iIcon 高 8 位取叠加图序号：本机
    ///    （Windows 11，200% DPI）对真实 .lnk 返回 0，拿不到箭头序号；
    /// ② 用 IImageList.GetIcon 从系统图像列表里取叠加图自己合成：取到的仍是文件类型
    ///    图标，不是箭头图。
    /// 所以走 SHGFI_ICON|SHGFI_LINKOVERLAY|SHGFI_LARGEICON —— 这是 Shell 唯一保证
    /// 「带叠加图」的出口，出图尺寸跟随系统大图标档（32 x DPI：100% = 32px，
    /// 200% = 64px）。Qbar 的 28 DIP 图标格按档位取到的正好也是 32/64px，两者对齐，
    /// 因此这里拿到的就是资源管理器里同一张图，不需要任何缩放。
    /// 若将来有调用方要 96/256px，这里仍返回系统档位那张图、由 Image 拉伸 ——
    /// 为快捷方式的超大图标引入 DIB 合成（32bpp + ILD_OVERLAYMASK）不划算。
    /// </summary>
    private static ImageSource? ShortcutIcon(string path, int pixelSize)
    {
        /* pixelSize 目前只留作将来判断是否要更大档位；实际档位由系统大图标尺寸决定，
         * 保留参数是为了签名稳定、调用点不需要跟着改。 */
        _ = pixelSize;

        SHFILEINFO info = NewInfo();
        IntPtr ok = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_LINKOVERLAY | SHGFI_LARGEICON);
        if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

        return FromHIcon(info.hIcon);   /* 句柄所有权交给 FromHIcon，内部 DestroyIcon */
    }

    /// <summary>
    /// 回退路径：系统图像列表。GetImage 对某些命名空间项（控制面板、回收站这类 PIDL）
    /// 会失败，图像列表基本都能给出一张。
    /// </summary>
    private static ImageSource? ImageListIcon(string path, int pixelSize)
    {
        SHFILEINFO info = NewInfo();
        IntPtr ok = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
        if (ok == IntPtr.Zero) return null;

        int index = info.iIcon & 0xFFFF;
        int listKind = pixelSize <= 16 ? SHIL_SMALL
                     : pixelSize <= 32 ? SHIL_LARGE
                     : pixelSize <= 48 ? SHIL_EXTRALARGE
                     : SHIL_JUMBO;

        Guid iid = typeof(IImageList).GUID;
        IImageList? list = null;
        try
        {
            int hr = SHGetImageList(listKind, ref iid, out list);
            if (hr != 0 || list == null) return null;

            hr = list.GetIcon(index, ILD_TRANSPARENT, out IntPtr hicon);
            if (hr != 0 || hicon == IntPtr.Zero) return null;
            return FromHIcon(hicon);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "图像列表取图标");
            return null;
        }
        finally
        {
            if (list != null) Marshal.ReleaseComObject(list);
        }
    }

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "HICON 转 BitmapSource");
            return null;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static int ClampPixel(int pixelSize)
    {
        if (pixelSize <= 0) return PickPixels(DefaultDip, 1.0);
        for (int i = 0; i < Buckets.Length; i++)
        {
            if (pixelSize <= Buckets[i]) return Buckets[i];
        }
        return Buckets[Buckets.Length - 1];
    }

    private static double NormDpi(double dpiScale) =>
        dpiScale > 0 && !double.IsNaN(dpiScale) && !double.IsInfinity(dpiScale) ? Math.Round(dpiScale, 3) : 1.0;

    /// <summary>回调统一扔线程池：调用方可能在 UI 线程上请求，同步回调会制造重入。</summary>
    private static void Post(Action<ImageSource?>? done, ImageSource? image)
    {
        if (done == null) return;
        ThreadPool.QueueUserWorkItem(_ => { try { done(image); } catch { } });
    }

    /// <summary>同一路径/同一场景只记一次日志，避免「每敲一个字刷一行」。</summary>
    private static void WarnOnce(string key, string message)
    {
        long now = Environment.TickCount64;
        if (Logged.TryGetValue(key, out long last) && now - last < 10 * 60_000) return;
        Logged[key] = now;
        CoreLog.Warn(message);
    }

    private static SHFILEINFO NewInfo() => new SHFILEINFO
    {
        hIcon = IntPtr.Zero,
        iIcon = 0,
        dwAttributes = 0,
        szDisplayName = "",
        szTypeName = "",
    };

    static ShellIcons()
    {
        try
        {
            /* 关联变更的整体失效：选 SystemEvents 而不是自建隐藏 NativeWindow ——
             * 广播的 WM_SETTINGCHANGE 只发给顶层窗口，隐藏窗口方案要自己维护
             * HWND 生命周期与线程归属；SystemEvents 内部就是同一套窗口，而本程序
             * 已经引用了 WinForms（托盘），不额外增加依赖。
             * 兜底还有一道：缓存 key 里带 UserChoice/DefaultIcon/FileExts 时间戳，
             * 即使事件没触发，下一次请求也会因为版本戳变化而重新取图。 */
            SystemEvents.UserPreferenceChanged += (_, e) => Invalidate("UserPreferenceChanged:" + e.Category);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "订阅系统设置变更");
        }
    }

    // ---------------------------------------------------------------- Win32 / COM

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;

        public SIZE(int width, int height)
        {
            cx = width;
            cy = height;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    /// <summary>
    /// IShellItemImageFactory：GetImage 是这条链上唯一的方法。
    /// 它的 HBITMAP 必须由调用方 DeleteObject，否则每次取图漏一个 GDI 句柄。
    /// </summary>
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    /// <summary>
    /// IImageList：只用到 GetIcon，但方法必须按 vtable 顺序声明到它为止
    /// （ComImport 接口的顺序就是槽位，少一个就会调到隔壁方法上）。
    /// </summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    /// <summary>只为了让 IImageList 的槽位对齐（Draw 是第 6 个方法）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct IMAGELISTDRAWPARAMS
    {
        public int cbSize;
        public IntPtr himl;
        public int i;
        public IntPtr hdcDst;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public int xBitmap;
        public int yBitmap;
        public int rgbBk;
        public int rgbFg;
        public uint fStyle;
        public uint dwRop;
        public uint fState;
        public uint Frame;
        public int crEffect;
    }

    private const uint COINIT_APARTMENTTHREADED = 0x2;

    private const int SIIGBF_RESIZETOFIT = 0x00000000;
    private const int SIIGBF_BIGGERSIZEOK = 0x00000001;
    private const int SIIGBF_ICONONLY = 0x00000004;

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SYSICONINDEX = 0x00004000;
    private const uint SHGFI_LINKOVERLAY = 0x00008000;
    private const uint SHGFI_OVERLAYINDEX = 0x000000040;

    private const int SHIL_LARGE = 0x0;
    private const int SHIL_SMALL = 0x1;
    private const int SHIL_EXTRALARGE = 0x2;
    private const int SHIL_JUMBO = 0x4;
    private const int ILD_TRANSPARENT = 0x1;

    private const int LOGPIXELSX = 88;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    /// <summary>
    /// SHGetImageList 在 shell32 里只有序号导出（#727），头文件里没声明名字。
    /// 这个序号从 Vista 到 Win11 一直没变。
    /// </summary>
    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(
        int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList? ppv);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern bool PathIsNetworkPath(string pszPath);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryInfoKeyW")]
    private static extern int RegQueryInfoKey(
        IntPtr hKey, IntPtr lpClass, ref uint lpcchClass, IntPtr lpReserved,
        out uint lpcSubKeys, out uint lpcbMaxSubKeyLen, out uint lpcbMaxClassLen,
        out uint lpcValues, out uint lpcbMaxValueNameLen, out uint lpcbMaxValueLen,
        out uint lpcbSecurityDescriptor, out long lpftLastWriteTime);
}
