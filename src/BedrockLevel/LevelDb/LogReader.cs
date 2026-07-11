using System;
using System.Collections.Generic;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Reads LevelDB log files (32KB-block framed records). Used for both the WAL
    /// (records are WriteBatches) and the MANIFEST (records are VersionEdits).
    /// Physical record types: 0=zero(padding) 1=full 2=first 3=middle 4=last.
    /// </summary>
    public static class LogReader
    {
        private const int BlockSize = 32768;

        public static void ReadLogicalRecords(byte[] file, Action<byte[]> onRecord)
        {
            int offset = 0;
            int leftover = 0;
            byte[] fragment = null;

            while (offset < file.Length)
            {
                if (leftover == 0)
                    leftover = Math.Min(BlockSize, file.Length - offset);
                if (leftover < 7) break;

                int length = BitConverter.ToUInt16(file, offset + 4);
                byte type = file[offset + 6];

                if (7 + length > leftover)
                {
                    // record spilled past the block boundary -> skip padding to next block
                    offset += leftover;
                    leftover = 0;
                    fragment = null;
                    continue;
                }

                byte[] data = new byte[length];
                Buffer.BlockCopy(file, offset + 7, data, 0, length);
                offset += 7 + length;
                leftover -= 7 + length;

                switch (type)
                {
                    case 0: // ZERO_TYPE (padding)
                        break;
                    case 1: // FULL
                        onRecord(data);
                        break;
                    case 2: // FIRST
                        fragment = data;
                        break;
                    case 3: // MIDDLE
                        fragment = Append(fragment, data);
                        break;
                    case 4: // LAST
                        if (fragment != null)
                        {
                            var full = Append(fragment, data);
                            onRecord(full);
                        }
                        fragment = null;
                        break;
                }
            }
        }

        private static byte[] Append(byte[] a, byte[] b)
        {
            if (a == null) return (byte[])b.Clone();
            var r = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, r, 0, a.Length);
            Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }

    /// <summary>Parses a LevelDB WriteBatch (WAL record payload).</summary>
    public static class WriteBatch
    {
        public const byte TypeDeletion = 0;
        public const byte TypeValue = 1;

        public static void Parse(byte[] batch, Action<byte[] /*userKey*/, byte /*valueType*/, long /*seq*/, byte[] /*value*/> onEntry)
        {
            int p = 0;
            long seq = (long)Varint.ReadUInt64(batch, ref p);
            uint count = Varint.ReadUInt32(batch, ref p);
            for (uint i = 0; i < count; i++)
            {
                if (p >= batch.Length) break;
                byte tag = batch[p++];
                int keyLen = (int)Varint.ReadUInt32(batch, ref p);
                if (p + keyLen > batch.Length) break;
                byte[] key = new byte[keyLen];
                Buffer.BlockCopy(batch, p, key, 0, keyLen);
                p += keyLen;
                int valLen = (int)Varint.ReadUInt32(batch, ref p);
                if (p + valLen > batch.Length) break;
                byte[] value = new byte[valLen];
                Buffer.BlockCopy(batch, p, value, 0, valLen);
                p += valLen;
                onEntry(key, tag, seq + i, value);
            }
        }
    }
}
