using System.Globalization;
using System.Text.RegularExpressions;

namespace BorgStudio.Server.Domain.Changelogs;

/// <summary>One release in the changelog: its version, date and changes grouped by kind.</summary>
public sealed record ChangelogRelease(Version Version, DateOnly? Date, IReadOnlyList<ChangelogSection> Sections);

/// <summary>A group of changes, e.g. "New features" or "Fixes".</summary>
/// <param name="Title">The heading as written ("New features", "Fixes", …).</param>
public sealed record ChangelogSection(string Title, IReadOnlyList<ChangelogItem> Items);

/// <param name="PullRequest">The number of the pull request that made the change, if named ("(#12)").</param>
public sealed record ChangelogItem(string Text, int? PullRequest);

/// <summary>
/// Reads CHANGELOG.md and release notes as written by the release workflow:
/// "## [0.2.0] - 2026-11-24", "### New features", "- Title (#12)". Anything else is ignored.
/// </summary>
public static partial class Changelog
{
    public static IReadOnlyList<ChangelogRelease> Parse(string? markdown)
    {
        var releases = new List<ChangelogRelease>();
        (Version Version, DateOnly? Date)? current = null;
        var sections = new List<ChangelogSection>();

        void Finish()
        {
            if (current is { } release)
            {
                releases.Add(new ChangelogRelease(release.Version, release.Date, sections));
            }

            sections = [];
        }

        foreach (var block in ReadBlocks(markdown))
        {
            switch (block)
            {
                case ReleaseBlock release:
                    Finish();
                    current = (release.Version, release.Date);
                    break;
                case SectionBlock section when current is not null:
                    sections.Add(section.Section);
                    break;
            }
        }

        Finish();
        return releases;
    }

    /// <summary>The sections of one release's notes (as on GitHub), up to the first release heading.</summary>
    public static IReadOnlyList<ChangelogSection> ParseNotes(string? markdown) =>
        ReadBlocks(markdown)
            .TakeWhile(block => block is not ReleaseBlock)
            .OfType<SectionBlock>()
            .Select(block => block.Section)
            .ToList();

    /// <summary>"0.2.0" or "v0.2.0" as a comparable version; <c>null</c> for anything else.</summary>
    public static Version? ParseVersion(string? text)
    {
        var value = text?.Trim().TrimStart('v', 'V');
        return Version.TryParse(value, out var version) && version.Build >= 0 && version.Revision < 0 ? version : null;
    }

    private abstract record Block;

    private sealed record ReleaseBlock(Version Version, DateOnly? Date) : Block;

    private sealed record SectionBlock(ChangelogSection Section) : Block;

    private static List<Block> ReadBlocks(string? markdown)
    {
        var blocks = new List<Block>();
        string? title = null;
        List<ChangelogItem>? items = null;

        void CloseSection()
        {
            if (title is not null && items is { Count: > 0 })
            {
                blocks.Add(new SectionBlock(new ChangelogSection(title, items)));
            }

            title = null;
            items = null;
        }

        foreach (var rawLine in (markdown ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ');
            if (ReleaseHeading().Match(line) is { Success: true } release)
            {
                CloseSection();
                if (ParseVersion(release.Groups["version"].Value) is { } version)
                {
                    var date = DateOnly.TryParseExact(release.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var parsed) ? parsed : (DateOnly?)null;
                    blocks.Add(new ReleaseBlock(version, date));
                }

                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                CloseSection();
                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    title = line[4..].Trim();
                    items = [];
                }

                continue;
            }

            if (items is not null && line.StartsWith("- ", StringComparison.Ordinal))
            {
                var text = line[2..].Trim();
                int? pullRequest = null;
                if (PullRequestSuffix().Match(text) is { Success: true } match)
                {
                    pullRequest = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
                    text = text[..match.Index].TrimEnd();
                }

                items.Add(new ChangelogItem(text, pullRequest));
            }
        }

        CloseSection();
        return blocks;
    }

    [GeneratedRegex(@"^## \[(?<version>[^\]]+)\](?:\s*-\s*(?<date>\d{4}-\d{2}-\d{2}))?")]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"\(#(?<number>\d+)\)$")]
    private static partial Regex PullRequestSuffix();
}
