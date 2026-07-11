using System;
using System.Collections.Generic;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Immutable byte-string used as a dictionary key for LevelDB user keys.
    /// </summary>
    public sealed class ByteString : IEquatable<ByteString>
    {
        private readonly byte[] _data;
        private readonly int _hashCode;

        public ByteString(byte[] data)
        {
            _data = data ?? Array.Empty<byte>();
            _hashCode = ComputeHash(_data);
        }

        public ByteString(ReadOnlySpan<byte> data)
        {
            _data = data.ToArray();
            _hashCode = ComputeHash(_data);
        }

        public ReadOnlySpan<byte> Span => _data;

        public int Length => _data.Length;

        public byte[] ToArray() => (byte[])_data.Clone();

        public string ToHex() => BitConverter.ToString(_data).Replace("-", "").ToLowerInvariant();

        public string ToUtf8() => System.Text.Encoding.UTF8.GetString(_data);

        public override bool Equals(object obj) => obj is ByteString other && Equals(other);

        public bool Equals(ByteString other)
        {
            if (other is null) return false;
            if (_data.Length != other._data.Length) return false;
            return _data.AsSpan().SequenceEqual(other._data);
        }

        public override int GetHashCode() => _hashCode;

        private static int ComputeHash(byte[] data)
        {
            unchecked
            {
                int h = 17;
                foreach (byte b in data) h = h * 31 + b;
                return h;
            }
        }
    }
}
