namespace UCHModLoader.App.ViewModels;

/// <summary>Shrink-to-fit font sizing for text that must stay on its layout budget.</summary>
public static class UiText
{
    /// <summary>
    /// Returns baseSize for text up to fitLength characters, then scales down
    /// proportionally, never below minSize.
    /// </summary>
    public static double FitFontSize(string? text, double baseSize, int fitLength, double minSize)
    {
        var length = text?.Length ?? 0;
        if (length <= fitLength) return baseSize;
        return Math.Max(minSize, baseSize * fitLength / length);
    }
}
