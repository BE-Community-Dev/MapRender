using System;
using System.Collections.Generic;
using BedrockLevel.Keys;

namespace BedrockLevel.Keys
{
    public sealed class ChunkPosComparer : IEqualityComparer<ChunkPos>
    {
        public bool Equals(ChunkPos a, ChunkPos b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            return a.X == b.X && a.Z == b.Z && a.Dim == b.Dim;
        }

        public int GetHashCode(ChunkPos p)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + p.X;
                h = h * 31 + p.Z;
                h = h * 31 + p.Dim;
                return h;
            }
        }
    }
}
