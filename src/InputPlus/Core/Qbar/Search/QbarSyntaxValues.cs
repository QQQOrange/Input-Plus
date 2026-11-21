using System;
using System.Globalization;

namespace InputPlus.Core.Qbar.Search;

/// <summary>
/// 语法值解析：尺寸、日期。
///
/// 单独一个文件的原因是这两块最容易"看起来对但边界全错"：<c>size:>1gb</c>
/// 里的 gb 到底是 1000³ 还是 1024³、<c>date:week</c> 到底含不含今天、
/// 跨月跨年怎么算 —— 这些都不该和分词、翻译搅在一个函数里，
/// 否则一个月份边界写错，排查时会以为整个翻译层坏了。
/// </summary>
internal static class QbarSyntaxValues
{
    /// <summary>索引里日期的基准日。存 ushort 天数，65535 表示"未知"。</summary>
    internal static readonly DateTime DayEpoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);

    internal const int UnknownDay = ushort.MaxValue;

    internal static int ToDay(DateTime time)
    {
        if (time.Year < 2000) return 0;
        int days = (int)(time.Date - DayEpoch).TotalDays;
        if (days < 0) return 0;
        return days >= UnknownDay - 1 ? UnknownDay - 1 : days;
    }

    internal static DateTime FromDay(int day) => DayEpoch.AddDays(Math.Max(0, Math.Min(day, UnknownDay - 1)));

    /// <summary>
    /// 尺寸。<c>1gb</c> 按 1024³ 算 —— 和资源管理器"大小"列一致，
    /// 用户拿 1GB 的文件试 <c>size:>1gb</c> 时不会出现"明明大于却搜不到"。
    /// </summary>
    internal static bool TryParseSize(string? raw, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = raw.Trim().ToLowerInvariant().Replace(" ", "");
        long multiplier = 1;
        if (value.EndsWith("tb", StringComparison.Ordinal)) { multiplier = 1024L * 1024 * 1024 * 1024; value = value.Substring(0, value.Length - 2); }
        else if (value.EndsWith("gb", StringComparison.Ordinal)) { multiplier = 1024L * 1024 * 1024; value = value.Substring(0, value.Length - 2); }
        else if (value.EndsWith("mb", StringComparison.Ordinal)) { multiplier = 1024L * 1024; value = value.Substring(0, value.Length - 2); }
        else if (value.EndsWith("kb", StringComparison.Ordinal)) { multiplier = 1024L; value = value.Substring(0, value.Length - 2); }
        else if (value.EndsWith("b", StringComparison.Ordinal)) { value = value.Substring(0, value.Length - 1); }

        value = value.Trim();
        if (value.Length == 0) return false;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return false;
        if (number < 0 || double.IsNaN(number) || double.IsInfinity(number)) return false;
        double product = number * multiplier;
        if (product > long.MaxValue) product = long.MaxValue;
        bytes = (long)product;
        return true;
    }

    /// <summary>
    /// 日期范围。返回的是"闭区间天数"，-1 表示该端不限制。
    /// 支持：today yesterday week|thisweek lastweek month|thismonth lastmonth year|thisyear lastyear
    ///      2026（整年）/ 2026-07（整月）/ 2026-07-01（整天）/ 2026-07-01..2026-07-31。
    /// </summary>
    internal static bool TryParseDateRange(string? raw, DateTime now, out int minDay, out int maxDay)
    {
        minDay = -1;
        maxDay = -1;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = raw.Trim().ToLowerInvariant();
        DateTime today = now.Date;

        int split = value.IndexOf("..", StringComparison.Ordinal);
        if (split >= 0)
        {
            string left = value.Substring(0, split).Trim();
            string right = value.Substring(split + 2).Trim();
            bool hasLeft = TryParseDatePoint(left, today, out int lo, out int loMax);
            bool hasRight = TryParseDatePoint(right, today, out int hi, out int hiMax);
            if (!hasLeft && !hasRight) return false;
            minDay = hasLeft ? lo : -1;
            maxDay = hasRight ? hiMax : (hasLeft ? loMax : -1);
            /* "2026-07-01..2026-07-31" 两端都是整块，取右侧的末日；
             * ">2026-07" 这种单边走下面的分支。 */
            if (hasRight && hiMax >= hi) maxDay = hiMax;
            return true;
        }

        /* 单点。"today" → 一整天；"2026-07" → 整月；"2026" → 整年。 */
        if (value.StartsWith(">=", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal))
        {
            string rest = value.TrimStart('>').TrimStart('=').Trim();
            if (!TryParseDatePoint(rest, today, out int lo, out _)) return false;
            minDay = lo;
            return true;
        }

        if (value.StartsWith("<=", StringComparison.Ordinal) || value.StartsWith("<", StringComparison.Ordinal))
        {
            string rest = value.TrimStart('<').TrimStart('=').Trim();
            if (!TryParseDatePoint(rest, today, out _, out int hiMax)) return false;
            maxDay = hiMax;
            return true;
        }

        if (!TryParseDatePoint(value, today, out int start, out int end)) return false;
        minDay = start;
        maxDay = end;
        return true;
    }

    private static bool TryParseDatePoint(string token, DateTime today, out int minDay, out int maxDay)
    {
        minDay = -1;
        maxDay = -1;
        if (string.IsNullOrEmpty(token)) return false;

        switch (token)
        {
            case "today":
            case "今天":
                minDay = maxDay = ToDay(today);
                return true;
            case "yesterday":
            case "昨天":
                minDay = maxDay = ToDay(today.AddDays(-1));
                return true;
            case "week":
            case "thisweek":
            case "本周":
            case "这周":
            {
                int dow = ((int)today.DayOfWeek + 6) % 7; // 周一为 0
                DateTime start = today.AddDays(-dow);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddDays(6));
                return true;
            }
            case "lastweek":
            case "上周":
            {
                int dow = ((int)today.DayOfWeek + 6) % 7;
                DateTime start = today.AddDays(-dow - 7);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddDays(6));
                return true;
            }
            case "month":
            case "thismonth":
            case "本月":
            {
                DateTime start = new DateTime(today.Year, today.Month, 1);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddMonths(1).AddDays(-1));
                return true;
            }
            case "lastmonth":
            case "上月":
            {
                DateTime start = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddMonths(1).AddDays(-1));
                return true;
            }
            case "year":
            case "thisyear":
            case "今年":
            {
                DateTime start = new DateTime(today.Year, 1, 1);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddYears(1).AddDays(-1));
                return true;
            }
            case "lastyear":
            case "去年":
            {
                DateTime start = new DateTime(today.Year - 1, 1, 1);
                minDay = ToDay(start);
                maxDay = ToDay(start.AddYears(1).AddDays(-1));
                return true;
            }
        }

        if (DateTime.TryParseExact(token, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exactDay))
        {
            minDay = maxDay = ToDay(exactDay);
            return true;
        }

        if (DateTime.TryParseExact(token, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime month))
        {
            minDay = ToDay(month);
            maxDay = ToDay(month.AddMonths(1).AddDays(-1));
            return true;
        }

        if (token.Length == 4 && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year >= 1900 && year <= 2200)
        {
            var start = new DateTime(year, 1, 1);
            minDay = ToDay(start);
            maxDay = ToDay(start.AddYears(1).AddDays(-1));
            return true;
        }

        return false;
    }

    /// <summary>日期范围 → 人话（调试面板 / 分面计数用）。</summary>
    internal static string DescribeDateRange(int minDay, int maxDay)
    {
        if (minDay < 0 && maxDay < 0) return "";
        string left = minDay < 0 ? "…" : FromDay(minDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string right = maxDay < 0 ? "…" : FromDay(maxDay).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return left + "..." + right;
    }
}
