using System;
using System.Collections.Generic;
using System.IO;

namespace BedrockLevel.LevelDb
{
    /// <summary>
    /// Read-only LevelDB store built entirely from the IO level (no third-party library).
    /// Opens a Bedrock "db" directory by reading CURRENT -> MANIFEST (SST live set) and
    /// every *.sst / *.ldb file plus the WAL *.log files, merging entries by
    /// (user key, sequence, type). Deletions (type 0) hide the key.
    /// </summary>
    public sealed class LevelDbStore
    {
        private readonly Dictionary<ByteString, Entry> _merged = new Dictionary<ByteString, Entry>();
        private readonly Dictionary<ByteString, byte[]> _values = new Dictionary<ByteString, byte[]>();

        public int KeyCount => _values.Count;

        public long? LastSequence { get; private set; }

        private struct Entry
        {
            public long Seq;
            public byte Type; // 0 = deletion, 1 = value
            public byte[] Value;
        }

        public void Open(string dbDirectory)
        {
            _merged.Clear();
            _values.Clear();
            LastSequence = null;

            if (!Directory.Exists(dbDirectory))
                throw new DirectoryNotFoundException($"LevelDB directory not found: {dbDirectory}");

            string manifestPath = ResolveManifest(dbDirectory);

            // Parse MANIFEST for the live SST set and last sequence (informational; we
            // still read every on-disk sst for safety, unioned with the manifest set).
            var liveFiles = new HashSet<long>();
            if (manifestPath != null && File.Exists(manifestPath))
            {
                var mf = File.ReadAllBytes(manifestPath);
                LogReader.ReadLogicalRecords(mf, rec =>
                {
                    var r = Manifest.ParseRecord(rec);
                    foreach (var f in r.AddedFiles) liveFiles.Add(f);
                    foreach (var f in r.DeletedFiles) liveFiles.Remove(f);
                    if (r.LastSequence.HasValue) LastSequence = r.LastSequence;
                });
            }

            // Read every SST / LDB file.
            foreach (var file in Directory.EnumerateFiles(dbDirectory))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".sst" && ext != ".ldb") continue;
                SstFile.Read(file, (internalKey, value) => MergeInternalKey(internalKey, value));
            }

            // Read every WAL log except the manifest file.
            foreach (var file in Directory.EnumerateFiles(dbDirectory, "*.log"))
            {
                if (manifestPath != null && string.Equals(file, manifestPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                var data = File.ReadAllBytes(file);
                LogReader.ReadLogicalRecords(data, rec =>
                    WriteBatch.Parse(rec, (key, type, seq, value) => MergeUserKey(key, type, seq, value)));
            }

            // Build the visible value map (drop deletions).
            foreach (var kv in _merged)
            {
                if (kv.Value.Type != 0)
                    _values[kv.Key] = kv.Value.Value;
            }
        }

        private void MergeInternalKey(byte[] internalKey, byte[] value)
        {
            if (internalKey.Length < 8) return;
            ulong trailer = ReadTrailer(internalKey, internalKey.Length - 8);
            long seq = (long)(trailer >> 8);
            byte type = (byte)(trailer & 0xFF);
            var userKey = new byte[internalKey.Length - 8];
            Buffer.BlockCopy(internalKey, 0, userKey, 0, userKey.Length);
            Merge(new ByteString(userKey), type, seq, value);
        }

        private void MergeUserKey(byte[] userKey, byte type, long seq, byte[] value)
        {
            Merge(new ByteString(userKey), type, seq, value);
        }

        private void Merge(ByteString key, byte type, long seq, byte[] value)
        {
            if (_merged.TryGetValue(key, out var existing))
            {
                if (seq > existing.Seq)
                    _merged[key] = new Entry { Seq = seq, Type = type, Value = value };
            }
            else
            {
                _merged[key] = new Entry { Seq = seq, Type = type, Value = value };
            }
        }

        public bool TryGetValue(ReadOnlySpan<byte> userKey, out byte[] value)
        {
            var bs = new ByteString(userKey);
            return _values.TryGetValue(bs, out value);
        }

        public IEnumerable<ByteString> Keys => _values.Keys;

        private static string ResolveManifest(string dbDirectory)
        {
            string currentPath = Path.Combine(dbDirectory, "CURRENT");
            if (!File.Exists(currentPath)) return null;
            string name = File.ReadAllText(currentPath).Trim();
            // CURRENT may contain a trailing newline or comment lines.
            foreach (var line in name.Split('\n'))
            {
                var l = line.Trim();
                if (l.Length > 0 && !l.StartsWith("#") && l.StartsWith("MANIFEST"))
                    return Path.Combine(dbDirectory, l);
            }
            return null;
        }

        private static ulong ReadTrailer(byte[] data, int offset)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++)
                v |= (ulong)data[offset + i] << (8 * i);
            return v;
        }
    }
}
