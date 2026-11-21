using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InputPlus.Core.Qbar;

/// <summary>
/// 一条搜索联想的来源。
///
/// 三个实现都遵守同一条约定：<see cref="QueryAsync"/> **永远不抛异常**，
/// 网络失败、超时、对方改版、返回乱码，一律给空列表。
/// Qbar 是"边打边搜"的面板，联想只是锦上添花；任何一次失败都不该让
/// 用户看到红字，更不该让输入卡住 —— 所以错误在这一层就被吃掉。
/// </summary>
public abstract class SuggestionProvider
{
    /// <summary>
    /// 共享 HttpClient。
    ///
    /// 每次请求 new 一个 HttpClient 会耗尽 socket（TIME_WAIT），
    /// 而联想是高频请求（每 250ms 防抖后一次），必须复用。
    /// 超时主要靠调用方传入的 CancellationToken；这里的 10 秒只是兜底，
    /// 防止某个实现忘了传 token 时永久挂住。
    /// </summary>
    private static readonly HttpClient Client = CreateClient();

    /// <summary>GB18030 解码器；百度接口返回的是 GBK 字节，不是 UTF-8。</summary>
    private static Encoding? _gb18030;

    protected SuggestionProvider(int max)
    {
        // 设置里允许 1..10；这里再夹一次，防止手改 settings.json 写出离谱值。
        Max = Math.Clamp(max, 1, 10);
    }

    /// <summary>最多返回几条（去重后）。</summary>
    public int Max { get; }

    /// <summary>展示名，仅用于日志/排查。</summary>
    public abstract string Name { get; }

    /// <summary>稳定标识：baidu / google / bing。</summary>
    public abstract string Id { get; }

    /// <summary>
    /// 查询联想词。实现内部必须把异常全部吞掉并返回空列表。
    /// </summary>
    public abstract Task<IReadOnlyList<string>> QueryAsync(string keyword, CancellationToken ct);

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 4,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// 发起 GET 并读回原始字节。失败返回 null，不抛。
    ///
    /// 读字节而不是读字符串：百度返回 GBK，HttpClient 会按响应头猜编码，
    /// 猜错就是一片问号；字节拿回来我们自己按来源解码最稳。
    /// </summary>
    protected static async Task<byte[]?> GetBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 部分联想接口对陌生 UA 直接 204/403，伪装成浏览器最省事。
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("Accept", "application/json,text/javascript,*/*;q=0.8");

            using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return bytes.Length == 0 ? null : bytes;
        }
        catch
        {
            // 网络、DNS、超时、证书……全部视为"这次没有联想"
            return null;
        }
    }

    /// <summary>
    /// 清洗 + 去重 + 截断。
    ///
    /// 联想接口经常把用户输入原样放回第一条（"天气" → "天气"），
    /// 也会返回带换行/多余空格的脏数据；这些都不该原样进列表。
    /// 用 OrdinalIgnoreCase 去重是为了英文关键词（"Chrome" / "chrome" 只留一条）。
    /// </summary>
    protected IReadOnlyList<string> Finish(IEnumerable<string>? items)
    {
        if (items == null) return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(Max);
        foreach (string? raw in items)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string text = Whitespace.Replace(raw, " ").Trim();
            if (text.Length == 0 || text.Length > 200) continue;
            if (!seen.Add(text)) continue;
            result.Add(text);
            if (result.Count >= Max) break;
        }
        return result;
    }

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 百度的响应是 GBK；但如果哪天它改成 UTF-8，不能把中文解成乱码。
    /// 先按严格 UTF-8 试一次，失败再退 GB18030 —— GBK 中文几乎必然不是合法 UTF-8，
    /// 这个判定在实际数据上很可靠，且不需要额外的 charset 探测。
    /// </summary>
    protected static string DecodeBaidu(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            // 不是 UTF-8，继续走 GB18030
        }

        try
        {
            // CodePagesEncodingProvider 在 .NET Core 里要显式注册才有 GBK/GB18030。
            // 注册是幂等的，放这里可以保证只依赖"用到时才初始化"。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gb18030 ??= Encoding.GetEncoding("gb18030");
            return _gb18030.GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>JSON 字符串反转义（百度返回的是 JS 字面量，不是严格 JSON）。</summary>
    protected static string UnescapeJson(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(c);
                continue;
            }

            char n = raw[++i];
            switch (n)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < raw.Length
                        && ushort.TryParse(raw.AsSpan(i + 1, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort cp))
                    {
                        sb.Append((char)cp);
                        i += 4;
                    }
                    else
                    {
                        sb.Append("\\u");
                    }
                    break;
                default:
                    // 未知转义原样保留，至少不丢字符
                    sb.Append('\\').Append(n);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析 Google/Bing 的 osjson：<c>["关键词",["建议1","建议2"]]</c>。
    ///
    /// Google 的 client=chrome 会返回嵌套数组（每条是 [文本, 类型, ...]），
    /// 所以这里对第二层既接受字符串，也接受"数组的第一个元素是字符串"。
    /// 版本变化时不至于整条链路失效。
    /// </summary>
    protected static List<string> ParseJsonSuggestions(string json)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return list;
        // 空数组也是合法响应（接口没词时会这样回），别让 root[0] 越界。
        if (root.GetArrayLength() == 0) return list;

        // 标准格式第二项才是建议数组；只有一项时也兼容一下。
        JsonElement arr = root.GetArrayLength() > 1 ? root[1] : root[0];
        if (arr.ValueKind != JsonValueKind.Array) return list;

        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
            else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() > 0
                     && item[0].ValueKind == JsonValueKind.String)
            {
                string? s = item[0].GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}

/// <summary>
/// 百度联想。
///
/// 接口：<c>https://suggestion.baidu.com/su?wd=关键词&amp;cb=</c>
/// 返回是 JS 而不是 JSON：<c>window.baidu.sug({q:"天气",p:false,s:["天气预报",...]});</c>
/// （cb 为空时有的节点会省掉外层函数名，只返回 <c>({...})</c>，所以解析只认 s:[...]，
/// 不依赖前缀。）
/// </summary>
public sealed class BaiduSuggestionProvider : SuggestionProvider
{
    public BaiduSuggestionProvider(int max) : base(max) { }

    public override string Name => "百度";
    public override string Id => SuggestionEngines.Baidu;

    public override async Task<IReadOnlyList<string>> QueryAsync(string keyword, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<string>();

            string url = "https://suggestion.baidu.com/su?wd="
                         + Uri.EscapeDataString(keyword) + "&cb=";
            byte[]? bytes = await GetBytesAsync(url, ct).ConfigureAwait(false);
            if (bytes == null) return Array.Empty<string>();

            string body = DecodeBaidu(bytes);
            return Finish(Parse(body));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 从 <c>s:[...]</c> 里逐个字面量地取字符串。
    ///
    /// 不用"正则匹配到第一个 ]"：联想词本身可能带方括号（"天气预报[北京]"），
    /// 那样会提前截断。这里扫一遍字符，只在字符串外认括号，能正确处理转义引号。
    /// </summary>
    internal static List<string> Parse(string body)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(body)) return list;

        int arrayStart = FindSuggestArray(body);
        if (arrayStart < 0) return list;

        int i = arrayStart;   // 指向 's:[' 之后
        int depth = 1;
        bool inString = false;
        bool escape = false;
        var sb = new StringBuilder();

        for (; i < body.Length; i++)
        {
            char c = body[i];
            if (inString)
            {
                if (escape)
                {
                    // 保留反斜杠，交给 UnescapeJson 统一处理 \uXXXX 之类
                    sb.Append('\\').Append(c);
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    list.Add(UnescapeJson(sb.ToString()));
                    sb.Clear();
                    inString = false;
                    if (list.Count >= 50) break;   // 防御性上限，正常不会到
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inString = true;
                sb.Clear();
            }
            else if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                if (depth == 0) break;
            }
        }
        return list;
    }

    /// <summary>
    /// 找出真正的 <c>s</c> 属性数组，返回 '[' 之后的下标。
    ///
    /// 不用正则直接找 <c>s:\s*\[</c>：用户搜的关键词会原样出现在前面的
    /// <c>q:"..."</c> 里，如果关键词本身带 <c>s:[</c>，正则会在字符串内部命中，
    /// 解析出来的就是关键词碎片。这里先逐字符跳过字符串（含转义），
    /// 只在字符串外认属性名，才能保证找的是结构里的那个 s。
    /// </summary>
    private static int FindSuggestArray(string body)
    {
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            if (c != 's' && c != 'S') continue;

            int j = i + 1;
            while (j < body.Length && char.IsWhiteSpace(body[j])) j++;
            if (j >= body.Length || body[j] != ':') continue;
            j++;
            while (j < body.Length && char.IsWhiteSpace(body[j])) j++;
            if (j < body.Length && body[j] == '[') return j + 1;
        }
        return -1;
    }
}

/// <summary>Google 联想：标准 osjson，返回 UTF-8 JSON 数组。</summary>
public sealed class GoogleSuggestionProvider : SuggestionProvider
{
    public GoogleSuggestionProvider(int max) : base(max) { }

    public override string Name => "谷歌";
    public override string Id => SuggestionEngines.Google;

    public override async Task<IReadOnlyList<string>> QueryAsync(string keyword, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<string>();

            string url = "https://suggestqueries.google.com/complete/search?client=firefox&hl=zh-CN&q="
                         + Uri.EscapeDataString(keyword);
            byte[]? bytes = await GetBytesAsync(url, ct).ConfigureAwait(false);
            if (bytes == null) return Array.Empty<string>();

            string json = Encoding.UTF8.GetString(bytes);
            return Finish(ParseJsonSuggestions(json));
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// 必应联想：osjson。
///
/// 主接口 api.bing.com 在部分网络/地区会直接返回 204（没有正文，但不是错误码），
/// 这时补一次 cn.bing.com —— 两者是同一套数据，只是入口不同。
/// 仍然失败就静默返回空，绝不让面板报错。
/// </summary>
public sealed class BingSuggestionProvider : SuggestionProvider
{
    private const string Primary = "https://api.bing.com/osjson.aspx?query=";
    private const string Fallback = "https://cn.bing.com/osjson.aspx?query=";

    /* osjson 是官方联想接口，但本机/部分网络实测会稳定返回 204 空正文
       （api 和 cn 都一样）。qsonhs 是必应搜索框自己用的接口，返回同一批词，
       只是包了一层 JSONP；多这一级兜底，才不会“必应联想永远是空的”。 */
    private const string Jsonp = "https://api.bing.com/qsonhs.aspx?type=cb&cb=cb&q=";

    public BingSuggestionProvider(int max) : base(max) { }

    public override string Name => "必应";
    public override string Id => SuggestionEngines.Bing;

    public override async Task<IReadOnlyList<string>> QueryAsync(string keyword, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword)) return Array.Empty<string>();

            string encoded = Uri.EscapeDataString(keyword);
            List<string> items = await FetchAsync(Primary + encoded, ct).ConfigureAwait(false);
            if (items.Count == 0 && !ct.IsCancellationRequested)
            {
                // 204/空数组时换入口再试一次；超时就不追了，避免拖长用户等待。
                items = await FetchAsync(Fallback + encoded, ct).ConfigureAwait(false);
            }
            if (items.Count == 0 && !ct.IsCancellationRequested)
            {
                byte[]? bytes = await GetBytesAsync(Jsonp + encoded, ct).ConfigureAwait(false);
                if (bytes != null) items = ParseJsonp(Encoding.UTF8.GetString(bytes));
            }
            return Finish(items);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<List<string>> FetchAsync(string url, CancellationToken ct)
    {
        byte[]? bytes = await GetBytesAsync(url, ct).ConfigureAwait(false);
        if (bytes == null) return new List<string>();
        return ParseJsonSuggestions(Encoding.UTF8.GetString(bytes));
    }

    /// <summary>
    /// 解析 qsonhs 的 JSONP：<c>if(typeof cb == 'function') cb({"AS":{"Results":[...]}});</c>
    ///
    /// 不用正则抠 Txt，而是先截出最外层 JSON 再走 JsonDocument：
    /// 联想词里可能带括号/分号，正则很容易截错。截取从第一个 '{' 到最后一个 '}'，
    /// 对“前缀函数 + 后缀注释”这种包装足够稳。
    /// </summary>
    internal static List<string> ParseJsonp(string body)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(body)) return list;

        int start = body.IndexOf('{');
        int end = body.LastIndexOf('}');
        if (start < 0 || end <= start) return list;

        using var doc = JsonDocument.Parse(body.Substring(start, end - start + 1));
        if (!doc.RootElement.TryGetProperty("AS", out JsonElement asNode)
            || !asNode.TryGetProperty("Results", out JsonElement results)
            || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement group in results.EnumerateArray())
        {
            if (!group.TryGetProperty("Suggests", out JsonElement suggests)
                || suggests.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (JsonElement item in suggests.EnumerateArray())
            {
                if (item.TryGetProperty("Txt", out JsonElement txt)
                    && txt.ValueKind == JsonValueKind.String)
                {
                    string? s = txt.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
            }
        }
        return list;
    }
}

/// <summary>
/// 联想引擎的取值与解析。
///
/// 设置里存的是 auto|baidu|google|bing；auto 表示"跟随当前搜索源"。
/// 跟随的判据是 Qbar 标签里那条 URL 模板的域名，而不是显示名 ——
/// 显示名可以被用户改（"百度"改成"度娘"），域名不会。
/// </summary>
public static class SuggestionEngines
{
    public const string Auto = "auto";
    public const string Baidu = "baidu";
    public const string Google = "google";
    public const string Bing = "bing";

    /// <summary>把任意输入归一化；未知值一律视为 auto（配置写错不炸）。</summary>
    public static string Normalize(string? value)
    {
        string v = (value ?? "").Trim().ToLowerInvariant();
        return v is Baidu or Google or Bing ? v : Auto;
    }

    /// <summary>
    /// 决定这次用哪家联想。
    ///
    /// auto 的识别顺序：百度 → 谷歌 → 必应 → 兜底必应。
    /// 兜底选必应而不是百度：必应是 JSON 接口，中英文都能出词，
    /// 而且 api/cn 两个入口可互为备份；对"用户自定义了一个我们不认识的搜索源"
    /// 这种情况，给一份可用的联想比什么都不给更符合预期。
    /// </summary>
    public static string Resolve(string? configured, string? urlTemplate)
    {
        string normalized = Normalize(configured);
        if (normalized != Auto) return normalized;

        string host = HostOf(urlTemplate);
        if (host.Contains("baidu.", StringComparison.Ordinal)) return Baidu;
        if (host.Contains("google.", StringComparison.Ordinal)
            || host == "g.cn" || host.EndsWith(".g.cn", StringComparison.Ordinal)) return Google;
        if (host.Contains("bing.", StringComparison.Ordinal)) return Bing;
        return Bing;
    }

    /// <summary>从 URL 模板里取主机名；模板里的 {q} 先换掉，否则 Uri 解析会失败。</summary>
    private static string HostOf(string? urlTemplate)
    {
        string probe = (urlTemplate ?? "").Replace("{q}", "x", StringComparison.Ordinal).Trim();
        if (probe.Length == 0) return "";
        return Uri.TryCreate(probe, UriKind.Absolute, out Uri? uri)
            ? uri.Host.ToLowerInvariant()
            : "";
    }

    public static SuggestionProvider Create(string? engine, int max) => Normalize(engine) switch
    {
        Baidu => new BaiduSuggestionProvider(max),
        Google => new GoogleSuggestionProvider(max),
        Bing => new BingSuggestionProvider(max),
        _ => new BingSuggestionProvider(max),
    };

    /// <summary>设置界面用的展示名（auto 单独显示为"跟随"）。</summary>
    public static string DisplayName(string? engine) => Normalize(engine) switch
    {
        Baidu => "百度",
        Google => "谷歌",
        Bing => "必应",
        _ => "跟随当前搜索引擎",
    };
}

/// <summary>
/// 关键词 + 引擎的内存缓存。
///
/// 联想请求是"每敲一个字一次"，而同一个词在删字/重打时会被反复查询；
/// 10 分钟 TTL 足够覆盖一次输入过程，又不会让热词永远不更新。
/// TTL=0 表示不缓存（每次真查）。
/// 只缓存非空结果：一次网络抖动导致的空列表如果被缓存，
/// 用户接下来十分钟都会看不到联想，反而更像坏了。
/// </summary>
public static class SuggestionCache
{
    private sealed class Entry
    {
        public DateTime CreatedUtc;
        public DateTime ExpiresUtc;
        public int Max;
        public IReadOnlyList<string> Items = Array.Empty<string>();
    }

    private const int Capacity = 256;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Map = new(StringComparer.Ordinal);

    public static bool TryGet(string engine, string keyword, int ttlMinutes, int max,
        out IReadOnlyList<string> items)
    {
        items = Array.Empty<string>();
        if (ttlMinutes <= 0) return false;

        string key = MakeKey(engine, keyword);
        DateTime now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!Map.TryGetValue(key, out Entry? entry)) return false;
            /* 同时看写入时的上限和**当前**设置：用户把缓存从 10 分钟改成 1 分钟，
               旧条目不该还按 10 分钟活着；ttl=0 已经在上面直接返回 false。 */
            if (entry.ExpiresUtc <= now
                || now - entry.CreatedUtc > TimeSpan.FromMinutes(ttlMinutes))
            {
                Map.Remove(key);
                return false;
            }
            /* 缓存是在某个 max 下抓的：用户把上限调大后，旧条目可能只有 6 条，
               直接复用会永远看不到第 7..10 条。让它 miss 一次、按新上限重抓；
               调小则继续复用，显示前由调用方截断。 */
            if (entry.Max < max) return false;
            items = entry.Items;
            return true;
        }
    }

    public static void Put(string engine, string keyword, int ttlMinutes, int max,
        IReadOnlyList<string> items)
    {
        if (ttlMinutes <= 0 || items.Count == 0) return;

        string key = MakeKey(engine, keyword);
        lock (Gate)
        {
            if (Map.Count >= Capacity) Prune();
            DateTime now = DateTime.UtcNow;
            Map[key] = new Entry
            {
                CreatedUtc = now,
                ExpiresUtc = now.AddMinutes(ttlMinutes),
                Max = max,
                Items = items,
            };
        }
    }

    /// <summary>测试/切换引擎时清空；正常输入流程不需要调用（key 里已经带了引擎）。</summary>
    public static void Clear()
    {
        lock (Gate) Map.Clear();
    }

    private static void Prune()
    {
        DateTime now = DateTime.UtcNow;
        var dead = new List<string>();
        foreach (var pair in Map)
            if (pair.Value.ExpiresUtc <= now) dead.Add(pair.Key);
        foreach (string key in dead) Map.Remove(key);

        // 还没降到容量以下就丢掉最早过期的一批；Map 很小，线性找最小值足够。
        while (Map.Count >= Capacity)
        {
            string? oldest = null;
            DateTime oldestAt = DateTime.MaxValue;
            foreach (var pair in Map)
            {
                if (pair.Value.ExpiresUtc >= oldestAt) continue;
                oldestAt = pair.Value.ExpiresUtc;
                oldest = pair.Key;
            }
            if (oldest == null) break;
            Map.Remove(oldest);
        }
    }

    private static string MakeKey(string engine, string keyword)
        => engine + "\n" + keyword.Trim().ToLowerInvariant();
}
