using System;
using System.Collections.Generic;
using System.IO;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;
using BedrockLevel.Level;
using BedrockLevel.LevelDb;

namespace BedrockLevel.Level
{
    /// <summary>Top-level Bedrock save reader. Mirrors bedrock_level in bedrock_level.cpp.</summary>
    public sealed class BedrockLevel
    {
        public const string LevelData = "level.dat";
        public const string LevelDb = "db";

        private readonly LevelDbStore store_ = new LevelDbStore();
        private readonly LevelDat dat_ = new LevelDat();
        private bool isOpen_;
        private string rootName_;

        public bool IsOpen => isOpen_;
        public string RootPath => rootName_;
        public LevelDat Dat => dat_;
        public LevelDbStore Store => store_;
        public int CachedChunkCount { get; private set; }

        public bool Open(string root)
        {
            rootName_ = root;
            string datPath = Path.Combine(root, LevelData);
            isOpen_ = dat_.LoadFromFile(datPath) && LoadDb(root);
            return isOpen_;
        }

        public void Close() => isOpen_ = false;

        private bool LoadDb(string root)
        {
            string dbDir = Path.Combine(root, LevelDb);
            if (!Directory.Exists(dbDir)) return false;
            store_.Open(dbDir);
            return true;
        }

        public bool TryGetRaw(ReadOnlySpan<byte> userKey, out byte[] value) => store_.TryGetValue(userKey, out value);

        /// <summary>
        /// Enumerates every distinct chunk position present in the database.
        /// Pass <paramref name="dimension"/> (0=Overworld, 1=Nether, 2=End) to restrict
        /// to a single dimension.
        /// </summary>
        public IEnumerable<ChunkPos> ChunkPositions(int? dimension = null)
        {
            var seen = new HashSet<ChunkPos>(new ChunkPosComparer());
            foreach (var key in store_.Keys)
            {
                var ck = ChunkKey.Parse(key.Span.ToArray());
                if (!ck.Valid()) continue;
                if (dimension.HasValue && ck.Cp.Dim != dimension.Value) continue;
                if (seen.Add(ck.Cp))
                    yield return ck.Cp;
            }
        }

        public RawChunk GetRawChunk(ChunkPos cp)
        {
            var rc = new RawChunk(cp);
            rc.Load(store_);
            return rc;
        }

        public global::BedrockLevel.Chunk.Chunk GetChunk(ChunkPos cp)
        {
            var rc = GetRawChunk(cp);
            if (!rc.Loaded()) return null;
            var c = new global::BedrockLevel.Chunk.Chunk(cp);
            return c.LoadFromRawChunk(rc) ? c : null;
        }
    }
}
