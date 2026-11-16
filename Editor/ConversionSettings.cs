using System;
using MineFarm.PixelToVoxel.Core;

namespace MineFarm.PixelToVoxel.Editor
{
    public enum ColliderMode { None, Box, Mesh }
    public enum ModelFacing { Forward, Backward, Up, Down }
    public enum ExistingOutputMode { UpdateOwned, SkipExisting }

    [Serializable]
    public sealed class ConversionSettings
    {
        public GeometrySettings geometry = new GeometrySettings();
        public ColliderMode collider;
        public ModelFacing facing;
        public ExistingOutputMode existingOutputs;
        public bool sharedAtlas = true;
        public int atlasSize = 256;
        public int warningDimension = 256;
        public int warningSolidPixels = 16384;
        public int warningVertices = 200000;

        public void Validate()
        {
            if (geometry == null) throw new ArgumentException("Geometry settings are missing.");
            geometry.Validate();
            new Palette(atlasSize);
            if (warningDimension < 1 || warningSolidPixels < 1 || warningVertices < 1)
                throw new ArgumentException("Warning thresholds must be positive.");
            if (!Enum.IsDefined(typeof(ColliderMode), collider) || !Enum.IsDefined(typeof(ModelFacing), facing)
                || !Enum.IsDefined(typeof(ExistingOutputMode), existingOutputs)) throw new ArgumentException("Invalid output settings.");
        }
    }
}
