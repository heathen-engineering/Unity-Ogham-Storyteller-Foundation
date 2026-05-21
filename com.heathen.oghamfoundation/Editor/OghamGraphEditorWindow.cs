using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    public class OghamGraphEditorWindow : EditorWindow
    {
        [MenuItem("Window/Heathen/Ogham Storyteller")]
        public static void Open() => GetWindow<OghamGraphEditorWindow>("Ogham Storyteller");

        public static void OpenAsset(OghamData data)
        {
            var w = GetWindow<OghamGraphEditorWindow>("Ogham Storyteller");
            w.LoadAsset(data);
        }

        private OghamCanvas    _canvas;
        private OghamTreePanel _treePanel;
        private IMGUIContainer _canvasContainer;

        private bool _snapToGrid;
        private readonly List<OghamData> _openAssets = new();

        private void CreateGUI()
        {
            _canvas = new OghamCanvas(this);

            var root    = rootVisualElement;
            var toolbar = new IMGUIContainer(DrawToolbar) { style = { height = 22f } };
            root.Add(toolbar);

            var split = new TwoPaneSplitView(0, 220f, TwoPaneSplitViewOrientation.Horizontal);
            root.Add(split);

            _treePanel = new OghamTreePanel();
            _treePanel.OnEntrySelected += HandleEntrySelected;
            _treePanel.OnAssetSelected += HandleAssetSelected;
            _treePanel.OnAssetClosed   += HandleAssetClosed;
            split.Add(_treePanel);

            _canvasContainer = new IMGUIContainer(DrawCanvas) { style = { flexGrow = 1f } };
            split.Add(_canvasContainer);

            _canvas.OnGraphChanged += () => _treePanel.Rebuild();
            _treePanel.NameResolver = _canvas.ResolveEntryName;
            _treePanel.ColorGetter  = data => _canvas.GetAssetColor(data);
            _treePanel.ColorSetter  = (data, color) => { _canvas.SetAssetColor(data, color); Repaint(); };

            LoadAllAssets();
        }

        private void DrawCanvas()
        {
            var r = _canvasContainer.contentRect;
            if (r.width < 2f || r.height < 2f) return;
            _canvas.Draw(r);
        }

        private void OnEnable()
        {
            foreach (var asset in _openAssets)
                if (asset != null) _canvas?.LoadAsset(asset);
        }

        private void OnDisable()
        {
            if (_treePanel != null)
            {
                _treePanel.OnEntrySelected -= HandleEntrySelected;
                _treePanel.OnAssetSelected -= HandleAssetSelected;
                _treePanel.OnAssetClosed   -= HandleAssetClosed;
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void LoadAsset(OghamData data)
        {
            if (data == null || _openAssets.Contains(data)) return;
            _openAssets.Add(data);
            _canvas?.LoadAsset(data);
            _treePanel?.AddAsset(data);
        }

        // Called by OghamTweeImportWindow after a commit to rebuild and layout the new nodes.
        public void RefreshAndLayoutAsset(OghamData data)
        {
            if (data == null || !_openAssets.Contains(data)) return;
            _canvas?.RebuildCanvas();
            _canvas?.AutoLayoutAsset(data);
            _treePanel?.Rebuild();
        }

        // Called by OghamAssetWatcher when assets are deleted from the project.
        public void UnloadDeletedAssets(string[] deletedPaths)
        {
            var pathSet = new System.Collections.Generic.HashSet<string>(deletedPaths);

            // Path-based match — works in the same postprocess frame while refs are still valid.
            var toClose = new System.Collections.Generic.List<OghamData>();
            foreach (var a in _openAssets)
                if (a != null && pathSet.Contains(AssetDatabase.GetAssetPath(a)))
                    toClose.Add(a);
            foreach (var a in toClose)
                HandleAssetClosed(a);

            // Null sweep — catches any refs Unity has already invalidated.
            for (int i = _openAssets.Count - 1; i >= 0; i--)
                if (_openAssets[i] == null) _openAssets.RemoveAt(i);

            if (toClose.Count > 0) _treePanel?.Rebuild();
        }

        private void LoadAllAssets()
        {
            var guids = AssetDatabase.FindAssets("t:OghamData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<OghamData>(path);
                if (data != null) LoadAsset(data);
            }
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("New File…", EditorStyles.toolbarButton, GUILayout.Width(72)))
                    ShowNewAssetDialog();

                if (GUILayout.Button("Add Entry", EditorStyles.toolbarButton, GUILayout.Width(76)))
                    ShowAddEntryMenu();

                GUILayout.Space(8f);

                var newSnap = GUILayout.Toggle(_snapToGrid, "Snap", EditorStyles.toolbarButton, GUILayout.Width(48));
                if (newSnap != _snapToGrid) { _snapToGrid = newSnap; if (_canvas != null) _canvas.SnapToGrid = newSnap; }

                GUILayout.FlexibleSpace();

                // Zoom display + snap-to-natural-size button (centred in the flexible space).
                if (_canvas != null)
                {
                    var zoomLabel = $"{Mathf.RoundToInt(_canvas.Zoom * 100f)}%";
                    GUILayout.Label(zoomLabel, EditorStyles.toolbarButton, GUILayout.Width(46f));
                    if (GUILayout.Button("100%", EditorStyles.toolbarButton, GUILayout.Width(42f)))
                        _canvas.SetZoom(1f);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Layout",     EditorStyles.toolbarButton, GUILayout.Width(52)))
                    _canvas.AutoLayout();

                if (GUILayout.Button("Compile…",  EditorStyles.toolbarButton, GUILayout.Width(74)))
                    ShowCompileDialog();

                if (GUILayout.Button("Play",       EditorStyles.toolbarButton, GUILayout.Width(48)))
                    OghamPlayWindow.Open(_openAssets, _canvas.SelectedEntryTagPath);

                if (GUILayout.Button("Import…",    EditorStyles.toolbarButton, GUILayout.Width(64)))
                    ShowImportMenu();

                if (GUILayout.Button(new GUIContent("⚙", "Open Ogham Editor Settings in Inspector"),
                    EditorStyles.toolbarButton, GUILayout.Width(26)))
                {
                    var settings = OghamEditorSettings.GetOrCreate();
                    Selection.activeObject = settings;
                    EditorGUIUtility.PingObject(settings);
                }
            }
        }

        // ── Import ────────────────────────────────────────────────────────────

        private void ShowImportMenu()
        {
            var importerTypes = TypeCache.GetTypesDerivedFrom<IOghamImporter>();
            if (importerTypes.Count == 0)
            {
                EditorUtility.DisplayDialog("Ogham Import",
                    "No importers found.\n\nInstall an Ogham Toolkit package to add import sources.", "OK");
                return;
            }
            if (importerTypes.Count == 1)
            {
                ((IOghamImporter)System.Activator.CreateInstance(importerTypes[0])).Open();
                return;
            }
            var menu = new GenericMenu();
            foreach (var t in importerTypes)
            {
                var imp = (IOghamImporter)System.Activator.CreateInstance(t);
                var cap = imp;
                menu.AddItem(new GUIContent(cap.DisplayName), false, () => cap.Open());
            }
            menu.ShowAsContext();
        }

        // ── New file ──────────────────────────────────────────────────────────

        private void ShowNewAssetDialog()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "New Dialogue File", "OghamData", "asset",
                "Create a new Ogham dialogue data file.", "Assets");
            if (string.IsNullOrEmpty(path)) return;
            var data = ScriptableObject.CreateInstance<OghamData>();
            AssetDatabase.CreateAsset(data, path);
            AssetDatabase.SaveAssets();
            LoadAsset(data);
        }

        // ── Compile ───────────────────────────────────────────────────────────

        private void ShowCompileDialog()
        {
            if (_openAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("Ogham", "Open at least one dialogue file first.", "OK");
                return;
            }
            var guids = AssetDatabase.FindAssets("t:OghamCompiledData");
            if (guids.Length == 0)
            {
                var p = EditorUtility.SaveFilePanelInProject(
                    "Create Compiled Story", "OghamStory", "asset",
                    "Choose where to save the compiled story asset.", "Assets");
                if (!string.IsNullOrEmpty(p)) CompileInto(p);
            }
            else if (guids.Length == 1)
            {
                CompileInto(AssetDatabase.GUIDToAssetPath(guids[0]));
            }
            else
            {
                var menu = new GenericMenu();
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    menu.AddItem(new GUIContent(Path.GetFileNameWithoutExtension(p)), false,
                        () => CompileInto(p));
                }
                menu.AddSeparator(string.Empty);
                menu.AddItem(new GUIContent("New compiled story…"), false, () =>
                {
                    var np = EditorUtility.SaveFilePanelInProject(
                        "Create Compiled Story", "OghamStory", "asset",
                        "Choose where to save the compiled story asset.", "Assets");
                    if (!string.IsNullOrEmpty(np)) CompileInto(np);
                });
                menu.ShowAsContext();
            }
        }

        private void CompileInto(string assetPath)
        {
            OghamCompiledData compiled;
            if (!File.Exists(assetPath) || AssetDatabase.LoadAssetAtPath<OghamCompiledData>(assetPath) == null)
            {
                compiled = ScriptableObject.CreateInstance<OghamCompiledData>();
                AssetDatabase.CreateAsset(compiled, assetPath);
            }
            else
            {
                compiled = AssetDatabase.LoadAssetAtPath<OghamCompiledData>(assetPath);
            }
            Undo.RecordObject(compiled, "Compile Story");
            compiled.SetSourceFiles(_openAssets);
            compiled.Compile();
            AssetDatabase.SaveAssetIfDirty(compiled);
            EditorUtility.DisplayDialog("Ogham",
                $"Compiled {_openAssets.Count} source file(s) into\n{assetPath}", "OK");
        }

        // ── Add entry ─────────────────────────────────────────────────────────

        private void ShowAddEntryMenu()
        {
            if (_openAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("Ogham", "No OghamData files found in project.", "OK");
                return;
            }
            var target = _canvas.ActiveAsset
                ?? (_openAssets.Count == 1 ? _openAssets[0] : null);

            if (target != null) { AddRootEntry(target); return; }

            var menu = new GenericMenu();
            foreach (var asset in _openAssets)
            {
                var cap = asset;
                menu.AddItem(new GUIContent(asset.name), false, () => AddRootEntry(cap));
            }
            menu.ShowAsContext();
        }

        private void AddRootEntry(OghamData asset)
        {
            Undo.RecordObject(asset, "Add Entry");
            var entry = new DialogueEntry();
            asset.Entries.Add(entry);
            asset.BuildIndex();
            EditorUtility.SetDirty(asset);
            _canvas.AddEntry(asset, entry, _canvas.CanvasCentre);
            _treePanel.Rebuild();
        }

        // ── Tree panel callbacks ──────────────────────────────────────────────

        private void HandleEntrySelected(OghamData asset, DialogueEntry entry)
            => _canvas.FrameEntry(entry.TagPath);

        private void HandleAssetSelected(OghamData asset)
        {
            Selection.activeObject = asset;
            _canvas.SetActiveAsset(asset);
        }

        private void HandleAssetClosed(OghamData asset)
        {
            _openAssets.Remove(asset);
            _canvas?.UnloadAsset(asset);
            _treePanel?.RemoveAsset(asset);
        }
    }
}
