using System;

namespace BedrockRender
{
    /// <summary>A rendered single-chunk image as a flat RGBA pixel buffer.</summary>
    public sealed class RenderedChunk
    {
        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; } // RGBA, row-major, length = Width*Height*4

        public RenderedChunk(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        /// <summary>Copies this RGBA image into a BGRA8888 framebuffer at (destX, destY).</summary>
        public void BlitTo(Span<byte> bgra, int canvasW, int canvasH, int destX, int destY)
        {
            for (int y = 0; y < Height; y++)
            {
                int cy = destY + y;
                if (cy < 0 || cy >= canvasH) continue;
                for (int x = 0; x < Width; x++)
                {
                    int cx = destX + x;
                    if (cx < 0 || cx >= canvasW) continue;
                    int src = (y * Width + x) * 4;
                    int dst = (cy * canvasW + cx) * 4;
                    bgra[dst + 0] = Pixels[src + 2]; // B
                    bgra[dst + 1] = Pixels[src + 1]; // G
                    bgra[dst + 2] = Pixels[src + 0]; // R
                    bgra[dst + 3] = Pixels[src + 3]; // A
                }
            }
        }
    }

    public enum ViewMode { Surface, Biome, Height }

    /// <summary>
    /// Renders a parsed Chunk into a per-column image. Ported logic from color.cpp
    /// (block color + biome shading) and chunk.cpp (top / solid block lookup).
    /// </summary>
    public sealed class ChunkRenderer
    {
        private readonly ColorPalette palette_;

        public ChunkRenderer(ColorPalette palette) => palette_ = palette;
        public RenderedChunk Render(BedrockLevel.Chunk.Chunk chunk, ViewMode mode, int scale = 4)
        {
            int size = 16 * scale;
            var pixels = new byte[size * size * 4];

            var (minY, maxY) = chunk.Pos.GetYRange(chunk.Version);

            for (int z = 0; z < 16; z++)
            {
                for (int x = 0; x < 16; x++)
                {
                    (byte r, byte g, byte b) col = ColorForColumn(chunk, mode, x, z, minY, maxY);
                    int px = x * scale;
                    int pz = z * scale;
                    for (int dy = 0; dy < scale; dy++)
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int idx = ((pz + dy) * size + (px + dx)) * 4;
                            pixels[idx + 0] = col.r;
                            pixels[idx + 1] = col.g;
                            pixels[idx + 2] = col.b;
                            pixels[idx + 3] = 255;
                        }
                }
            }
            return new RenderedChunk(size, size, pixels);
        }

        private (byte r, byte g, byte b) ColorForColumn(BedrockLevel.Chunk.Chunk chunk, ViewMode mode, int x, int z, int minY, int maxY)
        {
            switch (mode)
            {
                case ViewMode.Biome:
                {
                    byte biome = chunk.GetTopBiome(x, z);
                    return palette_.GetBiomeColor(biome);
                }
                case ViewMode.Height:
                {
                    var (_, solidY) = chunk.GetTopY(x, z, maxY);
                    int h = solidY < minY ? minY : solidY;
                    double t = (maxY == minY) ? 0 : (double)(h - minY) / (maxY - minY);
                    t = Math.Max(0, Math.Min(1, t));
                    return HeightRamp(t);
                }
                case ViewMode.Surface:
                default:
                {
                    var (topY, _) = chunk.GetTopY(x, z, maxY);
                    if (topY < minY)
                    {
                        byte biome = chunk.GetTopBiome(x, z);
                        return palette_.GetBiomeColor(biome);
                    }
                    string name = chunk.GetBlockName(x, topY, z);
                    byte biomeId = chunk.GetTopBiome(x, z);
                    var c = palette_.ResolveSurfaceColor(name, biomeId);
                    double shade = HeightShadeFactor(topY, minY, maxY);
                    return ((byte)(c.r * shade), (byte)(c.g * shade), (byte)(c.b * shade));
                }
            }
        }

        /// <summary>Height-based shadow factor: lower terrain is darker, higher terrain brighter.</summary>
        private static double HeightShadeFactor(int h, int minY, int maxY)
        {
            double range = Math.Max(1, maxY - minY);
            double t = (double)(h - minY) / range;
            t = Math.Max(0, Math.Min(1, t));
            return 0.55 + t * 0.45;
        }

        private static (byte r, byte g, byte b) HeightRamp(double t)
        {
            // blue (low) -> green -> yellow -> red (high)
            int r, g, b;
            if (t < 0.33)
            {
                double k = t / 0.33;
                r = 0; g = (int)(k * 255); b = (int)((1 - k) * 255);
            }
            else if (t < 0.66)
            {
                double k = (t - 0.33) / 0.33;
                r = (int)(k * 255); g = 255; b = 0;
            }
            else
            {
                double k = (t - 0.66) / 0.34;
                r = 255; g = (int)((1 - k) * 255); b = 0;
            }
            return ((byte)r, (byte)g, (byte)b);
        }
    }
}
