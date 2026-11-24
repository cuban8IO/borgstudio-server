using BorgStudio.Server.Application.Statistics;

namespace BorgStudio.Server.Web.Components.Shared.Charts;

/// <summary>One line of a <see cref="LineChart"/>.</summary>
/// <param name="Color">A CSS color, e.g. "var(--mud-palette-primary)".</param>
public sealed record ChartSeries(string Name, IReadOnlyList<(DateTime X, double Y)> Points, string Color, bool Area = false, bool Dashed = false);

/// <summary>One column of a <see cref="ColumnChart"/>.</summary>
public sealed record ChartColumn(string Label, double Value, Health Health, string Tooltip);

public static class ChartColors
{
    public const string Primary = "var(--mud-palette-primary)";
    public const string Info = "var(--mud-palette-info)";
    public const string Secondary = "var(--mud-palette-secondary)";
    public const string Warning = "var(--mud-palette-warning)";
    public const string Error = "var(--mud-palette-error)";
    public const string Success = "var(--mud-palette-success)";
    public const string Muted = "var(--mud-palette-text-disabled)";

    public static string Of(Health health) => health switch
    {
        Health.Ok => Success,
        Health.Warning => Warning,
        Health.Failed => Error,
        _ => Info,
    };
}
