using UnityEngine;

namespace MineFarm.PixelToVoxel.Editor
{
    [CreateAssetMenu(menuName = "Pixel To Voxel/Settings Preset", fileName = "VoxelPreset")]
    public sealed class ConversionPreset : ScriptableObject
    {
        public ConversionSettings settings = new ConversionSettings();
    }
}
