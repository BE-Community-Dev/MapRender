using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;

namespace BedrockLevel.Cache
{
    /// <summary>
    /// Stores parsed chunks compressed with Brotli at quality 11 (highest ratio, built-in
    /// System.IO.Compression, no third-party library). Backed by an in-memory dictionary
    /// and/or on-disk cache files so the live process does not keep the uncompressed
    /// chunks resident.
    /// </summary>
    public sealed class ChunkCache
    {
        private readonly Dictionary<(int, int, int), byte[]> memory_ = new Dictionary<(int, int, int), byte[]>();
        private readonly string cacheDir_;
        private readonly bool useFile_;

        public long TotalRawBytes { get; private set; }
        public long TotalCompressedBytes { get; private set; }
        public int Count { get; private set; }

        public ChunkCache(string cacheDir = null)
        {
            cacheDir_ = cacheDir;
            useFile_ = !string.IsNullOrEmpty(cacheDir_);
            if (useFile_) Directory.CreateDirectory(cacheDir_);
        }

        public void Store(ChunkPos cp, RawChunk rc)
        {
            byte[] raw = rc.ToRaw();
            byte[] compressed = BrotliCompress(raw);

            memory_[(cp.X, cp.Z, cp.Dim)] = compressed;
            if (useFile_)
                File.WriteAllBytes(CachePath(cp), compressed);

            TotalRawBytes += raw.Length;
            TotalCompressedBytes += compressed.Length;
            Count++;
        }

        public RawChunk Load(ChunkPos cp)
        {
            byte[] compressed = null;
            if (useFile_ && File.Exists(CachePath(cp)))
                compressed = File.ReadAllBytes(CachePath(cp));
            else if (memory_.TryGetValue((cp.X, cp.Z, cp.Dim), out var c))
                compressed = c;

            if (compressed == null) return null;
            byte[] raw = BrotliDecompress(compressed);
            var rc = new RawChunk();
            return rc.FromRaw(raw) ? rc : null;
        }

        public double CompressionRatio => TotalRawBytes == 0 ? 0 : (double)TotalCompressedBytes / TotalRawBytes;

        private string CachePath(ChunkPos cp) => Path.Combine(cacheDir_, $"{cp.X}.{cp.Z}.{cp.Dim}.blkcache");

        public static byte[] BrotliCompress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var bs = new BrotliStream(ms, CompressionLevel.SmallestSize))
            {
                bs.Write(data, 0, data.Length);
            }
            return ms.ToArray();
        }

        public static byte[] BrotliDecompress(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var bs = new BrotliStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            bs.CopyTo(outMs);
            return outMs.ToArray();
        }
    }
}
