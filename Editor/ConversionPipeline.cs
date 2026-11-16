using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MineFarm.PixelToVoxel.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Editor
{
    public sealed class BatchResult
    {
        public int Succeeded, Failed, Skipped;
        public bool Cancelled;
        public override string ToString() => $"Succeeded: {Succeeded} · Failed: {Failed} · Skipped: {Skipped}" + (Cancelled ? " · Cancelled" : "");
    }

    public static class ConversionPipeline
    {
        public static List<SourceAnalysis> Analyze(ConversionGroup group, bool showProgress = true)
        {
            Validate(group);
            List<Object> sources = SourceReader.Expand(group.sources);
            if (sources.Count == 0) throw new ArgumentException("No PNGs or Sprites found in the selected inputs.");
            var results = new List<SourceAnalysis>();
            long bufferedCells = 0;
            var progress = new ProgressReporter("Pixel To Voxel · Analyze", showProgress);
            try
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    int index = i;
                    Func<bool> cancel = () => progress.Cancel($"{index + 1}/{sources.Count} · {sources[index].name}", (float)index / sources.Count);
                    if (cancel()) throw new OperationCanceledException();
                    results.Add(SourceReader.Analyze(sources[i], group.settings, cancel));
                    PixelGrid grid = results[results.Count - 1].Grid;
                    if (grid != null) bufferedCells += grid.Colors.Length;
                    if (bufferedCells > 16777216)
                        throw new ArgumentException("Batch exceeds 16,777,216 buffered cells. Split it into smaller groups or increase Pixel Step.");
                }
            }
            finally { if (showProgress) EditorUtility.ClearProgressBar(); }
            return results;
        }

        public static void Validate(ConversionGroup group)
        {
            if (!group) throw new ArgumentException("Conversion Group is missing.");
            group.settings.Validate();
            string root = group.outputRoot;
            if (string.IsNullOrEmpty(root) || !(root == "Assets" || root.StartsWith("Assets/", StringComparison.Ordinal))
                || root.Contains('\\') || root.Split('/').Any(p => p == ".." || p == "." || p.Length == 0)
                || root.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) >= 0)
                throw new ArgumentException("Output must be a project folder under Assets, without trailing slash or relative segments.");
            if (!string.IsNullOrEmpty(group.generatedRoot))
            {
                if (group.generatedGroupGuid != GroupIdentity(group))
                    throw new ArgumentException("This group is a copy of a generated group. Create a new group and apply a settings preset to avoid modifying the original outputs.");
                if (group.generatedSharedAtlas != group.settings.sharedAtlas || group.generatedAtlasSize != group.settings.atlasSize)
                    throw new ArgumentException("Atlas mode and size are fixed after generation to preserve UVs. Create a new group to change them.");
                if (!group.generatedRoot.StartsWith(root + "/", StringComparison.Ordinal))
                    throw new ArgumentException("The output root is fixed after generation. Use a new group for another location.");
            }
        }

        public static BatchResult Generate(ConversionGroup group, IList<SourceAnalysis> analyses, bool includeWarnings,
            bool showProgress = true)
        {
            Validate(group);
            var result = new BatchResult();
            var candidates = analyses.Where(a => a.Included && string.IsNullOrEmpty(a.Error) && a.Grid != null
                && (includeWarnings || string.IsNullOrEmpty(a.Warning))).ToList();
            result.Skipped = analyses.Count - candidates.Count;
            foreach (SourceAnalysis a in candidates)
                if (a.SettingsJson != JsonUtility.ToJson(group.settings) || !a.Source
                    || a.SourceHash != AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(a.Source)).ToString())
                    throw new InvalidOperationException("Source or settings changed after analysis. Analyze Inputs again before generating.");
            foreach (SourceAnalysis a in analyses) a.Result = "Skipped";
            if (group.settings.existingOutputs == ExistingOutputMode.SkipExisting)
                candidates.RemoveAll(a =>
                {
                    GenerationRecord old = group.history.Find(r => r.sourceId == a.Id);
                    if (old == null || !old.prefab) return false;
                    a.Result = "Skipped: existing output"; result.Skipped++; return true;
                });
            if (candidates.Count == 0) return result;
            string root = GetRoot(group);
            var shared = new Palette(group.settings.atlasSize, group.paletteColors.Select(c => unchecked((uint)c)));
            if (group.settings.sharedAtlas)
                foreach (SourceAnalysis a in candidates) shared.AddColors(a.Grid.Colors);
            // Check ownership for every output before any writes. Never replace unrelated assets.
            foreach (SourceAnalysis a in candidates) ValidateOwnership(group, a, root);
            if (group.settings.sharedAtlas)
            {
                CheckAsset(root + "/Shared/Palette.asset", group.sharedAtlas);
                CheckAsset(root + "/Shared/Palette.mat", group.sharedMaterial);
            }
            Shader shader = FindShader();
            if (!shader) throw new InvalidOperationException("No supported Unlit shader found. Install URP or use the Built-in pipeline.");
            try
            {
                var progress = new ProgressReporter("Pixel To Voxel · Generate", showProgress);
                if (progress.Cancel("Preparing outputs", 0)) { result.Cancelled = true; result.Skipped += candidates.Count; return result; }
                // Palette slots only append, so cancellation/failure cannot invalidate earlier model UVs.
                if (group.settings.sharedAtlas)
                {
                    group.sharedAtlas = WriteAtlas(shared, root + "/Shared/Palette.asset", group.sharedAtlas);
                    group.sharedMaterial = WriteMaterial(shader, group.sharedAtlas, root + "/Shared/Palette.mat", group.sharedMaterial);
                    group.paletteColors = shared.Colors.Select(c => unchecked((int)c)).ToList();
                }
                group.generatedRoot = root;
                group.generatedGroupGuid = GroupIdentity(group);
                group.generatedSharedAtlas = group.settings.sharedAtlas;
                group.generatedAtlasSize = group.settings.atlasSize;
                EditorUtility.SetDirty(group);
                for (int i = 0; i < candidates.Count; i++)
                {
                    SourceAnalysis a = candidates[i];
                    int index = i;
                    Func<bool> cancel = () => progress.Cancel($"{index + 1}/{candidates.Count} · {a.Label}", (float)index / candidates.Count);
                    if (cancel()) { result.Cancelled = true; break; }
                    try
                    {
                        Palette palette = shared;
                        if (!group.settings.sharedAtlas) { palette = new Palette(group.settings.atlasSize); palette.AddColors(a.Grid.Colors); }
                        MeshData data = VoxelMesher.Build(a.Grid, group.settings.geometry, palette, cancel);
                        SaveModel(group, a, data, palette, shader);
                        a.Result = "Succeeded"; result.Succeeded++;
                    }
                    catch (OperationCanceledException) { result.Cancelled = true; break; }
                    catch (Exception e) { a.Result = "Failed: " + e.Message; result.Failed++; Debug.LogException(e); }
                }
                if (result.Cancelled)
                    result.Skipped += candidates.Count - result.Succeeded - result.Failed;
            }
            finally
            {
                if (showProgress) EditorUtility.ClearProgressBar();
                group.lastSummary = result.ToString();
                EditorUtility.SetDirty(group);
                AssetDatabase.SaveAssets();
            }
            return result;
        }

        private static string GetRoot(ConversionGroup group)
        {
            if (!string.IsNullOrEmpty(group.generatedRoot)) return group.generatedRoot;
            return group.outputRoot + "/" + SafeName(group.name) + "_" + GroupIdentity(group);
        }

        private static string GroupIdentity(ConversionGroup group)
        {
            string path = AssetDatabase.GetAssetPath(group);
            return string.IsNullOrEmpty(path) ? group.GetTemporaryGroupGuid() : AssetDatabase.AssetPathToGUID(path);
        }

        private static string Folder(ConversionGroup group, SourceAnalysis a, string root)
        {
            GenerationRecord previous = group.history.Find(r => r.sourceId == a.Id);
            return previous != null ? previous.outputFolder : root + "/" + SafeName(a.Label) + "_" + a.Id;
        }

        private static void ValidateOwnership(ConversionGroup group, SourceAnalysis a, string root)
        {
            GenerationRecord old = group.history.Find(r => r.sourceId == a.Id);
            string folder = Folder(group, a, root);
            string stem = old != null ? old.outputName : SafeName(a.Label);
            if (!folder.StartsWith(root + "/", StringComparison.Ordinal)) throw new ArgumentException("Recorded output is outside the group folder.");
            CheckAsset(folder + "/" + stem + ".asset", old?.mesh);
            CheckAsset(folder + "/" + stem + ".prefab", old?.prefab);
            if (!group.settings.sharedAtlas)
            {
                CheckAsset(folder + "/Palette.asset", old?.atlas);
                CheckAsset(folder + "/Palette.mat", old?.material);
            }
        }

        private static void CheckAsset(string path, Object owned)
        {
            Object current = AssetDatabase.LoadMainAssetAtPath(path);
            if (current && current != owned) throw new IOException($"Unrelated asset at {path}. Choose a new group or remove the conflicting file yourself.");
            if (!current && File.Exists(path)) throw new IOException($"Unrecognized existing file at {path}.");
            if (owned && AssetDatabase.GetAssetPath(owned) != path) throw new IOException($"A generated asset was moved: {owned.name}. Restore its original path before regenerating.");
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int split = path.LastIndexOf('/');
            if (split <= 0) throw new IOException("Invalid asset folder: " + path);
            EnsureFolder(path.Substring(0, split));
            if (string.IsNullOrEmpty(AssetDatabase.CreateFolder(path.Substring(0, split), path.Substring(split + 1))))
                throw new IOException("Could not create output folder: " + path);
        }

        internal static Mesh CreateMesh(MeshData data, string name, ModelFacing facing)
        {
            Quaternion rotation = FacingRotation(facing);
            var mesh = new Mesh { name = name, indexFormat = data.Positions.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.SetVertices(data.Positions.Select(p => rotation * new Vector3(p.X, p.Y, p.Z)).ToList());
                mesh.SetNormals(data.Normals.Select(p => rotation * new Vector3(p.X, p.Y, p.Z)).ToList());
                mesh.SetUVs(0, data.UVs.Select(p => new Vector2(p.X, p.Y)).ToList());
                mesh.SetTriangles(data.Indices, 0);
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { Object.DestroyImmediate(mesh); throw; }
        }

        private static Quaternion FacingRotation(ModelFacing facing)
        {
            switch (facing)
            {
                case ModelFacing.Backward: return Quaternion.Euler(0, 180, 0);
                case ModelFacing.Up: return Quaternion.Euler(-90, 0, 0);
                case ModelFacing.Down: return Quaternion.Euler(90, 0, 0);
                default: return Quaternion.identity;
            }
        }

        public static Shader FindShader() => GraphicsSettings.currentRenderPipeline == null
            ? Shader.Find("Unlit/Texture") : Shader.Find("Universal Render Pipeline/Unlit");

        internal static Texture2D MakeAtlas(Palette palette)
        {
            var texture = new Texture2D(palette.Size, palette.Size, TextureFormat.RGBA32, false, false)
                { name = "Palette", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0 };
            texture.SetPixels32(palette.Pixels().Select(c => new Color32((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c)).ToArray());
            texture.Apply(false, false);
            return texture;
        }

        private static T SaveAsset<T>(T fresh, string path, T previous) where T : Object
        {
            try
            {
                EnsureFolder(path.Substring(0, path.LastIndexOf('/')));
                if (previous)
                {
                    EditorUtility.CopySerialized(fresh, previous);
                    EditorUtility.SetDirty(previous);
                    return previous;
                }
                AssetDatabase.CreateAsset(fresh, path);
                return fresh;
            }
            finally { if (fresh && !EditorUtility.IsPersistent(fresh)) Object.DestroyImmediate(fresh); }
        }

        private static Texture2D WriteAtlas(Palette palette, string path, Texture2D old) => SaveAsset(MakeAtlas(palette), path, old);

        internal static Material MakeMaterial(Shader shader, Texture2D atlas)
        {
            var material = new Material(shader) { name = "Palette", enableInstancing = true };
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", atlas);
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", atlas);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.white);
            return material;
        }

        private static Material WriteMaterial(Shader shader, Texture2D atlas, string path, Material old) => SaveAsset(MakeMaterial(shader, atlas), path, old);

        private static void SaveModel(ConversionGroup group, SourceAnalysis a, MeshData data, Palette palette, Shader shader)
        {
            GenerationRecord record = group.history.Find(r => r.sourceId == a.Id);
            bool isNew = record == null;
            if (isNew) record = new GenerationRecord { sourceId = a.Id, source = a.Source,
                outputName = SafeName(a.Label), outputFolder = Folder(group, a, group.generatedRoot) };
            EnsureFolder(record.outputFolder);
            // Record ownership immediately: a failed save can be retried without overwriting unrelated files.
            if (isNew) group.history.Add(record);
            record.atlas = group.settings.sharedAtlas ? group.sharedAtlas : WriteAtlas(palette, record.outputFolder + "/Palette.asset", record.atlas);
            record.material = group.settings.sharedAtlas ? group.sharedMaterial : WriteMaterial(shader, record.atlas, record.outputFolder + "/Palette.mat", record.material);
            record.mesh = SaveAsset(CreateMesh(data, a.Label, group.settings.facing), record.outputFolder + "/" + record.outputName + ".asset", record.mesh);
            GameObject root = null;
            bool loaded = record.prefab;
            try
            {
                root = loaded ? PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(record.prefab)) : new GameObject(a.Label);
                MeshFilter filter = GetOrAddComponent<MeshFilter>(root);
                MeshRenderer renderer = GetOrAddComponent<MeshRenderer>(root);
                filter.sharedMesh = record.mesh; renderer.sharedMaterial = record.material;
                ConfigureCollider(root, record.mesh, group.settings.collider);
                GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, record.outputFolder + "/" + record.outputName + ".prefab", out bool success);
                if (!success || !saved) throw new IOException("Prefab save failed: " + record.outputFolder);
                record.prefab = saved;
                record.generatedUtc = DateTime.UtcNow.ToString("O");
                record.settingsJson = JsonUtility.ToJson(group.settings);
                record.vertices = data.Positions.Count; record.triangles = data.Indices.Count / 3;
            }
            finally
            {
                if (root) { if (loaded) PrefabUtility.UnloadPrefabContents(root); else Object.DestroyImmediate(root); }
                EditorUtility.SetDirty(group);
            }
        }

        private static void ConfigureCollider(GameObject root, Mesh mesh, ColliderMode mode)
        {
            // Root Box/Mesh colliders belong to the generator. Other components and children are retained.
            foreach (BoxCollider c in root.GetComponents<BoxCollider>()) if (mode != ColliderMode.Box) Object.DestroyImmediate(c);
            foreach (MeshCollider c in root.GetComponents<MeshCollider>()) if (mode != ColliderMode.Mesh) Object.DestroyImmediate(c);
            if (mode == ColliderMode.Box)
            {
                BoxCollider c = GetOrAddComponent<BoxCollider>(root);
                c.center = mesh.bounds.center; c.size = mesh.bounds.size;
            }
            if (mode == ColliderMode.Mesh)
            {
                MeshCollider c = GetOrAddComponent<MeshCollider>(root);
                c.sharedMesh = null; c.sharedMesh = mesh; c.convex = false;
            }
        }

        private static T GetOrAddComponent<T>(GameObject root) where T : Component
        {
            T component = root.GetComponent<T>();
            return component ? component : root.AddComponent<T>();
        }

        private static string SafeName(string label)
        {
            string clean = new string(label.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').Take(40).ToArray());
            return string.IsNullOrEmpty(clean) ? "Voxel" : clean;
        }

        private sealed class ProgressReporter
        {
            private readonly string title;
            private readonly bool enabled;
            private double last = -100;
            public ProgressReporter(string title, bool enabled) { this.title = title; this.enabled = enabled; }
            public bool Cancel(string text, float fraction)
            {
                if (!enabled || EditorApplication.timeSinceStartup - last < 0.1) return false;
                last = EditorApplication.timeSinceStartup;
                return EditorUtility.DisplayCancelableProgressBar(title, text, fraction);
            }
        }
    }
}
