using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace BedrockRender.Demo
{
    internal class App : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var args = desktop.Args ?? Array.Empty<string>();
                desktop.MainWindow = new MainWindow(RenderOptions.Parse(args));
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
