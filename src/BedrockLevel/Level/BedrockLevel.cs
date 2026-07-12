using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;
using BedrockLevel.LevelDb;

namespace BedrockLevel.Level
{
    public sealed class BedrockLevel
    {
        public const string LevelData = "level.dat";
        public const string LevelDb = "db";

        private DbChunkIndex index_;
        private readonly LevelDat dat_ = new LevelDat();
        private bool isOpen_;
        private string rootName_;

        // Shared raw-file cache: active during a render pass.  Each .sst/.ldb file's
        // raw (compressed) bytes are loaded from disk exactly once and reused by all
        // parallel blocks.  Each thread independently decompresses only the data blocks
        // it needs via ReadTargetKeysFromBytes — no full-file decompression, no
        // entry-dictionary bloat cross-thread.
        private ConcurrentDictionary<string, Lazy<byte[]>> sharedRawCache_;

        public bool IsOpen => isOpen_;
        public string RootPath => rootName_;
        public LevelDat Dat => dat_;
        public int CachedChunkCount => index_?.Count ?? 0;

        public bool Open(string root)
        {
            rootName_ = root;
            string datPath = Path.Combine(root, LevelData);
            isOpen_ = dat_.LoadFromFile(datPath) && LoadDb(root);
            return isOpen_;
        }

        public void Close()
        {
            isOpen_ = false;
            sharedRawCache_ = null;
            index_ = null;
        }

        private bool LoadDb(string root)
        {
            string dbDir = Path.Combine(root, LevelDb);
            if (!Directory.Exists(dbDir)) return false;
            index_ = new DbChunkIndex();
            index_.Build(dbDir);
            return true;
        }

        public IEnumerable<ChunkPos> ChunkPositions(int? dimension = null)
        {
            if (index_ == null) yield break;
            foreach (var cp in index_.ChunkPositions)
            {
                if (dimension.HasValue && cp.Dim != dimension.Value) continue;
                yield return cp;
            }
        }

        /// <summary>Returns the best (highest-seq) source file for a chunk, or null.</summary>
        public string GetChunkFilePath(ChunkPos cp)
        {
            if (index_ == null) return null;
            if (!index_.TryGetFiles(cp, out var files) || files.Count == 0) return null;
            return files[0].file;
        }

        // ----- Shared cache lifecycle -----

        public void BeginSharedLoad()
        {
            sharedRawCache_ = new ConcurrentDictionary<string, Lazy<byte[]>>(
                StringComparer.OrdinalIgnoreCase);
        }

        public void EndSharedLoad()
        {
            sharedRawCache_ = null;
        }

        /// <summary>Returns the raw (compressed) bytes for a file.  When the shared
        /// cache is active, reads the file once on first access.  When inactive,
        /// returns null (caller uses the file-path path).</summary>
        private byte[] GetOrLoadRawBytes(string filePath)
        {
            var cache = sharedRawCache_;
            if (cache == null) return null;
            try
            {
                return cache.GetOrAdd(filePath,
                    _ => new Lazy<byte[]>(() => File.ReadAllBytes(filePath))).Value;
            }
            catch
            {
                return null;
            }
        }

        // ----- Chunk loading -----

        public RawChunk GetRawChunk(ChunkPos cp)
        {
            var rc = new RawChunk(cp);
            if (index_ == null) return rc;
            if (!index_.TryGetFiles(cp, out var files) || files.Count == 0)
                return rc;

            var targetKeys = BuildChunkKeySet(new List<ChunkPos> { cp });

            foreach (var (filePath, _) in files)
            {
                var chunkEntries = SelectiveRead(filePath, targetKeys);
                if (chunkEntries.Count == 0) continue;

                rc.LoadFromLookup(uk =>
                {
                    var bs = new ByteString(uk);
                    return chunkEntries.TryGetValue(bs, out var v) ? v : null;
                });
                if (rc.Loaded())
                    break;
            }
            return rc;
        }

        /// <summary>Batch-loads multiple chunks from the same file.</summary>
        public Dictionary<ChunkPos, RawChunk> GetRawChunksFromFile(string filePath, List<ChunkPos> cps)
        {
            var result = new Dictionary<ChunkPos, RawChunk>();
            foreach (var cp in cps) result[cp] = new RawChunk(cp);
            if (index_ == null || cps.Count == 0 || string.IsNullOrEmpty(filePath))
                return result;

            var allTargetKeys = BuildChunkKeySet(cps);
            if (allTargetKeys.Count == 0) return result;

            var allEntries = SelectiveRead(filePath, allTargetKeys);
            if (allEntries.Count == 0) return result;

            foreach (var cp in cps)
            {
                var rc = result[cp];
                rc.LoadFromLookup(uk =>
                {
                    var bs = new ByteString(uk);
                    return allEntries.TryGetValue(bs, out var v) ? v : null;
                });
            }
            return result;
        }

        public global::BedrockLevel.Chunk.Chunk GetChunk(ChunkPos cp)
        {
            var rc = GetRawChunk(cp);
            if (!rc.Loaded()) return null;
            var c = new global::BedrockLevel.Chunk.Chunk(cp);
            return c.LoadFromRawChunk(rc) ? c : null;
        }

        // ----- Helpers -----

        /// <summary>
        /// Reads entries matching targetKeys from a file.  When the shared raw cache
        /// is active, uses cached raw bytes + selective block decompression.  Otherwise
        /// falls back to ReadTargetKeys (file-path path) with full-file fallback.
        /// </summary>
        private Dictionary<ByteString, byte[]> SelectiveRead(string filePath, HashSet<ByteString> targetKeys)
        {
            var rawBytes = GetOrLoadRawBytes(filePath);
            if (rawBytes != null)
            {
                // Shared cache: selective block decompression from cached raw bytes.
                try
                {
                    return SstFile.ReadTargetKeysFromBytes(rawBytes, targetKeys);
                }
                catch
                {
                    return new Dictionary<ByteString, byte[]>();
                }
            }

            // Non-cached path: selective with file path + full-file fallback.
            return ReadFromFile(filePath, targetKeys);
        }

        /// <summary>Selective block read with fallback to full decompress.</summary>
        private static Dictionary<ByteString, byte[]> ReadFromFile(
            string filePath, HashSet<ByteString> targetKeys)
        {
            Dictionary<ByteString, byte[]> entries = null;
            try
            {
                entries = SstFile.ReadTargetKeys(filePath, targetKeys);
            }
            catch
            {
                entries = new Dictionary<ByteString, byte[]>();
            }

            if (entries.Count == 0)
            {
                var all = new Dictionary<ByteString, byte[]>();
                SstFile.Read(filePath, (ik, v) =>
                {
                    if (ik.Length <= 8) return;
                    var uk = new byte[ik.Length - 8];
                    Buffer.BlockCopy(ik, 0, uk, 0, uk.Length);
                    var bs = new ByteString(uk);
                    if (targetKeys.Contains(bs) && !all.ContainsKey(bs))
                        all[bs] = v;
                });
                entries = all;
            }
            return entries;
        }

        private static HashSet<ByteString> BuildChunkKeySet(List<ChunkPos> cps)
        {
            var keys = new HashSet<ByteString>();
            foreach (var cp in cps)
            {
                foreach (var kt in RawChunk.NormalKeys)
                {
                    var ck = new ChunkKey { Type = kt, Cp = cp };
                    keys.Add(new ByteString(ck.ToRawModern()));
                    keys.Add(new ByteString(ck.ToRawLegacy()));
                }
                for (sbyte y = -4; y <= 19; y++)
                {
                    var ck = new ChunkKey { Type = ChunkKey.KeyType.SubChunkTerrain, Cp = cp, YIndex = y };
                    keys.Add(new ByteString(ck.ToRawModern()));
                    keys.Add(new ByteString(ck.ToRawLegacy()));
                }
                var dk = new ActorDigestKey { Cp = cp };
                keys.Add(new ByteString(dk.ToRawModern()));
                keys.Add(new ByteString(dk.ToRaw()));
            }
            return keys;
        }
    }
}
