using System;
using System.Collections.Generic;
using System.IO;
using BedrockLevel.Keys;
using BedrockLevel.LevelDb;

namespace BedrockLevel.Level
{
    /// <summary>
    /// Maps each chunk position to ALL .sst/.ldb files that contain entries for it,
    /// along with the highest sequence number seen per file.
    /// During rendering we read files in highest-seq order so that the most recent
    /// entry for each key wins.
    /// </summary>
    public sealed class DbChunkIndex
    {
        private static readonly ChunkPosComparer CpComparer = new();
        // Chunk → list of (filePath, highestSeq) – ALL files containing this chunk,
        // ordered by seq descending so the most recent file is tried first.
        private readonly Dictionary<ChunkPos, List<(string file, long seq)>> map_ = new(CpComparer);
        private readonly HashSet<ChunkPos> allPositions_ = new(CpComparer);

        public int Count => allPositions_.Count;
        public IEnumerable<ChunkPos> ChunkPositions => allPositions_;

        public void Build(string dbDir)
        {
            map_.Clear();
            allPositions_.Clear();

            if (!Directory.Exists(dbDir))
                throw new DirectoryNotFoundException($"LevelDB directory not found: {dbDir}");

            foreach (var file in Directory.EnumerateFiles(dbDir))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".sst" && ext != ".ldb") continue;

                SstFile.Read(file, (internalKey, _) =>
                {
                    if (internalKey.Length <= 8) return;
                    ulong trailer = BitConverter.ToUInt64(internalKey, internalKey.Length - 8);
                    long seq = (long)(trailer >> 8);

                    var userKey = new byte[internalKey.Length - 8];
                    Buffer.BlockCopy(internalKey, 0, userKey, 0, userKey.Length);

                    var ck = ChunkKey.Parse(userKey);
                    if (!ck.Valid()) return;

                    if (!map_.TryGetValue(ck.Cp, out var list))
                    {
                        list = new List<(string, long)>();
                        map_[ck.Cp] = list;
                        allPositions_.Add(ck.Cp);
                    }

                    // Update this file's highest seq (or add file if new).
                    bool found = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (string.Equals(list[i].file, file, StringComparison.OrdinalIgnoreCase))
                        {
                            if (seq > list[i].seq)
                                list[i] = (file, seq);
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                        list.Add((file, seq));
                });
            }

            // Sort each chunk's file list by seq descending so callers try the
            // most recent file first.
            foreach (var kv in map_)
            {
                kv.Value.Sort((a, b) => b.seq.CompareTo(a.seq));
            }
        }

        /// <summary>Returns all files containing this chunk, sorted by descending seq.</summary>
        public bool TryGetFiles(ChunkPos cp, out List<(string file, long seq)> files)
            => map_.TryGetValue(cp, out files);
    }
}
