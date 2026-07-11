using System;
using System.IO;
using System.IO.Compression;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Decompresses LevelDB block contents. Bedrock's LevelDB fork stores a 1-byte
    /// compression id at the end of every block:
    ///   0 = none, 1 = snappy, 2 = zlib (RFC1950), 4 = zlib-raw (RFC1951, no header).
    /// We decompress with the built-in .NET DeflateStream (raw) / ZLibStream (zlib).
    /// No third-party library is used.
    /// </summary>
    public static class ZlibDecompressor
    {
        public static byte[] Decompress(byte[] input, byte compressionType)
        {
            if (compressionType == 0) return input;
            if (input.Length == 0) return input;

            switch (compressionType)
            {
                case 1: // snappy
                    return Snappy.Decompress(input);
                case 2: // zlib (RFC1950)
                    return InflateZlib(input);
                case 4: // zlib raw deflate (RFC1951)
                    return InflateRaw(input);
                default:
                    // Unknown type: try the available decoders, then fall back as-is.
                    try { return InflateRaw(input); }
                    catch (Exception) { /* fall through */ }
                    try { return InflateZlib(input); }
                    catch (Exception) { /* fall through */ }
                    return input;
            }
        }

        private static byte[] InflateRaw(byte[] input)
        {
            using var ms = new MemoryStream(input, false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            return ReadToEnd(ds);
        }

        private static byte[] InflateZlib(byte[] input)
        {
            using var ms = new MemoryStream(input, false);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            return ReadToEnd(zs);
        }

        private static byte[] ReadToEnd(Stream s)
        {
            using var outMs = new MemoryStream();
            s.CopyTo(outMs);
            return outMs.ToArray();
        }
    }
}
