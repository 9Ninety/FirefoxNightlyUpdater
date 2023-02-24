using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System.Runtime.InteropServices;

namespace FirefoxNightlyMSIXUpdater
{
    public partial class App : Application
    {
        // https://learn.microsoft.com/en-us/windows/console/allocconsole
        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole();
        public App()
        {
            AttachConsole();
        }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }
}
