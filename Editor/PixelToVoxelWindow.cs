using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MineFarm.PixelToVoxel.Editor
{
    public sealed class PixelToVoxelWindow : EditorWindow
    {
        private const string LayoutPath = "Packages/net.minefarm.pixel-to-voxel/Editor/PixelToVoxelWindow.uxml";
        internal const string StylePath = "Packages/net.minefarm.pixel-to-voxel/Editor/PixelToVoxelWindow.uss";
        private const int PageSize = 30;
        private static readonly int[] AtlasSizes = { 16, 32, 64, 128, 256, 512, 1024 };

        [SerializeField] private ConversionGroup group;
        [SerializeField] private ConversionPreset preset;
        [SerializeField] private bool historyExpanded;

        private List<SourceAnalysis> analyses;
        private readonly List<VisualElement> lockedAfterGeneration = new List<VisualElement>();
        private VoxelPreview preview;
        private Texture2D windowIcon;
        private SerializedObject serializedGroup;
        private string notice, analyzedFingerprint, sourceFingerprint;
        private string previewLabel;
        private NoticeTone noticeTone;
        private int page;
        private bool working;

        private ObjectField groupField, presetField;
        private PopupField<int> atlasSizeField;
        private VisualElement temporaryNotice, noticePanel, preflightSection, pagination, analysisList;
        private VisualElement historyList, dropZone, previewHost, sourcesFieldSlot, presetFieldSlot;
        private VisualElement geometryFields, outputFields, safetyFields;
        private Button groupPrimaryButton, temporaryButton, addSelectionButton, clearInputsButton;
        private Button applyPresetButton, savePresetButton, analyzeButton, generateButton;
        private Button includeAllButton, excludeWarningsButton, previousPageButton, nextPageButton, selectGroupButton;
        private Label workingBadge, noticeLabel, outputPathLabel, previewStatusLabel;
        private Label analysisSummaryLabel, pageLabel, historySummaryLabel;
        private Foldout historyFoldout;
        private IMGUIContainer previewContainer;

        private enum NoticeTone { Info, Success, Error }

        [MenuItem("Tools/Pixel To Voxel")]
        public static void Open()
        {
            var window = GetWindow<PixelToVoxelWindow>();
            window.EnsureWindowTitle();
            window.Show();
        }

        public static void Open(ConversionGroup selected)
        {
            var window = GetWindow<PixelToVoxelWindow>();
            window.EnsureWindowTitle();
            window.SetGroup(selected);
            window.Show();
        }

        private void OnEnable()
        {
            minSize = new Vector2(640, 520);
            EnsureWindowTitle();
            EditorApplication.projectChanged += OnProjectChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            if (!group)
            {
                group = MakeTemporaryGroup();
                AddSourcesToGroup(Selection.objects, false);
            }
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            preview?.Dispose();
            preview = null;
            if (windowIcon) DestroyImmediate(windowIcon);
            windowIcon = null;
            ReleaseTemporaryGroup();
        }

        public void CreateGUI()
        {
            EnsureWindowTitle();
            rootVisualElement.Clear();
            VisualTreeAsset layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LayoutPath);
            if (!layout)
            {
                rootVisualElement.Add(new HelpBox("Pixel To Voxel UI layout is missing: " + LayoutPath, HelpBoxMessageType.Error));
                return;
            }

            layout.CloneTree(rootVisualElement);
            CacheElements();
            BuildObjectFields();
            BuildPreview();
            WireEvents();
            BindGroup();
            RefreshAll();
        }

        private void EnsureWindowTitle()
        {
            if (!windowIcon) windowIcon = CreateWindowIcon();
            titleContent = new GUIContent("Pixel To Voxel", windowIcon, "Convert pixel art into voxel meshes and prefabs");
        }

        private static Texture2D CreateWindowIcon()
        {
            const int size = 32;
            var pixels = Enumerable.Repeat(new Color32(0, 0, 0, 0), size * size).ToArray();
            var top = new[] { new Vector2Int(16, 2), new Vector2Int(29, 9), new Vector2Int(16, 16), new Vector2Int(3, 9) };
            var left = new[] { new Vector2Int(3, 9), new Vector2Int(16, 16), new Vector2Int(16, 30), new Vector2Int(3, 23) };
            var right = new[] { new Vector2Int(16, 16), new Vector2Int(29, 9), new Vector2Int(29, 23), new Vector2Int(16, 30) };
            FillPolygon(pixels, size, top, new Color32(102, 238, 203, 255));
            FillPolygon(pixels, size, left, new Color32(41, 151, 127, 255));
            FillPolygon(pixels, size, right, new Color32(66, 111, 184, 255));
            var outline = new Color32(13, 29, 34, 255);
            DrawPolygon(pixels, size, top, outline);
            DrawPolygon(pixels, size, left, outline);
            DrawPolygon(pixels, size, right, outline);
            var icon = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            {
                name = "Pixel To Voxel Window Icon",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave
            };
            icon.SetPixels32(pixels);
            icon.Apply(false, true);
            return icon;
        }

        private static void FillPolygon(Color32[] pixels, int size, Vector2Int[] points, Color32 color)
        {
            int minX = points.Min(point => point.x), maxX = points.Max(point => point.x);
            int minY = points.Min(point => point.y), maxY = points.Max(point => point.y);
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (Contains(points, x + 0.5f, y + 0.5f)) pixels[y * size + x] = color;
        }

        private static bool Contains(Vector2Int[] points, float x, float y)
        {
            bool inside = false;
            for (int i = 0, previous = points.Length - 1; i < points.Length; previous = i++)
            {
                Vector2Int a = points[i], b = points[previous];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private static void DrawPolygon(Color32[] pixels, int size, Vector2Int[] points, Color32 color)
        {
            for (int i = 0; i < points.Length; i++) DrawLine(pixels, size, points[i], points[(i + 1) % points.Length], color);
        }

        private static void DrawLine(Color32[] pixels, int size, Vector2Int from, Vector2Int to, Color32 color)
        {
            int x = from.x, y = from.y;
            int dx = Math.Abs(to.x - x), stepX = x < to.x ? 1 : -1;
            int dy = -Math.Abs(to.y - y), stepY = y < to.y ? 1 : -1;
            int error = dx + dy;
            while (true)
            {
                if (x >= 0 && y >= 0 && x < size && y < size) pixels[y * size + x] = color;
                if (x == to.x && y == to.y) break;
                int doubled = error * 2;
                if (doubled >= dy) { error += dy; x += stepX; }
                if (doubled <= dx) { error += dx; y += stepY; }
            }
        }

        private void CacheElements()
        {
            temporaryNotice = Required<VisualElement>("temporaryNotice");
            noticePanel = Required<VisualElement>("noticePanel");
            preflightSection = Required<VisualElement>("preflightSection");
            pagination = Required<VisualElement>("pagination");
            analysisList = Required<VisualElement>("analysisList");
            historyList = Required<VisualElement>("historyList");
            dropZone = Required<VisualElement>("dropZone");
            previewHost = Required<VisualElement>("previewHost");
            sourcesFieldSlot = Required<VisualElement>("sourcesFieldSlot");
            presetFieldSlot = Required<VisualElement>("presetFieldSlot");
            geometryFields = Required<VisualElement>("geometryFields");
            outputFields = Required<VisualElement>("outputFields");
            safetyFields = Required<VisualElement>("safetyFields");

            groupPrimaryButton = Required<Button>("groupPrimaryButton");
            temporaryButton = Required<Button>("temporaryButton");
            addSelectionButton = Required<Button>("addSelectionButton");
            clearInputsButton = Required<Button>("clearInputsButton");
            applyPresetButton = Required<Button>("applyPresetButton");
            savePresetButton = Required<Button>("savePresetButton");
            analyzeButton = Required<Button>("analyzeButton");
            generateButton = Required<Button>("generateButton");
            includeAllButton = Required<Button>("includeAllButton");
            excludeWarningsButton = Required<Button>("excludeWarningsButton");
            previousPageButton = Required<Button>("previousPageButton");
            nextPageButton = Required<Button>("nextPageButton");
            selectGroupButton = Required<Button>("selectGroupButton");

            workingBadge = Required<Label>("workingBadge");
            noticeLabel = Required<Label>("noticeLabel");
            outputPathLabel = Required<Label>("outputPathLabel");
            previewStatusLabel = Required<Label>("previewStatusLabel");
            analysisSummaryLabel = Required<Label>("analysisSummaryLabel");
            pageLabel = Required<Label>("pageLabel");
            historySummaryLabel = Required<Label>("historySummaryLabel");
            historyFoldout = Required<Foldout>("historyFoldout");
        }

        private T Required<T>(string name) where T : VisualElement
        {
            T element = rootVisualElement.Q<T>(name);
            if (element == null) throw new InvalidOperationException($"Pixel To Voxel UI is missing '{name}'.");
            return element;
        }

        private void BuildObjectFields()
        {
            groupField = new ObjectField
            {
                name = "groupField",
                objectType = typeof(ConversionGroup),
                allowSceneObjects = false
            };
            Required<VisualElement>("groupFieldSlot").Add(groupField);

            presetField = new ObjectField("Preset")
            {
                name = "presetField",
                objectType = typeof(ConversionPreset),
                allowSceneObjects = false
            };
            presetField.AddToClassList("ptv-field");
            presetFieldSlot.Add(presetField);
        }

        private void BuildPreview()
        {
            previewContainer = new IMGUIContainer(DrawPreview) { name = "previewContainer" };
            previewContainer.AddToClassList("ptv-preview");
            previewHost.Add(previewContainer);
        }

        private void WireEvents()
        {
            groupField.RegisterValueChangedCallback(evt => SetGroup(evt.newValue as ConversionGroup));
            presetField.RegisterValueChangedCallback(evt =>
            {
                preset = evt.newValue as ConversionPreset;
                RefreshState();
            });
            groupPrimaryButton.clicked += CreateGroup;
            temporaryButton.clicked += () => CreateTemporaryGroup(true);
            addSelectionButton.clicked += () => AddSources(Selection.objects);
            clearInputsButton.clicked += ClearInputs;
            applyPresetButton.clicked += ApplyPreset;
            savePresetButton.clicked += SavePreset;
            analyzeButton.clicked += RunAnalysis;
            generateButton.clicked += Generate;
            includeAllButton.clicked += () =>
            {
                foreach (SourceAnalysis analysis in analyses ?? Enumerable.Empty<SourceAnalysis>())
                    analysis.Included = string.IsNullOrEmpty(analysis.Error);
                RefreshAnalyses();
            };
            excludeWarningsButton.clicked += () =>
            {
                foreach (SourceAnalysis analysis in analyses ?? Enumerable.Empty<SourceAnalysis>())
                    if (!string.IsNullOrEmpty(analysis.Warning)) analysis.Included = false;
                RefreshAnalyses();
            };
            previousPageButton.clicked += () => { page--; RefreshAnalyses(); };
            nextPageButton.clicked += () => { page++; RefreshAnalyses(); };
            selectGroupButton.clicked += () => Selection.activeObject = group;
            historyFoldout.value = historyExpanded;
            historyFoldout.RegisterValueChangedCallback(evt => historyExpanded = evt.newValue);

            dropZone.RegisterCallback<DragUpdatedEvent>(OnDragUpdated);
            dropZone.RegisterCallback<DragPerformEvent>(OnDragPerform);
            dropZone.RegisterCallback<DragLeaveEvent>(_ => dropZone.RemoveFromClassList("ptv-drop-zone--active"));
        }

        private void BindGroup()
        {
            sourcesFieldSlot?.Clear();
            geometryFields?.Clear();
            outputFields?.Clear();
            safetyFields?.Clear();
            lockedAfterGeneration.Clear();
            serializedGroup = null;
            atlasSizeField = null;

            if (!group || sourcesFieldSlot == null) return;
            serializedGroup = new SerializedObject(group);
            serializedGroup.Update();

            PropertyField sources = AddPropertyField(sourcesFieldSlot, "sources", "Assets");
            sources.AddToClassList("ptv-sources-field");

            AddPropertyField(geometryFields, "settings.geometry.pixelStep", "Pixel Step");
            AddPropertyField(geometryFields, "settings.geometry.pixelSize", "Pixel Size");
            AddPropertyField(geometryFields, "settings.geometry.depth", "Depth");
            AddPropertyField(geometryFields, "settings.geometry.alphaThreshold", "Alpha Threshold");
            AddPropertyField(geometryFields, "settings.geometry.trimTransparent", "Trim Transparent");
            AddPropertyField(geometryFields, "settings.geometry.flipX", "Flip X");
            AddPropertyField(geometryFields, "settings.geometry.flipY", "Flip Y");
            AddPropertyField(geometryFields, "settings.geometry.pivot", "Pivot");

            AddPropertyField(outputFields, "settings.facing", "Facing");
            AddPropertyField(outputFields, "settings.collider", "Collider");
            AddPropertyField(outputFields, "settings.existingOutputs", "Existing Outputs");
            lockedAfterGeneration.Add(AddPropertyField(outputFields, "settings.sharedAtlas", "Shared Atlas"));
            AddAtlasSizeField();
            lockedAfterGeneration.Add(AddPropertyField(outputFields, "outputRoot", "Output Root"));

            AddPropertyField(safetyFields, "settings.warningDimension", "Dimension Warning");
            AddPropertyField(safetyFields, "settings.warningSolidPixels", "Solid Pixel Warning");
            AddPropertyField(safetyFields, "settings.warningVertices", "Vertex Warning");
            UpdateGenerationLocks();
        }

        private PropertyField AddPropertyField(VisualElement parent, string path, string label)
        {
            SerializedProperty property = serializedGroup.FindProperty(path);
            if (property == null) throw new InvalidOperationException("Missing serialized property: " + path);
            var field = new PropertyField(property, label) { name = path.Replace('.', '-') };
            field.AddToClassList("ptv-field");
            field.BindProperty(property);
            field.RegisterCallback<SerializedPropertyChangeEvent>(_ => OnSerializedGroupChanged());
            parent.Add(field);
            return field;
        }

        private void AddAtlasSizeField()
        {
            List<int> choices = AtlasSizes.ToList();
            if (!choices.Contains(group.settings.atlasSize))
            {
                choices.Add(group.settings.atlasSize);
                choices.Sort();
            }
            atlasSizeField = new PopupField<int>("Atlas Size", choices, group.settings.atlasSize,
                size => $"{size} × {size}", size => $"{size} × {size}") { name = "atlasSizeField" };
            atlasSizeField.AddToClassList("ptv-field");
            atlasSizeField.RegisterValueChangedCallback(evt =>
            {
                if (!group || group.settings.atlasSize == evt.newValue) return;
                Undo.RecordObject(group, "Change voxel atlas size");
                group.settings.atlasSize = evt.newValue;
                EditorUtility.SetDirty(group);
                Invalidate();
            });
            outputFields.Add(atlasSizeField);
            lockedAfterGeneration.Add(atlasSizeField);
        }

        private void OnSerializedGroupChanged()
        {
            if (!group) return;
            EditorUtility.SetDirty(group);
            Invalidate();
            UpdateGenerationLocks();
            RefreshOutputPath();
        }

        private void OnProjectChanged()
        {
            if (working || analyses == null) return;
            try
            {
                if (analyzedFingerprint != Fingerprint() || sourceFingerprint != InputFingerprint()) Invalidate();
            }
            catch { Invalidate(); }
        }

        private void OnUndoRedo()
        {
            Invalidate();
            BindGroup();
            RefreshAll();
        }

        private void Invalidate()
        {
            analyses = null;
            analyzedFingerprint = null;
            sourceFingerprint = null;
            page = 0;
            previewLabel = null;
            preview?.Dispose();
            preview = null;
            RefreshAnalyses();
            RefreshPreview();
            RefreshState();
        }

        private string Fingerprint() => group ? EditorJsonUtility.ToJson(group) : "";

        private string InputFingerprint() => group ? string.Join("|", SourceReader.Expand(group.sources)
            .Select(source => SourceReader.Id(source) + ":" + AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(source)))) : "";

        private void RefreshAll()
        {
            RefreshState();
            RefreshNotice();
            RefreshAnalyses();
            RefreshPreview();
            RefreshHistory();
            RefreshOutputPath();
        }

        private void RefreshState()
        {
            if (groupField == null) return;
            groupField.SetValueWithoutNotify(group);
            presetField.SetValueWithoutNotify(preset);
            bool temporary = IsTemporaryGroup;
            groupPrimaryButton.text = temporary ? "Save Group…" : "New Group…";
            temporaryButton.text = temporary ? "Reset" : "Use Temporary";
            temporaryNotice.EnableInClassList("ptv-hidden", !temporary);
            workingBadge.EnableInClassList("ptv-hidden", !working);
            groupPrimaryButton.SetEnabled(!working);
            temporaryButton.SetEnabled(!working);
            addSelectionButton.SetEnabled(!working && group);
            clearInputsButton.SetEnabled(!working && group && group.sources.Count > 0);
            applyPresetButton.SetEnabled(!working && group && preset);
            savePresetButton.SetEnabled(!working && group);
            analyzeButton.SetEnabled(!working && group && group.sources.Count > 0);
            generateButton.SetEnabled(!working && analyses != null && analyses.Any(a => a.Included && a.Grid != null));
            selectGroupButton.EnableInClassList("ptv-hidden", temporary || !group);
            UpdateGenerationLocks();
        }

        private void UpdateGenerationLocks()
        {
            bool editable = group && string.IsNullOrEmpty(group.generatedRoot) && !working;
            foreach (VisualElement element in lockedAfterGeneration) element.SetEnabled(editable);
        }

        private void SetNotice(string message, NoticeTone tone)
        {
            notice = message;
            noticeTone = tone;
            RefreshNotice();
        }

        private void RefreshNotice()
        {
            if (noticePanel == null) return;
            bool visible = !string.IsNullOrEmpty(notice);
            noticePanel.EnableInClassList("ptv-hidden", !visible);
            noticePanel.EnableInClassList("ptv-banner--success", visible && noticeTone == NoticeTone.Success);
            noticePanel.EnableInClassList("ptv-banner--error", visible && noticeTone == NoticeTone.Error);
            noticeLabel.text = notice ?? "";
        }

        private void RefreshOutputPath()
        {
            if (outputPathLabel == null || !group) return;
            outputPathLabel.text = string.IsNullOrEmpty(group.generatedRoot)
                ? $"Output will be created below {group.outputRoot}."
                : "Generated output  ·  " + group.generatedRoot;
        }

        private void OnDragUpdated(DragUpdatedEvent evt)
        {
            if (!HasSupportedSource(DragAndDrop.objectReferences)) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            dropZone.AddToClassList("ptv-drop-zone--active");
            evt.StopPropagation();
        }

        private void OnDragPerform(DragPerformEvent evt)
        {
            if (!HasSupportedSource(DragAndDrop.objectReferences)) return;
            DragAndDrop.AcceptDrag();
            dropZone.RemoveFromClassList("ptv-drop-zone--active");
            AddSources(DragAndDrop.objectReferences);
            evt.StopPropagation();
        }

        private static bool HasSupportedSource(IEnumerable<Object> objects)
        {
            return objects.Any(source => source && (source is Texture2D || source is Sprite
                || AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(source))));
        }

        private void AddSources(IEnumerable<Object> objects)
        {
            if (!group || !AddSourcesToGroup(objects, true)) return;
            Invalidate();
            BindGroup();
            RefreshAll();
        }

        private bool AddSourcesToGroup(IEnumerable<Object> objects, bool recordUndo)
        {
            if (!group) return false;
            List<Object> additions = objects.Where(source => source && !group.sources.Contains(source))
                .Where(source => source is Texture2D || source is Sprite
                    || AssetDatabase.IsValidFolder(AssetDatabase.GetAssetPath(source))).ToList();
            if (additions.Count == 0) return false;
            if (recordUndo) Undo.RecordObject(group, "Add voxel inputs");
            group.sources.AddRange(additions);
            EditorUtility.SetDirty(group);
            return true;
        }

        private void ClearInputs()
        {
            if (!group || group.sources.Count == 0) return;
            Undo.RecordObject(group, "Clear voxel inputs");
            group.sources.Clear();
            EditorUtility.SetDirty(group);
            Invalidate();
            BindGroup();
            RefreshAll();
        }

        private void ApplyPreset()
        {
            if (!group || !preset) return;
            Undo.RecordObject(group, "Apply voxel preset");
            group.settings = JsonUtility.FromJson<ConversionSettings>(JsonUtility.ToJson(preset.settings));
            EditorUtility.SetDirty(group);
            Invalidate();
            BindGroup();
            RefreshAll();
            SetNotice("Preset applied. Analyze the inputs again before generating.", NoticeTone.Info);
        }

        private void RunAnalysis()
        {
            working = true;
            RefreshState();
            try
            {
                Invalidate();
                analyses = ConversionPipeline.Analyze(group);
                analyzedFingerprint = Fingerprint();
                sourceFingerprint = InputFingerprint();
                int warnings = analyses.Count(a => !string.IsNullOrEmpty(a.Warning));
                int errors = analyses.Count(a => !string.IsNullOrEmpty(a.Error));
                SetNotice($"Preflight complete  ·  {analyses.Count} inputs  ·  {warnings} warnings  ·  {errors} errors", NoticeTone.Info);
            }
            catch (OperationCanceledException)
            {
                SetNotice("Analysis cancelled. No output was generated.", NoticeTone.Info);
            }
            catch (Exception e)
            {
                SetNotice(e.Message, NoticeTone.Error);
            }
            finally
            {
                working = false;
                RefreshAnalyses();
                RefreshState();
            }
        }

        private void RefreshAnalyses()
        {
            if (preflightSection == null) return;
            analysisList.Clear();
            bool visible = analyses != null;
            preflightSection.EnableInClassList("ptv-hidden", !visible);
            if (!visible) return;

            int warnings = analyses.Count(a => !string.IsNullOrEmpty(a.Warning));
            int errors = analyses.Count(a => !string.IsNullOrEmpty(a.Error));
            analysisSummaryLabel.text = $"{analyses.Count} sources  ·  {warnings} warnings  ·  {errors} errors";
            int pages = Math.Max(1, (analyses.Count + PageSize - 1) / PageSize);
            page = Mathf.Clamp(page, 0, pages - 1);
            pagination.EnableInClassList("ptv-hidden", pages <= 1);
            pageLabel.text = $"{page + 1} / {pages}";
            previousPageButton.SetEnabled(page > 0);
            nextPageButton.SetEnabled(page < pages - 1);

            foreach (SourceAnalysis analysis in analyses.Skip(page * PageSize).Take(PageSize))
                analysisList.Add(BuildAnalysisCard(analysis));
            RefreshState();
        }

        private VisualElement BuildAnalysisCard(SourceAnalysis analysis)
        {
            var card = new VisualElement();
            card.AddToClassList("ptv-analysis-card");
            var top = new VisualElement();
            top.AddToClassList("ptv-analysis-card__top");

            var included = new Toggle { value = analysis.Included };
            included.AddToClassList("ptv-analysis-card__toggle");
            included.SetEnabled(string.IsNullOrEmpty(analysis.Error));
            included.RegisterValueChangedCallback(evt =>
            {
                analysis.Included = evt.newValue;
                RefreshState();
            });
            top.Add(included);

            var source = new ObjectField
            {
                objectType = typeof(Object),
                allowSceneObjects = false,
                value = analysis.Source
            };
            source.AddToClassList("ptv-analysis-card__source");
            source.SetEnabled(false);
            top.Add(source);

            var status = new Label();
            status.AddToClassList("ptv-status");
            if (!string.IsNullOrEmpty(analysis.Error))
            {
                status.text = "ERROR";
                status.AddToClassList("ptv-status--error");
            }
            else if (!string.IsNullOrEmpty(analysis.Warning))
            {
                status.text = "WARNING";
                status.AddToClassList("ptv-status--warning");
            }
            else
            {
                status.text = "READY";
                status.AddToClassList("ptv-status--ready");
            }
            top.Add(status);

            var previewButton = new Button(() => PreviewAnalysis(analysis)) { text = "Preview" };
            previewButton.AddToClassList("ptv-button");
            previewButton.AddToClassList("ptv-button--quiet");
            previewButton.SetEnabled(analysis.Grid != null);
            top.Add(previewButton);
            card.Add(top);

            if (analysis.Statistics != null)
            {
                var stats = analysis.Statistics;
                var statsLabel = new Label($"{analysis.SourceWidth}×{analysis.SourceHeight}  →  {analysis.Grid?.Width}×{analysis.Grid?.Height}  ·  {stats.SolidPixels:N0} cells  ·  {stats.Vertices:N0} vertices  ·  {stats.Triangles:N0} triangles");
                statsLabel.AddToClassList("ptv-analysis-card__stats");
                card.Add(statsLabel);
                if (analysis.Grid != null)
                {
                    float unit = group.settings.geometry.pixelSize;
                    var sizeLabel = new Label($"Size before facing  ·  {analysis.Grid.Width * unit:G4} × {analysis.Grid.Height * unit:G4} × {group.settings.geometry.depth * unit:G4} units");
                    sizeLabel.AddToClassList("ptv-analysis-card__size");
                    card.Add(sizeLabel);
                }
            }

            if (!string.IsNullOrEmpty(analysis.Error))
            {
                var error = new HelpBox(analysis.Error, HelpBoxMessageType.Error);
                error.AddToClassList("ptv-analysis-help");
                card.Add(error);
            }
            else if (!string.IsNullOrEmpty(analysis.Warning))
            {
                var warning = new HelpBox(analysis.Warning + ". Generation may take time and memory.", HelpBoxMessageType.Warning);
                warning.AddToClassList("ptv-analysis-help");
                card.Add(warning);
            }
            if (!string.IsNullOrEmpty(analysis.Result))
            {
                var result = new Label(analysis.Result);
                result.AddToClassList("ptv-analysis-card__result");
                card.Add(result);
            }
            return card;
        }

        private void PreviewAnalysis(SourceAnalysis analysis)
        {
            try
            {
                if (preview == null) preview = new VoxelPreview();
                preview.Set(analysis, group.settings);
                previewLabel = analysis.Label;
                RefreshPreview();
            }
            catch (Exception e)
            {
                SetNotice(e.Message, NoticeTone.Error);
            }
        }

        private void DrawPreview()
        {
            if (preview == null) preview = new VoxelPreview();
            Rect content = previewContainer.contentRect;
            var rect = new Rect(0, 0, content.width, content.height);
            preview.Draw(rect, () => previewContainer.MarkDirtyRepaint());
        }

        private void RefreshPreview()
        {
            if (previewStatusLabel == null) return;
            previewStatusLabel.text = string.IsNullOrEmpty(previewLabel) ? "No selection" : previewLabel;
            previewContainer?.MarkDirtyRepaint();
        }

        private void Generate()
        {
            if (analyses == null) return;
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
            RefreshState();
            try
            {
                if (analyzedFingerprint != Fingerprint())
                    throw new InvalidOperationException("Settings changed. Analyze Inputs again before generating.");
                if (sourceFingerprint != InputFingerprint())
                    throw new InvalidOperationException("Source images changed. Analyze Inputs again before generating.");
                BatchResult result = ConversionPipeline.Generate(group, analyses, includeWarnings);
                analyzedFingerprint = Fingerprint();
                SetNotice(result.ToString(), result.Failed == 0 ? NoticeTone.Success : NoticeTone.Error);
            }
            catch (Exception e)
            {
                SetNotice(e.Message, NoticeTone.Error);
            }
            finally
            {
                working = false;
                RefreshAnalyses();
                RefreshHistory();
                RefreshOutputPath();
                RefreshState();
            }
        }

        private void RefreshHistory()
        {
            if (historyList == null || !group) return;
            historyFoldout.text = $"Generation History  ·  {group.history.Count}";
            historySummaryLabel.text = string.IsNullOrEmpty(group.lastSummary)
                ? "Generated outputs will appear here."
                : group.lastSummary;
            historyList.Clear();
            foreach (GenerationRecord record in group.history)
            {
                var row = new VisualElement();
                row.AddToClassList("ptv-history-row");
                var asset = new ObjectField
                {
                    objectType = typeof(GameObject),
                    allowSceneObjects = false,
                    value = record.prefab
                };
                asset.AddToClassList("ptv-history-row__asset");
                asset.SetEnabled(false);
                row.Add(asset);
                var stats = new Label($"{record.generatedUtc}  ·  {record.vertices:N0} vertices  ·  {record.triangles:N0} triangles");
                stats.AddToClassList("ptv-history-row__stats");
                row.Add(stats);
                historyList.Add(row);
            }
        }

        private void CreateGroup()
        {
            string title = IsTemporaryGroup ? "Save Conversion Group" : "Create Conversion Group";
            string path = EditorUtility.SaveFilePanelInProject(title, "VoxelGroup", "asset", "Choose a project location.");
            if (string.IsNullOrEmpty(path)) return;
            path = AssetDatabase.GenerateUniqueAssetPath(path);
            if (IsTemporaryGroup)
            {
                HideFlags oldFlags = group.hideFlags;
                group.hideFlags = HideFlags.None;
                try
                {
                    AssetDatabase.CreateAsset(group, path);
                    if (!string.IsNullOrEmpty(group.generatedRoot))
                        group.generatedGroupGuid = AssetDatabase.AssetPathToGUID(path);
                    EditorUtility.SetDirty(group);
                    AssetDatabase.SaveAssets();
                    SetNotice("Saved Conversion Group  ·  " + path, NoticeTone.Success);
                    BindGroup();
                    RefreshAll();
                    EditorGUIUtility.PingObject(group);
                }
                catch
                {
                    if (group && !EditorUtility.IsPersistent(group)) group.hideFlags = oldFlags;
                    throw;
                }
                return;
            }

            var created = CreateInstance<ConversionGroup>();
            AssetDatabase.CreateAsset(created, path);
            SetGroup(created);
            AddSources(Selection.objects);
            EditorGUIUtility.PingObject(group);
        }

        private bool IsTemporaryGroup => group && !EditorUtility.IsPersistent(group);

        private void SetGroup(ConversionGroup selected)
        {
            if (group == selected) return;
            ReleaseTemporaryGroup();
            group = selected ? selected : MakeTemporaryGroup();
            Invalidate();
            BindGroup();
            RefreshAll();
        }

        private void CreateTemporaryGroup(bool addSelection)
        {
            ReleaseTemporaryGroup();
            group = MakeTemporaryGroup();
            if (addSelection) AddSourcesToGroup(Selection.objects, false);
            Invalidate();
            BindGroup();
            RefreshAll();
        }

        private static ConversionGroup MakeTemporaryGroup()
        {
            var temporary = CreateInstance<ConversionGroup>();
            temporary.name = "Temporary Group";
            temporary.hideFlags = HideFlags.HideAndDontSave;
            return temporary;
        }

        private void ReleaseTemporaryGroup()
        {
            if (!group || EditorUtility.IsPersistent(group)) return;
            DestroyImmediate(group);
            group = null;
        }

        private void SavePreset()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Settings Preset", "VoxelPreset", "asset", "Choose a project location.");
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<ConversionPreset>();
            created.settings = JsonUtility.FromJson<ConversionSettings>(JsonUtility.ToJson(group.settings));
            AssetDatabase.CreateAsset(created, AssetDatabase.GenerateUniqueAssetPath(path));
            preset = created;
            presetField?.SetValueWithoutNotify(preset);
            RefreshState();
            EditorGUIUtility.PingObject(preset);
        }
    }

    [CustomEditor(typeof(ConversionGroup))]
    public sealed class ConversionGroupInspector : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            StyleSheet style = AssetDatabase.LoadAssetAtPath<StyleSheet>(PixelToVoxelWindow.StylePath);
            if (style) root.styleSheets.Add(style);
            root.AddToClassList("ptv-inspector");
            var button = new Button(() => PixelToVoxelWindow.Open((ConversionGroup)target)) { text = "Open Pixel To Voxel" };
            button.AddToClassList("ptv-button");
            button.AddToClassList("ptv-button--primary");
            root.Add(button);
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            return root;
        }
    }
}
