namespace UCHModLoader.Core.Services;

/// <summary>
/// Minimal version constraint parser. Supports "*", "1.2.3" (exact),
/// ">=1.2.3", ">1.2.3", "<=1.2.3", "<1.2.3".
/// </summary>
public static class VersionRange
{
    public static bool Satisfies(string installedVersion, string constraint)
    {
        constraint = constraint.Trim();
        if (constraint is "*" or "") return true;

        var installed = Version.Parse(installedVersion);

        if (constraint.StartsWith(">=")) return installed >= Version.Parse(constraint[2..].Trim());
        if (constraint.StartsWith("<=")) return installed <= Version.Parse(constraint[2..].Trim());
        if (constraint.StartsWith(">"))  return installed >  Version.Parse(constraint[1..].Trim());
        if (constraint.StartsWith("<"))  return installed <  Version.Parse(constraint[1..].Trim());
        if (constraint.StartsWith("="))  return installed == Version.Parse(constraint[1..].Trim());
        return installed == Version.Parse(constraint);
    }
}
