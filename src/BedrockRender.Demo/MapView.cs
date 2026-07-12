using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockRender;

namespace BedrockRender.Demo
{
    internal sealed class MapView : Avalonia.Controls.Control
    {
        private const int MaxTiles = 2000;

        private global::BedrockLevel.Level.BedrockLevel level_;
        private List<ChunkPos> positions_;
        private ColorPalette palette_;
        private ChunkRenderer renderer_;

        private const int BlockChunks = 8;

        private int dimension_ = 0;
        private ViewMode mode_ = ViewMode.Surface;
        private int tileScale_ = 4;

        private int worldBlocksX_, worldBlocksZ_;
        private int minCx_, maxCx_, minCz_, maxCz_;
        private int chunkCount_;

        private readonly ConcurrentDictionary<(int, int), WriteableBitmap> tiles_ = new();
        private readonly ConcurrentDictionary<(int, int), int> lastUsed_ = new();
        private int frame_;
        private bool backgroundFillRunning_;
        private volatile bool renderAllRunning_;
        private CancellationTokenSource cts_;
        private int totalBlocks_;
        private int doneBlocks_;

        private double viewScale_ = 4;
        private double centerX_ = 0;
        private double centerZ_ = 0;
        private double renderCenterX_;
        private double renderCenterZ_;

        private Point dragStart_;
        private double dragCenterX_;
        private double dragCenterZ_;
        private bool dragging_;
        private double panX_, panY_;

        private int hoverWorldX_;
        private int hoverWorldZ_;
        private int hoverChunkX_;
        private int hoverChunkZ_;
        private bool hoverValid_;

        private RenderTargetBitmap view_;
        private bool viewDirty_;
        private bool renderPending_;
        private int margin_;

        public event Action<string> StatusChanged;
        public event Action<double?> ProgressChanged;

        public string LevelName { get; private set; } = "";

        public MapView()
        {
            ClipToBounds = true;
            AttachedToVisualTree += (_, _) => FitToView();
            SizeChanged += (_, _) => InvalidateView();
        }

        public void SetLevel(global::BedrockLevel.Level.BedrockLevel level, List<ChunkPos> positions,
            string levelName, int dimension, ViewMode mode, int tileScale)
        {
            level_ = level;
            positions_ = positions;
            LevelName = levelName;
            dimension_ = dimension;
            mode_ = mode;
            tileScale_ = tileScale;
            palette_ = ColorPalette.LoadEmbedded();
            renderer_ = new ChunkRenderer(palette_);
            ComputeBounds();
            FitToView();
            RenderView();
            RenderAll();
        }

        public void UpdateView(int dimension, ViewMode mode, int tileScale)
        {
            dimension_ = dimension;
            mode_ = mode;
            tileScale_ = tileScale;
            if (level_ != null)
            {
                ComputeBounds();
                RenderAll();
            }
        }

        public void FitToView()
        {
            if (chunkCount_ == 0) return;
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            margin_ = (int)Math.Max(64, Math.Min(300, w / 2 - 1));
            double s = Math.Min(w / worldBlocksX_, h / worldBlocksZ_) * 0.95;
            viewScale_ = ClampScale(s);
            centerX_ = (minCx_ * 16.0 + (maxCx_ + 1) * 16.0) / 2.0;
            centerZ_ = (minCz_ * 16.0 + (maxCz_ + 1) * 16.0) / 2.0;
            panX_ = panY_ = 0;
            renderCenterX_ = centerX_;
            renderCenterZ_ = centerZ_;
            InvalidateView();
            RaiseStatus();
        }

        public static (global::BedrockLevel.Level.BedrockLevel level, List<ChunkPos> positions, string name)
            LoadWorker(string dir, Action<double?, string> report)
        {
            report(null, "正在打开存档（读取 db/）…");
            var level = new global::BedrockLevel.Level.BedrockLevel();
            if (!level.Open(dir))
                return (null, null, null);

            report(null, "正在枚举区块…");
            var positions = new List<ChunkPos>(level.ChunkPositions());
            report(1, $"已就绪：{positions.Count} 个区块");
            return (level, positions, level.Dat.LevelName);
        }

        private void ComputeBounds()
        {
            minCx_ = int.MaxValue;
            maxCx_ = int.MinValue;
            minCz_ = int.MaxValue;
            maxCz_ = int.MinValue;
            chunkCount_ = 0;
            if (positions_ == null) return;
            foreach (var cp in positions_)
            {
                if (cp.Dim != dimension_) continue;
                chunkCount_++;
                if (cp.X < minCx_) minCx_ = cp.X;
                if (cp.X > maxCx_) maxCx_ = cp.X;
                if (cp.Z < minCz_) minCz_ = cp.Z;
                if (cp.Z > maxCz_) maxCz_ = cp.Z;
            }

            if (chunkCount_ == 0)
            {
                minCx_ = maxCx_ = minCz_ = maxCz_ = 0;
                worldBlocksX_ = worldBlocksZ_ = 1;
            }
            else
            {
                worldBlocksX_ = (maxCx_ - minCx_ + 1) * 16;
                worldBlocksZ_ = (maxCz_ - minCz_ + 1) * 16;
            }
        }

        // ----- Render pipeline -----

        private void RenderAll()
        {
            renderCenterX_ = centerX_;
            renderCenterZ_ = centerZ_;
            cts_?.Cancel();

            // Dispose old tiles immediately (no compress on UI thread — that would freeze).
            // They were at a potentially different LOD and wouldn't match new cache keys anyway.
            // The cache is populated by EvictTiles during normal rendering.
            foreach (var bmp in tiles_.Values)
                bmp.Dispose();
            tiles_.Clear();
            lastUsed_.Clear();
            doneBlocks_ = 0;

            if (level_ == null || chunkCount_ == 0)
            {
                InvalidateView();
                RaiseStatus();
                return;
            }

            int bc = BlockChunks;
            int bxMin = (int)Math.Floor((double)minCx_ / bc);
            int bxMax = (int)Math.Floor((double)maxCx_ / bc);
            int bzMin = (int)Math.Floor((double)minCz_ / bc);
            int bzMax = (int)Math.Floor((double)maxCz_ / bc);

            var blocks = new List<(int, int)>();
            for (int bx = bxMin; bx <= bxMax; bx++)
            for (int bz = bzMin; bz <= bzMax; bz++)
                blocks.Add((bx, bz));

            totalBlocks_ = blocks.Count;
            var cts = new CancellationTokenSource();
            cts_ = cts;
            var token = cts.Token;

            InvalidateView();
            RaiseStatus();

            int maxPar = Math.Max(1, Environment.ProcessorCount);
            renderAllRunning_ = true;
            level_?.BeginSharedLoad();
            Task.Factory.StartNew(() =>
            {
                try
                {
                    Parallel.ForEach(blocks,
                        new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = token },
                        b => RenderBlock(b, token));
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    renderAllRunning_ = false;
                    if (!token.IsCancellationRequested)
                        QueueInvalidate();
                    level_?.EndSharedLoad();
                }
            }, TaskCreationOptions.LongRunning);
        }

        private void RenderBlock((int bx, int bz) b, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            int bx = b.bx, bz = b.bz;
            int ts = tileScale_;
            int bc = BlockChunks;
            int baseCx = bx * bc;
            int baseCz = bz * bc;
            int blockPx = bc * 16 * ts;

            var bmp = new WriteableBitmap(new PixelSize(blockPx, blockPx), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            var fileGroups = new Dictionary<string, List<(int lcx, int lcz, ChunkPos cp)>>();
            for (int lcx = 0; lcx < bc; lcx++)
            {
                int cx = baseCx + lcx;
                if (cx < minCx_ || cx > maxCx_) continue;
                for (int lcz = 0; lcz < bc; lcz++)
                {
                    int cz = baseCz + lcz;
                    if (cz < minCz_ || cz > maxCz_) continue;
                    var cp = new ChunkPos(cx, cz, dimension_);
                    var fp = level_?.GetChunkFilePath(cp);
                    if (fp == null) continue;
                    if (!fileGroups.TryGetValue(fp, out var list))
                        fileGroups[fp] = list = new List<(int, int, ChunkPos)>();
                    list.Add((lcx, lcz, cp));
                }
            }

            using (var fb = bmp.Lock())
            {
                int len = fb.RowBytes * fb.Size.Height;
                unsafe
                {
                    var span = new Span<byte>((void*)fb.Address, len);
                    for (int i = 0; i < len; i += 4)
                    {
                        span[i + 0] = 28;
                        span[i + 1] = 28;
                        span[i + 2] = 32;
                        span[i + 3] = 255;
                    }
                }

                foreach (var group in fileGroups)
                {
                    var cps = new List<ChunkPos>(group.Value.Count);
                    var posMap = new List<(int lcx, int lcz, ChunkPos)>();
                    foreach (var (lcx, lcz, cp) in group.Value)
                    {
                        cps.Add(cp);
                        posMap.Add((lcx, lcz, cp));
                    }

                    var raws = level_.GetRawChunksFromFile(group.Key, cps);

                    foreach (var (lcx, lcz, cp) in posMap)
                    {
                        var raw = raws[cp];
                        if (!raw.Loaded())
                            raw = level_.GetRawChunk(cp);
                        if (!raw.Loaded()) continue;
                        var chunk = new Chunk(cp);
                        if (!chunk.LoadFromRawChunk(raw)) continue;
                        var rc = renderer_.Render(chunk, mode_, ts);
                        int offX = lcx * 16 * ts;
                        int offY = lcz * 16 * ts;
                        unsafe
                        {
                            var span = new Span<byte>((void*)fb.Address, len);
                            rc.BlitTo(span, blockPx, blockPx, offX, offY);
                        }
                    }
                }
            }

            tiles_[(bx, bz)] = bmp;
            lastUsed_[(bx, bz)] = int.MaxValue;
            int done2 = Interlocked.Increment(ref doneBlocks_);
            QueueInvalidate();
            if (done2 % 8 == 0 || done2 == totalBlocks_)
                RaiseStatus();
        }

        // ----- Offscreen compositing -----

        private void InvalidateView()
        {
            if (viewDirty_) return;
            viewDirty_ = true;
            if (renderPending_) return;
            renderPending_ = true;
            Dispatcher.UIThread.Post(RenderView, DispatcherPriority.Background);
        }

        private void QueueInvalidate() => InvalidateView();

        private void RenderView()
{
    renderPending_ = false;
    if (!viewDirty_) return;
    viewDirty_ = false;

    double w = Bounds.Width, h = Bounds.Height;
    if (w <= 0 || h <= 0)
    {
        viewDirty_ = true;
        return;
    }

    int margin = (int)Math.Max(64, Math.Min(300, w / 2 - 1));
    margin_ = margin;
    
    int ow = (int)w + 2 * margin;
    int oh = (int)h + 2 * margin;

    if (view_ == null ||
        view_.PixelSize.Width != ow ||
        view_.PixelSize.Height != oh)
    {
        view_?.Dispose();
        view_ = new RenderTargetBitmap(new PixelSize(ow, oh), new Vector(96, 96));
    }

    using (var dc = view_.CreateDrawingContext())
    {
        // ============ 关键修改 2：使用 centerX_/centerZ_ 而不是 renderCenterX_/renderCenterZ_ ============
        RenderScene(dc, ow, oh, w, h);
    }
    EvictTiles();
    InvalidateVisual();
}

private void RenderScene(DrawingContext context, double viewW, double viewH, double displayW, double displayH)
{
    context.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), new Rect(0, 0, viewW, viewH));

    if (level_ == null)
    {
        DrawCenteredText(context, viewW, viewH, "请选择存档文件夹", 18);
        return;
    }
    if (chunkCount_ == 0)
    {
        DrawCenteredText(context, viewW, viewH, "该维度没有可用区块", 18);
        return;
    }

    int bc = BlockChunks;
    int ts = tileScale_;
    double scale = viewScale_;
    
    // ============ 关键修改：使用 centerX_/centerZ_ 而不是 renderCenterX_/renderCenterY_ ============
    // 计算偏移：view_ 的分辨率是 viewW × viewH，显示区域在 view_ 中的位置是 (viewW/2 - displayW/2) 到 (viewW/2 + displayW/2)
    // 所以渲染时，世界坐标 (0,0) 应该对应 view_ 中 (viewW/2, viewH/2) 的位置
    double centerOffsetX = (viewW - displayW) / 2.0;
    double centerOffsetY = (viewH - displayH) / 2.0;
    
    // 注意：这里使用 centerX_/centerZ_（当前视图中心），并且用 scale 计算偏移
    // 实际上 RenderScene 应该以 display 区域为中心渲染，但 view_ 分辨率更大
    // 所以我们把世界坐标映射到 view_ 的像素坐标
    double ox = viewW / 2.0 - centerX_ * scale;
    double oy = viewH / 2.0 - centerZ_ * scale;

    // 计算可见范围（在世界坐标中）
    double blkLeft = -ox / scale;
    double blkRight = (viewW - ox) / scale;
    double blkTop = -oy / scale;
    double blkBottom = (viewH - oy) / scale;

    int bxMin = (int)Math.Floor(blkLeft / (bc * 16));
    int bxMax = (int)Math.Floor(blkRight / (bc * 16));
    int bzMin = (int)Math.Floor(blkTop / (bc * 16));
    int bzMax = (int)Math.Floor(blkBottom / (bc * 16));

    // Clamp to world bounds
    int bcMin = (int)Math.Floor((double)minCx_ / bc);
    int bcMaxW = (int)Math.Floor((double)maxCx_ / bc);
    int bzMinW = (int)Math.Floor((double)minCz_ / bc);
    int bzMaxW = (int)Math.Floor((double)maxCz_ / bc);
    bxMin = Math.Max(bxMin, bcMin);
    bxMax = Math.Min(bxMax, bcMaxW);
    bzMin = Math.Max(bzMin, bzMinW);
    bzMax = Math.Min(bzMax, bzMaxW);

    int f = Interlocked.Increment(ref frame_);
    bool needsFill = false;

    for (int bx = bxMin; bx <= bxMax; bx++)
    {
        for (int bz = bzMin; bz <= bzMax; bz++)
        {
            if (tiles_.TryGetValue((bx, bz), out var tile))
            {
                // 计算 tile 在 view_ 中的位置（像素坐标）
                double dx = (bx * bc * 16.0) * scale + ox;
                double dy = (bz * bc * 16.0) * scale + oy;
                double dw = bc * 16.0 * scale;
                
                // 使用最近邻插值绘制 tile
                using (context.PushRenderOptions(new Avalonia.Media.RenderOptions
                {
                    BitmapInterpolationMode = BitmapInterpolationMode.None
                }))
                {
                    context.DrawImage(tile, new Rect(dx, dy, dw, dw));
                }
                
                if (lastUsed_.TryGetValue((bx, bz), out var lu) && lu != int.MaxValue)
                    lastUsed_[(bx, bz)] = f;
            }
            else
            {
                needsFill = true;
            }
        }
    }

    // Background fill for missing tiles...
    if (needsFill && level_ != null && !backgroundFillRunning_ && !renderAllRunning_)
    {
        backgroundFillRunning_ = true;
        var token = cts_?.Token ?? CancellationToken.None;
        int maxPar = Math.Max(1, Environment.ProcessorCount);
        Task.Run(() =>
        {
            try
            {
                var fillBlocks = new List<(int, int)>();
                for (int bx = bxMin; bx <= bxMax; bx++)
                    for (int bz = bzMin; bz <= bzMax; bz++)
                        if (!tiles_.ContainsKey((bx, bz)))
                            fillBlocks.Add((bx, bz));

                Parallel.ForEach(fillBlocks,
                    new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = token },
                    b => RenderBlock(b, token));
            }
            catch (OperationCanceledException) { }
            finally
            {
                backgroundFillRunning_ = false;
                if (!token.IsCancellationRequested)
                    QueueInvalidate();
            }
        });
    }
}

        private void EvictTiles()
        {
            if (tiles_.Count <= MaxTiles) return;
            int threshold = frame_ - 2;
            var candidates = new List<(int, int, int)>();
            foreach (var kv in lastUsed_)
            {
                int lu = kv.Value;
                if (lu != int.MaxValue && lu < threshold)
                    candidates.Add((kv.Key.Item1, kv.Key.Item2, lu));
            }

            if (candidates.Count == 0) return;
            candidates.Sort((a, b) => a.Item3.CompareTo(b.Item3));
            int need = tiles_.Count - MaxTiles;
            for (int i = 0; i < candidates.Count && i < need; i++)
            {
                var key = (candidates[i].Item1, candidates[i].Item2);
                if (tiles_.TryRemove(key, out var bmp))
                {
                    bmp.Dispose();
                }
                lastUsed_.TryRemove(key, out _);
            }
        }

        // ----- Frame -----

        public override void Render(DrawingContext context)
        {
            double w = Bounds.Width, h = Bounds.Height;
    
            if (view_ == null)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), new Rect(0, 0, w, h));
                if (level_ == null)
                    DrawCenteredText(context, w, h, "请选择存档文件夹", 18);
                return;
            }

            context.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), new Rect(0, 0, w, h));

            // ============ 关键修改 3：从 view_ 中裁剪显示区域 ============
            // view_ 的分辨率是 (w * viewScale_ + 2*margin) × (h * viewScale_ + 2*margin)
            // 显示区域是 w × h，从 view_ 的中心裁剪
            double viewW = view_.PixelSize.Width;
            double viewH = view_.PixelSize.Height;
    
            // 计算裁剪区域（从 view_ 的中心裁剪出 w×h 的区域）
            double srcX = (viewW - w) / 2.0;
            double srcY = (viewH - h) / 2.0;
            double srcW = w;
            double srcH = h;
    
            // 使用最近邻插值，不做任何平滑处理
            using (context.PushRenderOptions(new Avalonia.Media.RenderOptions
                   {
                       BitmapInterpolationMode = BitmapInterpolationMode.None
                   }))
            {
                // 如果 view_ 分辨率大于显示区域，裁剪显示（1:1 像素映射）
                // 如果 view_ 分辨率小于显示区域，放大显示（但用最近邻，保持像素块）
                context.DrawImage(view_, 
                    new Rect(Math.Max(0, srcX), Math.Max(0, srcY), srcW, srcH),
                    new Rect(0, 0, w, h));
            }

            // 绘制网格（使用正常插值保持文字清晰）
            if (level_ != null && chunkCount_ > 0)
            {
                double ox = w / 2.0 - centerX_ * viewScale_;
                double oy = h / 2.0 - centerZ_ * viewScale_;
                DrawGrid(context, w, h, viewScale_, ox, oy);
            }
        }

        // ----- Interaction -----

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                dragging_ = true;
                dragStart_ = e.GetPosition(this);
                dragCenterX_ = centerX_;
                dragCenterZ_ = centerZ_;
                renderCenterX_ = centerX_;
                renderCenterZ_ = centerZ_;
                panX_ = panY_ = 0;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(this);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            UpdateHover(p);
            if (dragging_)
            {
                double dx = p.X - dragStart_.X;
                double dy = p.Y - dragStart_.Y;
                centerX_ = dragCenterX_ - dx / viewScale_;
                centerZ_ = dragCenterZ_ - dy / viewScale_;

                int m = margin_ > 0 ? margin_ : 100;
                if (Math.Abs(dx) >= m || Math.Abs(dy) >= m)
                {
                    renderCenterX_ = centerX_;
                    renderCenterZ_ = centerZ_;
                    dragStart_ = p;
                    dragCenterX_ = centerX_;
                    dragCenterZ_ = centerZ_;
                    RenderView();
                }

                panX_ = p.X - dragStart_.X;
                panY_ = p.Y - dragStart_.Y;
                panX_ = Math.Max(-m, Math.Min(m, panX_));
                panY_ = Math.Max(-m, Math.Min(m, panY_));

                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragging_)
            {
                dragging_ = false;
                renderCenterX_ = centerX_;
                renderCenterZ_ = centerZ_;
                panX_ = panY_ = 0;
                Cursor = new Cursor(StandardCursorType.Arrow);
                e.Pointer.Capture(null);
                InvalidateView();
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var p = e.GetPosition(this);
            double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;
            double newScale = ClampScale(viewScale_ * factor);

            double bx = (p.X - Bounds.Width / 2.0) / viewScale_ + centerX_;
            double bz = (p.Y - Bounds.Height / 2.0) / viewScale_ + centerZ_;
            centerX_ = bx - (p.X - Bounds.Width / 2.0) / newScale;
            centerZ_ = bz - (p.Y - Bounds.Height / 2.0) / newScale;
            viewScale_ = newScale;
            renderCenterX_ = centerX_;
            renderCenterZ_ = centerZ_;

            UpdateHover(p);
            InvalidateView();
            e.Handled = true;
        }

        private void UpdateHover(Point p)
        {
            double w = Bounds.Width, h = Bounds.Height;
            double bx = (p.X - w / 2.0) / viewScale_ + centerX_;
            double bz = (p.Y - h / 2.0) / viewScale_ + centerZ_;
            hoverValid_ = bx >= minCx_ * 16 && bx <= (maxCx_ + 1) * 16 && bz >= minCz_ * 16 && bz <= (maxCz_ + 1) * 16;
            hoverWorldX_ = (int)Math.Floor(bx);
            hoverWorldZ_ = (int)Math.Floor(bz);
            hoverChunkX_ = (int)Math.Floor(bx / 16.0);
            hoverChunkZ_ = (int)Math.Floor(bz / 16.0);
            RaiseStatus();
        }

        // ----- Helpers -----

        private static double ClampScale(double s) => Math.Max(0.02, Math.Min(64, s));

        private void DrawGrid(DrawingContext context, double w, double h, double scale, double ox, double oy)
        {
            double chunkPx = 16.0 * scale;
            int cxMin = (int)Math.Floor(-ox / scale / 16.0);
            int cxMax = (int)Math.Ceiling((w - ox) / scale / 16.0);
            int czMin = (int)Math.Floor(-oy / scale / 16.0);
            int czMax = (int)Math.Ceiling((h - oy) / scale / 16.0);

            bool drawMinor = chunkPx >= 6;
            bool drawMajor = (8.0 * chunkPx) >= 6;

            var minorPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
            var majorPen = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1);

            for (int cx = cxMin; cx <= cxMax; cx++)
            {
                double x = cx * 16.0 * scale + ox;
                double xp = x + 0.5;
                bool major = cx % 8 == 0;
                if (major && drawMajor)
                {
                    context.DrawLine(majorPen, new Point(xp, 0), new Point(xp, h));
                    DrawAxisLabel(context, $"x={cx * 16}", xp + 2, 2, Colors.White);
                }
                else if (!major && drawMinor)
                {
                    context.DrawLine(minorPen, new Point(xp, 0), new Point(xp, h));
                }
            }

            for (int cz = czMin; cz <= czMax; cz++)
            {
                double y = cz * 16.0 * scale + oy;
                double yp = y + 0.5;
                bool major = cz % 8 == 0;
                if (major && drawMajor)
                {
                    context.DrawLine(majorPen, new Point(0, yp), new Point(w, yp));
                    DrawAxisLabel(context, $"z={cz * 16}", 2, yp + 2, Colors.White);
                }
                else if (!major && drawMinor)
                {
                    context.DrawLine(minorPen, new Point(0, yp), new Point(w, yp));
                }
            }
        }

        private void DrawAxisLabel(DrawingContext context, string text, double x, double y, Color color)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default), 11, new SolidColorBrush(color));
            context.DrawText(ft, new Point(x, y));
        }

        private void DrawCenteredText(DrawingContext context, double w, double h, string text, double size)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default), size, new SolidColorBrush(Colors.LightGray));
            context.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
        }

        private void RaiseStatus()
        {
            Dispatcher.UIThread.Post(() =>
            {
                string coord = hoverValid_
                    ? $"世界坐标: ({hoverWorldX_}, {hoverWorldZ_})   区块坐标: ({hoverChunkX_}, {hoverChunkZ_})"
                    : "世界坐标: -   区块坐标: -";
                int done = Math.Min(doneBlocks_, totalBlocks_);
                string progress = totalBlocks_ > 0 ? $"已渲染区块块: {done}/{totalBlocks_}" : "无区块";
                string lodInfo = $" | LOD: {tileScale_}px/格 x{BlockChunks}区块";
                string status = $"存档: {LevelName} | 维度: {DimName(dimension_)} | 模式: {mode_} | " +
                                $"区块数: {chunkCount_} | {progress}{lodInfo} | 缩放: {viewScale_:F2} px/格 | {coord}";
                StatusChanged?.Invoke(status);

                double? frac = totalBlocks_ > 0 ? (double)done / totalBlocks_ : (double?)null;
                ProgressChanged?.Invoke(frac);
            }, DispatcherPriority.Background);
        }

        private static string DimName(int d) => d switch { 1 => "下界", 2 => "末地", _ => "主世界" };

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            cts_?.Cancel();
            view_?.Dispose();
            foreach (var bmp in tiles_.Values)
                bmp.Dispose();
            tiles_.Clear();
        }
    }
}