using System;
using System.Collections.Generic;
using System.IO;
using BedrockLevel.Cache;
using BedrockLevel.Chunk;
using BedrockLevel.IO;
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockLevel.LevelDb;
using BedrockLevel.Nbt;

namespace BedrockLevel.Demo
{
    /// <summary>
    /// Builds a synthetic Bedrock save (one SST file + level.dat) and runs the full
    /// parse -> cache pipeline to validate the from-scratch LevelDB/NBT readers.
    /// </summary>
    internal static class SelfTest
    {
        public static int Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "bedrock-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "db"));

            WriteLevelDat(Path.Combine(dir, "level.dat"));
            WriteSst(Path.Combine(dir, "db", "000001.sst"));

            var level = new global::BedrockLevel.Level.BedrockLevel();
            bool opened = level.Open(dir);
            Console.WriteLine($"[selftest] Open = {opened}, keys = {level.Store.KeyCount}");
            if (!opened) return 2;

            int fail = 0;
            var positions = new List<ChunkPos>(level.ChunkPositions());
            Console.WriteLine($"[selftest] chunk positions = {positions.Count}");
            if (positions.Count != 1) { Console.WriteLine("  FAIL: expected 1 chunk"); fail++; }

            var cache = new ChunkCache(Path.Combine(dir, "cache"));
            foreach (var cp in positions)
            {
                var rc = level.GetRawChunk(cp);
                if (!rc.Loaded()) { Console.WriteLine("  FAIL: raw chunk not loaded"); fail++; continue; }
                cache.Store(cp, rc);
            }
            Console.WriteLine($"[selftest] cache count = {cache.Count}, ratio = {cache.CompressionRatio:P2}");

            var chunk = level.GetChunk(new ChunkPos(0, 0, 0));
            if (chunk == null) { Console.WriteLine("  FAIL: GetChunk returned null"); fail++; }
            else
            {
                string name = chunk.GetBlockName(0, 0, 0);
                Console.WriteLine($"[selftest] surface block = {name}");
                if (name != "minecraft:stone") { Console.WriteLine("  FAIL: expected minecraft:stone"); fail++; }
                byte biome = chunk.GetTopBiome(0, 0);
                Console.WriteLine($"[selftest] top biome = {biome}");
            }

            // round-trip through cache
            var rc2 = cache.Load(new ChunkPos(0, 0, 0));
            if (rc2 == null || !rc2.Loaded()) { Console.WriteLine("  FAIL: cache round-trip failed"); fail++; }
            else
            {
                var chunk2 = new global::BedrockLevel.Chunk.Chunk(new ChunkPos(0, 0, 0));
                if (!chunk2.LoadFromRawChunk(rc2)) { Console.WriteLine("  FAIL: chunk re-parse from cache failed"); fail++; }
                else if (chunk2.GetBlockName(0, 0, 0) != "minecraft:stone")
                { Console.WriteLine("  FAIL: cached chunk block mismatch"); fail++; }
                else Console.WriteLine("[selftest] cache round-trip OK");
            }

            Console.WriteLine(fail == 0 ? "[selftest] PASS" : $"[selftest] FAIL ({fail})");
            try { Directory.Delete(dir, true); } catch { }
            return fail == 0 ? 0 : 3;
        }

        private static void WriteLevelDat(string path)
        {
            var w = new ByteWriter();
            w.WriteByte((byte)'D'); w.WriteByte((byte)'A'); w.WriteByte((byte)'T'); w.WriteByte((byte)'A');
            w.WriteByte(0); w.WriteByte(0); w.WriteByte(0); w.WriteByte(1); // 8-byte header
            // NBT root compound
            w.WriteByte((byte)NbtTagType.Compound);
            w.WriteNbtString(""); // root name empty
            WriteStringTag(w, "LevelName", "SelfTestWorld");
            WriteIntTag(w, "SpawnX", 12);
            WriteIntTag(w, "SpawnY", 64);
            WriteIntTag(w, "SpawnZ", -8);
            w.WriteByte((byte)NbtTagType.End);
            File.WriteAllBytes(path, w.ToArray());
        }

        private static void WriteStringTag(ByteWriter w, string name, string value)
        {
            w.WriteByte((byte)NbtTagType.String);
            w.WriteNbtString(name);
            w.WriteNbtString(value);
        }

        private static void WriteIntTag(ByteWriter w, string name, int value)
        {
            w.WriteByte((byte)NbtTagType.Int);
            w.WriteNbtString(name);
            w.WriteInt32(value);
        }

        private static byte[] MakeSubChunkValue()
        {
            var w = new ByteWriter();
            w.WriteByte(8);      // version 8
            w.WriteByte(1);      // 1 layer
            w.WriteByte(0);      // layer header: type 0, bits 0 (uniform) -> single palette entry, no length prefix
            // NBT compound: { name: "minecraft:stone" }
            w.WriteByte((byte)NbtTagType.Compound);
            w.WriteNbtString("");
            w.WriteByte((byte)NbtTagType.String);
            w.WriteNbtString("name");
            w.WriteNbtString("minecraft:stone");
            w.WriteByte((byte)NbtTagType.End);
            return w.ToArray();
        }

        private static byte[] MakeInternalKey(ChunkKey.KeyType type, int x, int z, int dim, sbyte yIndex, long seq)
        {
            var ck = new ChunkKey { Type = type, Cp = new ChunkPos(x, z, dim), YIndex = yIndex };
            byte[] user = ck.ToRaw();
            byte[] ik = new byte[user.Length + 8];
            Buffer.BlockCopy(user, 0, ik, 0, user.Length);
            ulong trailer = ((ulong)seq << 8) | 1; // type 1 = value
            for (int i = 0; i < 8; i++) ik[user.Length + i] = (byte)((trailer >> (8 * i)) & 0xFF);
            return ik;
        }

        private static void WriteSst(string path)
        {
            var entries = new List<(byte[], byte[])>();
            byte[] key = MakeInternalKey(ChunkKey.KeyType.SubChunkTerrain, 0, 0, 0, 0, 1);
            byte[] value = MakeSubChunkValue();
            entries.Add((key, value));

            // also a Data3D-style entry with a couple of biomes (optional sanity)
            byte[] key2 = MakeInternalKey(ChunkKey.KeyType.VersionNew, 0, 0, 0, 0, 2);
            byte[] value2 = new byte[] { 0x01, 0, 0, 0 }; // version marker
            entries.Add((key2, value2));

            byte[] dataBlock = BuildBlock(entries);
            byte[] bh = BlockHandleBytes(0, dataBlock.Length);
            byte[] indexBlock = BuildBlock(new List<(byte[], byte[])> { (new byte[] { 0x7E }, bh) });
            var file = new ByteWriter();
            long dataOffset = 0;
            long dataSize = dataBlock.Length;
            long indexOffset = dataOffset + dataSize + 5; // + trailer (comp type + crc)
            long indexSize = indexBlock.Length;
            WriteBlockWithTrailer(file, dataBlock, 0);
            WriteBlockWithTrailer(file, indexBlock, 0);
            file.WriteBytes(BuildFooter(dataOffset, dataSize, indexOffset, indexSize));
            File.WriteAllBytes(path, file.ToArray());
        }

        private static byte[] BlockHandleBytes(long offset, long size)
        {
            var w = new ByteWriter();
            w.WriteBytes(Varint.EncodeUInt32((uint)offset));
            w.WriteBytes(Varint.EncodeUInt32((uint)size));
            return w.ToArray();
        }

        private static byte[] BuildBlock(List<(byte[], byte[])> entries)
        {
            var w = new ByteWriter();
            foreach (var (k, v) in entries)
            {
                w.WriteBytes(Varint.EncodeUInt32(0));
                w.WriteBytes(Varint.EncodeUInt32((uint)k.Length));
                w.WriteBytes(Varint.EncodeUInt32((uint)v.Length));
                w.WriteBytes(k);
                w.WriteBytes(v);
            }
            w.WriteInt32(0); // restart[0]
            w.WriteInt32(1); // num restarts
            return w.ToArray();
        }

        private static void WriteBlockWithTrailer(ByteWriter file, byte[] block, byte compressionType)
        {
            file.WriteBytes(block);
            file.WriteByte(compressionType);      // compression type byte
            for (int i = 0; i < 4; i++) file.WriteByte(0); // 4-byte CRC placeholder
        }

        private static byte[] BuildFooter(long dataOffset, long dataSize, long indexOffset, long indexSize)
        {
            var w = new ByteWriter();
            w.WriteBytes(Varint.EncodeUInt32((uint)dataOffset));
            w.WriteBytes(Varint.EncodeUInt32((uint)dataSize));
            w.WriteBytes(Varint.EncodeUInt32((uint)indexOffset));
            w.WriteBytes(Varint.EncodeUInt32((uint)indexSize));
            while (w.Length < 40) w.WriteByte(0);
            ulong magic = 0xdb4775248b80fb57UL;
            for (int i = 0; i < 8; i++) w.WriteByte((byte)((magic >> (8 * i)) & 0xFF));
            return w.ToArray();
        }
    }
}
