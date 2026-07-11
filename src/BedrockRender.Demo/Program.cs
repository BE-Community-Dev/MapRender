using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace BedrockRender.Demo
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var opts = RenderOptions.Parse(args);

            if (string.IsNullOrEmpty(opts.SaveDir))
            {
                Console.Error.WriteLine("usage: BedrockRender.Demo <saveDir> [--dim 0|1|2] [--mode surface|biome|height] [--scale N] [--save out.png]");
                return 1;
            }

            bool saveMode = Array.Exists(args, a => a == "--save");

            var app = BuildAvaloniaApp();
            if (saveMode)
            {
                app.SetupWithoutStarting();
                WorldRenderer.Save(opts);
                return 0;
            }

            app.StartWithClassicDesktopLifetime(args);
            return 0;
        }

        private static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace();
    }
}
