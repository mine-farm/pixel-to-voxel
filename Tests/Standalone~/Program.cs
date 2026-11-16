using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MineFarm.PixelToVoxel.Core;

internal static class Program
{
    private const uint Red = 0xff0000ff, Green = 0x00ff00ff, Blue = 0x0000ffff;
    private static int passed;
    private static int Main(string[] args)
    {
        try
        {
            Test("single pixel produces six outward faces", SinglePixel);
            Test("neighbors cull internal faces regardless of color", Neighbors);
            Test("depth does not add internal geometry", Depth);
            Test("binary alpha threshold including transparent black", Alpha);
            Test("center sampling never blends pixel blocks", Sampling);
            Test("non-divisible source fails instead of silent crop", NonDivisible);
            Test("trim and full-canvas pivots", Pivots);
            Test("horizontal and vertical flips", Flips);
            Test("empty images fail before mesh allocation", Empty);
            Test("hole boundaries stay exposed", Hole);
            Test("palette slots survive reload and append", StablePalette);
            Test("palette capacity failure is atomic", PaletteOverflow);
            Test("palette UVs point inside their padded color tile", PalettePixels);
            Test("invalid values rejected", InvalidSettings);
            Test("hard mesh budget enforced", HardBudget);
            Test("cancellation interrupts sampling and mesh building", Cancellation);
            Test("large meshes use indices beyond 16 bits", LargeMesh);
            Test("300 randomized extrusions match independent voxel surfaces", Randomized);
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
            Test("all package C# files parse with C# 9", () => Syntax(root));
            Test("package metadata and assembly references are valid", () => Metadata(root));
            Console.WriteLine($"PASS: {passed} tests (including 300 randomized cases).");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool value, string message = "Assertion failed") { if (!value) throw new Exception(message); }
    private static void Near(float a, float b) => Assert(Math.Abs(a - b) < 0.0002f, $"{a} != {b}");
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static GeometrySettings Settings() => new GeometrySettings { pixelSize = 1, pivot = PivotMode.Center };
    private static PixelGrid Grid(int w, int h, uint[] colors, GeometrySettings settings = null) => PixelGrid.Sample(w, h, colors, settings ?? Settings());
    private static MeshData Mesh(PixelGrid g, GeometrySettings settings = null)
    {
        var p = new Palette(256); p.AddColors(g.Colors); return VoxelMesher.Build(g, settings ?? Settings(), p);
    }
    private static void SinglePixel()
    {
        MeshData m = Mesh(Grid(1, 1, new[] { Red }));
        Assert(m.Positions.Count == 24 && m.Indices.Count == 36);
        Normals(m); Near(Area(m), 6);
    }
    private static void Neighbors()
    {
        PixelGrid g = Grid(2, 1, new[] { Red, Blue });
        Assert(VoxelMesher.Analyze(g, Settings()).Faces == 10); Near(Area(Mesh(g)), 10);
    }
    private static void Depth()
    {
        var s = Settings(); s.depth = 7;
        PixelGrid g = Grid(1, 1, new[] { Red });
        MeshData m = Mesh(g, s); Assert(m.Positions.Count == 24);
        Near(m.Positions.Max(v => v.Z) - m.Positions.Min(v => v.Z), 7); Near(Area(m), 30);
        Assert(VoxelMesher.Analyze(g, s).EquivalentVoxels == 7);
    }
    private static void Alpha()
    {
        var s = Settings(); s.trimTransparent = false;
        PixelGrid g = Grid(4, 1, new uint[] { 0, 0xff00007f, 0x00ff0080, 0x000000ff }, s);
        Assert(g.SolidCount == 2 && g.Colors[2] == Green && g.Colors[3] == 255);
    }
    private static void Sampling()
    {
        var s = Settings(); s.pixelStep = 2;
        PixelGrid g = Grid(4, 2, new[] { Blue, Blue, Green, Green, Red, Red, Green, Green }, s);
        Assert(g.Width == 2 && g.Height == 1 && g.Colors[0] == Red && g.Colors[1] == Green);
    }
    private static void NonDivisible()
    {
        var s = Settings(); s.pixelStep = 2;
        Throws<ArgumentException>(() => Grid(3, 2, new uint[6], s));
    }
    private static void Pivots()
    {
        var s = Settings(); uint[] pixels = new uint[12]; pixels[5] = Red;
        PixelGrid trim = Grid(4, 3, pixels, s);
        Assert(trim.Width == 1 && trim.Height == 1 && trim.OffsetX == 1 && trim.OffsetY == 1);
        s.trimTransparent = false;
        PixelGrid full = Grid(4, 3, pixels, s); MeshData m = Mesh(full, s);
        Near(m.Positions.Min(v => v.X), -1); Near(m.Positions.Min(v => v.Y), -0.5f);
        s.pivot = PivotMode.BottomCenter; m = Mesh(full, s); Near(m.Positions.Min(v => v.Y), 1);
    }
    private static void Flips()
    {
        var s = Settings(); s.flipX = true; s.flipY = true;
        PixelGrid g = Grid(2, 2, new[] { Red, Green, Blue, 255u }, s);
        Assert(g.Colors.SequenceEqual(new[] { 255u, Blue, Green, Red }));
    }
    private static void Empty() => Throws<ArgumentException>(() => Grid(2, 2, new uint[4]));
    private static void Hole()
    {
        uint[] cells = Enumerable.Repeat(Red, 9).ToArray(); cells[4] = 0;
        PixelGrid g = Grid(3, 3, cells); Assert(VoxelMesher.Analyze(g, Settings()).Faces == 32); Near(Area(Mesh(g)), 32);
    }
    private static void StablePalette()
    {
        var p = new Palette(16); p.AddColors(new[] { Red }); Float2 uv = p.UV(Red);
        p.AddColors(new[] { Blue }); var reload = new Palette(16, p.Colors);
        Near(uv.X, reload.UV(Red).X); Near(uv.Y, reload.UV(Red).Y);
        reload.AddColors(new[] { Green }); Near(uv.X, reload.UV(Red).X);
        Assert(reload.Colors.SequenceEqual(new[] { Red, Blue, Green }));
    }
    private static void PaletteOverflow()
    {
        var p = new Palette(16); p.AddColors(new[] { Red });
        Throws<ArgumentException>(() => p.AddColors(Enumerable.Range(0, 17).Select(i => ((uint)i << 8) | 255u)));
        Assert(p.Colors.Count == 1 && p.Colors[0] == Red);
    }
    private static void PalettePixels()
    {
        var p = new Palette(16); p.AddColors(new[] { Red, Blue, Red, 0u, Green });
        Assert(p.Colors.Count == 3);
        uint[] pixels = p.Pixels();
        foreach (uint c in p.Colors)
        {
            Float2 uv = p.UV(c); int x = (int)(uv.X * p.Size), y = (int)(uv.Y * p.Size);
            Assert(pixels[y * p.Size + x] == c && pixels[(y-1) * p.Size + x-1] == c);
        }
    }
    private static void InvalidSettings()
    {
        foreach (float value in new[] { 0, -1, float.NaN, float.PositiveInfinity })
        { var s = Settings(); s.pixelSize = value; Throws<ArgumentException>(() => s.Validate()); }
        var z = Settings(); z.depth = 0; Throws<ArgumentException>(() => z.Validate());
        z = Settings(); z.alphaThreshold = 0; Throws<ArgumentException>(() => z.Validate());
        Throws<ArgumentException>(() => new Palette(17));
        Throws<ArgumentException>(() => Grid(3, 3, new uint[1]));
        Throws<ArgumentException>(() => Grid(2048, 1024, new uint[2048 * 1024]));
    }
    private static void HardBudget()
    {
        uint[] pixels = new uint[512 * 512];
        for (int y = 0; y < 512; y++) for (int x = 0; x < 512; x++) if ((x + y) % 2 == 0) pixels[y * 512 + x] = Red;
        PixelGrid g = Grid(512, 512, pixels);
        Assert(VoxelMesher.Analyze(g, Settings()).Faces > SafetyLimits.MaxFaces);
        var palette = new Palette(16); palette.AddColors(g.Colors);
        Throws<ArgumentException>(() => VoxelMesher.Build(g, Settings(), palette));
    }
    private static void Cancellation()
    {
        Throws<OperationCanceledException>(() => PixelGrid.Sample(1, 1, new[] { Red }, Settings(), () => true));
        PixelGrid g = Grid(1, 1, new[] { Red }); var p = new Palette(16); p.AddColors(g.Colors);
        Throws<OperationCanceledException>(() => VoxelMesher.Build(g, Settings(), p, () => true));
    }
    private static void LargeMesh()
    {
        PixelGrid g = Grid(128, 128, Enumerable.Repeat(Red, 128 * 128).ToArray());
        MeshData m = Mesh(g); Assert(m.Positions.Count > 65535 && m.Indices.Max() > 65535);
        Assert(m.Indices.All(i => i >= 0 && i < m.Positions.Count));
    }
    private static void Randomized()
    {
        var random = new Random(20261116);
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int w = random.Next(1, 9), h = random.Next(1, 9), depth = random.Next(1, 6);
            uint[] colors = new uint[w * h];
            for (int i = 0; i < colors.Length; i++) colors[i] = random.Next(3) == 0 ? 0 : (random.Next(2) == 0 ? Red : Blue);
            colors[0] = Red;
            var s = Settings(); s.trimTransparent = false; s.depth = depth;
            PixelGrid g = Grid(w, h, colors, s); MeshData m = Mesh(g, s);
            int exposed = 0;
            bool Solid(int x, int y, int z) => x >= 0 && x < w && y >= 0 && y < h && z >= 0 && z < depth && colors[y*w+x] != 0;
            for (int z = 0; z < depth; z++) for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                if (!Solid(x,y,z)) continue;
                if (!Solid(x-1,y,z)) exposed++; if (!Solid(x+1,y,z)) exposed++;
                if (!Solid(x,y-1,z)) exposed++; if (!Solid(x,y+1,z)) exposed++;
                if (!Solid(x,y,z-1)) exposed++; if (!Solid(x,y,z+1)) exposed++;
            }
            Near(Area(m), exposed); Normals(m);
            Assert(m.Indices.Count / 3 == VoxelMesher.Analyze(g, s).Triangles);
        }
    }
    private static Float3 Cross(Float3 a, Float3 b) => new Float3(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
    private static Float3 Sub(Float3 a, Float3 b) => new Float3(a.X-b.X, a.Y-b.Y, a.Z-b.Z);
    private static float Area(MeshData m)
    {
        float area = 0;
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            Float3 a = m.Positions[m.Indices[i]], b = m.Positions[m.Indices[i+1]], c = m.Positions[m.Indices[i+2]];
            Float3 cross = Cross(Sub(b,a), Sub(c,a)); area += (float)Math.Sqrt(cross.X*cross.X+cross.Y*cross.Y+cross.Z*cross.Z) * 0.5f;
        }
        return area;
    }
    private static void Normals(MeshData m)
    {
        for (int i = 0; i < m.Indices.Count; i += 3)
        {
            int j = m.Indices[i]; Float3 a = m.Positions[j], b = m.Positions[m.Indices[i+1]], c = m.Positions[m.Indices[i+2]];
            Float3 cross = Cross(Sub(b,a), Sub(c,a)), n = m.Normals[j];
            Assert(cross.X*n.X+cross.Y*n.Y+cross.Z*n.Z > 0, "Inward or degenerate triangle");
        }
    }
    private static void Syntax(string root)
    {
        foreach (string folder in new[] { "Editor", "Tests/Editor" })
            foreach (string path in Directory.GetFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.CSharp9), path);
                var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
                Assert(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(d => d.ToString())));
            }
    }
    private static void Metadata(string root)
    {
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json"))))
            Assert(json.RootElement.GetProperty("name").GetString() == "net.minefarm.pixel-to-voxel");
        var names = new HashSet<string>(); var refs = new List<string>();
        foreach (string file in Directory.GetFiles(root, "*.asmdef", SearchOption.AllDirectories))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file));
            names.Add(json.RootElement.GetProperty("name").GetString());
            Assert(json.RootElement.GetProperty("includePlatforms").EnumerateArray().Any(x => x.GetString() == "Editor"));
            if (json.RootElement.TryGetProperty("references", out JsonElement references))
                foreach (JsonElement reference in references.EnumerateArray()) refs.Add(reference.GetString());
        }
        Assert(refs.All(names.Contains), "Assembly reference is missing.");
        Assert(!Directory.Exists(Path.Combine(root, "Runtime")), "Editor-only package should not introduce runtime dependencies.");
        var guids = new HashSet<string>();
        foreach (string directory in new[] { "Editor", "Tests/Editor" })
            foreach (string path in Directory.GetFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".asmdef", StringComparison.Ordinal)))
            {
                Assert(File.Exists(path + ".meta"), "Missing Unity metadata: " + path);
                string guid = File.ReadLines(path + ".meta").Single(l => l.StartsWith("guid: ", StringComparison.Ordinal)).Substring(6);
                Assert(guid.Length == 32 && guids.Add(guid), "Invalid/duplicate Unity GUID: " + path);
            }
    }
}
