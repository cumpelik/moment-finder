using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MediaLens.App.ViewModels;
using MediaLens.App.Views;

namespace MediaLens.App;

public sealed class App : Application
{
    private LibraryHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
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
            _host = new LibraryHost();
            var vm = new MainViewModel(_host);
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                _host.Dispose();
            };
            vm.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
