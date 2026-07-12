using System;
using System.Collections.Generic;
using System.IO;

namespace BedrockLevel.LevelDb
{
    public static class SstFile
    {
        private const ulong KTableMagic = 0xdb4775248b80fb57UL;

        /// <summary>Iterates every entry in the file (decompresses ALL data blocks).</summary>
        public static void Read(string path, Action<byte[], byte[]> onEntry)
        {
            byte[] file = File.ReadAllBytes(path);
            if (file.Length < 48) return;

            int footerStart = file.Length - 48;
            int p = footerStart;
            BlockHandle.Read(file, ref p); // metaindex (unused)
            BlockHandle index = BlockHandle.Read(file, ref p);
            if (BitConverter.ToUInt64(file, footerStart + 40) != KTableMagic) return;

            byte[] indexContent = ReadBlockBytes(file, index);
            var indexEntries = new List<(byte[], byte[])>();
            Block.Parse(indexContent, indexEntries);

            var dataEntries = new List<(byte[], byte[])>();
            foreach (var (_, value) in indexEntries)
            {
                BlockHandle bh = BlockHandle.Parse(value);
                byte[] dataContent = ReadBlockBytes(file, bh);
                dataEntries.Clear();
                Block.Parse(dataContent, dataEntries);
                foreach (var (k, v) in dataEntries)
                    onEntry(k, v);
            }
        }

        /// <summary>
        /// Reads only the data blocks whose key ranges overlap any of the target keys,
        /// decompresses them, and returns a dictionary of matching user-key → value.
        /// Avoids decompressing 90%+ of the file when only a few chunks are needed.
        /// </summary>
        public static Dictionary<ByteString, byte[]> ReadTargetKeys(string path, HashSet<ByteString> targetKeys)
        {
            byte[] file = File.ReadAllBytes(path);
            return ReadTargetKeysFromBytes(file, targetKeys);
        }

        /// <summary>Same as ReadTargetKeys but operates on an already-loaded byte[].</summary>
        public static Dictionary<ByteString, byte[]> ReadTargetKeysFromBytes(byte[] file, HashSet<ByteString> targetKeys)
        {
            var result = new Dictionary<ByteString, byte[]>();
            if (targetKeys == null || targetKeys.Count == 0 || file == null || file.Length < 48)
                return result;

            int footerStart = file.Length - 48;
            int p = footerStart;
            BlockHandle.Read(file, ref p); // metaindex (unused)
            BlockHandle index = BlockHandle.Read(file, ref p);
            if (BitConverter.ToUInt64(file, footerStart + 40) != KTableMagic) return result;

            // Read and parse the index block → (lastKey, blockHandleValue) list
            byte[] indexContent = ReadBlockBytes(file, index);
            var rawIdx = new List<(byte[], byte[])>();
            Block.Parse(indexContent, rawIdx);
            if (rawIdx.Count == 0) return result;

            // Build (userKey, BlockHandle) for binary search.
            var idxKeys = new List<(byte[] userKey, BlockHandle bh)>(rawIdx.Count);
            foreach (var (ik, handleBytes) in rawIdx)
                idxKeys.Add((ExtractUserKey(ik), BlockHandle.Parse(handleBytes)));

            // Sort target keys for deterministic traversal (already unique from HashSet).
            var sortedTargets = new List<byte[]>(targetKeys.Count);
            foreach (var bs in targetKeys) sortedTargets.Add(bs.ToArray());
            sortedTargets.Sort(CompareByteArrays);

            // Find unique blocks containing any target key.
            var neededBlocks = new HashSet<(long offset, long size)>();
            foreach (var tk in sortedTargets)
            {
                int bi = LowerBound(idxKeys, tk);
                if (bi >= 0)
                {
                    var bh = idxKeys[bi].bh;
                    neededBlocks.Add((bh.Offset, bh.Size));
                }
            }

            // Read, decompress, and filter only those blocks.
            var scratch = new List<(byte[], byte[])>();
            foreach (var (off, sz) in neededBlocks)
            {
                byte[] dataContent = ReadBlockBytes(file, new BlockHandle(off, sz));
                scratch.Clear();
                Block.Parse(dataContent, scratch);
                foreach (var (ik, val) in scratch)
                {
                    var uk = ExtractUserKey(ik);
                    var bs = new ByteString(uk);
                    if (targetKeys.Contains(bs) && !result.ContainsKey(bs))
                        result[bs] = val;
                }
            }

            return result;
        }

        // ----- binary search helpers -----

        /// <summary>Returns the first index where idxKeys[i].userKey >= targetKey, or -1.</summary>
        private static int LowerBound(List<(byte[] userKey, BlockHandle bh)> list, byte[] target)
        {
            int lo = 0, hi = list.Count - 1;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                int c = CompareByteArrays(list[mid].userKey, target);
                if (c < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return lo < list.Count ? lo : -1;
        }

        private static int CompareByteArrays(byte[] a, byte[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static byte[] ExtractUserKey(byte[] internalKey)
        {
            if (internalKey.Length <= 8) return internalKey;
            var uk = new byte[internalKey.Length - 8];
            Buffer.BlockCopy(internalKey, 0, uk, 0, uk.Length);
            return uk;
        }

        // ----- block reading (shared with Read / ReadTargetKeys) -----

        internal static byte[] ReadBlockBytes(byte[] file, BlockHandle handle)
        {
            long start = handle.Offset;
            long total = handle.Size;
            if (start < 0 || total <= 0 || start + total > file.Length) return Array.Empty<byte>();

            byte compType = (start + total < file.Length) ? file[start + total] : (byte)0;
            byte[] raw = new byte[total];
            Buffer.BlockCopy(file, (int)start, raw, 0, (int)total);
            byte[] canonical = ZlibDecompressor.Decompress(raw, compType);
            if (IsValidBlock(canonical)) return canonical;

            if (total >= 2)
            {
                byte altType = raw[(int)total - 1];
                byte[] alt = new byte[(int)total - 1];
                Buffer.BlockCopy(raw, 0, alt, 0, alt.Length);
                byte[] fb = ZlibDecompressor.Decompress(alt, altType);
                if (IsValidBlock(fb)) return fb;
            }
            return canonical;
        }

        private static bool IsValidBlock(byte[] b)
        {
            if (b == null || b.Length < 4) return false;
            int numRestarts = BitConverter.ToInt32(b, b.Length - 4);
            if (numRestarts <= 0) return false;
            long restartStart = (long)b.Length - 4 - (long)numRestarts * 4;
            return restartStart >= 0 && restartStart <= b.Length - 4;
        }
    }
}
