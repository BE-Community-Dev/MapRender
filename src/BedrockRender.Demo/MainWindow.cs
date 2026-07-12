using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BedrockRender.Controls;

namespace BedrockRender.Demo
{
    internal class MainWindow : Window
    {
        private readonly MapView map_;
        private readonly TextBlock status_;
        private readonly ProgressBar progress_;
        private readonly ComboBox dimBox_;
        private readonly ComboBox modeBox_;
        private readonly ComboBox detailBox_;
        private readonly CheckBox entityCheck_;
        private readonly Button pickBtn_;
        private global::BedrockLevel.Level.BedrockLevel level_;

        public MainWindow(RenderOptions opts)
        {
            Title = "Bedrock 存档地图预览";
            Width = 1200;
            Height = 820;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 34));

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8) };

            pickBtn_ = new Button { Content = "选择存档文件夹", Width = 130 };
            pickBtn_.Click += async (_, __) => await PickFolder();

            top.Children.Add(pickBtn_);
            top.Children.Add(new TextBlock { Text = "维度:", VerticalAlignment = VerticalAlignment.Center });
            dimBox_ = new ComboBox
            {
                Width = 90,
                ItemsSource = new[] { "主世界", "下界", "末地" },
                SelectedIndex = opts.Dimension
            };
            top.Children.Add(dimBox_);

            top.Children.Add(new TextBlock { Text = "模式:", VerticalAlignment = VerticalAlignment.Center });
            modeBox_ = new ComboBox
            {
                Width = 110,
                ItemsSource = new[] { "表面", "生物群系", "高度" },
                SelectedIndex = (int)opts.Mode
            };
            top.Children.Add(modeBox_);

            top.Children.Add(new TextBlock { Text = "精度:", VerticalAlignment = VerticalAlignment.Center });
            detailBox_ = new ComboBox
            {
                Width = 70,
                ItemsSource = new[] { "1", "2", "4", "8" },
                SelectedIndex = 0
            };
            top.Children.Add(detailBox_);

            var applyBtn = new Button { Content = "应用", Width = 60 };
            applyBtn.Click += (_, __) => ApplyView();
            top.Children.Add(applyBtn);

            entityCheck_ = new CheckBox { Content = "显示实体", IsChecked = true, Margin = new Thickness(8, 0, 0, 0) };
            entityCheck_.IsCheckedChanged += (_, _) => UpdateEntityFilter();
            top.Children.Add(entityCheck_);

            var fitBtn = new Button { Content = "适应窗口", Width = 80 };
            fitBtn.Click += (_, __) => map_.FitToView();
            top.Children.Add(fitBtn);

            map_ = new MapView();
            map_.StatusChanged += s => status_.Text = s;
            map_.ProgressChanged += UpdateProgress;

            progress_ = new ProgressBar
            {
                Width = 220,
                Height = 14,
                Minimum = 0,
                Maximum = 1,
                IsIndeterminate = false,
                Value = 0
            };

            status_ = new TextBlock
            {
                Text = "请选择存档文件夹",
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.LightGray),
                FontSize = 12
            };

            var bottom = new DockPanel { Margin = new Thickness(8, 4, 8, 6) };
            DockPanel.SetDock(progress_, Dock.Left);
            DockPanel.SetDock(status_, Dock.Right);
            bottom.Children.Add(progress_);
            bottom.Children.Add(status_);

            var dock = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(map_);

            Content = dock;

            if (!string.IsNullOrEmpty(opts.SaveDir))
                LoadLevelAsync(opts.SaveDir);
        }

        private void UpdateProgress(double? frac)
        {
            if (frac == null)
            {
                progress_.IsIndeterminate = true;
            }
            else
            {
                progress_.IsIndeterminate = false;
                progress_.Value = frac.Value;
            }
        }

        private void ApplyView()
        {
            if (level_ == null) return;
            int dim = dimBox_.SelectedIndex;
            var mode = (ViewMode)modeBox_.SelectedIndex;
            int detail = int.Parse((string)detailBox_.SelectedItem, CultureInfo.InvariantCulture);
            map_.UpdateView(dim, mode, detail);
        }

        private void UpdateEntityFilter()
        {
            if (entityCheck_.IsChecked == true)
                map_.EnabledEntityTypes = new HashSet<string>(); // show all
            else
                map_.EnabledEntityTypes = null; // show none
            map_.RefreshView();
        }

        private void LoadLevelAsync(string dir)
        {
            pickBtn_.IsEnabled = false;
            progress_.IsIndeterminate = true;
            status_.Text = "正在准备…";

            Task.Run(() =>
            {
                var result = MapView.LoadWorker(dir, ReportProgress);
                Dispatcher.UIThread.Post(() =>
                {
                    pickBtn_.IsEnabled = true;
                    if (result.level == null)
                    {
                        status_.Text = "无法打开存档: " + dir;
                        progress_.IsIndeterminate = false;
                        return;
                    }
                    level_ = result.level;
                    int dim = dimBox_.SelectedIndex;
                    var mode = (ViewMode)modeBox_.SelectedIndex;
                    int detail = int.Parse((string)detailBox_.SelectedItem, CultureInfo.InvariantCulture);
                    map_.SetLevel(result.level, result.positions, result.name, dim, mode, detail);
                });
            });
        }

        private void ReportProgress(double? frac, string text)
        {
            Dispatcher.UIThread.Post(() =>
            {
                status_.Text = text;
                UpdateProgress(frac);
            });
        }

        private async Task PickFolder()
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择 Minecraft 基岩版存档目录",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            LoadLevelAsync(folders[0].Path.LocalPath);
        }
    }
}
