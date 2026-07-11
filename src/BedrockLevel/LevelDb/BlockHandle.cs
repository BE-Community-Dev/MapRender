using System;

namespace BedrockLevel.LevelDb
{
    /// <summary>LevelDB block handle: offset + size, varint encoded.</summary>
    public readonly struct BlockHandle
    {
        public long Offset { get; }
        public long Size { get; }

        public BlockHandle(long offset, long size)
        {
            Offset = offset;
            Size = size;
        }

        public static BlockHandle Read(byte[] data, ref int pos)
        {
            ulong offset = Varint.ReadUInt64(data, ref pos);
            ulong size = Varint.ReadUInt64(data, ref pos);
            return new BlockHandle((long)offset, (long)size);
        }

        /// <summary>Decodes a BlockHandle stored as a value (used in index / version-edit blocks).</summary>
        public static BlockHandle Parse(byte[] value)
        {
            int p = 0;
            return Read(value, ref p);
        }
    }
}
