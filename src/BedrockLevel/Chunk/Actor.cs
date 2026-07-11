using System;
using System.Collections.Generic;
using BedrockLevel.Nbt;

namespace BedrockLevel.Chunk
{
    public sealed class Vec3
    {
        public float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    /// <summary>Mirrors actor in actor.h / actor.cpp.</summary>
    public sealed class Actor
    {
        private long uid_ = -1;
        private string identifier_ = "minecraft:unknown";
        private Vec3 pos_ = new Vec3(0, 0, 0);
        private CompoundTag root_;

        public long Uid => uid_;
        public string Identifier => identifier_;
        public Vec3 Pos => pos_;
        public CompoundTag Root => root_;

        public bool Load(byte[] data)
        {
            root_ = NbtReader.ReadOne(data);
            if (root_ == null) return false;
            return Preload(root_);
        }

        public bool LoadFromNbt(CompoundTag nbt)
        {
            root_ = nbt;
            return Preload(nbt);
        }

        private bool Preload(CompoundTag root)
        {
            var posTag = root.Get("Pos") as ListTag;
            if (posTag == null || posTag.Value.Count < 3) return false;
            if (!(posTag.Value[0] is FloatTag fx) || !(posTag.Value[1] is FloatTag fy) || !(posTag.Value[2] is FloatTag fz))
                return false;
            pos_ = new Vec3(fx.Value, fy.Value, fz.Value);

            var idTag = root.Get("identifier") as StringTag;
            if (idTag == null) return false;
            identifier_ = idTag.Value;

            var uidTag = root.Get("UniqueID") as LongTag;
            if (uidTag == null) return false;
            uid_ = uidTag.Value;

            return true;
        }

        // ---- storage key helpers (match actor.h) ----

        public static long StorageKey(long uid)
        {
            ulong u = (ulong)uid;
            uint wsc = (uint)(u >> 32);
            uint index = (uint)(u & 0xFFFFFFFF);
            ulong key = ((0xFFFFFFFFFFFFFFFFUL - wsc) << 32) | index;
            return (long)key;
        }

        public static byte[] StorageKeyRaw(long uid)
        {
            long key = StorageKey(uid);
            var b = new byte[8];
            for (int i = 0; i < 8; i++)
                b[i] = (byte)((key >> (56 - i * 8)) & 0xFF);
            return b;
        }
    }
}
