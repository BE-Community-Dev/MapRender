using System;
using System.Collections.Generic;

namespace BedrockLevel.Keys
{
    public enum ChunkVersion { Old = 0, New = 1 }

    public sealed class ChunkPos
    {
        public int X { get; set; }
        public int Z { get; set; }
        public int Dim { get; set; }

        public ChunkPos() { }
        public ChunkPos(int x, int z, int dim) { X = x; Z = z; Dim = dim; }

        public bool Valid() => Dim >= 0 && Dim <= 2;

        public override string ToString() => $"{X}, {Z}, {Dim}";

        public (int MinY, int MaxY) GetYRange(ChunkVersion v)
        {
            switch (Dim)
            {
                case 1: return (0, 127);
                case 2: return (0, 255);
                case 0: return v == ChunkVersion.New ? (-64, 319) : (0, 255);
                default: return (0, -1);
            }
        }

        public (sbyte Min, sbyte Max) GetSubchunkIndexRange(ChunkVersion v)
        {
            switch (Dim)
            {
                case 1: return (0, 7);
                case 2: return (0, 15);
                case 0: return v == ChunkVersion.New ? ((sbyte)-4, (sbyte)19) : ((sbyte)0, (sbyte)15);
                default: return (0, -1);
            }
        }

        public BlockPos GetMinPos(ChunkVersion v)
        {
            var (y, _) = GetYRange(v);
            return new BlockPos(X * 16, y, Z * 16);
        }

        public BlockPos GetMaxPos(ChunkVersion v)
        {
            var (_, y) = GetYRange(v);
            return new BlockPos(X * 16 + 15, y, Z * 16 + 15);
        }

        public bool IsSlime()
        {
            uint seed = (uint)(X * 0x1f1f1f1f) ^ (uint)Z;
            var mt = new Random((int)seed);
            return mt.Next(10) == 0;
        }

        public static void MapYToSubchunk(int y, out int index, out int offset)
        {
            index = y < 0 ? (y - 15) / 16 : y / 16;
            offset = y % 16;
            if (offset < 0) offset += 16;
        }
    }

    public sealed class BlockPos
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }

        public BlockPos() { }
        public BlockPos(int x, int y, int z) { X = x; Y = y; Z = z; }

        public ChunkPos ToChunkPos()
        {
            int cx = X < 0 ? X - 15 : X;
            int cz = Z < 0 ? Z - 15 : Z;
            return new ChunkPos(cx / 16, cz / 16, -1);
        }

        public ChunkPos InChunkOffset()
        {
            int ox = X % 16;
            int oz = Z % 16;
            if (ox < 0) ox += 16;
            if (oz < 0) oz += 16;
            return new ChunkPos(ox, oz, -1);
        }
    }

    public sealed class ChunkKey
    {
        public enum KeyType
        {
            Unknown = -1,
            Data3D = 43,
            VersionNew = 44,
            Data2D = 45,
            Data2DLegacy = 46,
            SubChunkTerrain = 47,
            LegacyTerrain = 48,
            BlockEntity = 49,
            Entity = 50,
            PendingTicks = 51,
            BlockExtraData = 52,
            BiomeState = 53,
            FinalizedState = 54,
            ConversionData = 55,
            BorderBlocks = 56,
            HardCodedSpawnAreas = 57,
            RandomTicks = 58,
            Checksums = 59,
            GenerationSeed = 60,
            GeneratedPreCavesAndCliffsBlending = 61,
            BlendingBiomeHeight = 62,
            MetaDataHash = 63,
            BlendingData = 64,
            ActorDigestVersion = 65,
            VersionOld = 118
        }

        public KeyType Type { get; set; } = KeyType.Unknown;
        public ChunkPos Cp { get; set; } = new ChunkPos();
        public sbyte YIndex { get; set; }

        public bool Valid() => Cp.Valid() && Type != KeyType.Unknown;

        public static readonly ChunkKey Invalid = new ChunkKey { Type = KeyType.Unknown, Cp = new ChunkPos() };

        public static ChunkKey Parse(byte[] key)
        {
            int sz = key.Length;
            bool legacySized = sz == 9 || sz == 10 || sz == 13 || sz == 14;
            // Legacy (4-byte int) keys use fixed lengths; modern (zigzag varint) keys
            // are variable length. Prefer the format that matches the key size, but
            // fall back to the other so either save layout is accepted.
            if (legacySized)
            {
                if (TryParseLegacy(key, out var lk) && lk.Valid()) return lk;
                if (TryParseModern(key, out var mk) && mk.Valid()) return mk;
            }
            else
            {
                if (TryParseModern(key, out var mk) && mk.Valid()) return mk;
                if (TryParseLegacy(key, out var lk) && lk.Valid()) return lk;
            }
            return Invalid;
        }

        /// <summary>
        /// Legacy layout (pre-1.18 Bedrock): [4B x][4B z][1B type][1B dim] (+1B y for subchunk).
        /// </summary>
        private static bool TryParseLegacy(byte[] key, out ChunkKey result)
        {
            result = Invalid;
            int sz = key.Length;
            if (sz != 9 && sz != 10 && sz != 13 && sz != 14)
                return false;

            int x = ReadInt32(key, 0);
            int z = ReadInt32(key, 4);
            int dim = 0;
            int keyTypeIdx = 8;
            if (sz == 13 || sz == 14)
            {
                dim = ReadInt32(key, 8);
                keyTypeIdx = 12;
            }

            if (dim < 0 || dim > 2) return false;

            var type = (KeyType)key[keyTypeIdx];
            if ((type < KeyType.Data3D || type > KeyType.ActorDigestVersion) && type != KeyType.VersionOld)
                return false;

            sbyte yIndex = 0;
            if (sz == 10 || sz == 14)
            {
                if (type != KeyType.SubChunkTerrain) return false;
                yIndex = (sbyte)key[key.Length - 1];
            }

            result = new ChunkKey { Type = type, Cp = new ChunkPos(x, z, dim), YIndex = yIndex };
            return true;
        }

        /// <summary>
        /// Modern layout (1.18+ Bedrock): zigzag-varint x, zigzag-varint z,
        /// optional zigzag-varint dim (only when != 0), 1B type, optional 1B y (subchunk).
        /// </summary>
        private static bool TryParseModern(byte[] key, out ChunkKey result)
        {
            result = Invalid;
            // Modern chunk keys are short: x(1-5) + z(1-5) + [dim(1-5)] + type(1) + [y(1)] = 4..17 bytes.
            // Keys outside this range (e.g. "VILLAGE_..." text keys) are not chunk keys.
            if (key.Length < 3 || key.Length > 20) return false;
            try
            {
                int p = 0;
                int x = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref p);
                int z = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref p);
                // Bedrock worlds span at most +/-30,000,000 blocks (~+/-1.875M chunks);
                // anything larger is a mis-parse of a non-chunk key.
                if (Math.Abs(x) > 100_000_000 || Math.Abs(z) > 100_000_000) return false;
                int remaining = key.Length - p;
                if (remaining <= 0) return false;

                int typePos;
                int yIndex = 0;
                if (remaining >= 2 && key[p + remaining - 2] == (byte)KeyType.SubChunkTerrain)
                {
                    // subchunk: type is second-to-last, y is last
                    typePos = p + remaining - 2;
                    yIndex = key[p + remaining - 1];
                }
                else
                {
                    typePos = p + remaining - 1;
                }

                var type = (KeyType)key[typePos];
                int dim = 0;
                if (typePos > p)
                {
                    int dp = p;
                    dim = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref dp);
                }
                if (dim < 0 || dim > 2) return false;
                if ((type < KeyType.Data3D || type > KeyType.ActorDigestVersion) && type != KeyType.VersionOld)
                    return false;

                result = new ChunkKey { Type = type, Cp = new ChunkPos(x, z, dim), YIndex = (sbyte)yIndex };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Legacy (4-byte int) on-disk key. Kept for backward compatibility.</summary>
        public byte[] ToRawLegacy()
        {
            int sz = 9;
            if (Type == KeyType.SubChunkTerrain) sz += 1;
            if (Cp.Dim != 0) sz += 4;
            var r = new byte[sz];
            WriteInt32(r, 0, Cp.X);
            WriteInt32(r, 4, Cp.Z);
            if (Cp.Dim != 0)
            {
                WriteInt32(r, 8, Cp.Dim);
                r[12] = (byte)Type;
            }
            else
            {
                r[8] = (byte)Type;
            }
            if (Type == KeyType.SubChunkTerrain)
                r[r.Length - 1] = (byte)YIndex;
            return r;
        }

        /// <summary>Modern (zigzag-varint) on-disk key. Used by current Bedrock saves.</summary>
        public byte[] ToRawModern()
        {
            var bytes = new System.Collections.Generic.List<byte>();
            bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.X));
            bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.Z));
            if (Cp.Dim != 0)
                bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.Dim));
            bytes.Add((byte)Type);
            if (Type == KeyType.SubChunkTerrain)
                bytes.Add((byte)YIndex);
            return bytes.ToArray();
        }

        public byte[] ToRaw() => ToRawLegacy();

        public override string ToString()
        {
            string typeInfo = $"{Type}({(int)Type})";
            string indexInfo = Type == KeyType.SubChunkTerrain ? $"y = {YIndex}" : "";
            return $"[{Cp}] {typeInfo} {indexInfo}";
        }

        private static int ReadInt32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        private static void WriteInt32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }
    }

    public sealed class ActorKey
    {
        public long ActorUid { get; set; } = -1;

        public bool Valid() => ActorUid != -1;

        public static ActorKey Parse(byte[] key)
        {
            var res = new ActorKey();
            if (key.Length != 19) return res;
            // prefix "actorprefix" (11 bytes)
            var prefix = new byte[] { (byte)'a', (byte)'c', (byte)'t', (byte)'o', (byte)'r', (byte)'p', (byte)'r', (byte)'e', (byte)'f', (byte)'i', (byte)'x' };
            for (int i = 0; i < 11; i++) if (key[i] != prefix[i]) return res;
            res.ActorUid = ReadInt64(key, 11);
            return res;
        }

        public byte[] ToRaw()
        {
            var r = new byte[19];
            var prefix = System.Text.Encoding.ASCII.GetBytes("actorprefix");
            Buffer.BlockCopy(prefix, 0, r, 0, 11);
            WriteInt64(r, 11, ActorUid);
            return r;
        }

        private static long ReadInt64(byte[] d, int o)
        {
            long v = 0;
            for (int i = 0; i < 8; i++) v |= (long)d[o + i] << (8 * i);
            return v;
        }
        private static void WriteInt64(byte[] d, int o, long v)
        {
            for (int i = 0; i < 8; i++) d[o + i] = (byte)((v >> (8 * i)) & 0xFF);
        }
    }

    public sealed class ActorDigestKey
    {
        public ChunkPos Cp { get; set; } = new ChunkPos();

        public bool Valid() => Cp.Valid();

        public static ActorDigestKey Parse(byte[] key)
        {
            var res = new ActorDigestKey();
            // prefix "digp" (4 bytes)
            if (key.Length < 4 ||
                key[0] != (byte)'d' || key[1] != (byte)'i' || key[2] != (byte)'g' || key[3] != (byte)'p')
                return res;

            // Legacy: "digp" + [4B x][4B z] [+4B dim]
            if (key.Length == 12 || key.Length == 16)
            {
                res.Cp.X = ReadInt32(key, 4);
                res.Cp.Z = ReadInt32(key, 8);
                if (key.Length == 16)
                    res.Cp.Dim = ReadInt32(key, 12);
                return res;
            }

            // Modern: "digp" + zigzag-varint x + zigzag-varint z [+ zigzag-varint dim]
            try
            {
                int p = 4;
                res.Cp.X = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref p);
                res.Cp.Z = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref p);
                if (p < key.Length)
                    res.Cp.Dim = BedrockLevel.LevelDb.Varint.ReadZigZagInt32(key, ref p);
                return res;
            }
            catch
            {
                return res;
            }
        }

        public byte[] ToRaw()
        {
            int sz = Cp.Dim != 0 ? 12 : 8;
            var r = new byte[sz + 4];
            var prefix = System.Text.Encoding.ASCII.GetBytes("digp");
            Buffer.BlockCopy(prefix, 0, r, 0, 4);
            WriteInt32(r, 4, Cp.X);
            WriteInt32(r, 8, Cp.Z);
            if (Cp.Dim != 0)
                WriteInt32(r, 12, Cp.Dim);
            return r;
        }

        public byte[] ToRawModern()
        {
            var bytes = new System.Collections.Generic.List<byte>(System.Text.Encoding.ASCII.GetBytes("digp"));
            bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.X));
            bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.Z));
            if (Cp.Dim != 0)
                bytes.AddRange(BedrockLevel.LevelDb.Varint.EncodeZigZagInt32(Cp.Dim));
            return bytes.ToArray();
        }

        private static int ReadInt32(byte[] d, int o) => d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24);
        private static void WriteInt32(byte[] d, int o, int v)
        {
            d[o] = (byte)(v & 0xFF);
            d[o + 1] = (byte)((v >> 8) & 0xFF);
            d[o + 2] = (byte)((v >> 16) & 0xFF);
            d[o + 3] = (byte)((v >> 24) & 0xFF);
        }
    }

    public sealed class VillageKey
    {
        public enum VType { Info, Dwellers, Players, Poi, Unknown }

        public string Uuid { get; set; } = string.Empty;
        public int Dim { get; set; }
        public VType Type { get; set; } = VType.Unknown;

        public bool Valid() => Uuid.Length == 36 && Type != VType.Unknown;

        public static VillageKey Parse(string key)
        {
            var res = new VillageKey();
            var tks = key.Split('_');
            if (tks.Length != 3 && tks.Length != 4) return res;
            if (tks[0] != "VILLAGE") return res;
            string uuid = tks[tks.Length - 2];
            if (uuid.Length != 36) return res;
            res.Uuid = uuid;
            string typeStr = tks[tks.Length - 1];
            switch (typeStr)
            {
                case "DWELLERS": res.Type = VType.Dwellers; break;
                case "INFO": res.Type = VType.Info; break;
                case "PLAYERS": res.Type = VType.Players; break;
                case "POI": res.Type = VType.Poi; break;
                default: res.Type = VType.Unknown; break;
            }
            if (tks.Length == 4)
            {
                switch (tks[1])
                {
                    case "Overworld": res.Dim = 0; break;
                    case "Nether": res.Dim = 1; break;
                    case "TheEnd": res.Dim = 2; break;
                }
            }
            return res;
        }
    }
}
