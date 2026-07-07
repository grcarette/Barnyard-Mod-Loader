namespace UCHModLoader.Core.Services;

/// <summary>
/// The curated tag vocabulary. Shared by the server (validation) and the
/// loader (tag pickers and filters) so the two can never disagree.
/// </summary>
public static class ModTags
{
    public const int MaxTagsPerMod = 3;

    public static readonly IReadOnlyList<string> All = new[]
    {
        "Quality of Life",
        "Content",
        "UI",
        "Party",
        "Challenge",
        "Freeplay"
    };

    public static bool IsValid(string tag) =>
        All.Contains(tag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonical casing for a tag (so "ui" stores as "UI").</summary>
    public static string Canonical(string tag) =>
        All.FirstOrDefault(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) ?? tag;
}