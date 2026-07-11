using System;
using System.Collections.Generic;
using System.IO;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Reads a LevelDB SSTable (file) from raw bytes, entirely from the IO level.
    /// Footer (48 bytes) -> index block -> data blocks. Each entry's key is the
    /// internal key; the value is the stored value (already block-decompressed).
    /// </summary>
    public static class SstFile
    {
        private const ulong KTableMagic = 0xdb4775248b80fb57UL;

        public static void Read(string path, Action<byte[] /*internalKey*/, byte[] /*value*/> onEntry)
        {
            byte[] file = File.ReadAllBytes(path);

            if (file.Length < 48) return;

            int footerStart = file.Length - 48;
            int p = footerStart;

            BlockHandle metaindex = BlockHandle.Read(file, ref p);
            BlockHandle index = BlockHandle.Read(file, ref p);

            // verify magic
            ulong magic = BitConverter.ToUInt64(file, footerStart + 40);
            if (magic != KTableMagic) return; // not a valid sstable

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

        private static byte[] ReadBlockBytes(byte[] file, BlockHandle handle)
        {
            long start = handle.Offset;
            long total = handle.Size;
            if (start < 0 || total <= 0 || start + total > file.Length) return Array.Empty<byte>();

            // Canonical LevelDB: BlockHandle.Size covers the compressed payload; the
            // 1-byte compression type (and 4-byte CRC) trail immediately after it.
            byte compType = (start + total < file.Length) ? file[start + total] : (byte)0;
            byte[] raw = new byte[total];
            Buffer.BlockCopy(file, (int)start, raw, 0, (int)total);
            byte[] canonical = ZlibDecompressor.Decompress(raw, compType);
            if (IsValidBlock(canonical)) return canonical;

            // Fallback: older layout where the 1-byte compression type is the LAST byte
            // of the payload (handle.Size includes it, no separate CRC trailer).
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

        /// <summary>Cheap sanity check that a decompressed buffer looks like a real LevelDB block.</summary>
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
