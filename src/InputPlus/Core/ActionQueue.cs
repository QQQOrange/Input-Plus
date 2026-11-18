using System.Collections.Concurrent;
using System.IO;

namespace InputPlus.Core;

/// <summary>
/// 单线程动作队列。钩子回调里绝对不能做耗时操作（超过 ~300ms 会被系统静默摘掉钩子），
/// 所以所有功能动作都丢进这里按顺序执行。
/// </summary>
public sealed class ActionQueue : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly Thread _thread;
    private volatile bool _disposed;

    public ActionQueue(string name = "InputPlus.Actions")
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = name,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                CoreLog.Error("动作执行失败", ex);
            }
        }
    }

    public void Post(Action action)
    {
        if (_disposed) return;
        try { _queue.Add(action); } catch (InvalidOperationException) { /* 已关闭 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
    }
}

/// <summary>
/// 程序使用的目录约定。
///
/// **数据跟着软件走**：所有用户数据都放在 exe 同级的 `InputPlus Data`
/// 文件夹里，备份放在 exe 同级的 `InputPlus Back` 里（见 SyncService）。
/// 好处很直接：整个文件夹拷到 U 盘 / 另一台机器，软件和全部数据一起搬走，
/// 不用再去 %LocalAppData% 里翻。
///
/// 装到 Program Files 这类**不可写**位置时才回落到 %LocalAppData%\InputPlus，
/// 并把回落事实暴露给界面（<see cref="DataDirIsFallback"/>），不静默换地方。
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录名（在软件所在目录下）。</summary>
    public const string DataFolderName = "InputPlus Data";

    /// <summary>改名前的数据目录名（在 %LocalAppData% 下）。只用于迁移。</summary>
    public const string LegacyFolderName = "InputPlus";

    /// <summary>
    /// 数据目录。可用环境变量 INPUTPLUS_DATA 覆盖（便携模式 / 自检用），
    /// 否则用 exe 同级的 <see cref="DataFolderName"/>。
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    /// <summary>数据目录是不是回落到了 %LocalAppData%（软件目录不可写）。</summary>
    public static bool DataDirIsFallback { get; private set; }

    /// <summary>本次进程是否因 --selftest 自动使用了一次性的隔离数据目录。</summary>
    public static bool DataDirIsSelfTest { get; private set; }

    /// <summary>数据目录是否被 INPUTPLUS_DATA 显式指到了别处。</summary>
    private static bool _customDataDir;

    /// <summary>
    /// 自定义数据目录的环境变量。
    ///
    /// 1.0 改名时把 CAPSLOCKPLUS_DATA 换成了 INPUTPLUS_DATA；老脚本、老快捷方式
    /// 还在用旧名字，**两个都认**，免得用户的便携启动脚本在升级后突然失效
    /// （表现会是"配置全没了"——其实只是读到了另一个目录）。
    /// </summary>
    private static string? EnvDataDir
        => Environment.GetEnvironmentVariable("INPUTPLUS_DATA")
           ?? Environment.GetEnvironmentVariable("CAPSLOCKPLUS_DATA");

    /// <summary>
    /// 数据目录是不是**用户那一份**。
    ///
    /// --selftest 的一次性目录、INPUTPLUS_DATA 指到别处、以及各种测试脚本，
    /// 都算"不是"。凡是想顺手写一写用户数据的地方（备份目录、清理、迁移）
    /// 都必须先问这个：写错了地方就是数据丢失，而且是无声的那种。
    /// </summary>
    public static bool DataDirIsRedirected => DataDirIsSelfTest || _customDataDir;

    /// <summary>数据位置的一句话说明（界面直接显示）。</summary>
    public static string DataDirNote => DataDirIsFallback
        ? Localization.Loc.T("软件目录不可写，数据改放在这里（换位置请先给软件目录写权限）")
        : Localization.Loc.F("数据就在软件旁边的「{0}」文件夹里", DataFolderName);

    /// <summary>
    /// 清掉以前自检留下的一次性目录。那里面是用户配置的副本，
    /// 攒上几十个既占地也容易在排查时看错目录。
    /// 只删 %TEMP% 下、名字对得上、且超过一天的；当前进程自己那个不动。
    /// </summary>
    private static void SweepOldSelfTestDirs()
    {
        try
        {
            string temp = Path.GetTempPath();
            foreach (string dir in Directory.EnumerateDirectories(temp, "InputPlus-selftest-*"))
            {
                try
                {
                    if (dir.EndsWith("-" + Environment.ProcessId, StringComparison.Ordinal)) continue;
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < TimeSpan.FromHours(24)) continue;
                    Directory.Delete(dir, true);
                }
                catch { }   /* 预期内：目录被别的自检占用或没权限，跳过即可 */
            }
        }
        catch { }
    }

    private static string ResolveDataDir()
    {
        /* --selftest 会构造/保存大量测试配置。它必须用一次性的临时目录，
         * 绝不能落在 exe 旁边的真实数据目录里；否则一次自检就可能把用户
         * 的按键、层、别名写成测试对象。显式 INPUTPLUS_DATA 仍然优先，
         * 便于测试脚本指定沙箱。 */
        bool selfTest = false;
        foreach (string arg in Environment.GetCommandLineArgs())
        {
            if (!string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) continue;
            selfTest = true;
            break;
        }
        if (selfTest && string.IsNullOrWhiteSpace(EnvDataDir))
        {
            try
            {
                SweepOldSelfTestDirs();
                string isolated = Path.Combine(
                    Path.GetTempPath(), "InputPlus-selftest-" + Environment.ProcessId);
                Directory.CreateDirectory(isolated);
                DataDirIsSelfTest = true;
                return isolated;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "准备自检临时数据目录"); }
        }

        var custom = EnvDataDir;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try
            {
                // 必须规范化成绝对路径：托盘程序的当前目录常常是 System32，
                // 相对路径会让数据目录、备份路径甚至 Process.Start 的目标
                // 全都随 CWD 漂移，排查时完全对不上。
                custom = Path.GetFullPath(custom);
                Directory.CreateDirectory(custom);
                _customDataDir = true;
                return custom;
            }
            catch (Exception ex) { CoreLog.Swallow(ex, "使用自定义数据目录"); }
        }

        /* 首选：exe 同级。先真写一个文件再删 —— 只测 CreateDirectory
         * 骗不过 UAC 虚拟化（它会"成功"地把文件重定向到 VirtualStore）。 */
        string primary = Path.Combine(AppDir, DataFolderName);
        if (TryPrepare(primary))
        {
            MigrateLegacyData(primary);
            return primary;
        }

        // 回落：Program Files 之类不可写位置
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InputPlus");
        try { Directory.CreateDirectory(fallback); } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
        DataDirIsFallback = true;
        MigrateLegacyData(fallback);
        return fallback;
    }

    /// <summary>
    /// 目录能不能真的写文件。
    ///
    /// 只调 CreateDirectory 是不够的：在 Program Files 下它会返回成功，
    /// 实际写入被 UAC 虚拟化重定向到 %LocalAppData%\VirtualStore ——
    /// 用户会看到"数据目录建好了"，但文件不在那儿。
    /// </summary>
    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".writable");
            File.WriteAllText(probe, "1");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 改名遗留的数据目录，按优先级排列。
    ///
    /// 1.0 之前软件叫 KeyboardEnhance（更早叫 CapsLockPlus），数据目录也就跟着换过名字。
    /// 用户升级到 Input Plus 时，配置还在老名字的目录里 —— 不主动搬，
    /// 表现就是"升级完所有设置全没了"。所以这里把所有用过的名字都列出来试一遍。
    ///
    /// 顺序讲究：**先软件旁边的目录，再 %LocalAppData%**。旁边那份是用户主动
    /// 放在软件目录里的（便携用法），比系统盘里可能几个月前的回落副本更可信。
    /// </summary>
    private static IEnumerable<string> LegacyDataDirs()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;

        // ① 软件旁边的老目录名（DataFolderName 改名前叫这些）
        yield return Path.Combine(appDir, "KeyboardEnhance Data");
        yield return Path.Combine(appDir, "Keyboard Enhance Data");
        yield return Path.Combine(appDir, "CapsLockPlus Data");

        // ② 软件目录不可写时%LocalAppData% 下的老目录名
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "KeyboardEnhance");
        yield return Path.Combine(local, "CapsLockPlus");

        // ③ 改名后、正式定名 "InputPlus Data" 之前，1.0 开发期间用过 InputPlus
        yield return Path.Combine(local, LegacyFolderName);
    }

    /// <summary>
    /// 把老位置的数据搬到新目录（候选见 <see cref="LegacyDataDirs"/>）。
    ///
    /// 三条规矩：
    ///   * 目标已经有 settings.json → 说明新目录在用，一个文件都不动
    ///     （否则会用旧数据覆盖用户刚改的配置）；
    ///   * 只拷目标没有的文件，绝不覆盖；
    ///   * 只拷用户数据，不拷 logs / backups / 自检产物 ——
    ///     logs 是诊断垃圾，backups 由 SyncService 单独搬到 Back 目录。
    ///
    /// 这里**不能写日志**：CoreLog 的路径依赖 DataDir，而 DataDir 正在初始化，
    /// 递归调用会拿到空路径。迁移结果写进 migrated.txt 供排查。
    /// </summary>
    private static void MigrateLegacyData(string target)
    {
        try
        {
            // 新目录已经有设置 → 不迁移，避免覆盖
            if (File.Exists(Path.Combine(target, "settings.json"))) return;

            /* 新目录里有 .bak / .good → 说明这台机器早就在用新版本了，
             * 这次只是主文件出问题，该由 SettingsStore 从 .bak 恢复。
             * 这时候绝不能把老目录（可能是一两个月前的）配置扶正：
             * 2026-11-18 的"别名全没了、层里的路径退回旧值"就是这么来的 ——
             * 老目录那份是 10-22 的，里面一条别名都没有。 */
            if (File.Exists(Path.Combine(target, "settings.json.bak"))) return;
            if (File.Exists(Path.Combine(target, "settings.json.good"))) return;

            string targetFull = Path.GetFullPath(target).TrimEnd('\\');

            foreach (string legacy in LegacyDataDirs())
            {
                if (!Directory.Exists(legacy)) continue;
                if (Path.GetFullPath(legacy).TrimEnd('\\')
                    .Equals(targetFull, StringComparison.OrdinalIgnoreCase)) continue;

                int n = CopyTree(legacy, target, 0);
                try
                {
                    Security.SecureFile.AppendLine(Path.Combine(target, "migrated.txt"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 从 {legacy} 迁移 {n} 个文件到 {target}");
                }
                catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

                // 已经拿到主配置就不再看后面的候选：再往下拷只会把更老的
                // 零散文件（backups、旧日志）混进来。
                if (File.Exists(Path.Combine(target, "settings.json"))) return;
            }
        }
        catch
        {
            // 迁移失败不能拦住启动：最坏情况是用户看到默认配置，旧数据还在原处
        }
    }

    /// <summary>递归拷目录，跳过诊断/备份类子目录与临时文件。返回拷了几个文件。</summary>
    private static int CopyTree(string from, string to, int depth)
    {
        if (depth > 3) return 0;
        int n = 0;

        try
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.EnumerateFiles(from))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("selftest", StringComparison.OrdinalIgnoreCase)) continue;

                string dest = Path.Combine(to, name);
                if (File.Exists(dest)) continue;       // 不覆盖
                try { File.Copy(file, dest); n++; } catch { }   /* 预期内：探测/重试型，失败即视为无结果 */
            }

            foreach (string dir in Directory.EnumerateDirectories(from))
            {
                string name = Path.GetFileName(dir);
                // logs = 诊断、backups = 由 SyncService 搬到 Back 目录
                if (name.Equals("logs", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("backups", StringComparison.OrdinalIgnoreCase)) continue;
                n += CopyTree(dir, Path.Combine(to, name), depth + 1);
            }
        }
        catch { }   /* 预期内：探测/重试型，失败即视为无结果 */

        return n;
    }

    /// <summary>仅供自检：跑一次目录复制（不碰真实的老数据目录）。</summary>
    internal static int CopyTreeForTest(string from, string to) => CopyTree(from, to, 0);

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string BindingsFile => Path.Combine(DataDir, "winsbind.json");
    public static string AppDir => AppContext.BaseDirectory;
}
