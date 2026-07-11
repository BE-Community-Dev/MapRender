using System;
using System.Collections.Generic;
using BedrockLevel.Keys;
using BedrockLevel.Nbt;

namespace BedrockLevel.Chunk
{
    public enum HSAType : sbyte
    {
        NetherFortress = 1,
        SwampHut = 2,
        OceanMonument = 3,
        PillagerOutpost = 5,
        Unknown = 6
    }

    public sealed class HardcodedSpawnArea
    {
        public HSAType Type { get; set; } = HSAType.Unknown;
        public BlockPos MinPos { get; set; }
        public BlockPos MaxPos { get; set; }
    }

    /// <summary>Parsed chunk. Consumes a RawChunk. Mirrors chunk.cpp.</summary>
    public sealed class Chunk
    {
        private readonly ChunkPos pos_;
        private readonly Dictionary<int, SubChunk> subChunks_ = new Dictionary<int, SubChunk>();
        private readonly Biome3D d3d_ = new Biome3D();
        private readonly List<Actor> entities_ = new List<Actor>();
        private readonly List<CompoundTag> blockEntities_ = new List<CompoundTag>();
        private readonly List<CompoundTag> pendingTicks_ = new List<CompoundTag>();
        private readonly List<HardcodedSpawnArea> hsa_ = new List<HardcodedSpawnArea>();
        private ChunkVersion version_ = ChunkVersion.New;
        private bool loaded_;

        public Chunk(ChunkPos pos) { pos_ = pos; }

        public bool Loaded => loaded_;
        public ChunkPos Pos => pos_;
        public ChunkVersion Version => version_;
        public IReadOnlyList<Actor> Entities => entities_;
        public IReadOnlyList<CompoundTag> BlockEntities => blockEntities_;
        public IReadOnlyList<CompoundTag> PendingTicks => pendingTicks_;
        public IReadOnlyList<HardcodedSpawnArea> HSAs => hsa_;

        public bool LoadFromRawChunk(RawChunk rc)
        {
            LoadSubchunks(rc);
            if (subChunks_.Count == 0) return false;
            LoadBiomes(rc);
            LoadEntities(rc);
            LoadBlockEntities(rc);
            LoadPendingTicks(rc);
            LoadHsa(rc);
            loaded_ = true;
            return true;
        }

        private void LoadSubchunks(RawChunk rc)
        {
            foreach (var kv in rc.SubChunkData)
            {
                if (kv.Value.Length == 0) continue;
                var sb = new SubChunk { YIndex = kv.Key };
                if (!sb.Load(kv.Value)) continue;
                subChunks_[kv.Key] = sb;
            }
            if (subChunks_.Count > 0)
            {
                foreach (var sb in subChunks_.Values)
                {
                    if (sb.Version == 9) { version_ = ChunkVersion.New; break; }
                }
            }
        }

        private void LoadBiomes(RawChunk rc)
        {
            d3d_.SetChunkPos(pos_);
            d3d_.SetVersion(version_);
            if (version_ == ChunkVersion.New)
            {
                var raw = rc.GetNormalKey(ChunkKey.KeyType.Data3D);
                if (raw.Length > 0) d3d_.LoadFromD3D(raw);
            }
            else
            {
                var raw = rc.GetNormalKey(ChunkKey.KeyType.Data2D);
                if (raw.Length == 0) raw = rc.GetNormalKey(ChunkKey.KeyType.Data2DLegacy);
                if (raw.Length > 0) d3d_.LoadFromD2D(raw);
            }
        }

        private void LoadEntities(RawChunk rc)
        {
            // old version: Entity key holds concatenated NBT compounds
            var raw = rc.GetNormalKey(ChunkKey.KeyType.Entity);
            if (raw.Length > 0)
            {
                foreach (var c in NbtReader.ReadPaletteToEnd(raw))
                {
                    var ac = new Actor();
                    if (ac.LoadFromNbt(c)) entities_.Add(ac);
                }
            }
            // new version: digest + actorprefix entries
            if (rc.ActorDigest.Length > 0)
            {
                int count = rc.ActorDigest.Length / 8;
                for (int i = 0; i < count; i++)
                {
                    var uid = new byte[8];
                    Buffer.BlockCopy(rc.ActorDigest, i * 8, uid, 0, 8);
                    if (rc.Entities.TryGetValue(new LevelDb.ByteString(uid), out var actorRaw) && actorRaw.Length > 0)
                    {
                        var ac = new Actor();
                        if (ac.Load(actorRaw))
                            entities_.Add(ac);
                    }
                }
            }
        }

        private void LoadBlockEntities(RawChunk rc)
        {
            var raw = rc.GetNormalKey(ChunkKey.KeyType.BlockEntity);
            if (raw.Length > 0) blockEntities_.AddRange(NbtReader.ReadPaletteToEnd(raw));
        }

        private void LoadPendingTicks(RawChunk rc)
        {
            var raw = rc.GetNormalKey(ChunkKey.KeyType.PendingTicks);
            if (raw.Length > 0) pendingTicks_.AddRange(NbtReader.ReadPaletteToEnd(raw));
        }

        private void LoadHsa(RawChunk rc)
        {
            var raw = rc.GetNormalKey(ChunkKey.KeyType.HardCodedSpawnAreas);
            if (raw.Length < 4) return;
            int count = BitConverter.ToInt32(raw, 0);
            if (raw.Length != count * 25L + 4) return;
            for (int i = 0; i < count; i++)
            {
                int o = i * 25 + 4;
                var area = new HardcodedSpawnArea
                {
                    MinPos = new BlockPos(ReadInt32(raw, o), ReadInt32(raw, o + 4), ReadInt32(raw, o + 8)),
                    MaxPos = new BlockPos(ReadInt32(raw, o + 12), ReadInt32(raw, o + 16), ReadInt32(raw, o + 20)),
                    Type = (HSAType)raw[o + 24]
                };
                hsa_.Add(area);
            }
        }

        public string GetBlockName(int cx, int y, int cz)
        {
            ChunkPos.MapYToSubchunk(y, out int index, out int offset);
            if (!subChunks_.TryGetValue(index, out var sb)) return "minecraft:unknown";
            return sb.GetBlockName(cx, offset, cz);
        }

        public byte GetBiome(int cx, int y, int cz) => d3d_.GetBiome(cx, y, cz);

        public int GetHeight(int cx, int cz) => d3d_.Height(cx, cz);

        public byte GetTopBiome(int cx, int cz) => d3d_.GetTopBiome(cx, cz);

        /// <summary>
        /// Returns (topY, solidY) for a column: topY = highest non-air block,
        /// solidY = highest non-air, non-water block. Uses a precomputed per-block
        /// category to avoid per-block string allocations in the hot scan.
        /// </summary>
        public (int topY, int solidY) GetTopY(int cx, int cz, int maxY)
        {
            var (minY, _) = pos_.GetYRange(version_);
            int topY = minY - 1;
            int solidY = minY - 1;
            int startSub = (maxY < 0) ? (maxY - 15) / 16 : maxY / 16;
            int endSub = (minY < 0) ? (minY - 15) / 16 : minY / 16;
            for (int si = startSub; si >= endSub; si--)
            {
                if (!subChunks_.TryGetValue(si, out var sb)) continue;
                int yHi = Math.Min(maxY, si * 16 + 15);
                int yLo = Math.Max(minY, si * 16);
                for (int y = yHi; y >= yLo; y--)
                {
                    int offset = y - si * 16;
                    byte cat = sb.GetBlockCategory(cx, offset, cz);
                    if (cat == 0) continue;                       // unknown
                    if (topY < minY && cat != 1) topY = y;        // air = 1
                    if (cat != 1 && cat != 2 && solidY < minY) solidY = y; // water = 2
                    if (topY >= minY && solidY >= minY) return (topY, solidY);
                }
            }
            return (topY, solidY);
        }

        private static int ReadInt32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
    }
}
