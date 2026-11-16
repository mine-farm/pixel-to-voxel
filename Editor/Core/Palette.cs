using System;
using System.Collections.Generic;

namespace MineFarm.PixelToVoxel.Core
{
    // Fixed dimensions and append-only slots preserve UVs of models omitted from a later batch.
    public sealed class Palette
    {
        public const int TileSize = 4;
        public readonly int Size;
        public readonly List<uint> Colors = new List<uint>();
        private readonly Dictionary<uint, int> slots = new Dictionary<uint, int>();
        public int Capacity => (Size / TileSize) * (Size / TileSize);

        public Palette(int size, IEnumerable<uint> existing = null)
        {
            if (size < 16 || size > 1024 || (size & (size - 1)) != 0)
                throw new ArgumentException("Atlas size must be a power of two from 16 to 1024.");
            Size = size;
            if (existing != null)
                foreach (uint raw in existing)
                {
                    if (raw == 0) continue;
                    uint color = raw | 255u;
                    if (slots.ContainsKey(color)) continue;
                    if (Colors.Count == Capacity) throw new ArgumentException("Stored palette exceeds atlas capacity.");
                    slots.Add(color, Colors.Count); Colors.Add(color);
                }
        }

        public void AddColors(IEnumerable<uint> colors)
        {
            var additions = new SortedSet<uint>();
            foreach (uint raw in colors)
            {
                if (raw == 0) continue;
                uint color = raw | 255u;
                if (!slots.ContainsKey(color)) additions.Add(color);
            }
            if ((long)Colors.Count + additions.Count > Capacity)
                throw new ArgumentException($"Palette capacity exceeded ({Capacity:N0} colors). Use a larger atlas in a new group or reduce colors.");
            foreach (uint color in additions) { slots.Add(color, Colors.Count); Colors.Add(color); }
        }

        public Float2 UV(uint color)
        {
            if (!slots.TryGetValue(color | 255u, out int slot)) throw new ArgumentException("Color is not in the palette.");
            int columns = Size / TileSize;
            return new Float2(((slot % columns) * TileSize + TileSize * 0.5f) / Size,
                ((slot / columns) * TileSize + TileSize * 0.5f) / Size);
        }

        public uint[] Pixels()
        {
            uint[] pixels = new uint[Size * Size];
            int columns = Size / TileSize;
            for (int i = 0; i < Colors.Count; i++)
            {
                int x = (i % columns) * TileSize, y = (i / columns) * TileSize;
                for (int dy = 0; dy < TileSize; dy++)
                    for (int dx = 0; dx < TileSize; dx++) pixels[(y + dy) * Size + x + dx] = Colors[i];
            }
            return pixels;
        }
    }
}
