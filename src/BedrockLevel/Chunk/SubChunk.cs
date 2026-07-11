using System;
using System.Collections.Generic;
using BedrockLevel.Nbt;

namespace BedrockLevel.Chunk
{
    public sealed class Biome
    {
        // bedrock biome ids (subset; values mirror data_3d.h)
        public const byte None = 255;
    }

    public sealed class SubChunk
    {
        public sealed class Layer
        {
            public byte Bits;
            public byte Type;          // 0 = palette, 1 = runtime
            public int PaletteLen;
            public int[] Blocks = Array.Empty<int>();
            public List<CompoundTag> Palette { get; } = new List<CompoundTag>();
            // Per-palette-index block category for fast top-block scans (no string lookup).
            // 0 = unknown, 1 = air, 2 = water, 3 = solid.
            public byte[] Cat;
        }

        /// <summary>Block category at a block coordinate, allocation-free.</summary>
        public byte GetBlockCategory(int rx, int ry, int rz)
        {
            if (rx < 0 || rx > 15 || ry < 0 || ry > 15 || rz < 0 || rz > 15) return 0;
            if (Layers.Count == 0) return 0;
            int index = ry + rz * 16 + rx * 256;
            int block = Layers[0].Blocks[index];
            var cat = Layers[0].Cat;
            if (cat == null || block < 0 || block >= cat.Length) return 0;
            return cat[block];
        }

        public byte Version { get; private set; } = 0xff;
        public sbyte YIndex { get; set; }
        public byte LayersNum { get; private set; } = 0xff;
        public List<Layer> Layers { get; } = new List<Layer>();

        public bool Load(byte[] data)
        {
            if (data == null || data.Length < 2) return false;
            int idx = 0;
            byte version = data[0];
            if (version != 8 && version != 9) return false;
            Version = version;
            LayersNum = data[1];
            idx = 2;
            if (version == 9)
            {
                YIndex = (sbyte)data[2];
                idx = 3;
            }

            const int BlockNum = 4096;
            for (int i = 0; i < LayersNum; i++)
            {
                var layer = new Layer();
                if (!ReadOneLayer(data, ref idx, layer, BlockNum)) return false;
                Layers.Add(layer);
            }
            return true;
        }

        private static bool ReadOneLayer(byte[] data, ref int idx, Layer layer, int blockNum)
        {
            byte header = data[idx++];
            layer.Type = (byte)(header & 0x1);
            layer.Bits = (byte)(header >> 1);

            if (layer.Bits != 0)
            {
                int bpw = 32 / layer.Bits;
                int wordCount = blockNum / bpw;
                if (blockNum % bpw != 0) wordCount++;
                layer.Blocks = new int[blockNum];
                int position = 0;
                for (int w = 0; w < wordCount; w++)
                {
                    int word = ReadInt32(data, idx + w * 4);
                    for (int b = 0; b < bpw; b++)
                    {
                        int state = (word >> ((position % bpw) * layer.Bits)) & ((1 << layer.Bits) - 1);
                        if (position < blockNum) layer.Blocks[position] = state;
                        position++;
                    }
                }
                idx += wordCount * 4;
                layer.PaletteLen = ReadInt32(data, idx);
                idx += 4;
            }
            else
            {
                layer.Blocks = new int[blockNum]; // uniform, all 0
                layer.PaletteLen = 1;
            }

            for (int p = 0; p < layer.PaletteLen; p++)
            {
                var tag = NbtReader.ReadOne(data, idx, out int consumed);
                if (tag == null) break;
                tag.Value.Remove("version"); // compatibility with color table
                layer.Palette.Add(tag);
                idx += consumed;
            }

            // Precompute block categories so top-block scans avoid per-block string lookups.
            layer.Cat = new byte[layer.Palette.Count];
            for (int p = 0; p < layer.Palette.Count; p++)
            {
                var nm = layer.Palette[p].Get("name") as StringTag;
                string name = nm?.Value ?? "";
                if (name == "minecraft:air" || name == "minecraft:cave_air")
                    layer.Cat[p] = 1;
                else if (name == "minecraft:water" || name == "minecraft:flowing_water")
                    layer.Cat[p] = 2;
                else if (name.Length == 0 || name == "minecraft:unknown")
                    layer.Cat[p] = 0;
                else
                    layer.Cat[p] = 3;
            }
            return true;
        }

        public CompoundTag GetBlockRaw(int rx, int ry, int rz)
        {
            if (rx < 0 || rx > 15 || ry < 0 || ry > 15 || rz < 0 || rz > 15) return null;
            if (Layers.Count == 0) return null;
            int index = ry + rz * 16 + rx * 256;
            int block = Layers[0].Blocks[index];
            if (block < 0 || block >= Layers[0].Palette.Count) return null;
            return Layers[0].Palette[block];
        }

        public string GetBlockName(int rx, int ry, int rz)
        {
            var tag = GetBlockRaw(rx, ry, rz);
            if (tag == null) return "minecraft:unknown";
            var nameTag = tag.Get("name") as StringTag;
            return nameTag?.Value ?? "minecraft:unknown";
        }

        private static int ReadInt32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
    }
}
