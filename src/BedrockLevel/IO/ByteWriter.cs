using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BedrockLevel.IO
{
    /// <summary>
    /// Little-endian growing byte writer (used for chunk cache serialization).
    /// </summary>
    public sealed class ByteWriter
    {
        private byte[] _buf;
        private int _len;

        public ByteWriter(int capacity = 256)
        {
            _buf = new byte[capacity];
            _len = 0;
        }

        public int Length => _len;

        public byte[] ToArray()
        {
            var outBuf = new byte[_len];
            Buffer.BlockCopy(_buf, 0, outBuf, 0, _len);
            return outBuf;
        }

        public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(_buf, 0, _len);

        private void Ensure(int extra)
        {
            if (_len + extra <= _buf.Length) return;
            int newSize = _buf.Length * 2;
            while (newSize < _len + extra) newSize *= 2;
            Array.Resize(ref _buf, newSize);
        }

        public void WriteByte(byte b)
        {
            Ensure(1);
            _buf[_len++] = b;
        }

        public void WriteSByte(sbyte b) => WriteByte((byte)b);

        public void WriteInt16(short v)
        {
            Ensure(2);
            _buf[_len++] = (byte)(v & 0xFF);
            _buf[_len++] = (byte)((v >> 8) & 0xFF);
        }

        public void WriteInt32(int v)
        {
            Ensure(4);
            _buf[_len++] = (byte)(v & 0xFF);
            _buf[_len++] = (byte)((v >> 8) & 0xFF);
            _buf[_len++] = (byte)((v >> 16) & 0xFF);
            _buf[_len++] = (byte)((v >> 24) & 0xFF);
        }

        public void WriteInt64(long v)
        {
            Ensure(8);
            for (int i = 0; i < 8; i++)
                _buf[_len++] = (byte)((v >> (8 * i)) & 0xFF);
        }

        public void WriteSingle(float v) => WriteInt32(BitConverter.SingleToInt32Bits(v));

        public void WriteDouble(double v) => WriteInt64(BitConverter.DoubleToInt64Bits(v));

        public void WriteBytes(ReadOnlySpan<byte> data)
        {
            Ensure(data.Length);
            data.CopyTo(new Span<byte>(_buf, _len, data.Length));
            _len += data.Length;
        }

        public void WriteBytes(byte[] data) => WriteBytes((ReadOnlySpan<byte>)data);

        public void WriteNbtString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
            if (bytes.Length > 0xFFFF) throw new ArgumentException("string too long for NBT string");
            WriteInt16((short)bytes.Length);
            WriteBytes(bytes);
        }
    }
}
