using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace BedrockRender.Demo
{
    internal class MainWindow : Window
    {
        public MainWindow()
        {
            Title = "Bedrock Chunk Render";
            Width = 1100;
            Height = 800;

            var opts = RenderOptions.Parse(Environment.GetCommandLineArgs().Skip(1));

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            try
            {
                var bmp = WorldRenderer.Render(opts);
                scroll.Content = new Image { Source = bmp, Stretch = Stretch.None };
            }
            catch (Exception ex)
            {
                scroll.Content = new TextBlock { Text = "Error: " + ex.Message };
            }

            Content = scroll;
        }
    }
}
