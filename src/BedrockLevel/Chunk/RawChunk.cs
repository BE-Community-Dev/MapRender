using System;
using System.Collections.Generic;
using BedrockLevel.IO;
using BedrockLevel.Keys;
using BedrockLevel.LevelDb;

namespace BedrockLevel.Chunk
{
    /// <summary>
    /// All raw (un-parsed) LevelDB key/values belonging to one chunk.
    /// Mirrors raw_chunk in chunk.cpp. Serialization format starts with magic "BCHK".
    /// </summary>
    public sealed class RawChunk
    {
        private static readonly ChunkKey.KeyType[] NormalKeys =
        {
            ChunkKey.KeyType.Data3D, ChunkKey.KeyType.VersionNew, ChunkKey.KeyType.VersionOld,
            ChunkKey.KeyType.Data2D, ChunkKey.KeyType.Data2DLegacy, ChunkKey.KeyType.BlockEntity,
            ChunkKey.KeyType.Entity, ChunkKey.KeyType.PendingTicks, ChunkKey.KeyType.BlockExtraData,
            ChunkKey.KeyType.BiomeState, ChunkKey.KeyType.FinalizedState, ChunkKey.KeyType.ConversionData,
            ChunkKey.KeyType.BorderBlocks, ChunkKey.KeyType.HardCodedSpawnAreas, ChunkKey.KeyType.RandomTicks,
            ChunkKey.KeyType.Checksums, ChunkKey.KeyType.GenerationSeed,
            ChunkKey.KeyType.GeneratedPreCavesAndCliffsBlending, ChunkKey.KeyType.BlendingBiomeHeight,
            ChunkKey.KeyType.MetaDataHash, ChunkKey.KeyType.BlendingData, ChunkKey.KeyType.ActorDigestVersion,
            ChunkKey.KeyType.VersionOld
        };

        public ChunkPos Pos { get; set; }
        public Dictionary<ChunkKey.KeyType, byte[]> NormalData { get; } = new Dictionary<ChunkKey.KeyType, byte[]>();
        public Dictionary<sbyte, byte[]> SubChunkData { get; } = new Dictionary<sbyte, byte[]>();
        public byte[] ActorDigest { get; set; } = Array.Empty<byte>();
        public Dictionary<ByteString, byte[]> Entities { get; } = new Dictionary<ByteString, byte[]>();

        public RawChunk() { }
        public RawChunk(ChunkPos pos) { Pos = pos; }

        public ChunkVersion Version()
        {
            return GetNormalKey(ChunkKey.KeyType.VersionNew).Length > 0 ? ChunkVersion.New : ChunkVersion.Old;
        }

        public bool Loaded()
        {
            foreach (var v in NormalData.Values)
                if (v != null && v.Length > 0) return true;
            foreach (var v in SubChunkData.Values)
                if (v != null && v.Length > 0) return true;
            return false;
        }

        public bool Load(LevelDbStore store)
        {
            if (Pos == null) return false;
            foreach (var kt in NormalKeys)
            {
                var ck = new ChunkKey { Type = kt, Cp = Pos };
                if (TryGetStoreValue(store, ck, out var raw) && raw.Length > 0)
                    NormalData[kt] = raw;
            }

            var (minIdx, maxIdx) = Pos.GetSubchunkIndexRange(Version());
            for (int idx = minIdx; idx <= maxIdx; idx++)
            {
                var ck = new ChunkKey { Type = ChunkKey.KeyType.SubChunkTerrain, Cp = Pos, YIndex = (sbyte)idx };
                if (TryGetStoreValue(store, ck, out var raw))
                    SubChunkData[(sbyte)idx] = raw;
            }

            var dk = new ActorDigestKey { Cp = Pos };
            byte[] dig = null;
            if (!store.TryGetValue(dk.ToRawModern(), out dig) || dig == null || dig.Length == 0)
                store.TryGetValue(dk.ToRaw(), out dig);
            if (dig != null && dig.Length > 0)
            {
                ActorDigest = dig;
                int count = dig.Length / 8;
                for (int i = 0; i < count; i++)
                {
                    // digest stores the (big-endian) storage key bytes directly; the
                    // lookup key is "actorprefix" + those 8 raw bytes.
                    var uid = new byte[8];
                    Buffer.BlockCopy(dig, i * 8, uid, 0, 8);
                    var ak = new byte[19];
                    var prefix = System.Text.Encoding.ASCII.GetBytes("actorprefix");
                    Buffer.BlockCopy(prefix, 0, ak, 0, 11);
                    Buffer.BlockCopy(uid, 0, ak, 11, 8);
                    if (store.TryGetValue(ak, out var araw) && araw.Length > 0)
                        Entities[new ByteString(uid)] = araw;
                }
            }
            return Loaded();
        }

        /// <summary>
        /// Looks up a chunk key in the store, trying both the modern (zigzag-varint)
        /// and legacy (4-byte int) key encodings so either save layout is supported.
        /// </summary>
        private static bool TryGetStoreValue(LevelDbStore store, ChunkKey ck, out byte[] raw)
        {
            if (store.TryGetValue(ck.ToRawModern(), out raw) && raw != null && raw.Length > 0)
                return true;
            if (store.TryGetValue(ck.ToRawLegacy(), out raw) && raw != null && raw.Length > 0)
                return true;
            raw = null;
            return false;
        }

        public byte[] GetNormalKey(ChunkKey.KeyType key)
        {
            return NormalData.TryGetValue(key, out var v) && v != null ? v : Array.Empty<byte>();
        }

        public byte[] GetSubChunk(sbyte yIndex)
        {
            return SubChunkData.TryGetValue(yIndex, out var v) && v != null ? v : Array.Empty<byte>();
        }

        // ---- serialization (cache) ----

        public byte[] ToRaw()
        {
            var w = new ByteWriter();
            w.WriteByte((byte)'B'); w.WriteByte((byte)'C'); w.WriteByte((byte)'H'); w.WriteByte((byte)'K');
            w.WriteInt32(Pos.X);
            w.WriteInt32(Pos.Z);
            w.WriteInt32(Pos.Dim);

            w.WriteInt32(NormalData.Count);
            foreach (var kv in NormalData)
            {
                w.WriteInt32((int)kv.Key);
                WriteBytes(w, kv.Value);
            }

            w.WriteInt32(SubChunkData.Count);
            foreach (var kv in SubChunkData)
            {
                w.WriteByte((byte)kv.Key);
                WriteBytes(w, kv.Value);
            }

            WriteBytes(w, ActorDigest);

            w.WriteInt32(Entities.Count);
            foreach (var kv in Entities)
            {
                WriteBytes(w, kv.Key.ToArray());
                WriteBytes(w, kv.Value);
            }
            return w.ToArray();
        }

        public bool FromRaw(byte[] data)
        {
            if (data == null || data.Length < 4) return false;
            var r = new ByteReader(data);
            if (r.ReadByte() != 'B' || r.ReadByte() != 'C' || r.ReadByte() != 'H' || r.ReadByte() != 'K')
                return false;
            Pos = new ChunkPos(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

            int dataCount = r.ReadInt32();
            for (int i = 0; i < dataCount; i++)
            {
                var kt = (ChunkKey.KeyType)r.ReadInt32();
                NormalData[kt] = ReadBytes(r);
            }

            int subCount = r.ReadInt32();
            for (int i = 0; i < subCount; i++)
            {
                sbyte index = (sbyte)r.ReadByte();
                SubChunkData[index] = ReadBytes(r);
            }

            ActorDigest = ReadBytes(r);

            int entityCount = r.ReadInt32();
            for (int i = 0; i < entityCount; i++)
            {
                var uid = ReadBytes(r);
                Entities[new ByteString(uid)] = ReadBytes(r);
            }
            return true;
        }

        private static void WriteBytes(ByteWriter w, byte[] b)
        {
            b = b ?? Array.Empty<byte>();
            w.WriteInt32(b.Length);
            w.WriteBytes(b);
        }

        private static byte[] ReadBytes(ByteReader r)
        {
            int len = r.ReadInt32();
            if (len < 0) len = 0;
            return r.ReadBytes(len);
        }
    }
}
