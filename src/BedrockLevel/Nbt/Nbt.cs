using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BedrockLevel.IO;

namespace BedrockLevel.Nbt
{
    public enum NbtTagType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12
    }

    public abstract class NbtTag
    {
        public string Name { get; set; } = string.Empty;

        public abstract NbtTagType TagType { get; }

        public abstract void Write(ByteWriter w);

        public CompoundTag GetCompound() => this as CompoundTag;
        public ListTag GetList() => this as ListTag;
        public string GetString() => (this as StringTag)?.Value ?? string.Empty;

        /// <summary>Reads a single tag (type byte + name + value). Returns null on TAG_End.</summary>
        public static NbtTag Read(ByteReader r)
        {
            NbtTagType type = (NbtTagType)r.ReadByte();
            if (type == NbtTagType.End) return null;
            string name = r.ReadNbtString();
            return ReadValue(r, type, name);
        }

        /// <summary>Reads a root compound (level.dat style: type/name/value).</summary>
        public static CompoundTag ReadRoot(ByteReader r)
        {
            var tag = Read(r);
            if (tag is CompoundTag c) return c;
            return null;
        }

        public static NbtTag ReadValue(ByteReader r, NbtTagType type, string name)
        {
            switch (type)
            {
                case NbtTagType.Byte:
                {
                    var t = new ByteTag { Name = name, Value = r.ReadSByte() };
                    return t;
                }
                case NbtTagType.Short:
                    return new ShortTag { Name = name, Value = r.ReadInt16() };
                case NbtTagType.Int:
                    return new IntTag { Name = name, Value = r.ReadInt32() };
                case NbtTagType.Long:
                    return new LongTag { Name = name, Value = r.ReadInt64() };
                case NbtTagType.Float:
                    return new FloatTag { Name = name, Value = r.ReadSingle() };
                case NbtTagType.Double:
                    return new DoubleTag { Name = name, Value = r.ReadDouble() };
                case NbtTagType.String:
                    return new StringTag { Name = name, Value = r.ReadNbtString() };
                case NbtTagType.ByteArray:
                {
                    int len = r.ReadInt32();
                    if (len < 0) throw new FormatException("negative ByteArray length");
                    var buf = r.ReadBytes(len);
                    var arr = new sbyte[len];
                    Buffer.BlockCopy(buf, 0, arr, 0, len);
                    return new ByteArrayTag { Name = name, Value = arr };
                }
                case NbtTagType.IntArray:
                {
                    int len = r.ReadInt32();
                    if (len < 0) throw new FormatException("negative IntArray length");
                    var arr = new int[len];
                    for (int i = 0; i < len; i++) arr[i] = r.ReadInt32();
                    return new IntArrayTag { Name = name, Value = arr };
                }
                case NbtTagType.LongArray:
                {
                    int len = r.ReadInt32();
                    if (len < 0) throw new FormatException("negative LongArray length");
                    var arr = new long[len];
                    for (int i = 0; i < len; i++) arr[i] = r.ReadInt64();
                    return new LongArrayTag { Name = name, Value = arr };
                }
                case NbtTagType.Compound:
                    return ReadCompound(r, name);
                case NbtTagType.List:
                    return ReadList(r, name);
                default:
                    throw new NotSupportedException($"unsupported NBT tag type {(int)type}");
            }
        }

        private static CompoundTag ReadCompound(ByteReader r, string name)
        {
            var tag = new CompoundTag { Name = name };
            while (true)
            {
                NbtTag child;
                try { child = Read(r); }
                catch (EndOfStreamException) { break; }
                if (child == null) break; // TAG_End
                if (!string.IsNullOrEmpty(child.Name))
                    tag.Value[child.Name] = child;
                else
                    tag.Value[$"#{tag.Value.Count}"] = child;
            }
            return tag;
        }

        private static ListTag ReadList(ByteReader r, string name)
        {
            var tag = new ListTag { Name = name };
            NbtTagType childType = (NbtTagType)r.ReadByte();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                if (childType == NbtTagType.End) break;
                var child = ReadValue(r, childType, string.Empty);
                tag.Value.Add(child);
            }
            return tag;
        }
    }

    public sealed class ByteTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Byte;
        public sbyte Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteSByte(Value);
        }
    }

    public sealed class ShortTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Short;
        public short Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt16(Value);
        }
    }

    public sealed class IntTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Int;
        public int Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt32(Value);
        }
    }

    public sealed class LongTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Long;
        public long Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt64(Value);
        }
    }

    public sealed class FloatTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Float;
        public float Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteSingle(Value);
        }
    }

    public sealed class DoubleTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Double;
        public double Value { get; set; }
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteDouble(Value);
        }
    }

    public sealed class StringTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.String;
        public string Value { get; set; } = string.Empty;
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteNbtString(Value);
        }
    }

    public sealed class ByteArrayTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.ByteArray;
        public sbyte[] Value { get; set; } = Array.Empty<sbyte>();
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt32(Value.Length);
            foreach (var b in Value) w.WriteSByte(b);
        }
    }

    public sealed class IntArrayTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.IntArray;
        public int[] Value { get; set; } = Array.Empty<int>();
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt32(Value.Length);
            foreach (var v in Value) w.WriteInt32(v);
        }
    }

    public sealed class LongArrayTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.LongArray;
        public long[] Value { get; set; } = Array.Empty<long>();
        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            w.WriteInt32(Value.Length);
            foreach (var v in Value) w.WriteInt64(v);
        }
    }

    public sealed class CompoundTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.Compound;
        public Dictionary<string, NbtTag> Value { get; set; } = new Dictionary<string, NbtTag>();

        public NbtTag Get(string key)
        {
            Value.TryGetValue(key, out var t);
            return t;
        }

        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            foreach (var kv in Value) kv.Value.Write(w);
            w.WriteByte((byte)NbtTagType.End);
        }

        public byte[] ToRaw() { var bw = new ByteWriter(); Write(bw); return bw.ToArray(); }
    }

    public sealed class ListTag : NbtTag
    {
        public override NbtTagType TagType => NbtTagType.List;
        public List<NbtTag> Value { get; set; } = new List<NbtTag>();

        public NbtTagType ChildType
        {
            get
            {
                if (Value.Count == 0) return NbtTagType.End;
                return Value[0].TagType;
            }
        }

        public override void Write(ByteWriter w)
        {
            w.WriteByte((byte)TagType);
            w.WriteNbtString(Name);
            var ct = ChildType;
            w.WriteByte((byte)ct);
            w.WriteInt32(Value.Count);
            foreach (var t in Value) t.Write(w);
        }
    }

    public static class NbtReader
    {
        /// <summary>Reads consecutive NBT compounds from a byte stream (used for block entities / actors / pending ticks).</summary>
        public static List<CompoundTag> ReadPaletteToEnd(byte[] data)
        {
            var result = new List<CompoundTag>();
            var r = new ByteReader(data);
            while (!r.IsEof)
            {
                try
                {
                    var tag = NbtTag.Read(r);
                    if (tag is CompoundTag c) result.Add(c);
                    else if (tag == null) break;
                }
                catch (EndOfStreamException) { break; }
                catch (FormatException) { break; }
            }
            return result;
        }

        public static CompoundTag ReadOne(byte[] data, int offset = 0)
        {
            var r = new ByteReader(data, offset);
            return NbtTag.ReadRoot(r);
        }

        /// <summary>Reads a single compound starting at <paramref name="offset"/> and reports how many bytes it consumed.</summary>
        public static CompoundTag ReadOne(byte[] data, int offset, out int consumed)
        {
            var r = new ByteReader(data, offset);
            var tag = NbtTag.ReadRoot(r);
            consumed = r.Position - offset;
            return tag;
        }
    }
}
