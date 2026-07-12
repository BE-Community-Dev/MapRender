using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BedrockLevel.Cache;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockRender;

namespace BedrockRender.Demo
{
    /// <summary>
    /// Interactive map view: pans with left-drag, zooms with the wheel, draws chunk / 8-chunk
    /// grid lines that adapt to the zoom level, and shows world + chunk coordinates. Chunks are
    /// rendered progressively in the background (multi-threaded, one chunk-block at a time) and
    /// shown as soon as each block finishes.
    ///
    /// Performance / memory:
    /// - The visible scene is composited once into an offscreen RenderTargetBitmap and blitted in
    ///   a single draw call per frame. Dragging translates that cached bitmap (no re-render).
    /// - Caching happens *while rendering*: each chunk block is compressed (Brotli) straight to
    ///   &lt;save&gt;/.cache and the byte buffer is released immediately (no in-memory retention).
    /// - Tile bitmaps are kept in a bounded LRU cache and evicted (Disposed) when off-screen or
    ///   over the cap, so memory stays low even for huge worlds.
    /// </summary>
    internal sealed class MapView : Avalonia.Controls.Control
    {
        // Number of chunks per side of a cached tile block.
        private const int BlockChunks = 8;
        // Hard cap on retained tile bitmaps (each is BlockChunks*16*tileScale pixels).
        private const int MaxTiles = 2000;

        private global::BedrockLevel.Level.BedrockLevel level_;
        private List<ChunkPos> positions_;
        private string cacheDir_;
        private ColorPalette palette_;
        private ChunkRenderer renderer_;

        private int dimension_ = 0;
        private ViewMode mode_ = ViewMode.Surface;
        private int tileScale_ = 4;

        // World chunk bounds (current dimension).
        private int minCx_, maxCx_, minCz_, maxCz_;
        private int chunkCount_;

        // Cached tile blocks keyed by block coordinate (bx, bz).
        private readonly ConcurrentDictionary<(int, int), WriteableBitmap> tiles_ = new();
        private readonly ConcurrentDictionary<(int, int), int> lastUsed_ = new();
        private int frame_;
        private CancellationTokenSource cts_;
        private int totalBlocks_;
        private int doneBlocks_;
        private int cachedTotal_;
        private int cachedDone_;

        // Camera: pixel-per-block zoom and the world block coordinate shown at the viewport center.
        private double viewScale_ = 4;
        private double centerX_ = 0;
        private double centerZ_ = 0;
        // Center the offscreen was last composited at. Frozen during a drag (and only advanced on
        // commit/release) so that async tile renders finishing mid-drag don't re-composite at the
        // live (dragged) center and cancel out the pan translate.
        private double renderCenterX_;
        private double renderCenterZ_;

        private Point dragStart_;
        private double dragCenterX_;
        private double dragCenterZ_;
        private bool dragging_;
        private double panX_, panY_;   // screen-space translate used while dragging

        private int hoverWorldX_;
        private int hoverWorldZ_;
        private int hoverChunkX_;
        private int hoverChunkZ_;
        private bool hoverValid_;

        private RenderTargetBitmap view_;
        private bool viewDirty_;
        private bool renderPending_;
        private int margin_;   // offscreen padding on each side (px); lets dragging translate w/o blanks

        public event Action<string> StatusChanged;
        public event Action<double?> ProgressChanged;   // null = indeterminate (marquee)

        public string LevelName { get; private set; } = "";

        public MapView()
        {
            ClipToBounds = true;
            AttachedToVisualTree += (_, _) => FitToView();
            SizeChanged += (_, _) => InvalidateView();
        }

        // ----- Public API -----

        public void SetLevel(global::BedrockLevel.Level.BedrockLevel level, List<ChunkPos> positions,
            string levelName, int dimension, ViewMode mode, int tileScale)
        {
            level_ = level;
            positions_ = positions;
            LevelName = levelName;
            cacheDir_ = Path.Combine(level.RootPath, ".cache");
            Directory.CreateDirectory(cacheDir_);
            dimension_ = dimension;
            mode_ = mode;
            tileScale_ = tileScale;
            palette_ = ColorPalette.LoadEmbedded();
            renderer_ = new ChunkRenderer(palette_);
            ComputeBounds();
            FitToView();
            // Composite the offscreen synchronously right now so view_ and margin_ are ready
            // before the user can interact (avoids the frozen-drag / blank-top-left problems).
            RenderView();
            RenderAll();
            StartCaching();
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
            double worldBlocksX = (maxCx_ - minCx_ + 1) * 16.0;
            double worldBlocksZ = (maxCz_ - minCz_ + 1) * 16.0;
            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            // Compute margin eagerly so that drag-within-margin works even if RenderView
            // (dispatched at Background priority) hasn't run yet.
            margin_ = (int)Math.Max(64, Math.Min(300, w / 2 - 1));
            double s = Math.Min(w / worldBlocksX, h / worldBlocksZ) * 0.95;
            viewScale_ = ClampScale(s);
            centerX_ = (minCx_ * 16.0 + (maxCx_ + 1) * 16.0) / 2.0;
            centerZ_ = (minCz_ * 16.0 + (maxCz_ + 1) * 16.0) / 2.0;
            panX_ = panY_ = 0;
            renderCenterX_ = centerX_;
            renderCenterZ_ = centerZ_;
            InvalidateView();
            RaiseStatus();
        }

        // ----- Loading pipeline -----

        /// <summary>Runs on a background thread; reports progress via the UI thread.</summary>
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

        // ----- Rendering pipeline -----

        private void ComputeBounds()
        {
            minCx_ = int.MaxValue; maxCx_ = int.MinValue;
            minCz_ = int.MaxValue; maxCz_ = int.MinValue;
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
                minCx_ = maxCx_ = minCz_ = maxCz_ = 0;
        }

        private void RenderAll()
        {
            renderCenterX_ = centerX_;
            renderCenterZ_ = centerZ_;
            cts_?.Cancel();
            tiles_.Clear();
            lastUsed_.Clear();
            doneBlocks_ = 0;

            if (level_ == null || chunkCount_ == 0)
            {
                InvalidateView();
                RaiseStatus();
                return;
            }

            int bxMin = (int)Math.Floor((double)minCx_ / BlockChunks);
            int bxMax = (int)Math.Floor((double)maxCx_ / BlockChunks);
            int bzMin = (int)Math.Floor((double)minCz_ / BlockChunks);
            int bzMax = (int)Math.Floor((double)maxCz_ / BlockChunks);

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
            level_?.BeginSharedLoad();
            Task.Factory.StartNew(() =>
            {
                try
                {
                    Parallel.ForEach(blocks,
                        new ParallelOptions { MaxDegreeOfParallelism = maxPar, CancellationToken = token },
                        b => RenderBlock(b, token));
                }
                catch (OperationCanceledException) { }
                finally
                {
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
            int baseCx = bx * BlockChunks;
            int baseCz = bz * BlockChunks;
            int blockPx = BlockChunks * 16 * tileScale_;

            var bmp = new WriteableBitmap(new PixelSize(blockPx, blockPx), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            // Group valid chunk positions by source file so the TLS cache stays hot.
            var fileGroups = new Dictionary<string, List<(int lcx, int lcz, ChunkPos cp)>>();
            for (int lcx = 0; lcx < BlockChunks; lcx++)
            {
                int cx = baseCx + lcx;
                if (cx < minCx_ || cx > maxCx_) continue;
                for (int lcz = 0; lcz < BlockChunks; lcz++)
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
                            raw = level_.GetRawChunk(cp); // fallback: try all files
                        if (!raw.Loaded()) continue;
                        var chunk = new Chunk(cp);
                        if (!chunk.LoadFromRawChunk(raw)) continue;
                        var rc = renderer_.Render(chunk, mode_, tileScale_);
                        int offX = lcx * 16 * tileScale_;
                        int offY = lcz * 16 * tileScale_;
                        unsafe
                        {
                            var span = new Span<byte>((void*)fb.Address, len);
                            rc.BlitTo(span, blockPx, blockPx, offX, offY);
                        }
                    }
                }
            }

            tiles_[(bx, bz)] = bmp;
            lastUsed_[(bx, bz)] = int.MaxValue;   // protect the freshly added tile from eviction
            int done = Interlocked.Increment(ref doneBlocks_);
            QueueInvalidate();
            if (done % 8 == 0 || done == totalBlocks_)
                RaiseStatus();
        }

        // ----- Disk cache (write-through, no in-memory retention) -----

        /// <summary>Runs concurrently with rendering: compresses each raw chunk to &lt;save&gt;/.cache
        /// in the background so it never blocks the multi-threaded render path.</summary>
        private void StartCaching()
        {
            if (level_ == null || positions_ == null) return;
            cachedTotal_ = positions_.Count;
            cachedDone_ = 0;
            var snapshot = positions_;
            Task.Factory.StartNew(() => CacheAll(snapshot), TaskCreationOptions.LongRunning);
        }

        private void CacheAll(List<ChunkPos> positions)
        {
            int maxPar = Math.Max(1, Environment.ProcessorCount);
            Parallel.ForEach(positions,
                new ParallelOptions { MaxDegreeOfParallelism = maxPar },
                cp =>
                {
                    try
                    {
                        var raw = level_.GetRawChunk(cp);
                        if (raw.Loaded())
                            StoreRaw(cp, raw);
                        int c = Interlocked.Increment(ref cachedDone_);
                        if (c % 1024 == 0) RaiseStatus();
                    }
                    catch (Exception) { /* best-effort cache; ignore errors */ }
                });
        }

        private void StoreRaw(ChunkPos cp, RawChunk rc)
        {
            try
            {
                var compressed = ChunkCache.BrotliCompress(rc.ToRaw());
                File.WriteAllBytes(CachePath(cp), compressed);
                // `compressed` becomes eligible for GC immediately; nothing is retained.
            }
            catch (Exception) { /* best-effort cache; ignore disk errors */ }
        }

        private string CachePath(ChunkPos cp) =>
            Path.Combine(cacheDir_, $"{cp.X}.{cp.Z}.{cp.Dim}.blkcache");

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
                viewDirty_ = true;   // retry once layout provides a size
                return;
            }

            // Offscreen is larger than the viewport by a margin on every side, so panning can
            // translate the cached bitmap (smooth, no re-render) without revealing blank edges.
            int margin = (int)Math.Max(64, Math.Min(300, w / 2 - 1));
            margin_ = margin;
            int ow = (int)w + 2 * margin;
            int oh = (int)h + 2 * margin;

            if (view_ == null ||
                view_.PixelSize.Width != ow ||
                view_.PixelSize.Height != oh)
            {
                view_ = new RenderTargetBitmap(new PixelSize(ow, oh), new Vector(96, 96));
            }

            using (var dc = view_.CreateDrawingContext())
            {
                RenderScene(dc, ow, oh);
            }
            EvictTiles();
            InvalidateVisual();
        }

        private void RenderScene(DrawingContext context, double w, double h)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 22)), new Rect(0, 0, w, h));

            if (level_ == null)
            {
                DrawCenteredText(context, w, h, "请选择存档文件夹", 18);
                return;
            }
            if (chunkCount_ == 0)
            {
                DrawCenteredText(context, w, h, "该维度没有可用区块", 18);
                return;
            }

            double scale = viewScale_;
            double ox = w / 2.0 - renderCenterX_ * scale;
            double oy = h / 2.0 - renderCenterZ_ * scale;

            double blkLeft = -ox / scale;
            double blkRight = (w - ox) / scale;
            double blkTop = -oy / scale;
            double blkBottom = (h - oy) / scale;

            int bxMin = (int)Math.Floor(blkLeft / (BlockChunks * 16));
            int bxMax = (int)Math.Floor(blkRight / (BlockChunks * 16));
            int bzMin = (int)Math.Floor(blkTop / (BlockChunks * 16));
            int bzMax = (int)Math.Floor(blkBottom / (BlockChunks * 16));

            int f = Interlocked.Increment(ref frame_);
            for (int bx = bxMin; bx <= bxMax; bx++)
            {
                for (int bz = bzMin; bz <= bzMax; bz++)
                {
                    if (tiles_.TryGetValue((bx, bz), out var tile))
                    {
                        double dx = (bx * BlockChunks * 16.0) * scale + ox;
                        double dy = (bz * BlockChunks * 16.0) * scale + oy;
                        double dw = BlockChunks * 16.0 * scale;
                        context.DrawImage(tile, new Rect(dx, dy, dw, dw));
                        if (lastUsed_.TryGetValue((bx, bz), out var lu) && lu != int.MaxValue)
                            lastUsed_[(bx, bz)] = f;
                    }
                }
            }

            // NOTE: the grid is intentionally NOT drawn here. It is composited as a fresh
            // overlay in Render() every frame so it always spans the full viewport (no blank
            // edges) and stays aligned with the (translated) map underneath during a drag.
        }

        /// <summary>Evicts tile bitmaps that are over the cap or off-screen, disposing them to free memory.</summary>
        private void EvictTiles()
        {
            if (tiles_.Count <= MaxTiles) return;
            int threshold = frame_ - 2;   // never evict a tile drawn in the current/last frame
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
                    bmp.Dispose();
                lastUsed_.TryRemove(key, out _);
            }
        }

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

            if (dragging_)
                context.DrawImage(view_, new Rect(panX_ - margin_, panY_ - margin_, view_.PixelSize.Width, view_.PixelSize.Height));
            else
                context.DrawImage(view_, new Rect(-margin_, -margin_, view_.PixelSize.Width, view_.PixelSize.Height));

            // Fresh grid overlay: always covers the whole viewport and stays aligned with the map,
            // because at every screen pixel the translated map and this grid map to the same world
            // coordinate (the offscreen was rendered at the center captured when the drag segment
            // started; panning just translates it by the drag delta).
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
                renderCenterZ_ = centerZ_;   // freeze the offscreen center for this drag segment
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
                // Translate the cached offscreen by the drag delta, clamped to the margin so no
                // blank edges are revealed; once the drag passes the margin we re-render at the new
                // center and re-anchor the drag (so panning stays smooth over long distances).
                double tdx = Math.Max(-m, Math.Min(m, dx));
                double tdy = Math.Max(-m, Math.Min(m, dy));
                panX_ = tdx;
                panY_ = tdy;

                if (Math.Abs(dx) >= m || Math.Abs(dy) >= m)
                {
                    dragStart_ = p;
                    dragCenterX_ = centerX_;
                    dragCenterZ_ = centerZ_;
                    renderCenterX_ = centerX_;
                    renderCenterZ_ = centerZ_;   // re-anchor the offscreen at the new center
                    panX_ = panY_ = 0;
                    InvalidateView();
                }
                else
                {
                    InvalidateVisual();
                }
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

        private void RaiseStatus()
        {
            Dispatcher.UIThread.Post(() =>
            {
                string coord = hoverValid_
                    ? $"世界坐标: ({hoverWorldX_}, {hoverWorldZ_})   区块坐标: ({hoverChunkX_}, {hoverChunkZ_})"
                    : "世界坐标: -   区块坐标: -";
                int done = Math.Min(doneBlocks_, totalBlocks_);
                string progress = totalBlocks_ > 0 ? $"已渲染区块块: {done}/{totalBlocks_}" : "无区块";
                string cacheInfo = cachedTotal_ > 0 ? $" | 已缓存: {Math.Min(cachedDone_, cachedTotal_)}/{cachedTotal_}" : "";
                string status = $"存档: {LevelName} | 维度: {DimName(dimension_)} | 模式: {mode_} | " +
                                $"区块数: {chunkCount_} | {progress}{cacheInfo} | 缩放: {viewScale_:F2} px/格 | {coord}";
                StatusChanged?.Invoke(status);

                double? frac = totalBlocks_ > 0 ? (double)done / totalBlocks_ : (double?)null;
                ProgressChanged?.Invoke(frac);
            }, DispatcherPriority.Background);
        }

        private static string DimName(int d) => d switch { 1 => "下界", 2 => "末地", _ => "主世界" };
    }
}
