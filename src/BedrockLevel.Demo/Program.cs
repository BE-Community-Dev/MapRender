using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BedrockLevel.Cache;
using BedrockLevel.Chunk;
using BedrockLevel.Keys;
using BedrockLevel.Level;

namespace BedrockLevel.Demo
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: BedrockLevel.Demo <path-to-bedrock-save-dir> [cacheDir]");
                Console.WriteLine("       BedrockLevel.Demo --selftest");
                Console.WriteLine("  The save dir must contain level.dat and a db/ sub-directory.");
                return 0;
            }

            if (args[0] == "--selftest")
                return SelfTest.Run();

            string saveDir = args[0];
            string cacheDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "bedrock-chunk-cache");

            if (!Directory.Exists(saveDir))
            {
                Console.Error.WriteLine($"Save directory not found: {saveDir}");
                return 1;
            }

            var level = new global::BedrockLevel.Level.BedrockLevel();
            Console.WriteLine($"Opening save: {saveDir}");
            if (!level.Open(saveDir))
            {
                Console.Error.WriteLine("Failed to open save (level.dat missing or db/ unreadable).");
                return 1;
            }

            Console.WriteLine($"  Level name : {level.Dat.LevelName}");
            Console.WriteLine($"  Spawn      : ({level.Dat.Spawn.X}, {level.Dat.Spawn.Y}, {level.Dat.Spawn.Z})");
            Console.WriteLine($"  DB keys    : {level.Store.KeyCount}");

            var positions = level.ChunkPositions().ToList();
            Console.WriteLine($"  Chunks     : {positions.Count}");

            // Compress + cache every chunk (memory + on-disk file cache, Brotli q11).
            var cache = new ChunkCache(cacheDir);
            var sw = Stopwatch.StartNew();
            int parsedOk = 0;
            foreach (var cp in positions)
            {
                var rc = level.GetRawChunk(cp);
                if (!rc.Loaded()) continue;
                cache.Store(cp, rc);
            }
            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"Cached {cache.Count} raw chunks in {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine($"  Cache dir        : {cacheDir}");
            Console.WriteLine($"  Raw bytes        : {cache.TotalRawBytes:N0}");
            Console.WriteLine($"  Compressed bytes : {cache.TotalCompressedBytes:N0}");
            Console.WriteLine($"  Compression ratio: {cache.CompressionRatio:P2} (lower = smaller)");
            long estMem = GC.GetTotalMemory(false);
            Console.WriteLine($"  Process memory   : {estMem:N0} bytes");

            // Verify round-trip: load one chunk back from the cache and fully parse it.
            if (positions.Count > 0)
            {
                var sample = positions[0];
                var rc2 = cache.Load(sample); // round-trip: chunk reloaded from compressed cache
                var chunk = level.GetChunk(sample);
                if (chunk != null)
                {
                    parsedOk++;
                    Console.WriteLine();
                    Console.WriteLine($"Sample chunk [{sample}]:");
                    Console.WriteLine($"  version        : {chunk.Version}");
                    Console.WriteLine($"  entities       : {chunk.Entities.Count}");
                    Console.WriteLine($"  block entities : {chunk.BlockEntities.Count}");
                    Console.WriteLine($"  top biome      : {chunk.GetTopBiome(0, 0)}");
                    string blockName = chunk.GetBlockName(0, chunk.GetHeight(0, 0), 0);
                    Console.WriteLine($"  surface block  : {blockName}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("Done.");
            return 0;
        }
    }
}
