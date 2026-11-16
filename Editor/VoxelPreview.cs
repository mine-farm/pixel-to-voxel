using System;
using MineFarm.PixelToVoxel.Core;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Editor
{
    internal sealed class VoxelPreview : IDisposable
    {
        private PreviewRenderUtility renderer;
        private Mesh mesh;
        private Material material;
        private Texture2D texture;
        private Vector2 orbit = new Vector2(20, -25);
        private float zoom = 1f;
        public bool Ready => mesh;

        public void Set(SourceAnalysis source, ConversionSettings settings)
        {
            ClearModel();
            var palette = new Palette(settings.atlasSize);
            palette.AddColors(source.Grid.Colors);
            try
            {
                Shader shader = ConversionPipeline.FindShader();
                if (!shader) throw new InvalidOperationException("Preview requires URP or Built-in Unlit shader.");
                mesh = ConversionPipeline.CreateMesh(VoxelMesher.Build(source.Grid, settings.geometry, palette), source.Label, settings.facing);
                texture = ConversionPipeline.MakeAtlas(palette);
                material = ConversionPipeline.MakeMaterial(shader, texture);
                zoom = 1f;
            }
            catch { ClearModel(); throw; }
        }

        public void Draw(Rect rect, Action repaint)
        {
            if (!Ready) { GUI.Box(rect, "Select Preview on an analyzed image.\nDrag to orbit · Scroll to zoom"); return; }
            Event e = Event.current;
            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    orbit += new Vector2(e.delta.y, e.delta.x) * 0.6f;
                    orbit.x = Mathf.Clamp(orbit.x, -89, 89); e.Use(); repaint();
                }
                if (e.type == EventType.ScrollWheel)
                {
                    zoom = Mathf.Clamp(zoom * Mathf.Exp(e.delta.y * 0.05f), 0.2f, 5f); e.Use(); repaint();
                }
            }
            if (e.type != EventType.Repaint || rect.width < 2 || rect.height < 2) return;
            if (renderer == null) renderer = new PreviewRenderUtility();
            float radius = Mathf.Max(mesh.bounds.extents.magnitude, 0.001f);
            Quaternion angle = Quaternion.Euler(orbit.x, orbit.y, 0);
            Vector3 center = mesh.bounds.center;
            float distance = radius * 3.5f * zoom;
            renderer.camera.fieldOfView = 40;
            renderer.camera.nearClipPlane = Mathf.Max(radius * 0.001f, 0.00001f);
            renderer.camera.farClipPlane = distance + radius * 10;
            renderer.camera.transform.SetPositionAndRotation(center + angle * new Vector3(0, 0, -distance), angle);
            renderer.camera.clearFlags = CameraClearFlags.SolidColor;
            renderer.camera.backgroundColor = new Color(0.12f, 0.13f, 0.15f, 1);
            renderer.BeginPreview(rect, GUIStyle.none);
            Texture rendered;
            try
            {
                renderer.DrawMesh(mesh, Vector3.zero, Quaternion.identity, material, 0);
                renderer.Render(true);
            }
            finally { rendered = renderer.EndPreview(); }
            GUI.DrawTexture(rect, rendered, ScaleMode.StretchToFill, false);
        }

        private void ClearModel()
        {
            if (mesh) Object.DestroyImmediate(mesh);
            if (material) Object.DestroyImmediate(material);
            if (texture) Object.DestroyImmediate(texture);
            mesh = null; material = null; texture = null;
        }

        public void Dispose()
        {
            ClearModel(); renderer?.Cleanup(); renderer = null;
        }
    }
}
