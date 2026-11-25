namespace BorgStudio.Server.Application.Statistics;

/// <summary>
/// When something will be full, from its daily history: a straight line through the last 90 days
/// (least squares), like the BorgStudio app. No forecast when it does not grow or the limit is
/// further away than five years.
/// </summary>
public static class StorageForecast
{
    private const int Days = 90;
    private static readonly int Horizon = 5 * 365;

    public static DateOnly? FullAt(IReadOnlyList<(DateOnly Day, long Used)> history, long? limit)
    {
        if (limit is not { } capacity || capacity <= 0 || history.Count < 3)
        {
            return null;
        }

        var last = history[^1];
        if (last.Used >= capacity)
        {
            return last.Day;
        }

        var points = history.Where(p => p.Day > last.Day.AddDays(-Days)).ToList();
        if (points.Count < 3)
        {
            return null;
        }

        // x: days since the first point, y: bytes.
        var first = points[0].Day;
        double n = points.Count;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        foreach (var (day, used) in points)
        {
            double x = day.DayNumber - first.DayNumber;
            sumX += x;
            sumY += used;
            sumXy += x * used;
            sumXx += x * x;
        }

        var denominator = n * sumXx - sumX * sumX;
        if (denominator == 0)
        {
            return null;
        }

        var slope = (n * sumXy - sumX * sumY) / denominator;
        if (slope <= 0)
        {
            return null;
        }

        var intercept = (sumY - slope * sumX) / n;
        var reachedAt = (capacity - intercept) / slope;
        var days = (int)Math.Ceiling(reachedAt) - (last.Day.DayNumber - first.DayNumber);
        return days > Horizon ? null : last.Day.AddDays(Math.Max(days, 0));
    }

    /// <summary>The worst outcome of borg's exit code: 0 fine, 1 warnings, anything else an error.</summary>
    public static Health HealthOf(int? exitCode) => exitCode switch
    {
        null => Health.Unknown,
        0 => Health.Ok,
        1 => Health.Warning,
        _ => Health.Failed,
    };
}

/// <summary>Traffic-light state, ordered from good to bad.</summary>
public enum Health
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Failed = 3,
}
