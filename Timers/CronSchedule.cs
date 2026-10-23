namespace ShiroBot.AiChatPlugin.Timers;

/// <summary>
/// 标准 5 段 cron：分 时 日 月 周，按宿主本地时区计算。每段支持 *、数字、a-b、逗号列表和 /步长；
/// 周的 0 和 7 都表示周日。日和周同时受限时按 cron 惯例取并集。
/// </summary>
internal sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private bool _dayRestricted;
    private bool _weekdayRestricted;

    public string Expression { get; }

    private CronSchedule(string expression) => Expression = expression;

    public static CronSchedule? TryParse(string? expression)
    {
        var fields = (expression ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return null;
        var cron = new CronSchedule(string.Join(' ', fields));
        var weekdays = new bool[8];
        if (!Fill(fields[0], cron._minutes, 0, 59) || !Fill(fields[1], cron._hours, 0, 23) ||
            !Fill(fields[2], cron._days, 1, 31) || !Fill(fields[3], cron._months, 1, 12) || !Fill(fields[4], weekdays, 0, 7))
            return null;
        for (var i = 0; i < 7; i++) cron._weekdays[i] = weekdays[i];
        if (weekdays[7]) cron._weekdays[0] = true;
        cron._dayRestricted = fields[2] != "*";
        cron._weekdayRestricted = fields[4] != "*";
        return cron;
    }

    /// <summary>返回严格晚于 after 的下一次触发时间；5 年内没有匹配（如 2 月 30 日）时返回 null。</summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0).AddMinutes(1);
        for (var day = start.Date; day < start.Date.AddYears(5); day = day.AddDays(1))
        {
            if (!MatchesDay(day)) continue;
            for (var hour = 0; hour < 24; hour++)
            {
                if (!_hours[hour]) continue;
                for (var minute = 0; minute < 60; minute++)
                {
                    if (!_minutes[minute]) continue;
                    var candidate = day.AddHours(hour).AddMinutes(minute);
                    // 夏令时跳过的本地时间不存在，直接跳到下一个匹配。
                    if (candidate < start || zone.IsInvalidTime(candidate)) continue;
                    return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
                }
            }
        }
        return null;
    }

    /// <summary>从 after 起相邻两次触发的最小间隔，用于拒绝过于频繁的重复规则。</summary>
    public TimeSpan MinimumGap(DateTimeOffset after, int samples = 50)
    {
        var min = TimeSpan.MaxValue;
        var previous = Next(after);
        for (var i = 0; i < samples && previous is { } current; i++)
        {
            var next = Next(current);
            if (next is null) break;
            if (next.Value - current < min) min = next.Value - current;
            previous = next;
        }
        return min;
    }

    private bool MatchesDay(DateTime day)
    {
        if (!_months[day.Month]) return false;
        var dayMatch = _days[day.Day];
        var weekdayMatch = _weekdays[(int)day.DayOfWeek];
        if (_dayRestricted && _weekdayRestricted) return dayMatch || weekdayMatch;
        return dayMatch && weekdayMatch;
    }

    private static bool Fill(string field, bool[] target, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            var step = 1;
            var range = part;
            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(part[(slash + 1)..], out step) || step < 1) return false;
                range = part[..slash];
            }
            int from, to;
            if (range == "*") (from, to) = (min, max);
            else if (range.Split('-') is [var a, var b])
            {
                if (!int.TryParse(a, out from) || !int.TryParse(b, out to) || from > to) return false;
            }
            else if (int.TryParse(range, out from)) to = slash >= 0 ? max : from;
            else return false;
            if (from < min || to > max) return false;
            for (var i = from; i <= to; i += step) target[i] = true;
        }
        return true;
    }
}
