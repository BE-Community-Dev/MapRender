using System;
using System.Collections.Generic;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Parses a single (decompressed) LevelDB table block into its key/value entries,
    /// handling prefix (restart-point) compression.
    /// </summary>
    public static class Block
    {
        public static void Parse(byte[] content, List<(byte[] Key, byte[] Value)> output)
        {
            if (content.Length < 4) return;
            int numRestarts = BitConverter.ToInt32(content, content.Length - 4);
            if (numRestarts <= 0) return;
            int restartStart = content.Length - 4 - numRestarts * 4;
            if (restartStart < 0) return;

            int pos = 0;
            byte[] prevKey = Array.Empty<byte>();

            while (pos < restartStart)
            {
                int shared = Varint.ReadInt32(content, ref pos);
                int nonShared = Varint.ReadInt32(content, ref pos);
                int valueLen = Varint.ReadInt32(content, ref pos);

                // Defensive bounds checks (use long to avoid int overflow / negative sizes).
                if (shared < 0 || nonShared < 0 || valueLen < 0) break;
                if ((long)pos + nonShared > content.Length) break;
                byte[] keyDelta = new byte[nonShared];
                Buffer.BlockCopy(content, pos, keyDelta, 0, nonShared);
                pos += nonShared;

                if ((long)pos + valueLen > content.Length) break;
                byte[] value = new byte[valueLen];
                Buffer.BlockCopy(content, pos, value, 0, valueLen);
                pos += valueLen;

                int keyLen = shared + nonShared;
                var key = new byte[keyLen];
                if (shared > 0 && shared <= prevKey.Length)
                    Buffer.BlockCopy(prevKey, 0, key, 0, shared);
                Buffer.BlockCopy(keyDelta, 0, key, shared, nonShared);
                prevKey = key;

                output.Add((key, value));
            }
        }
    }
}
