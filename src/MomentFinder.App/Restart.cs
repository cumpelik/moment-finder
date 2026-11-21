using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;

namespace MomentFinder.App;

/// <summary>MomentFinder starts again: the new one waits until this one has let go of the library.</summary>
public static class Restart
{
    public const string Argument = "--restarted";

    public static void Now()
    {
        if (Environment.ProcessPath is { } exe)
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, ArgumentList = { Argument } });
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
