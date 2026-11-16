using System;
using System.Collections.Generic;
using UnityEngine;

namespace MineFarm.PixelToVoxel.Editor
{
    [Serializable]
    public sealed class GenerationRecord
    {
        public string sourceId;
        public UnityEngine.Object source;
        public string outputFolder;
        public string outputName;
        public Mesh mesh;
        public GameObject prefab;
        public Texture2D atlas;
        public Material material;
        public string generatedUtc;
        public string settingsJson;
        public int vertices;
        public int triangles;
    }

    [CreateAssetMenu(menuName = "Pixel To Voxel/Conversion Group", fileName = "VoxelGroup")]
    public sealed class ConversionGroup : ScriptableObject
    {
        [SerializeField, HideInInspector] private string temporaryGroupGuid;
        public List<UnityEngine.Object> sources = new List<UnityEngine.Object>();
        public ConversionSettings settings = new ConversionSettings();
        public string outputRoot = "Assets/Generated/PixelToVoxel";
        [HideInInspector] public string generatedRoot;
        [HideInInspector] public string generatedGroupGuid;
        [HideInInspector] public bool generatedSharedAtlas;
        [HideInInspector] public int generatedAtlasSize;
        [HideInInspector] public Texture2D sharedAtlas;
        [HideInInspector] public Material sharedMaterial;
        [HideInInspector] public List<int> paletteColors = new List<int>();
        [HideInInspector] public List<GenerationRecord> history = new List<GenerationRecord>();
        [HideInInspector] public string lastSummary;

        internal string GetTemporaryGroupGuid()
        {
            if (string.IsNullOrEmpty(temporaryGroupGuid)) temporaryGroupGuid = Guid.NewGuid().ToString("N");
            return temporaryGroupGuid;
        }
    }
}
