using System;
using System.Collections.Generic;
using System.Linq;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 内置过滤器 + 用户自定义过滤器的合并视图。
///
/// 之所以要"合并视图"而不是把内置项写进设置：内置项的关键字（<c>doc:</c>）
/// 是用户肌肉记忆的一部分，被误删一次就是永久性的体验退化。所以内置项永远存在，
/// 只是允许覆盖扩展名表 / 图标 / 是否启用；用户新增的过滤器则完全自由。
/// 合并按关键字去重：同关键字时用户项覆盖内置项的属性，但 <see cref="QbarFilter.Id"/> 仍是内置 Id，
/// 这样"恢复默认"只要清空覆盖值即可。
/// </summary>
internal sealed class QbarFilterCatalog
{
    /// <summary>内置过滤器的 Id，和关键字一一对应。改这两个会破坏设置里的覆盖项，别再动。</summary>
    internal const string IdDoc = "builtin.doc";
    internal const string IdPic = "builtin.pic";
    internal const string IdVideo = "builtin.video";
    internal const string IdAudio = "builtin.audio";
    internal const string IdFile = "builtin.file";
    internal const string IdFolder = "builtin.folder";
    internal const string IdContent = "builtin.content";

    /* 扩展名表照用户需求文档 2.4 抄下来，顺序也保持原样：
     * 这批表本身是 Listary 的默认值，用户换过来时希望一眼认出"和我以前的一样"。
     * 排序对查询没有影响，但会出现在调试面板里，改顺序只会让人以为出了问题。 */
    internal const string DocExtensions =
        "c;chm;cpp;cxx;doc;docm;docx;dot;dotm;dotx;h;hpp;htm;html;hxx;ini;java;js;lua;mdb;mht;mhtml;" +
        "pdf;potm;potx;ppam;pps;ppsm;ppsx;ppt;pptm;pptx;rtf;sldm;sldx;thmx;txt;vsd;wpd;wps;wri;" +
        "xlam;xls;xlsb;xlsm;xlsx;xltm;xltx;xml";

    internal const string PicExtensions =
        "ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;svg;tga;tif;tiff;webp;wmf";

    internal const string VideoExtensions =
        "3g2;3gp;3gp2;3gpp;asf;avi;bdmv;bik;divx;drc;f4v;flc;fli;flv;ifo;ivf;m1v;m2ts;m2v;m4v;" +
        "mkv;mov;mp4;mpe;mpeg;mpg;mpv2;mts;ogm;pss;pva;qt;rm;rmvb;roq;rpm;smk;swf;vob;vp6;webm;wm;wmp;wmv";

    internal const string AudioExtensions =
        "aac;ac3;aif;aifc;aiff;amr;ape;au;cda;dts;flac;it;m3u;m4a;mid;midi;mka;mod;mp2;mp3;mpa;" +
        "ogg;ogm;ra;rmi;snd;spc;umx;vgm;vgz;voc;wav;wma";

    private readonly List<QbarFilter> _filters = new List<QbarFilter>();
    private readonly Dictionary<string, QbarFilter> _byKeyword = new Dictionary<string, QbarFilter>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 内置关键字别名：<c>doc / pic / video / audio / file / folder</c> 永远能被解析，
    /// 哪怕用户把关键字改成了拼音缩写（真实配置里就有人改成了 wd / tp / sp / yp / wj / wjj）。
    ///
    /// 为什么不做成"改名后旧名字失效"：肌肉记忆是长在手指上的，用户把 doc 改成 wd 是为了更快，
    /// 不是为了让 doc 从此查不到东西；而验收用例 4 要求 doc: 可用，不该被一次改名永久破坏。
    /// 别名只指向内置项、不进设置，所以也不会让设置文件多出一份"看不见的过滤器"。
    /// </summary>
    private readonly Dictionary<string, QbarFilter> _byAlias = new Dictionary<string, QbarFilter>(StringComparer.OrdinalIgnoreCase);

    private QbarFilterCatalog()
    {
    }

    /// <summary>全部过滤器，内置在前、自定义在后（设置页和调试面板都按这个顺序展示）。</summary>
    internal IReadOnlyList<QbarFilter> All => _filters;

    internal static QbarFilterCatalog Create(IEnumerable<QbarFilter>? userFilters)
    {
        var catalog = new QbarFilterCatalog();
        foreach (QbarFilter builtin in Builtins())
        {
            catalog.Add(builtin);
        }

        if (userFilters == null) return catalog;

        var byId = new Dictionary<string, QbarFilter>(StringComparer.OrdinalIgnoreCase);
        foreach (QbarFilter builtin in catalog._filters)
        {
            byId[builtin.Id] = builtin;
        }

        /* 第一轮：Id 命中内置项 → 覆盖那一条。
         *
         * 为什么必须先按 Id：用户把「文档」的关键字从 doc 改成 WD 时，按关键字找
         * 永远找不到内置的 doc，于是列表里会多出一条"新过滤器"，而内置那条还写着
         * doc —— 用户看到的就是"改完没生效，还多了一份"。Id 是稳定身份，
         * 关键字是可改的名字，认身份不认名字。 */
        var leftovers = new List<QbarFilter>();
        foreach (QbarFilter item in userFilters)
        {
            /* 停用项也要进来：它的 Enabled=false 本身就是一条设置（不是"不存在"）。
             * 以前这里跳过停用项，结果内置项永远启着、用户自定义项会整行消失。 */
            if (item == null || string.IsNullOrWhiteSpace(item.Keyword))
            {
                continue;
            }

            string keyword = NormalizeKeyword(item.Keyword);
            if (keyword.Length == 0) continue;

            if (item.Id != null && byId.TryGetValue(item.Id, out QbarFilter? target))
            {
                catalog.Override(target, item, keyword);
                continue;
            }

            leftovers.Add(item);
        }

        /* 第二轮：老配置/用户自建项仍然按关键字盖内置项（兼容旧行为）。 */
        foreach (QbarFilter item in leftovers)
        {
            string keyword = NormalizeKeyword(item.Keyword);
            if (keyword.Length == 0) continue;

            if (catalog._byKeyword.TryGetValue(keyword, out QbarFilter? exist) && IsBuiltinId(exist.Id))
            {
                catalog.Override(exist, item, keyword);
                continue;
            }

            var clone = item.Clone();
            clone.Keyword = keyword;
            if (string.IsNullOrWhiteSpace(clone.Id))
            {
                clone.Id = "user." + keyword;
            }

            catalog.Add(clone);
        }

        return catalog;
    }

    /// <summary>按关键字找过滤器。关键字统一小写去尾部冒号，<c>Doc:</c> 和 <c>doc</c> 是同一个。</summary>
    internal QbarFilter? TryFind(string keyword)
    {
        string key = NormalizeKeyword(keyword);
        if (key.Length == 0) return null;
        if (_byKeyword.TryGetValue(key, out QbarFilter? found)) return found;
        return _byAlias.TryGetValue(key, out QbarFilter? aliased) ? aliased : null;
    }

    /// <summary>
    /// 把用户项盖到内置项上：字段取用户值，Id 保留内置 Id（"恢复默认"靠它），
    /// 关键字按用户改后的值重建索引键 —— 旧关键字必须摘掉，
    /// 否则输入 doc 还能命中已经改名成 WD 的那条。
    /// </summary>
    private void Override(QbarFilter target, QbarFilter item, string keyword)
    {
        string oldKey = NormalizeKeyword(target.Keyword);
        if (oldKey.Length > 0 && oldKey != keyword
            && _byKeyword.TryGetValue(oldKey, out QbarFilter? held) && ReferenceEquals(held, target))
        {
            _byKeyword.Remove(oldKey);
        }

        target.Name = string.IsNullOrWhiteSpace(item.Name) ? target.Name : item.Name;
        target.Icon = string.IsNullOrWhiteSpace(item.Icon) ? target.Icon : item.Icon;
        target.Extensions = string.IsNullOrWhiteSpace(item.Extensions) ? target.Extensions : item.Extensions;
        target.SearchForFiles = item.SearchForFiles;
        target.SearchForFolders = item.SearchForFolders;
        target.ExtraQuery = item.ExtraQuery ?? "";
        target.SearchInFolders = item.SearchInFolders ?? "";
        target.DateModifiedRange = item.DateModifiedRange ?? "";
        /* Enabled 必须听用户的：写死 true 会让设置页那个开关变成"点了没用、刷新又弹回来"，
         * 而编辑器里也确实给了「启用」这一项。 */
        target.Enabled = item.Enabled;
        target.Keyword = keyword;
        _byKeyword[keyword] = target;
    }

    private void Add(QbarFilter filter)
    {
        _filters.Add(filter);
        string key = NormalizeKeyword(filter.Keyword);
        _byKeyword[key] = filter;
        if (IsBuiltinId(filter.Id)) _byAlias[key] = filter;
    }

    internal static string NormalizeKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return "";
        string value = keyword.Trim().TrimEnd(':').ToLowerInvariant();
        return value.Trim();
    }

    internal static bool IsBuiltinId(string? id) =>
        id != null && id.StartsWith("builtin.", StringComparison.Ordinal);

    /// <summary>内置项。每次返回新对象，调用方随便改，不会污染下一次合并。</summary>
    internal static IEnumerable<QbarFilter> Builtins()
    {
        yield return new QbarFilter
        {
            Id = IdDoc, Name = "文档", Keyword = "doc", Icon = "\uE8A5",
            Extensions = DocExtensions, SearchForFiles = true,
        };
        yield return new QbarFilter
        {
            Id = IdPic, Name = "图片", Keyword = "pic", Icon = "\uEB9F",
            Extensions = PicExtensions, SearchForFiles = true,
        };
        yield return new QbarFilter
        {
            Id = IdVideo, Name = "视频", Keyword = "video", Icon = "\uE714",
            Extensions = VideoExtensions, SearchForFiles = true,
        };
        yield return new QbarFilter
        {
            Id = IdAudio, Name = "音频", Keyword = "audio", Icon = "\uE8D6",
            Extensions = AudioExtensions, SearchForFiles = true,
        };
        yield return new QbarFilter
        {
            Id = IdFile, Name = "只看文件", Keyword = "file", Icon = "\uE7C3",
            SearchForFiles = true, SearchForFolders = false,
        };
        yield return new QbarFilter
        {
            Id = IdFolder, Name = "只看文件夹", Keyword = "folder", Icon = "\uE8B7",
            SearchForFiles = false, SearchForFolders = true,
        };
        yield return new QbarFilter
        {
            /* 只看正文：关键词只去正文索引里匹配。
             * 关键字用 content —— 和 content: 谓词同一个词，用户不用记两套写法。 */
            Id = IdContent, Name = "只看正文", Keyword = "content", Icon = "\uE721",
            SearchForFiles = true, SearchForFolders = false, ContentOnly = true,
        };
    }

    /// <summary>设置页"恢复默认"用：返回内置项的默认值（含当前用户覆盖过的字段）。</summary>
    internal static List<QbarFilter> DefaultUserOverrides() => new List<QbarFilter>();

    /// <summary>扩展名串 → 去重点集的集合。<c>ext:pdf;docx</c> 和过滤器共用这一套解析。</summary>
    internal static void FillExtensions(string? raw, ISet<string> target)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        foreach (string piece in raw.Split(new[] { ';', ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string ext = piece.Trim().TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0) continue;
            if (ext == "*") continue;
            target.Add(ext);
        }
    }

    /// <summary>扩展名集合 → <c>ext:</c> 参数串（调试面板显示用）。</summary>
    internal static string JoinExtensions(IEnumerable<string> extensions) => string.Join(";", extensions);
}
