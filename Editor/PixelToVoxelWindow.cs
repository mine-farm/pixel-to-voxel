using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Editor
{
    public sealed class PixelToVoxelWindow : EditorWindow
    {
        [SerializeField] private ConversionGroup group;
        [SerializeField] private ConversionPreset preset;
        private List<SourceAnalysis> analyses;
        private VoxelPreview preview;
        private Vector2 scroll;
        private string notice, analyzedFingerprint, sourceFingerprint;
        private int page;
        private bool working;
        private bool showHistory;
        private const int PageSize = 30;

        [MenuItem("Tools/Pixel To Voxel")]
        public static void Open() => GetWindow<PixelToVoxelWindow>("Pixel To Voxel");

        public static void Open(ConversionGroup selected)
        {
            var window = GetWindow<PixelToVoxelWindow>("Pixel To Voxel");
            window.group = selected; window.Invalidate(); window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(540, 600);
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed += Invalidate;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= Invalidate;
            preview?.Dispose(); preview = null;
        }

        private void OnProjectChanged()
        {
            if (working || analyses == null) return;
            try { if (analyzedFingerprint != Fingerprint() || sourceFingerprint != InputFingerprint()) Invalidate(); }
            catch { Invalidate(); }
        }

        private void Invalidate()
        {
            analyses = null; analyzedFingerprint = null; page = 0;
            preview?.Dispose(); preview = null; Repaint();
        }

        private string Fingerprint() => group ? EditorJsonUtility.ToJson(group) : "";
        private string InputFingerprint() => group ? string.Join("|", SourceReader.Expand(group.sources)
            .Select(s => SourceReader.Id(s) + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(s)))) : "";

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Pixel To Voxel", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("PNG / Sprite → extruded mesh → prefab", EditorStyles.miniLabel);
            EditorGUI.BeginChangeCheck();
            group = (ConversionGroup)EditorGUILayout.ObjectField("Conversion Group", group, typeof(ConversionGroup), false);
            if (EditorGUI.EndChangeCheck()) Invalidate();
            if (GUILayout.Button("New Conversion Group…")) CreateGroup();
            if (!group)
            {
                EditorGUILayout.HelpBox("Create or select a group to save sources, settings and generation history. No runtime component is required.", MessageType.Info);
                return;
            }
            if (analyses != null && analyzedFingerprint != Fingerprint()) Invalidate();
            scroll = EditorGUILayout.BeginScrollView(scroll);
            try
            {
                DrawInputs();
                DrawSettings();
                if (!string.IsNullOrEmpty(notice)) EditorGUILayout.HelpBox(notice, MessageType.Info);
                if (GUILayout.Button("1. Analyze Inputs", GUILayout.Height(30))) RunAnalysis();
                DrawAnalyses();
                Rect rect = GUILayoutUtility.GetRect(100, 260, GUILayout.ExpandWidth(true));
                if (preview == null) preview = new VoxelPreview();
                preview.Draw(rect, Repaint);
                EditorGUILayout.LabelField("Drag to orbit · Scroll to zoom", EditorStyles.centeredGreyMiniLabel);
                DrawHistory();
            }
            finally { EditorGUILayout.EndScrollView(); }
        }

        private void DrawInputs()
        {
            var serialized = new SerializedObject(group);
            serialized.Update();
            EditorGUILayout.PropertyField(serialized.FindProperty("sources"), new GUIContent("Inputs (PNG, Sprite, project folder)"), true);
            if (serialized.ApplyModifiedProperties()) Invalidate();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Project Selection")) AddSources(Selection.objects);
                if (GUILayout.Button("Clear Inputs"))
                {
                    Undo.RecordObject(group, "Clear voxel inputs"); group.sources.Clear(); EditorUtility.SetDirty(group); Invalidate();
                }
            }
            Rect drop = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "Drop project PNGs, Sprites or folders here");
            Event e = Event.current;
            if (drop.Contains(e.mousePosition) && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); AddSources(DragAndDrop.objectReferences); }
                e.Use();
            }
        }

        private void AddSources(IEnumerable<Object> objects)
        {
            Undo.RecordObject(group, "Add voxel inputs");
            foreach (Object obj in objects)
            {
                if (!obj || group.sources.Contains(obj)) continue;
                string path = AssetDatabase.GetAssetPath(obj);
                if (obj is Texture2D || obj is Sprite || AssetDatabase.IsValidFolder(path)) group.sources.Add(obj);
            }
            EditorUtility.SetDirty(group); Invalidate();
        }

        private void DrawSettings()
        {
            EditorGUILayout.Space();
            preset = (ConversionPreset)EditorGUILayout.ObjectField("Settings Preset", preset, typeof(ConversionPreset), false);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!preset))
                    if (GUILayout.Button("Apply Preset"))
                    {
                        Undo.RecordObject(group, "Apply voxel preset");
                        group.settings = JsonUtility.FromJson<ConversionSettings>(JsonUtility.ToJson(preset.settings));
                        EditorUtility.SetDirty(group); Invalidate();
                    }
                if (GUILayout.Button("Save Settings as Preset…")) SavePreset();
            }
            var serialized = new SerializedObject(group);
            serialized.Update();
            SerializedProperty settings = serialized.FindProperty("settings");
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("geometry"), true);
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("facing"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("collider"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("existingOutputs"));
            using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(group.generatedRoot)))
            {
                EditorGUILayout.PropertyField(settings.FindPropertyRelative("sharedAtlas"));
                SerializedProperty size = settings.FindPropertyRelative("atlasSize");
                int[] sizes = { 16, 32, 64, 128, 256, 512, 1024 };
                size.intValue = EditorGUILayout.IntPopup("Atlas Size", size.intValue, sizes.Select(s => s + " × " + s).ToArray(), sizes);
                EditorGUILayout.PropertyField(serialized.FindProperty("outputRoot"));
            }
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("warningDimension"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("warningSolidPixels"));
            EditorGUILayout.PropertyField(settings.FindPropertyRelative("warningVertices"));
            if (serialized.ApplyModifiedProperties()) Invalidate();
            EditorGUILayout.HelpBox("Depth is measured in pixel-size layers. Alpha is binary after thresholding. Pixel Step samples the center of each block; image dimensions must be divisible by it.", MessageType.None);
            if (!string.IsNullOrEmpty(group.generatedRoot))
                EditorGUILayout.LabelField("Output: " + group.generatedRoot, EditorStyles.wordWrappedMiniLabel);
        }

        private void RunAnalysis()
        {
            working = true;
            try
            {
                Invalidate();
                analyses = ConversionPipeline.Analyze(group);
                analyzedFingerprint = Fingerprint();
                sourceFingerprint = InputFingerprint();
                notice = $"{analyses.Count} inputs · {analyses.Count(a => !string.IsNullOrEmpty(a.Warning))} warnings · {analyses.Count(a => !string.IsNullOrEmpty(a.Error))} errors";
            }
            catch (OperationCanceledException) { notice = "Analysis cancelled. No output was generated."; }
            catch (Exception e) { notice = e.Message; }
            finally { working = false; }
        }

        private void DrawAnalyses()
        {
            if (analyses == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preflight", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Include All Valid")) foreach (SourceAnalysis a in analyses) a.Included = string.IsNullOrEmpty(a.Error);
                if (GUILayout.Button("Exclude Warnings")) foreach (SourceAnalysis a in analyses) if (!string.IsNullOrEmpty(a.Warning)) a.Included = false;
            }
            int pages = Math.Max(1, (analyses.Count + PageSize - 1) / PageSize);
            page = Mathf.Clamp(page, 0, pages - 1);
            if (pages > 1)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(page == 0)) if (GUILayout.Button("Previous")) page--;
                    GUILayout.Label($"{page + 1} / {pages}");
                    using (new EditorGUI.DisabledScope(page == pages - 1)) if (GUILayout.Button("Next")) page++;
                }
            }
            foreach (SourceAnalysis a in analyses.Skip(page * PageSize).Take(PageSize))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(a.Error))) a.Included = EditorGUILayout.Toggle(a.Included, GUILayout.Width(18));
                        EditorGUILayout.ObjectField(a.Source, typeof(Object), false);
                        using (new EditorGUI.DisabledScope(a.Grid == null))
                            if (GUILayout.Button("Preview", GUILayout.Width(65)))
                            {
                                try { if (preview == null) preview = new VoxelPreview(); preview.Set(a, group.settings); }
                                catch (Exception e) { notice = e.Message; }
                            }
                    }
                    if (a.Statistics != null)
                    {
                        var s = a.Statistics;
                        EditorGUILayout.LabelField($"{a.SourceWidth}×{a.SourceHeight} → {a.Grid?.Width}×{a.Grid?.Height} · {s.SolidPixels:N0} cells · {s.Vertices:N0} vertices · {s.Triangles:N0} triangles", EditorStyles.wordWrappedMiniLabel);
                        float unit = group.settings.geometry.pixelSize;
                        if (a.Grid != null) EditorGUILayout.LabelField($"Size before facing: {a.Grid.Width * unit:G4} × {a.Grid.Height * unit:G4} × {group.settings.geometry.depth * unit:G4} units", EditorStyles.miniLabel);
                    }
                    if (!string.IsNullOrEmpty(a.Error)) EditorGUILayout.HelpBox(a.Error, MessageType.Error);
                    else if (!string.IsNullOrEmpty(a.Warning)) EditorGUILayout.HelpBox(a.Warning + ". Generation may take time and memory.", MessageType.Warning);
                    if (!string.IsNullOrEmpty(a.Result)) EditorGUILayout.LabelField(a.Result, EditorStyles.wordWrappedMiniLabel);
                }
            }
            using (new EditorGUI.DisabledScope(!analyses.Any(a => a.Included && a.Grid != null)))
                if (GUILayout.Button("2. Generate Selected", GUILayout.Height(32))) Generate();
        }

        private void Generate()
        {
            bool includeWarnings = true;
            if (analyses.Any(a => a.Included && !string.IsNullOrEmpty(a.Warning)))
            {
                int choice = EditorUtility.DisplayDialogComplex("Large pixel-art inputs",
                    "Some selected images exceed warning thresholds. Review their source size and estimated vertices in Preflight.",
                    "Include Warnings", "Cancel", "Exclude Warnings");
                if (choice == 1) return;
                includeWarnings = choice == 0;
            }
            working = true;
            try
            {
                if (analyzedFingerprint != Fingerprint()) throw new InvalidOperationException("Settings changed. Analyze Inputs again before generating.");
                if (sourceFingerprint != InputFingerprint()) throw new InvalidOperationException("Source images changed. Analyze Inputs again before generating.");
                notice = ConversionPipeline.Generate(group, analyses, includeWarnings).ToString();
                analyzedFingerprint = Fingerprint();
            }
            catch (Exception e) { notice = e.Message; }
            finally { working = false; }
        }

        private void CreateGroup()
        {
            string path = EditorUtility.SaveFilePanelInProject("Create Conversion Group", "VoxelGroup", "asset", "Choose a project location.");
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<ConversionGroup>();
            AssetDatabase.CreateAsset(created, AssetDatabase.GenerateUniqueAssetPath(path));
            group = created; Invalidate(); AddSources(Selection.objects); EditorGUIUtility.PingObject(group);
        }

        private void SavePreset()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Settings Preset", "VoxelPreset", "asset", "Choose a project location.");
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<ConversionPreset>();
            created.settings = JsonUtility.FromJson<ConversionSettings>(JsonUtility.ToJson(group.settings));
            AssetDatabase.CreateAsset(created, AssetDatabase.GenerateUniqueAssetPath(path)); preset = created;
        }

        private void DrawHistory()
        {
            showHistory = EditorGUILayout.Foldout(showHistory, $"Generation History ({group.history.Count})", true);
            if (!showHistory) return;
            EditorGUILayout.LabelField(group.lastSummary ?? "", EditorStyles.wordWrappedMiniLabel);
            foreach (GenerationRecord record in group.history)
            {
                EditorGUILayout.ObjectField(record.prefab, typeof(GameObject), false);
                EditorGUILayout.LabelField($"{record.generatedUtc} · {record.vertices:N0} vertices · {record.triangles:N0} triangles", EditorStyles.wordWrappedMiniLabel);
            }
            if (GUILayout.Button("Select Group Asset")) Selection.activeObject = group;
        }
    }

    [CustomEditor(typeof(ConversionGroup))]
    public sealed class ConversionGroupInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (GUILayout.Button("Open Pixel To Voxel")) PixelToVoxelWindow.Open((ConversionGroup)target);
            DrawDefaultInspector();
        }
    }
}
