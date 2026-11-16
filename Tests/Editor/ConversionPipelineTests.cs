using System;
using System.IO;
using System.Linq;
using MineFarm.PixelToVoxel.Core;
using MineFarm.PixelToVoxel.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Tests
{
    public sealed class ConversionPipelineTests
    {
        private string root;
        private ConversionGroup group;

        [SetUp]
        public void SetUp()
        {
            root = "Assets/__PixelToVoxelTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", root.Substring("Assets/".Length));
            group = ScriptableObject.CreateInstance<ConversionGroup>();
            group.outputRoot = root + "/Output";
            group.settings.atlasSize = 16;
            AssetDatabase.CreateAsset(group, root + "/Group.asset");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(root);
            AssetDatabase.Refresh();
        }

        [Test]
        public void GeneratesMeshPrefabWithoutChangingSourceImporter()
        {
            Texture2D source = Png("Item", Color.red, Color.blue);
            group.sources.Add(source);
            string importerBefore = EditorJsonUtility.ToJson(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)));
            var analyses = ConversionPipeline.Analyze(group, false);
            BatchResult result = ConversionPipeline.Generate(group, analyses, true, false);
            Assert.That(result.Succeeded, Is.EqualTo(1));
            GenerationRecord record = group.history.Single();
            Assert.That(record.mesh.vertexCount, Is.EqualTo(40));
            Assert.That(record.prefab.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(record.mesh));
            Assert.That(Identity(record.prefab.GetComponent<MeshRenderer>().sharedMaterial),
                Is.EqualTo(Identity(group.sharedMaterial)));
            Assert.That(group.sharedAtlas.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(group.sharedAtlas.mipmapCount, Is.EqualTo(1));
            Assert.That(record.prefab.GetComponentsInChildren<Transform>().Length, Is.EqualTo(1));
            Assert.That(EditorJsonUtility.ToJson(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source))), Is.EqualTo(importerBefore));
        }

        [Test]
        public void RegenerationPreservesAssetAndSceneReferencesAndCustomChildren()
        {
            Texture2D source = Png("Item", Color.red, Color.red);
            group.sources.Add(source); Generate();
            GenerationRecord record = group.history.Single();
            string meshIdentity = Identity(record.mesh), prefabIdentity = Identity(record.prefab);
            string materialIdentity = Identity(record.material), atlasIdentity = Identity(record.atlas);
            Vector2 oldUv = record.mesh.uv[0];
            GameObject contents = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(record.prefab));
            try
            {
                new GameObject("UserAttachment").transform.SetParent(contents.transform);
                PrefabUtility.SaveAsPrefabAsset(contents, AssetDatabase.GetAssetPath(record.prefab));
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(record.prefab);
            try
            {
                Png("Item", Color.red, Color.blue); group.settings.geometry.depth = 4;
                Generate();
                Assert.That(Identity(record.mesh), Is.EqualTo(meshIdentity));
                Assert.That(Identity(record.prefab), Is.EqualTo(prefabIdentity));
                Assert.That(Identity(record.material), Is.EqualTo(materialIdentity));
                Assert.That(Identity(record.atlas), Is.EqualTo(atlasIdentity));
                Assert.That(record.mesh.uv[0], Is.EqualTo(oldUv));
                Assert.That(instance.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(record.mesh));
                Assert.That(record.prefab.transform.Find("UserAttachment"), Is.Not.Null);
                Assert.That(record.mesh.bounds.size.z, Is.EqualTo(0.4f).Within(0.0001f));
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void BatchDeduplicatesInputsAndSharesAtlasAndMaterial()
        {
            Texture2D a = Png("A", Color.red, Color.red), b = Png("B", Color.green, Color.blue);
            group.sources.Add(a); group.sources.Add(a); group.sources.Add(b);
            var analyses = ConversionPipeline.Analyze(group, false);
            Assert.That(analyses.Count, Is.EqualTo(2));
            Assert.That(ConversionPipeline.Generate(group, analyses, true, false).Succeeded, Is.EqualTo(2));
            Assert.That(group.history[0].atlas, Is.SameAs(group.history[1].atlas));
            Assert.That(group.history[0].material, Is.SameAs(group.history[1].material));
            Assert.That(group.paletteColors.Count, Is.EqualTo(3));
        }

        [Test]
        public void AddingColorsLeavesSkippedModelsUvAndColorUnchanged()
        {
            Texture2D a = Png("A", Color.red, Color.red);
            group.sources.Add(a); Generate();
            GenerationRecord old = group.history.Single(); Vector2 uv = old.mesh.uv[0];
            Color32 color = old.atlas.GetPixels32()[(int)(uv.y * old.atlas.height) * old.atlas.width + (int)(uv.x * old.atlas.width)];
            group.sources.Add(Png("B", Color.blue, Color.green));
            group.settings.existingOutputs = ExistingOutputMode.SkipExisting;
            var result = ConversionPipeline.Generate(group, ConversionPipeline.Analyze(group, false), true, false);
            Assert.That(result.Skipped, Is.EqualTo(1)); Assert.That(result.Succeeded, Is.EqualTo(1));
            Assert.That(old.mesh.uv[0], Is.EqualTo(uv));
            Assert.That(old.atlas.GetPixels32()[(int)(uv.y * old.atlas.height) * old.atlas.width + (int)(uv.x * old.atlas.width)], Is.EqualTo(color));
        }

        [Test]
        public void WarningsCanBeExcludedWithoutWritingOutput()
        {
            group.sources.Add(Png("Item", Color.red, Color.blue));
            group.settings.warningSolidPixels = 1;
            var analyses = ConversionPipeline.Analyze(group, false);
            Assert.That(analyses[0].Warning, Is.Not.Empty);
            var result = ConversionPipeline.Generate(group, analyses, false, false);
            Assert.That(result.Succeeded, Is.Zero); Assert.That(result.Skipped, Is.EqualTo(1));
            Assert.That(group.generatedRoot, Is.Null.Or.Empty);
        }

        [Test]
        public void TemporaryGroupGeneratesAndRegeneratesWithoutAnAsset()
        {
            var temporary = ScriptableObject.CreateInstance<ConversionGroup>();
            temporary.name = "Temporary Group";
            temporary.outputRoot = root + "/Output";
            temporary.settings.atlasSize = 16;
            temporary.sources.Add(Png("Temporary", Color.red, Color.blue));
            try
            {
                Assert.That(AssetDatabase.GetAssetPath(temporary), Is.Empty);
                BatchResult first = ConversionPipeline.Generate(temporary, ConversionPipeline.Analyze(temporary, false), true, false);
                Assert.That(first.Succeeded, Is.EqualTo(1));
                string generatedRoot = temporary.generatedRoot;
                Assert.That(generatedRoot, Does.StartWith(temporary.outputRoot + "/TemporaryGroup_"));
                Assert.That(temporary.generatedGroupGuid, Has.Length.EqualTo(32));

                temporary.settings.geometry.depth = 2;
                BatchResult second = ConversionPipeline.Generate(temporary, ConversionPipeline.Analyze(temporary, false), true, false);
                Assert.That(second.Succeeded, Is.EqualTo(1));
                Assert.That(temporary.generatedRoot, Is.EqualTo(generatedRoot));
                Assert.That(temporary.history, Has.Count.EqualTo(1));
            }
            finally { Object.DestroyImmediate(temporary); }
        }

        [Test]
        public void InvalidAndEmptyImagesDoNotPreventOtherInputs()
        {
            group.sources.Add(Png("Empty", Color.clear, Color.clear));
            group.sources.Add(Png("Valid", Color.red, Color.red));
            var analyses = ConversionPipeline.Analyze(group, false);
            Assert.That(analyses.Count(a => !string.IsNullOrEmpty(a.Error)), Is.EqualTo(1));
            Assert.That(ConversionPipeline.Generate(group, analyses, true, false).Succeeded, Is.EqualTo(1));
        }

        [Test]
        public void PrivateAtlasModeUsesSeparateMaterials()
        {
            group.settings.sharedAtlas = false;
            group.sources.Add(Png("A", Color.red, Color.red)); group.sources.Add(Png("B", Color.blue, Color.blue));
            Generate();
            Assert.That(group.history[0].material, Is.Not.SameAs(group.history[1].material));
            Assert.That(group.sharedAtlas, Is.Null);
        }

        [Test]
        public void BoxMeshAndNoColliderCanBeRegenerated()
        {
            group.sources.Add(Png("Item", Color.red, Color.red));
            group.settings.collider = ColliderMode.Box; Generate();
            Assert.That(group.history[0].prefab.GetComponent<BoxCollider>(), Is.Not.Null);
            group.settings.collider = ColliderMode.Mesh; Generate();
            Assert.That(group.history[0].prefab.GetComponent<BoxCollider>(), Is.Null);
            Assert.That(group.history[0].prefab.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(group.history[0].mesh));
            group.settings.collider = ColliderMode.None; Generate();
            Assert.That(group.history[0].prefab.GetComponent<Collider>(), Is.Null);
        }

        [Test]
        public void AtlasLayoutChangesAreRejectedAfterGeneration()
        {
            group.sources.Add(Png("Item", Color.red, Color.red)); Generate();
            group.settings.atlasSize = 32;
            Assert.Throws<ArgumentException>(() => ConversionPipeline.Validate(group));
        }

        [Test]
        public void CopiedGroupCannotModifyOriginalOutputs()
        {
            group.sources.Add(Png("Item", Color.red, Color.red)); Generate();
            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(group), root + "/Copy.asset");
            var copy = AssetDatabase.LoadAssetAtPath<ConversionGroup>(root + "/Copy.asset");
            Assert.Throws<ArgumentException>(() => ConversionPipeline.Validate(copy));
        }

        [Test]
        public void StaleAnalysisIsRejectedBeforeWritingAssets()
        {
            group.sources.Add(Png("Item", Color.red, Color.red));
            var analyses = ConversionPipeline.Analyze(group, false);
            group.settings.geometry.alphaThreshold = 200;
            Assert.Throws<InvalidOperationException>(() => ConversionPipeline.Generate(group, analyses, true, false));
            Assert.That(group.generatedRoot, Is.Null.Or.Empty);
        }

        [Test]
        public void UnrelatedFilesAreNeverOverwritten()
        {
            Texture2D source = Png("Item", Color.red, Color.red);
            group.sources.Add(source);
            string generated = group.outputRoot + "/Group_" + AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group));
            Directory.CreateDirectory(generated + "/Shared");
            AssetDatabase.Refresh();
            var ownedByUser = new Texture2D(2, 2) { name = "UserOwned" };
            AssetDatabase.CreateAsset(ownedByUser, generated + "/Shared/Palette.asset");
            string identity = Identity(ownedByUser);
            Assert.Throws<IOException>(() => ConversionPipeline.Generate(group, ConversionPipeline.Analyze(group, false), true, false));
            Assert.That(Identity(AssetDatabase.LoadMainAssetAtPath(generated + "/Shared/Palette.asset")), Is.EqualTo(identity));
        }

        [TestCase("../Outside")]
        [TestCase("Packages/Output")]
        [TestCase("Assets/../Outside")]
        [TestCase("Assets/Output/")]
        public void UnsafeOutputPathsAreRejected(string path)
        {
            group.outputRoot = path;
            Assert.Throws<ArgumentException>(() => ConversionPipeline.Validate(group));
        }

        private void Generate()
        {
            var result = ConversionPipeline.Generate(group, ConversionPipeline.Analyze(group, false), true, false);
            Assert.That(result.Failed, Is.Zero); Assert.That(result.Succeeded, Is.GreaterThan(0));
        }

        private Texture2D Png(string name, Color a, Color b)
        {
            string path = root + "/" + name + ".png";
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false);
            try { texture.SetPixels(new[] { a, b }); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG()); }
            finally { Object.DestroyImmediate(texture); }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static string Identity(Object obj)
        {
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId), Is.True);
            return guid + ":" + localId;
        }
    }
}
