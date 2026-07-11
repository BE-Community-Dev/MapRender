using System;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Raw Snappy decompression (Google Snappy block format, as used by LevelDB
    /// with compression id 1). Implemented from the IO level; no third-party library.
    /// </summary>
    public static class Snappy
    {
        public static byte[] Decompress(byte[] input)
        {
            byte[] buf = new byte[Math.Max(1024, input.Length)];
            int op = 0;
            int ip = 0;
            int n = input.Length;

            while (ip < n)
            {
                int tag = input[ip++];
                int type = tag & 3;

                if (type == 0)
                {
                    // LITERAL
                    int len = tag >> 2;
                    if (len < 60)
                    {
                        len += 1;
                    }
                    else
                    {
                        int extra = len - 59; // 1..4
                        int x = 0;
                        for (int i = 0; i < extra; i++)
                            x |= input[ip++] << (8 * i);
                        len = x + 1;
                    }
                    if (ip + len > n) throw new FormatException("snappy: literal exceeds input");
                    Ensure(ref buf, op, len);
                    Buffer.BlockCopy(input, ip, buf, op, len);
                    op += len;
                    ip += len;
                }
                else
                {
                    // COPY (back reference). See Snappy format_description.txt.
                    int copyLen, copyOffset;
                    if (type == 1)
                    {
                        if (ip >= n) throw new FormatException("snappy: truncated copy");
                        int b1 = input[ip++];
                        copyOffset = ((tag & 0xE0) << 3) | b1;   // 11-bit offset
                        copyLen = ((tag >> 2) & 0x7) + 4;        // 4..11
                    }
                    else if (type == 2)
                    {
                        if (ip + 1 >= n) throw new FormatException("snappy: truncated copy");
                        int b1 = input[ip++];
                        int b2 = input[ip++];
                        copyOffset = b1 | (b2 << 8);             // 16-bit LE
                        copyLen = ((tag >> 2) & 0x3F) + 1;       // 1..64
                    }
                    else // type == 3
                    {
                        if (ip + 3 >= n) throw new FormatException("snappy: truncated copy");
                        int b1 = input[ip++];
                        int b2 = input[ip++];
                        int b3 = input[ip++];
                        int b4 = input[ip++];
                        copyOffset = b1 | (b2 << 8) | (b3 << 16) | (b4 << 24); // 32-bit LE
                        copyLen = ((tag >> 2) & 0x3F) + 1;       // 1..64
                    }

                    if (copyOffset <= 0 || copyOffset > op)
                        throw new FormatException("snappy: invalid copy offset");
                    Ensure(ref buf, op, copyLen);
                    for (int i = 0; i < copyLen; i++)
                        buf[op + i] = buf[op - copyOffset + i];
                    op += copyLen;
                }
            }

            byte[] result = new byte[op];
            Buffer.BlockCopy(buf, 0, result, 0, op);
            return result;
        }

        private static void Ensure(ref byte[] buf, int op, int need)
        {
            if (op + need <= buf.Length) return;
            int ns = buf.Length;
            while (ns < op + need) ns *= 2;
            Array.Resize(ref buf, ns);
        }
    }
}
