using System;
using System.Collections.Generic;

namespace MineFarm.PixelToVoxel.Core
{
    public enum PivotMode { Center, BottomCenter }

    [Serializable]
    public sealed class GeometrySettings
    {
        public int pixelStep = 1;
        public float pixelSize = 0.1f;
        public int depth = 1;
        public int alphaThreshold = 128;
        public bool trimTransparent = true;
        public bool flipX;
        public bool flipY;
        public PivotMode pivot = PivotMode.BottomCenter;

        public void Validate()
        {
            if (pixelStep < 1 || pixelStep > 4096) throw new ArgumentException("Pixel step must be 1–4096.");
            if (float.IsNaN(pixelSize) || float.IsInfinity(pixelSize) || pixelSize <= 0 || pixelSize > 10000)
                throw new ArgumentException("Pixel size must be positive and finite (maximum 10000).");
            if (depth < 1 || depth > 4096) throw new ArgumentException("Depth must be 1–4096.");
            if (alphaThreshold < 1 || alphaThreshold > 255) throw new ArgumentException("Alpha threshold must be 1–255.");
            if (!Enum.IsDefined(typeof(PivotMode), pivot)) throw new ArgumentException("Unknown pivot.");
        }
    }

    public readonly struct Float3
    {
        public readonly float X, Y, Z;
        public Float3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }

    public readonly struct Float2
    {
        public readonly float X, Y;
        public Float2(float x, float y) { X = x; Y = y; }
    }

    // Pixels are RGBA packed as 0xRRGGBBAA; rows start at the bottom, like Unity textures.
    public sealed class PixelGrid
    {
        public readonly int Width, Height, OriginalWidth, OriginalHeight, OffsetX, OffsetY;
        public readonly uint[] Colors; // zero means empty; retained cells are fully opaque.
        public readonly int SolidCount;

        public PixelGrid(int width, int height, uint[] colors, int originalWidth, int originalHeight, int offsetX, int offsetY)
        {
            Width = width; Height = height; Colors = colors;
            OriginalWidth = originalWidth; OriginalHeight = originalHeight; OffsetX = offsetX; OffsetY = offsetY;
            foreach (uint color in colors) if (color != 0) SolidCount++;
        }

        public bool Solid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Colors[y * Width + x] != 0;

        public static PixelGrid Sample(int width, int height, uint[] rgba, GeometrySettings settings, Func<bool> cancelled = null)
        {
            settings.Validate();
            if (width <= 0 || height <= 0 || rgba == null || (long)width * height != rgba.Length)
                throw new ArgumentException("Invalid source pixel buffer.");
            if (width % settings.pixelStep != 0 || height % settings.pixelStep != 0)
                throw new ArgumentException("Image dimensions must be divisible by Pixel Step. Crop or select another step.");
            int w = width / settings.pixelStep, h = height / settings.pixelStep;
            if ((long)w * h > SafetyLimits.MaxGridCells)
                throw new ArgumentException("Sampled image exceeds 1,048,576 cells. Increase Pixel Step.");
            uint[] cells = new uint[w * h];
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                for (int x = 0; x < w; x++)
                {
                    // Center sample: never blend neighboring pixel-art colors.
                    int sx = x * settings.pixelStep + settings.pixelStep / 2;
                    int sy = y * settings.pixelStep + settings.pixelStep / 2;
                    uint color = rgba[sy * width + sx];
                    if ((color & 255) < settings.alphaThreshold) continue;
                    int tx = settings.flipX ? w - 1 - x : x;
                    int ty = settings.flipY ? h - 1 - y : y;
                    cells[ty * w + tx] = color | 255u;
                    minX = Math.Min(minX, tx); maxX = Math.Max(maxX, tx);
                    minY = Math.Min(minY, ty); maxY = Math.Max(maxY, ty);
                }
            }
            if (maxX < 0) throw new ArgumentException("No pixels pass the alpha threshold.");
            if (!settings.trimTransparent) return new PixelGrid(w, h, cells, w, h, 0, 0);
            int tw = maxX - minX + 1, th = maxY - minY + 1;
            uint[] trimmed = new uint[tw * th];
            for (int y = 0; y < th; y++) Array.Copy(cells, (y + minY) * w + minX, trimmed, y * tw, tw);
            return new PixelGrid(tw, th, trimmed, w, h, minX, minY);
        }
    }

    public static class SafetyLimits
    {
        public const int MaxSourcePixels = 16777216;
        public const int MaxGridCells = 1048576;
        public const int MaxFaces = 500000;
    }

    public sealed class MeshStatistics
    {
        public int SolidPixels, Faces, Vertices, Triangles;
        public long EquivalentVoxels;
    }

    public sealed class MeshData
    {
        public readonly List<Float3> Positions = new List<Float3>();
        public readonly List<Float3> Normals = new List<Float3>();
        public readonly List<Float2> UVs = new List<Float2>();
        public readonly List<int> Indices = new List<int>();
    }

    public static class VoxelMesher
    {
        public static MeshStatistics Analyze(PixelGrid grid, GeometrySettings settings)
        {
            settings.Validate();
            int faces = 0;
            for (int y = 0; y < grid.Height; y++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!grid.Solid(x, y)) continue;
                    faces += 2;
                    if (!grid.Solid(x - 1, y)) faces++;
                    if (!grid.Solid(x + 1, y)) faces++;
                    if (!grid.Solid(x, y - 1)) faces++;
                    if (!grid.Solid(x, y + 1)) faces++;
                }
            return new MeshStatistics { SolidPixels = grid.SolidCount, Faces = faces,
                Vertices = checked(faces * 4), Triangles = checked(faces * 2),
                EquivalentVoxels = (long)grid.SolidCount * settings.depth };
        }

        public static MeshData Build(PixelGrid grid, GeometrySettings settings, Palette palette, Func<bool> cancelled = null)
        {
            MeshStatistics stats = Analyze(grid, settings);
            if (stats.Faces > SafetyLimits.MaxFaces)
                throw new ArgumentException("Mesh exceeds 500,000 exposed faces. Increase Pixel Step or split the image.");
            var data = new MeshData();
            data.Positions.Capacity = data.Normals.Capacity = data.UVs.Capacity = stats.Vertices;
            data.Indices.Capacity = stats.Triangles * 3;
            float size = settings.pixelSize;
            float ox = grid.Width * size * 0.5f;
            float oy = settings.pivot == PivotMode.Center ? grid.Height * size * 0.5f : 0;
            float z0 = -settings.depth * size * 0.5f, z1 = -z0;
            for (int y = 0; y < grid.Height; y++)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!grid.Solid(x, y)) continue;
                    float x0 = x * size - ox, x1 = x0 + size, y0 = y * size - oy, y1 = y0 + size;
                    Float2 uv = palette.UV(grid.Colors[y * grid.Width + x]);
                    Quad(data, new Float3(x0,y0,z0), new Float3(x0,y1,z0), new Float3(x1,y1,z0), new Float3(x1,y0,z0), new Float3(0,0,-1), uv);
                    Quad(data, new Float3(x0,y0,z1), new Float3(x1,y0,z1), new Float3(x1,y1,z1), new Float3(x0,y1,z1), new Float3(0,0,1), uv);
                    if (!grid.Solid(x - 1, y)) Quad(data, new Float3(x0,y0,z0), new Float3(x0,y0,z1), new Float3(x0,y1,z1), new Float3(x0,y1,z0), new Float3(-1,0,0), uv);
                    if (!grid.Solid(x + 1, y)) Quad(data, new Float3(x1,y0,z0), new Float3(x1,y1,z0), new Float3(x1,y1,z1), new Float3(x1,y0,z1), new Float3(1,0,0), uv);
                    if (!grid.Solid(x, y - 1)) Quad(data, new Float3(x0,y0,z0), new Float3(x1,y0,z0), new Float3(x1,y0,z1), new Float3(x0,y0,z1), new Float3(0,-1,0), uv);
                    if (!grid.Solid(x, y + 1)) Quad(data, new Float3(x0,y1,z0), new Float3(x0,y1,z1), new Float3(x1,y1,z1), new Float3(x1,y1,z0), new Float3(0,1,0), uv);
                }
            }
            return data;
        }

        private static void Quad(MeshData data, Float3 a, Float3 b, Float3 c, Float3 d, Float3 normal, Float2 uv)
        {
            int i = data.Positions.Count;
            data.Positions.Add(a); data.Positions.Add(b); data.Positions.Add(c); data.Positions.Add(d);
            for (int n = 0; n < 4; n++) { data.Normals.Add(normal); data.UVs.Add(uv); }
            data.Indices.Add(i); data.Indices.Add(i+1); data.Indices.Add(i+2);
            data.Indices.Add(i); data.Indices.Add(i+2); data.Indices.Add(i+3);
        }
    }
}
