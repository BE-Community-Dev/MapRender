using System;
using System.Collections.Generic;
using BedrockLevel.Keys;

namespace BedrockLevel.Chunk
{
    /// <summary>3D biome + height map for a chunk. Mirrors data_3d.cpp.</summary>
    public sealed class Biome3D
    {
        private readonly short[] heightMap_ = new short[256];
        // biomes_[y][x, z]  (one entry per block column in the vertical stack)
        private readonly List<byte[,]> biomes_ = new List<byte[,]>();
        private ChunkPos pos_;
        private ChunkVersion version_;

        public void SetChunkPos(ChunkPos cp) => pos_ = cp;
        public void SetVersion(ChunkVersion v) => version_ = v;
        public short[] HeightMap => heightMap_;

        public bool LoadFromD3D(byte[] data)
        {
            if (data == null || data.Length < 512) return false;
            for (int i = 0; i < 256; i++)
                heightMap_[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
            int idx = 512;
            while (idx < data.Length)
            {
                var sub = LoadSubchunkBiome(data, ref idx);
                for (int y = 0; y < 16; y++)
                {
                    var layer = new byte[16, 16];
                    for (int x = 0; x < 16; x++)
                        for (int z = 0; z < 16; z++)
                            layer[x, z] = sub[x * 256 + z * 16 + y];
                    biomes_.Add(layer);
                }
            }
            return true;
        }

        public bool LoadFromD2D(byte[] data)
        {
            if (data == null || data.Length != 768) return false;
            for (int i = 0; i < 256; i++)
                heightMap_[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8));
            var layer = new byte[16, 16];
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                    layer[x, z] = data[512 + x + 16 * z];
            biomes_.Add(layer);
            return true;
        }

        public int Height(int x, int z)
        {
            var (my, _) = pos_.GetYRange(version_);
            return heightMap_[x + z * 16] + my;
        }

        public byte GetBiome(int cx, int y, int cz)
        {
            if (version_ == ChunkVersion.Old)
                return biomes_.Count == 0 ? (byte)255 : biomes_[0][cx, cz];
            var (my, _) = pos_.GetYRange(version_);
            int yy = y - my;
            if (yy < 0 || yy >= biomes_.Count) return 255;
            return biomes_[yy][cx, cz];
        }

        public byte GetTopBiome(int cx, int cz)
        {
            if (version_ == ChunkVersion.Old) return GetBiome(cx, 0, cz);
            int y = biomes_.Count - 1;
            while (y >= 0 && biomes_[y][cx, cz] == 255) y--;
            return y < 0 ? (byte)255 : biomes_[y][cx, cz];
        }

        private static byte[] LoadSubchunkBiome(byte[] data, ref int idx)
        {
            byte head = data[idx];
            idx++;
            if (head == 0xff) return new byte[4096]; // all "none"

            int bits = head >> 1;
            int[] index = new int[4096];
            int paletteLen = 1;
            if (bits != 0)
            {
                int bpw = 32 / bits;
                int wordCount = 4096 / bpw;
                if (4096 % bpw != 0) wordCount++;
                int position = 0;
                for (int w = 0; w < wordCount; w++)
                {
                    int word = ReadInt32(data, idx + w * 4);
                    for (int b = 0; b < bpw; b++)
                    {
                        int state = (word >> ((position % bpw) * bits)) & ((1 << bits) - 1);
                        if (position < 4096) index[position] = state;
                        position++;
                    }
                }
                idx += wordCount * 4;
                paletteLen = ReadInt32(data, idx);
                idx += 4;
            }

            var palettes = new List<byte>();
            for (int i = 0; i < paletteLen; i++)
            {
                palettes.Add((byte)ReadInt32(data, idx));
                idx += 4;
            }

            var res = new byte[4096];
            for (int i = 0; i < 4096; i++)
            {
                if (index[i] >= 0 && index[i] < palettes.Count)
                    res[i] = palettes[index[i]];
            }
            return res;
        }

        private static int ReadInt32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
    }
}
