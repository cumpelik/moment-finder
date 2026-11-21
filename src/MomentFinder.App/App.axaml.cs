using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MomentFinder.App.ViewModels;
using MomentFinder.App.Views;

namespace MomentFinder.App;

public sealed class App : Application
{
    private LibraryHost? _host;
    private LibraryLock? _lock;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // The video player loads its codecs in the background meanwhile; the first video does not wait for it.
        _ = Video.VideoPlayer.LoadAsync();
        // An error in one action must not take the whole browser down: log it and tell the user.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Console.Error.WriteLine(e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } w })
                _ = Views.Dialogs.InfoAsync(w, "Something went wrong", e.Exception.Message);
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // MomentFinder is open already with this library (another window, or the command line indexing it).
            var paths = new AppPaths();
            if (LibraryLock.TryAcquire(paths.DataDir) is not { } libraryLock)
            {
                var window = new Avalonia.Controls.Window { Title = "MomentFinder", Width = 1, Height = 1, ShowInTaskbar = false, Opacity = 0 };
                desktop.MainWindow = window;
                window.Opened += async (_, _) =>
                {
                    await Views.Dialogs.InfoAsync(window, "MomentFinder is open already",
                        "MomentFinder is running already with this library (look in the taskbar). It works on one library from one place at a time, " +
                        "so that the analysis is not done twice. Close the other one first to open it here.");
                    desktop.Shutdown();
                };
                base.OnFrameworkInitializationCompleted();
                return;
            }
            _lock = libraryLock;
            _host = new LibraryHost(paths);
            var vm = new MainViewModel(_host);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                _host.Dispose();
                _lock?.Dispose();
            };
            vm.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
