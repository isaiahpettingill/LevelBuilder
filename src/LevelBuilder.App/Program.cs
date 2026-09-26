using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

namespace LevelBuilder.App;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
}
public sealed class App : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new EditorWindow();
            desktop.MainWindow = window;
            if (desktop.Args?.Contains("--smoke-test") == true)
                window.Opened += (_, _) => Dispatcher.UIThread.Post(() => { Console.WriteLine("LevelBuilder startup OK"); desktop.Shutdown(); }, DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
