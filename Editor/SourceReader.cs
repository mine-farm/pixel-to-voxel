using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MineFarm.PixelToVoxel.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Editor
{
    public sealed class SourceAnalysis
    {
        public Object Source;
        public string Id, Label, Error, Warning;
        public string SettingsJson, SourceHash;
        public int SourceWidth, SourceHeight;
        public PixelGrid Grid;
        public MeshStatistics Statistics;
        public bool Included = true;
        public string Result;
    }

    public static class SourceReader
    {
        public static string Id(Object source)
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long fileId))
                throw new ArgumentException("Select a saved project PNG or Sprite.");
            return guid + "_" + fileId;
        }

        public static List<Object> Expand(IEnumerable<Object> inputs)
        {
            var result = new Dictionary<string, Object>(StringComparer.Ordinal);
            foreach (Object input in inputs)
            {
                if (!input) continue;
                string path = AssetDatabase.GetAssetPath(input);
                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { path }).OrderBy(x => x, StringComparer.Ordinal))
                        AddPath(AssetDatabase.GUIDToAssetPath(guid), result);
                }
                else if (input is Sprite) result[Id(input)] = input;
                else if (input is Texture2D) AddPath(path, result);
                else throw new ArgumentException($"{input.name}: select a PNG Texture2D, Sprite or project folder.");
            }
            return result.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Value).ToList();
        }

        private static void AddPath(string path, Dictionary<string, Object> result)
        {
            if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
            var sprites = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().ToArray();
            if (sprites.Length > 0)
                foreach (Sprite sprite in sprites) result[Id(sprite)] = sprite;
            else
            {
                Object texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture) result[Id(texture)] = texture;
            }
        }

        public static SourceAnalysis Analyze(Object source, ConversionSettings settings, Func<bool> cancelled = null)
        {
            var analysis = new SourceAnalysis { Source = source, Label = source ? source.name : "Missing source" };
            Texture2D decoded = null;
            try
            {
                settings.Validate();
                analysis.Id = Id(source);
                string path = AssetDatabase.GetAssetPath(source);
                analysis.SettingsJson = JsonUtility.ToJson(settings);
                analysis.SourceHash = AssetDatabase.GetAssetDependencyHash(path).ToString();
                if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only source PNG files are supported.");
                if (new FileInfo(path).Length > 134217728) throw new ArgumentException("Source PNG exceeds 128 MiB. Resize or split it first.");
                // Decode source bytes: no Read/Write or compression changes to the user's importer.
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
                    throw new ArgumentException("Invalid PNG header.");
                uint rawWidth = BigEndian(bytes, 16), rawHeight = BigEndian(bytes, 20);
                if (rawWidth == 0 || rawHeight == 0 || (ulong)rawWidth * rawHeight > SafetyLimits.MaxSourcePixels)
                    throw new ArgumentException("Source exceeds 16,777,216 pixels. Resize or split the PNG first.");
                decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!ImageConversion.LoadImage(decoded, bytes, false)) throw new ArgumentException("PNG decoding failed.");
                var rect = new RectInt(0, 0, decoded.width, decoded.height);
                if (source is Sprite sprite)
                {
                    if (sprite.packed) throw new ArgumentException("Use the original project Sprite, not an atlas-packed Sprite.");
                    if (sprite.texture.width != decoded.width || sprite.texture.height != decoded.height)
                        throw new ArgumentException("Sprite import is resized. Set its Max Size to cover the original PNG, then reimport.");
                    Rect r = sprite.rect;
                    rect = new RectInt(Mathf.RoundToInt(r.x), Mathf.RoundToInt(r.y), Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height));
                }
                else if (!(source is Texture2D)) throw new ArgumentException("Input is not a Texture2D or Sprite.");
                if (rect.width < 1 || rect.height < 1 || rect.x < 0 || rect.y < 0 || rect.xMax > decoded.width || rect.yMax > decoded.height)
                    throw new ArgumentException("Sprite rectangle is outside the source PNG.");
                analysis.SourceWidth = rect.width; analysis.SourceHeight = rect.height;
                Color32[] colors = decoded.GetPixels32();
                uint[] rgba = new uint[rect.width * rect.height];
                for (int y = 0; y < rect.height; y++)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    for (int x = 0; x < rect.width; x++)
                    {
                        Color32 c = colors[(rect.y + y) * decoded.width + rect.x + x];
                        rgba[y * rect.width + x] = ((uint)c.r << 24) | ((uint)c.g << 16) | ((uint)c.b << 8) | c.a;
                    }
                }
                analysis.Grid = PixelGrid.Sample(rect.width, rect.height, rgba, settings.geometry, cancelled);
                analysis.Statistics = VoxelMesher.Analyze(analysis.Grid, settings.geometry);
                var paletteCheck = new Palette(settings.atlasSize);
                paletteCheck.AddColors(analysis.Grid.Colors);
                if (analysis.Statistics.Faces > SafetyLimits.MaxFaces)
                    throw new ArgumentException("Mesh exceeds 500,000 faces. Increase Pixel Step or split the image.");
                var warnings = new List<string>();
                if (rect.width > settings.warningDimension || rect.height > settings.warningDimension)
                    warnings.Add($"large source ({rect.width} × {rect.height})");
                if (analysis.Grid.SolidCount > settings.warningSolidPixels)
                    warnings.Add($"{analysis.Grid.SolidCount:N0} opaque cells");
                if (analysis.Statistics.Vertices > settings.warningVertices)
                    warnings.Add($"{analysis.Statistics.Vertices:N0} vertices");
                analysis.Warning = string.Join(", ", warnings);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { analysis.Error = e.Message; analysis.Included = false; analysis.Grid = null; }
            finally { if (decoded) Object.DestroyImmediate(decoded); }
            return analysis;
        }

        private static uint BigEndian(byte[] bytes, int offset) => ((uint)bytes[offset] << 24) | ((uint)bytes[offset+1] << 16)
            | ((uint)bytes[offset+2] << 8) | bytes[offset+3];
    }
}
