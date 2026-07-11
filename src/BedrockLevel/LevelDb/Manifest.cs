using System;
using System.Collections.Generic;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Parses LevelDB MANIFEST VersionEdit records to determine the live set of SST files.
    /// Tags follow leveldb's version_edit encoding.
    /// </summary>
    public static class Manifest
    {
        private const int TagComparator = 1;
        private const int TagLogNumber = 2;
        private const int TagNextFileNumber = 3;
        private const int TagLastSequence = 4;
        private const int TagCompactPointer = 5;
        private const int TagDeletedFile = 6;
        private const int TagNewFile = 7;
        private const int TagPrevLogNumber = 8;
        private const int TagNewFile2 = 9;
        private const int TagNewFile3 = 10;
        private const int TagNewFile4 = 11;

        public sealed class Result
        {
            public HashSet<long> AddedFiles { get; } = new HashSet<long>();
            public HashSet<long> DeletedFiles { get; } = new HashSet<long>();
            public long? LogNumber { get; set; }
            public long? LastSequence { get; set; }
            public long? NextFileNumber { get; set; }
            public string Comparator { get; set; }
        }

        public static Result ParseRecord(byte[] record)
        {
            var res = new Result();
            int p = 0;
            try
            {
                while (p < record.Length)
                {
                    int tag = Varint.ReadInt32(record, ref p);
                    switch (tag)
                    {
                        case TagComparator:
                        {
                            int len = (int)Varint.ReadUInt32(record, ref p);
                            res.Comparator = System.Text.Encoding.UTF8.GetString(record, p, len);
                            p += len;
                            break;
                        }
                        case TagLogNumber:
                            res.LogNumber = (long)Varint.ReadUInt64(record, ref p);
                            break;
                        case TagNextFileNumber:
                            res.NextFileNumber = (long)Varint.ReadUInt64(record, ref p);
                            break;
                        case TagLastSequence:
                            res.LastSequence = (long)Varint.ReadUInt64(record, ref p);
                            break;
                        case TagCompactPointer:
                        {
                            Varint.ReadUInt32(record, ref p); // level
                            int klen = (int)Varint.ReadUInt32(record, ref p);
                            p += klen; // internal key
                            break;
                        }
                        case TagDeletedFile:
                        {
                            Varint.ReadUInt32(record, ref p); // level
                            long fileNum = (long)Varint.ReadUInt64(record, ref p);
                            res.DeletedFiles.Add(fileNum);
                            break;
                        }
                        case TagNewFile:
                        {
                            Varint.ReadUInt32(record, ref p); // level
                            long fileNum = (long)Varint.ReadUInt64(record, ref p);
                            SkipBlockHandle(record, ref p);
                            res.AddedFiles.Add(fileNum);
                            break;
                        }
                        case TagNewFile2:
                        case TagNewFile3:
                        case TagNewFile4:
                        {
                            Varint.ReadUInt32(record, ref p); // level
                            long fileNum = (long)Varint.ReadUInt64(record, ref p);
                            SkipBlockHandle(record, ref p);
                            Varint.ReadUInt64(record, ref p); // file size
                            SkipInternalKey(record, ref p);
                            SkipInternalKey(record, ref p);
                            if (tag == TagNewFile3 || tag == TagNewFile4)
                                Varint.ReadUInt64(record, ref p); // path id
                            res.AddedFiles.Add(fileNum);
                            break;
                        }
                        default:
                            // Unknown tag; cannot safely continue parsing this record.
                            p = record.Length;
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // best-effort; ignore malformed tail
            }
            return res;
        }

        private static void SkipBlockHandle(byte[] data, ref int p)
        {
            Varint.ReadUInt64(data, ref p); // offset
            Varint.ReadUInt64(data, ref p); // size
        }

        private static void SkipInternalKey(byte[] data, ref int p)
        {
            int klen = (int)Varint.ReadUInt32(data, ref p);
            p += klen;
        }
    }
}
