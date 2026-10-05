using System.Collections.Generic;
using UnityEngine;

namespace GunAndParry
{
    /// <summary>
    /// Allocation-free spatial hash on the XZ plane. Rebuilt every frame from scratch (O(n)).
    /// Items are referenced by insertion index.
    /// </summary>
    public sealed class SpatialGrid
    {
        const int BucketCount = 4096; // power of two
        readonly float cellSize;
        readonly int[] heads = new int[BucketCount];
        int[] next = new int[512];
        int[] stamp = new int[512];
        Vector2[] positions = new Vector2[512];
        int count, queryId;

        public SpatialGrid(float cellSize) { this.cellSize = cellSize; Clear(); }

        public void Clear()
        {
            for (int i = 0; i < BucketCount; i++) heads[i] = -1;
            count = 0;
        }

        public int Add(Vector2 pos)
        {
            if (count == next.Length)
            {
                System.Array.Resize(ref next, count * 2);
                System.Array.Resize(ref stamp, count * 2);
                System.Array.Resize(ref positions, count * 2);
            }
            int b = Bucket(Cell(pos.x), Cell(pos.y));
            next[count] = heads[b];
            positions[count] = pos;
            stamp[count] = 0;
            heads[b] = count;
            return count++;
        }

        /// <summary>Appends indices of items within radius of center. Each item is reported once.</summary>
        public void Query(Vector2 center, float radius, List<int> results)
        {
            int x0 = Cell(center.x - radius), x1 = Cell(center.x + radius);
            int z0 = Cell(center.y - radius), z1 = Cell(center.y + radius);
            float r2 = radius * radius;
            queryId++; // distinct cells can hash to the same bucket; the stamp prevents duplicates
            for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                for (int i = heads[Bucket(x, z)]; i != -1; i = next[i])
                {
                    if (stamp[i] == queryId) continue;
                    stamp[i] = queryId;
                    if ((positions[i] - center).sqrMagnitude <= r2) results.Add(i);
                }
            }
        }

        int Cell(float v) => Mathf.FloorToInt(v / cellSize);
        static int Bucket(int x, int z) => (int)(((uint)(x * 73856093) ^ (uint)(z * 19349663)) & (BucketCount - 1));
    }
}
