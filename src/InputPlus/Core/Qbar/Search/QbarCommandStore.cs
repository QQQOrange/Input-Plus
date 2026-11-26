using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using InputPlus.Core.Windows;

namespace InputPlus.Core.Qbar.Search;

/// <summary>命令执行结果。失败文案分三层，别让用户看到 "Win32 错误 2"。</summary>
internal readonly record struct QbarCommandResult(bool Ok, string Message, bool Cancelled);

/// <summary>
/// 命令表：内置命令 + 用户命令合并成一张表，关键字唯一（用户同名覆盖内置）。
/// 命令是"输入 keyword 之后整段尾词都归它"的语义，和过滤器同一个触发方式，
/// 所以展示时两者并列 —— 选谁决定是"执行动作"还是"改查询"。
/// </summary>
internal static class QbarCommandStore
{
    public const string BuiltinCmdId = "builtin.cmd";
    public const string BuiltinPowerShellId = "builtin.powershell";
    public const string BuiltinNewFileId = "builtin.newfile";
    public const string BuiltinNewFolderId = "builtin.newfolder";
    public const string BuiltinCopyPathId = "builtin.copypath";
    public const string BuiltinParentId = "builtin.parent";
    public const string BuiltinRefreshId = "builtin.refresh";
    public const string BuiltinOpenFolderId = "builtin.openfolder";
    public const string BuiltinDictId = "builtin.dict";

    /// <summary>
    /// 查词命令的出厂参数：欧路词典的 -w 就是"查这个词"。
    /// 写成一个常量是因为它有两个用处：出厂命令的 Parameter 初值，
    /// 以及旧配置（v1.7.2 之前参数是空的）留空时的兜底 —— 参数框空着看起来像没配。
    /// </summary>
    public const string DefaultDictParameter = "-w \"{query}\"";

    /// <summary>查词命令的出厂关键字（老配置里用户可能自己建过同关键字的命令）。</summary>
    public const string DictKeyword = "ol";

    public static List<QbarCommand> Builtins()
    {
        return new List<QbarCommand>
        {
            new QbarCommand
            {
                Id = BuiltinCmdId,
                Keyword = "cmd",
                Title = "在此处打开命令提示符",
                Path = "cmd.exe",
                Parameter = "/K cd /d \"{current_folder}\"",
                WorkingDirectory = "{current_folder}",
            },
            new QbarCommand
            {
                Id = BuiltinPowerShellId,
                Keyword = "psh",
                Title = "在此处打开 PowerShell（管理员）",
                Path = "powershell.exe",
                Parameter = "-ExecutionPolicy RemoteSigned -NoExit -Command \"cd -LiteralPath '{current_folder}'\"",
                WorkingDirectory = "{current_folder}",
                RunAsAdmin = true,
            },
            new QbarCommand
            {
                Id = BuiltinOpenFolderId,
                Keyword = "explore",
                Title = "在资源管理器中打开所在目录",
                Path = "explorer.exe",
                Parameter = "\"{action_path:parent}\"",
            },
            /* 查词：**必须开箱能用**。欧路词典装没装差别太大（装了走 -w 参数，
               没装就开网页词典），所以 Path 留空，交给运行期解析 ——
               写死 eudic.exe 会让没装的人只看到「找不到程序」。 */
            new QbarCommand
            {
                Id = BuiltinDictId,
                Keyword = DictKeyword,
                Title = "查词（欧路词典 / 网页词典）",
                /* 参数必须写出来：欧路不带 -w 只会被打开、不会查词，
                   而且设置页里空着的话用户没法知道该填什么。Path 仍然留空 ——
                   那一段由运行期的定位器补（装在哪都能找到）。 */
                Parameter = DefaultDictParameter,
            },

            /* 下面这批没有 Path：它们不是"启动外部程序"，而是调用 Qbar 自己的 API
             * （新建、复制、跳目录、刷新索引）。放在同一张表里是为了共用
             * "首词命中 = 命令分支"这套匹配逻辑，界面上再按 id 分派到内部实现。
             * Path 留空还有个好处：就算哪天分派代码漏了分支，Execute 也会以
             * "命令没有配置程序"明确拒绝，而不是去 shell 里找一个叫 newfile.exe 的东西。 */
            new QbarCommand
            {
                Id = BuiltinNewFileId,
                Keyword = "newfile",
                Title = "新建文件",
            },
            new QbarCommand
            {
                Id = BuiltinNewFolderId,
                Keyword = "newfolder",
                Title = "新建文件夹",
            },
            new QbarCommand
            {
                Id = BuiltinCopyPathId,
                Keyword = "copypath",
                Title = "复制选中项的路径",
            },
            new QbarCommand
            {
                Id = BuiltinParentId,
                Keyword = "parent",
                Title = "跳到上一级目录",
            },
            new QbarCommand
            {
                Id = BuiltinRefreshId,
                Keyword = "reindex",
                Title = "刷新文件索引状态",
            },
        };
    }

    /// <summary>没有 Path、由调用方（Qbar 窗口）接管的内部动作 id。</summary>
    public static bool IsInternalAction(string? id)
    {
        return id == BuiltinNewFileId || id == BuiltinNewFolderId || id == BuiltinCopyPathId
            || id == BuiltinParentId || id == BuiltinRefreshId || id == BuiltinDictId;
    }

    /// <summary>这条命令是不是内置的（按 id 前缀判断，别用关键字 —— 内置的 ol 可以被改名为 dict）。</summary>
    public static bool IsBuiltin(string? id) => id != null && id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase);

    /// <summary>内置命令的出厂版本；不是内置 id 返回 null。</summary>
    public static QbarCommand? Default(string? id)
    {
        if (!IsBuiltin(id)) return null;
        foreach (QbarCommand builtin in Builtins())
        {
            if (string.Equals(builtin.Id, id, StringComparison.OrdinalIgnoreCase)) return builtin;
        }
        return null;
    }

    /// <summary>用户这份改动是否和出厂值完全一样（一样就不用存，等于"恢复默认"）。</summary>
    public static bool SameAsDefault(QbarCommand command)
    {
        QbarCommand? def = Default(command.Id);
        if (def == null) return false;
        return string.Equals((command.Keyword ?? "").Trim(), (def.Keyword ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(command.Title ?? "", def.Title ?? "", StringComparison.Ordinal)
            && string.Equals(command.Path ?? "", def.Path ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(command.Parameter ?? "", def.Parameter ?? "", StringComparison.Ordinal)
            && string.Equals(command.WorkingDirectory ?? "", def.WorkingDirectory ?? "", StringComparison.OrdinalIgnoreCase)
            && command.RunAsAdmin == def.RunAsAdmin
            && command.Silent == def.Silent;
    }

    /// <summary>
    /// 内置 + 用户命令。合并规则：**先按 id 覆盖内置**（这样内置命令能改名、换程序，
    /// 不会留下"原关键字 + 新关键字"两条），再按关键字去重（同关键字时用户命令盖掉
    /// 内置的，兼容旧行为，也是"自己加一条盖掉 cmd"的用法）。
    /// </summary>
    public static List<QbarCommand> Effective(IEnumerable<QbarCommand>? user)
    {
        List<QbarCommand> builtins = Builtins();
        var replaced = new Dictionary<string, QbarCommand>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<QbarCommand>();

        if (user != null)
        {
            foreach (QbarCommand command in user)
            {
                if (command == null) continue;
                if ((command.Keyword ?? "").Trim().Length == 0) continue;
                if (IsBuiltin(command.Id) && Default(command.Id) != null) replaced[command.Id!] = command;
                else extra.Add(command);
            }
        }

        var map = new Dictionary<string, QbarCommand>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        void Put(QbarCommand command)
        {
            string key = (command.Keyword ?? "").Trim();
            if (key.Length == 0) return;
            if (!map.ContainsKey(key)) order.Add(key);
            map[key] = command;
        }

        foreach (QbarCommand builtin in builtins)
        {
            QbarCommand effective = replaced.TryGetValue(builtin.Id, out QbarCommand? mine) ? mine.Clone() : builtin;
            /* ol 的参数模板是 1.7.3 才写进出厂值的：老配置里存的是空字符串，
               这里补上默认 -w "{query}"。不补的话设置页看着像没配参数，
               点回车也只是把欧路打开、不带词（用户反馈的就是这个）。 */
            EnsureDefaults(effective);
            Put(effective);
        }
        foreach (QbarCommand command in extra) Put(command.Clone());

        var list = new List<QbarCommand>(order.Count);
        foreach (string key in order)
        {
            QbarCommand command = map[key];
            EnsureDefaults(command);   // 用户自建的 ol 也要补上 -w "{query}"
            list.Add(command);
        }
        return list;
    }

    /// <summary>
    /// 补出厂默认值（目前只有「查词」的参数模板）。
    ///
    /// 认两条线：Id 是内置查词命令，**或者**关键字就是出厂的 ol ——
    /// 老配置里有人是自己建了一条 ol（Id 不是 builtin.dict），只按 Id 补的话
    /// 设置页的参数框还是空的，用户看到的就是"参数这里什么都没有"。
    /// </summary>
    public static void EnsureDefaults(QbarCommand command)
    {
        if (command == null) return;
        if (!string.IsNullOrWhiteSpace(command.Parameter)) return;
        bool isDict = string.Equals(command.Id, BuiltinDictId, StringComparison.OrdinalIgnoreCase)
                      || string.Equals((command.Keyword ?? "").Trim(), DictKeyword, StringComparison.OrdinalIgnoreCase);
        if (isDict) command.Parameter = DefaultDictParameter;
    }

    public static QbarCommand? Match(IReadOnlyList<QbarCommand> commands, string input, out string tail)
    {
        tail = "";
        if (string.IsNullOrWhiteSpace(input)) return null;

        string text = input.TrimStart();
        int space = text.IndexOf(' ');
        string head = space < 0 ? text : text.Substring(0, space);
        tail = space < 0 ? "" : text.Substring(space + 1).Trim();

        for (int i = 0; i < commands.Count; i++)
        {
            QbarCommand command = commands[i];
            if (string.Equals(command.Keyword, head, StringComparison.OrdinalIgnoreCase)) return command;
        }
        return null;
    }

    /// <summary>
    /// 占位符一次替换、不递归：模板里写了 {query} 就换成尾词原文，
    /// 尾词里再出现 {query} 也不会被再展开一次（否则用户输入能构造出无限展开）。
    /// </summary>
    public static string Expand(string? template, string query, string? currentFolder, string? actionPath)
    {
        if (string.IsNullOrEmpty(template)) return "";

        string text = template!;
        if (text.IndexOf('{') < 0) return text;

        var sb = new StringBuilder(text.Length + query.Length + 32);
        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('{', i);
            if (open < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            int close = text.IndexOf('}', open + 1);
            if (close < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }

            sb.Append(text, i, open - i);
            string name = text.Substring(open + 1, close - open - 1).Trim();
            sb.Append(Resolve(name.ToLowerInvariant(), query, currentFolder, actionPath));
            i = close + 1;
        }

        return sb.ToString();
    }

    private static string Resolve(string name, string query, string? currentFolder, string? actionPath)
    {
        switch (name)
        {
            case "query": return query ?? "";
            case "current_folder": return currentFolder ?? "";
            case "action_path": return actionPath ?? "";
            case "action_path:parent":
                if (string.IsNullOrEmpty(actionPath)) return "";
                if (Directory.Exists(actionPath))
                {
                    DirectoryInfo? info = Directory.GetParent(actionPath);
                    return info?.FullName ?? actionPath!;
                }
                return Path.GetDirectoryName(actionPath!) ?? "";
            default: return "";
        }
    }

    /// <summary>
    /// 执行。跑在调用方线程上（Process.Start 不要求 STA），异常一律翻译成人话。
    /// </summary>
    public static QbarCommandResult Execute(QbarCommand command, string tail, string? currentFolder, string? actionPath)
    {
        if (command == null) return new QbarCommandResult(false, "命令不存在", false);

        string path = Expand(command.Path, tail, currentFolder, actionPath).Trim();
        if (path.Length == 0) return new QbarCommandResult(false, "命令没有配置程序路径", false);
        if (path.IndexOf('\0') >= 0) return new QbarCommandResult(false, "命令路径包含非法字符", false);
        if (Directory.Exists(path)) return new QbarCommandResult(false, "命令路径是一个目录，不是程序", false);

        string arguments = Expand(command.Parameter, tail, currentFolder, actionPath);
        string workdir = Expand(command.WorkingDirectory, tail, currentFolder, actionPath).Trim();

        try
        {
            /* 工作目录三级优先：用户填的最优先 → 程序自己的目录 → 系统默认。
             * 中间那级以前是缺的：命令里没填工作目录时子进程会**继承我们的当前目录**，
             * 于是 wb2api 这类按相对路径读 config 的程序把配置落在了 InputPlus 目录里。
             * 拼 ProcessStartInfo 的活儿交给 ExternalLaunch，规则全仓库只剩一份。 */
            var psi = ExternalLaunch.ForTarget(path, arguments, command.RunAsAdmin, command.Silent);
            if (workdir.Length > 0 && Directory.Exists(workdir)) psi.WorkingDirectory = workdir;

            Process.Start(psi);
            return new QbarCommandResult(true, "", false);
        }
        catch (Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED：UAC 被用户点了"否"，这跟"程序不存在"是两件事
            if (ex.NativeErrorCode == 1223) return new QbarCommandResult(false, "提权被取消", true);
            if (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
                return new QbarCommandResult(false, "找不到程序：" + path, false);
            return new QbarCommandResult(false, ex.Message, false);
        }
        catch (Exception ex)
        {
            CoreLog.Swallow(ex, "执行 Qbar 命令");
            return new QbarCommandResult(false, ex.Message, false);
        }
    }
}
