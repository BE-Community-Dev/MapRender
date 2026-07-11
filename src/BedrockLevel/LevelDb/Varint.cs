using System;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// LevelDB varint (little-endian base-128, MSB continuation) helpers.
    /// </summary>
    public static class Varint
    {
        public static int ReadInt32(byte[] data, ref int pos)
        {
            long v = (long)ReadUInt64(data, ref pos);
            return (int)v;
        }

        public static uint ReadUInt32(byte[] data, ref int pos)
        {
            return (uint)ReadUInt64(data, ref pos);
        }

        public static long ReadInt64(byte[] data, ref int pos)
        {
            return (long)ReadUInt64(data, ref pos);
        }

        public static ulong ReadUInt64(byte[] data, ref int pos)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                if (pos >= data.Length)
                    throw new FormatException("varint extends past end of buffer");
                byte b = data[pos++];
                if (shift < 64)
                    result |= (ulong)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) break;
            }
            return result;
        }

        /// <summary>Encodes a 32-bit value. Returns number of bytes written into <paramref name="dest"/>.</summary>
        public static int WriteUInt32(byte[] dest, int pos, uint value)
        {
            int n = 0;
            while (true)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                dest[pos + n++] = b;
                if (value == 0) break;
            }
            return n;
        }

        public static byte[] EncodeUInt32(uint value)
        {
            var tmp = new byte[5];
            int n = WriteUInt32(tmp, 0, value);
            var r = new byte[n];
            Buffer.BlockCopy(tmp, 0, r, 0, n);
            return r;
        }

        /// <summary>Reads a signed (zigzag-encoded) 32-bit varint.</summary>
        public static int ReadZigZagInt32(byte[] data, ref int pos)
        {
            uint u = ReadUInt32(data, ref pos);
            return (int)(u >> 1) ^ -(int)(u & 1);
        }

        /// <summary>Encodes a signed 32-bit value as a zigzag varint.</summary>
        public static byte[] EncodeZigZagInt32(int value)
        {
            uint u = (uint)((value << 1) ^ (value >> 31));
            return EncodeUInt32(u);
        }
    }
}
