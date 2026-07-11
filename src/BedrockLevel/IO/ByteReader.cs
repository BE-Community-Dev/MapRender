using System;
using System.IO;
using System.Text;

namespace BedrockLevel.IO
{
    /// <summary>
    /// Little-endian binary reader over a byte buffer with an explicit cursor.
    /// Bedrock NBT and LevelDB chunk keys are little-endian.
    /// </summary>
    public sealed class ByteReader
    {
        private readonly byte[] _data;
        private int _pos;

        public ByteReader(byte[] data) : this(data, 0) { }

        public ByteReader(byte[] data, int offset)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _pos = offset;
        }

        public int Position
        {
            get => _pos;
            set => _pos = value;
        }

        public int Length => _data.Length;

        public int Remaining => _data.Length - _pos;

        public byte[] Data => _data;

        public bool IsEof => _pos >= _data.Length;

        public byte ReadByte()
        {
            Ensure(1);
            return _data[_pos++];
        }

        public sbyte ReadSByte()
        {
            Ensure(1);
            return (sbyte)_data[_pos++];
        }

        public bool TryReadByte(out byte value)
        {
            if (_pos >= _data.Length) { value = 0; return false; }
            value = _data[_pos++];
            return true;
        }

        public short ReadInt16()
        {
            Ensure(2);
            short v = (short)(_data[_pos] | (_data[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public ushort ReadUInt16()
        {
            Ensure(2);
            ushort v = (ushort)(_data[_pos] | (_data[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public int ReadInt32()
        {
            Ensure(4);
            int v = _data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24);
            _pos += 4;
            return v;
        }

        public uint ReadUInt32()
        {
            Ensure(4);
            uint v = (uint)(_data[_pos] | (_data[_pos + 1] << 8) | (_data[_pos + 2] << 16) | (_data[_pos + 3] << 24));
            _pos += 4;
            return v;
        }

        public long ReadInt64()
        {
            Ensure(8);
            long v = 0;
            for (int i = 0; i < 8; i++)
                v |= (long)_data[_pos + i] << (8 * i);
            _pos += 8;
            return v;
        }

        public ulong ReadUInt64()
        {
            Ensure(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++)
                v |= (ulong)_data[_pos + i] << (8 * i);
            _pos += 8;
            return v;
        }

        public float ReadSingle()
        {
            var v = ReadInt32();
            return BitConverter.Int32BitsToSingle(v);
        }

        public double ReadDouble()
        {
            var v = ReadInt64();
            return BitConverter.Int64BitsToDouble(v);
        }

        public byte[] ReadBytes(int count)
        {
            Ensure(count);
            var buf = new byte[count];
            Buffer.BlockCopy(_data, _pos, buf, 0, count);
            _pos += count;
            return buf;
        }

        public void ReadBytes(byte[] dest, int destOffset, int count)
        {
            Ensure(count);
            Buffer.BlockCopy(_data, _pos, dest, destOffset, count);
            _pos += count;
        }

        /// <summary>Bedrock NBT strings are length-prefixed UTF-8 (uint16 length).</summary>
        public string ReadNbtString()
        {
            ushort len = ReadUInt16();
            if (len == 0) return string.Empty;
            Ensure(len);
            string s = Encoding.UTF8.GetString(_data, _pos, len);
            _pos += len;
            return s;
        }

        private void Ensure(int n)
        {
            if (_pos + n > _data.Length)
                throw new EndOfStreamException($"ByteReader out of bounds (need {n}, have {_data.Length - _pos}).");
        }
    }
}
