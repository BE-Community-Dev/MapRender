using System;
using System.Collections.Generic;
using System.IO;
using BedrockLevel.Chunk;
using BedrockLevel.Nbt;

namespace BedrockLevel.Level
{
    /// <summary>Mirrors level_dat in level_dat.cpp.</summary>
    public sealed class LevelDat
    {
        private string header_ = string.Empty;
        private CompoundTag root_;
        private string levelName_ = string.Empty;
        private Vec3 spawn_ = new Vec3(0, 0, 0);
        private long worldStartCount_;

        public string LevelName => levelName_;
        public Vec3 Spawn => spawn_;
        public long WorldStartCount => worldStartCount_;
        public CompoundTag Root => root_;

        public bool LoadFromFile(string path)
        {
            if (!File.Exists(path)) return false;
            var data = File.ReadAllBytes(path);
            return LoadFromRawData(data);
        }

        public bool LoadFromRawData(byte[] data)
        {
            if (data == null || data.Length <= 8) return false;
            header_ = System.Text.Encoding.ASCII.GetString(data, 0, 8);
            root_ = NbtReader.ReadOne(data, 8);
            if (root_ == null) return false;
            Preload();
            return true;
        }

        private void Preload()
        {
            var name = root_.Get("LevelName") as StringTag;
            if (name != null) levelName_ = name.Value;

            var x = root_.Get("SpawnX") as IntTag;
            var y = root_.Get("SpawnY") as IntTag;
            var z = root_.Get("SpawnZ") as IntTag;
            if (x != null) spawn_.X = x.Value;
            if (y != null) spawn_.Y = y.Value;
            if (z != null) spawn_.Z = z.Value;

            var wsc = root_.Get("worldStartCount") as LongTag;
            if (wsc != null) worldStartCount_ = wsc.Value;
        }
    }
}
