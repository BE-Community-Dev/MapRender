using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockRender;

namespace BedrockRender.Demo
{
    /// <summary>
    /// Reads every chunk of a save (filtered by dimension), renders each chunk with
    /// BedrockRender, and stitches them into one big WriteableBitmap.
    /// </summary>
    public static class WorldRenderer
    {
        public static WriteableBitmap Render(RenderOptions opts)
        {
            var level = new global::BedrockLevel.Level.BedrockLevel();
            if (!level.Open(opts.SaveDir))
                throw new InvalidOperationException("failed to open save: " + opts.SaveDir);

            var positions = level.ChunkPositions(opts.Dimension).ToList();
            Console.WriteLine($"[render] chunk positions found: {positions.Count} (dim={opts.Dimension})");
            if (positions.Count == 0)
                throw new InvalidOperationException("no chunks found for the given dimension");

            int minX = int.MaxValue, maxX = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue;
            foreach (var cp in positions)
            {
                if (cp.X < minX) minX = cp.X;
                if (cp.X > maxX) maxX = cp.X;
                if (cp.Z < minZ) minZ = cp.Z;
                if (cp.Z > maxZ) maxZ = cp.Z;
            }

            int chunkPx = 16 * opts.Scale;
            long colsX = (long)maxX - minX + 1;
            long colsZ = (long)maxZ - minZ + 1;
            long W = colsX * chunkPx;
            long H = colsZ * chunkPx;
            Console.WriteLine($"[render] bounds X=[{minX},{maxX}] Z=[{minZ},{maxZ}] -> image {W}x{H} (scale={opts.Scale})");

            if (W <= 0 || H <= 0 || W > 200_000 || H > 200_000)
                throw new InvalidOperationException(
                    $"cannot render: computed image size {W}x{H} is out of range " +
                    $"(chunks X=[{minX},{maxX}] Z=[{minZ},{maxZ}], scale={opts.Scale}). " +
                    "The save may use an unsupported chunk-key layout.");

            var bmp = new WriteableBitmap(new PixelSize((int)W, (int)H), Vector.One, PixelFormat.Bgra8888, AlphaFormat.Premul);
            var palette = ColorPalette.LoadEmbedded();
            var renderer = new ChunkRenderer(palette);

            using (var fb = bmp.Lock())
            {
                int len = fb.RowBytes * fb.Size.Height;
                // Capture the raw pointer (not a ref-struct Span) so the lambda can build
                // its own Span per iteration. Chunks blit disjoint rectangles, so concurrent
                // writes to the framebuffer are safe.
                IntPtr addr = fb.Address;
                int len_ = len;
                int minX_ = minX, minZ_ = minZ, chunkPx_ = chunkPx, W_ = (int)W, H_ = (int)H;
                var mode_ = opts.Mode;
                var scale_ = opts.Scale;
                Parallel.ForEach(positions, cp =>
                {
                    var chunk = level.GetChunk(cp);
                    if (chunk == null) return;
                    var rc = renderer.Render(chunk, mode_, scale_);
                    int destX = (cp.X - minX_) * chunkPx_;
                    int destY = (cp.Z - minZ_) * chunkPx_;
                    unsafe
                    {
                        var span = new Span<byte>((void*)addr, len_);
                        rc.BlitTo(span, W_, H_, destX, destY);
                    }
                });
            }
            return bmp;
        }

        public static void Save(RenderOptions opts)
        {
            var bmp = Render(opts);
            using var fs = File.Create(opts.Output);
            bmp.Save(fs);
            Console.WriteLine($"wrote {opts.Output} ({bmp.PixelSize.Width}x{bmp.PixelSize.Height})");
        }
    }
}
