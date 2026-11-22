using System.Reflection;

namespace MomentFinder.App.Services;

/// <summary>
/// Which MomentFinder this is, for the window's title and Settings: the version of the release (its tag) and the
/// commit it was built from. A build made while developing has no release version of its own and says so.
/// </summary>
public static class AppVersion
{
    // "0.6.0+49796ee44d31...": the version, and the commit the build system adds.
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    public static string Number => Informational.Split('+')[0];

    /// <summary>The first characters of the commit, as GitHub shows it; empty when the build does not know it.</summary>
    public static string Commit => Informational.IndexOf('+') is >= 0 and var plus ? Informational[(plus + 1)..][..Math.Min(7, Informational.Length - plus - 1)] : "";

#if DEBUG
    public static bool IsRelease => false;
#else
    public static bool IsRelease => true;
#endif

    /// <summary>The main window's title.</summary>
    public static string Title => IsRelease ? $"MomentFinder {Number}" : "MomentFinder (development build)";

    /// <summary>In Settings: the version, and the commit to compare with GitHub.</summary>
    public static string Long =>
        (IsRelease ? $"Version {Number}" : "Development build, not a released version") + (Commit.Length > 0 ? $" (commit {Commit})" : "");
}
