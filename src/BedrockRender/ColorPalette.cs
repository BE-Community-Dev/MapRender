using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace BedrockRender
{
    /// <summary>
    /// Loads the biome / block color palettes and resolves block + biome colors,
    /// including biome shading for water / grass / leaves (ported from color.cpp).
    /// </summary>
    public sealed class ColorPalette
    {
        private readonly Dictionary<int, (byte r, byte g, byte b)> biomeRgb_ = new Dictionary<int, (byte, byte, byte)>();
        private readonly Dictionary<int, (byte r, byte g, byte b)> biomeGrass_ = new Dictionary<int, (byte, byte, byte)>();
        private readonly Dictionary<int, (byte r, byte g, byte b)> biomeLeaves_ = new Dictionary<int, (byte, byte, byte)>();
        private readonly Dictionary<int, (byte r, byte g, byte b)> biomeWater_ = new Dictionary<int, (byte, byte, byte)>();

        private (byte r, byte g, byte b) defaultGrass_ = (142, 185, 113);
        private (byte r, byte g, byte b) defaultLeaves_ = (113, 167, 77);
        private (byte r, byte g, byte b) defaultWater_ = (63, 118, 228);

        private readonly Dictionary<string, byte[]> singleBlock_ = new Dictionary<string, byte[]>();
        private readonly Dictionary<string, Dictionary<string, byte[]>> multiBlock_ = new Dictionary<string, Dictionary<string, byte[]>>();

        // Caches the final surface color per (block name, biome id). Block names repeat
        // heavily across a world, so after warmup this avoids most palette lookups.
        // ConcurrentDictionary because rendering is parallelized across chunks.
        private readonly ConcurrentDictionary<(string name, byte biome), (byte r, byte g, byte b)> surfCache_
            = new ConcurrentDictionary<(string, byte), (byte, byte, byte)>();

        public static ColorPalette LoadEmbedded()
        {
            var asm = typeof(ColorPalette).Assembly;
            string biome = ReadResource(asm, "BedrockRender.Resources.biome_color.json");
            string block = ReadResource(asm, "BedrockRender.Resources.block_color.json");
            var p = new ColorPalette();
            p.Load(biome, block);
            return p;
        }

        public static ColorPalette LoadFiles(string biomePath, string blockPath)
        {
            var p = new ColorPalette();
            p.Load(File.ReadAllText(biomePath), File.ReadAllText(blockPath));
            return p;
        }

        public void Load(string biomeJson, string blockJson)
        {
            LoadBiome(biomeJson);
            LoadBlock(blockJson);
        }

        private static string ReadResource(Assembly asm, string name)
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) throw new FileNotFoundException("embedded resource not found: " + name);
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        private void LoadBiome(string json)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value;
                if (!v.TryGetProperty("id", out var idEl)) continue;
                int id = idEl.GetInt32();
                if (v.TryGetProperty("rgb", out var rgb) && rgb.GetArrayLength() == 3)
                    biomeRgb_[id] = (rgb[0].GetByte(), rgb[1].GetByte(), rgb[2].GetByte());
                if (v.TryGetProperty("grass", out var g) && g.GetArrayLength() == 3)
                    biomeGrass_[id] = (g[0].GetByte(), g[1].GetByte(), g[2].GetByte());
                if (v.TryGetProperty("leaves", out var l) && l.GetArrayLength() == 3)
                    biomeLeaves_[id] = (l[0].GetByte(), l[1].GetByte(), l[2].GetByte());
                if (v.TryGetProperty("water", out var w) && w.GetArrayLength() == 3)
                    biomeWater_[id] = (w[0].GetByte(), w[1].GetByte(), w[2].GetByte());
            }
        }

        private void LoadBlock(string json)
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string blockName = prop.Name;
                var value = prop.Value;
                var vec = new List<KeyValuePair<string, byte[]>>();
                foreach (var tag in value.EnumerateObject())
                {
                    var arr = tag.Value;
                    if (arr.GetArrayLength() >= 3)
                    {
                        byte a = arr.GetArrayLength() >= 4 ? arr[3].GetByte() : (byte)255;
                        vec.Add(new KeyValuePair<string, byte[]>(tag.Name, new[] { arr[0].GetByte(), arr[1].GetByte(), arr[2].GetByte(), a }));
                    }
                }
                if (vec.Count == 1) singleBlock_[blockName] = vec[0].Value;
                else if (vec.Count > 1)
                {
                    var m = new Dictionary<string, byte[]>();
                    foreach (var kv in vec) m[kv.Key] = kv.Value;
                    multiBlock_[blockName] = m;
                }
            }
        }

        public (byte r, byte g, byte b) GetBiomeColor(byte biomeId)
        {
            return biomeRgb_.TryGetValue(biomeId, out var c) ? c : ((byte)128, (byte)128, (byte)128);
        }

        public (byte r, byte g, byte b, byte a) GetBlockColor(string name, string tag)
        {
            if (singleBlock_.TryGetValue(name, out var c)) return (r: c[0], g: c[1], b: c[2], a: c[3]);
            if (multiBlock_.TryGetValue(name, out var m) && m.Count > 0)
            {
                if (!string.IsNullOrEmpty(tag) && m.TryGetValue(tag, out var ct)) return (r: ct[0], g: ct[1], b: ct[2], a: ct[3]);
                foreach (var kv in m) return (r: kv.Value[0], g: kv.Value[1], b: kv.Value[2], a: kv.Value[3]);
            }
            return (r: 173, g: 8, b: 172, a: 255); // unknown (matches C++ default)
        }

        /// <summary>Applies biome shading for water / grass / leaves blocks (ported from blend_color_with_biome).</summary>
        public (byte r, byte g, byte b) BlendWithBiome(string blockName, (byte r, byte g, byte b) baseColor, byte biomeId)
        {
            if (blockName.Contains("water"))
                return Blend(biomeWater_, baseColor, defaultWater_, biomeId);
            if (blockName.Contains("leave"))
                return Blend(biomeLeaves_, baseColor, defaultLeaves_, biomeId);
            if (blockName.Contains("grass"))
                return Blend(biomeGrass_, baseColor, defaultGrass_, biomeId);
            return baseColor;
        }

        /// <summary>
        /// Resolves the final surface color for a top block, caching by (block name, biome).
        /// Avoids repeated palette lookups for the same block/biome pair across the world.
        /// </summary>
        public (byte r, byte g, byte b) ResolveSurfaceColor(string name, byte biomeId)
        {
            return surfCache_.GetOrAdd((name, biomeId), k =>
            {
                var bc = GetBlockColor(k.name, "");
                return BlendWithBiome(k.name, (bc.r, bc.g, bc.b), k.biome);
            });
        }

        private static (byte r, byte g, byte b) Blend(Dictionary<int, (byte r, byte g, byte b)> map,
            (byte r, byte g, byte b) gray, (byte r, byte g, byte b) defaultColor, byte biomeId)
        {
            var x = map.TryGetValue(biomeId, out var c) ? c : defaultColor;
            int r = (int)(gray.r / 255.0 * x.r);
            int g = (int)(gray.g / 255.0 * x.g);
            int b = (int)(gray.b / 255.0 * x.b);
            return ((byte)r, (byte)g, (byte)b);
        }
    }
}
