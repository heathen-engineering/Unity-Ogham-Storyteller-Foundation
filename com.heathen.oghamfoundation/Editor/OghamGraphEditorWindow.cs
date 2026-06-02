using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    public class OghamGraphEditorWindow : EditorWindow
    {
        [MenuItem("Window/Ogham Storyteller")]
        public static void Open() => GetWindow<OghamGraphEditorWindow>(typeof(SceneView));

        public static void OpenAsset(OghamData data)
        {
            var w = GetWindow<OghamGraphEditorWindow>(typeof(SceneView));
            w.LoadAsset(data);
        }

        // Opens a .ogham source file in the graph editor. Called from OghamImporterEditor
        // and from LoadAllAssets when the window opens.
        public static void OpenOghamFile(string assetPath)
        {
            var w = GetWindow<OghamGraphEditorWindow>(typeof(SceneView));
            w.LoadOghamFile(assetPath);
        }

        private OghamCanvas    _canvas;
        private OghamTreePanel _treePanel;
        private IMGUIContainer _canvasContainer;

        private bool  _snapToGrid;
        private float _lastSavedSplit = -1f;
        private const string SplitPrefKey = "Ogham.Editor.SplitWidth";
        private readonly List<OghamData> _openAssets = new();

        // Tracks .ogham JSON-backed assets (synthetic, not in AssetDatabase).
        // Key: synthetic OghamData. Value: (parsed document, AssetDatabase-relative path).
        private readonly Dictionary<OghamData, (OghamJsonDocument Doc, string Path)> _jsonBacked = new();

        private void CreateGUI()
        {
            _canvas = new OghamCanvas(this);

            var root = rootVisualElement;

            // Toolbar lives in its own fixed-height IMGUI container so the split view below
            // occupies the remaining space. The canvas IMGUIContainer's contentRect then
            // reflects only the canvas area — no y-offset arithmetic needed.
            var toolbarContainer = new IMGUIContainer(DrawToolbar);
            toolbarContainer.style.height    = 22f;
            toolbarContainer.style.flexShrink = 0f;
            root.Add(toolbarContainer);

            float initSplit = EditorPrefs.GetFloat(SplitPrefKey, 220f);
            var split = new TwoPaneSplitView(0, initSplit, TwoPaneSplitViewOrientation.Horizontal);
            split.style.flexGrow = 1f;
            root.Add(split);

            _treePanel = new OghamTreePanel();
            _treePanel.OnEntrySelected          += HandleEntrySelected;
            _treePanel.OnAssetSelected          += HandleAssetSelected;
            _treePanel.OnAssetClosed            += HandleAssetClosed;
            _treePanel.OnAssetVisibilityChanged += (data, hidden) => _canvas?.SetAssetHidden(data, hidden);
            _treePanel.PathResolver              = data => _jsonBacked.TryGetValue(data, out var kv) ? kv.Path : null;
            split.Add(_treePanel);

            _canvasContainer = new IMGUIContainer(DrawCanvas) { style = { flexGrow = 1f } };
            split.Add(_canvasContainer);

            _canvas.OnGraphChanged       += () => _treePanel.Rebuild();
            _canvas.OnSaveRequested      += SaveOghamFiles;
            _canvas.OnActiveAssetChanged += _treePanel.Rebuild;
            _treePanel.NameResolver    = _canvas.ResolveEntryName;
            _treePanel.ColorGetter     = data => _canvas.GetAssetColor(data);
            _treePanel.ColorSetter     = (data, color) => { _canvas.SetAssetColor(data, color); Repaint(); };
            _treePanel.IsActiveAsset   = data => _canvas.ActiveAsset == data;

            // Persist split position across window sessions.
            _treePanel.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                float w = _treePanel.resolvedStyle.width;
                if (w > 50f && Mathf.Abs(w - _lastSavedSplit) > 2f)
                {
                    _lastSavedSplit = w;
                    EditorPrefs.SetFloat(SplitPrefKey, w);
                }
            });

            EditorApplication.projectChanged += OnProjectChanged;

            // Defer asset loading to avoid triggering AssetDatabase writes (and assembly reloads)
            // while the editor is still initializing — particularly during window restoration on startup.
            EditorApplication.delayCall += LoadAllAssets;
        }

        private void DrawCanvas()
        {
            var r = _canvasContainer.contentRect;
            if (r.width < 2f) return;
            var rect = new Rect(0f, 0f, r.width, r.height);
            if (_openAssets.Count == 0) { DrawEmptyState(rect); return; }
            _canvas.Draw(rect);
        }

        private static GUIStyle _emptyMsgStyle;
        private void DrawEmptyState(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.165f, 0.165f, 0.165f));
            _emptyMsgStyle ??= new GUIStyle(EditorStyles.label) {
                fontSize  = 15,
                alignment = TextAnchor.MiddleCenter,
                wordWrap  = true,
                normal    = { textColor = new Color(0.55f, 0.55f, 0.55f) },
            };
            float msgW = 380f, msgH = 60f;
            var msgR = new Rect(r.x + (r.width - msgW) * 0.5f,
                                r.y + (r.height - msgH) * 0.5f - 22f, msgW, msgH);
            GUI.Label(msgR, "No Stories Found.\nCreate an Ogham Story to get started.", _emptyMsgStyle);

            float btnW = 140f, btnH = 28f;
            var btnR = new Rect(r.x + (r.width - btnW) * 0.5f, msgR.yMax + 10f, btnW, btnH);
            if (GUI.Button(btnR, "Create Story"))
                ShowNewOghamFileDialog();
        }

        private void OnEnable()
        {
            var icon = EditorGUIUtility.IconContent("d_console.infoicon").image;
            titleContent = new GUIContent("Ogham Storyteller", icon);
            wantsMouseMove = true;
            foreach (var asset in _openAssets)
                if (asset != null) _canvas?.LoadAsset(asset);
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= OnProjectChanged;

            if (_canvas != null)
            {
                _canvas.OnSaveRequested      -= SaveOghamFiles;
                _canvas.OnActiveAssetChanged -= _treePanel.Rebuild;
            }

            if (_treePanel != null)
            {
                _treePanel.OnEntrySelected -= HandleEntrySelected;
                _treePanel.OnAssetSelected -= HandleAssetSelected;
                _treePanel.OnAssetClosed   -= HandleAssetClosed;
            }

            // Destroy synthetic ScriptableObjects (created from .ogham JSON files, not in AssetDatabase)
            // so they don't accumulate in Unity's object system across assembly reloads.
            foreach (var kv in _jsonBacked)
            {
                if (_canvas != null) { var m = _canvas.GetMeta(kv.Key); if (m != null) UnityEngine.Object.DestroyImmediate(m); }
                _openAssets.Remove(kv.Key); // evict before destroy so LoadAllAssets re-discovers from file
                if (kv.Key != null) UnityEngine.Object.DestroyImmediate(kv.Key);
            }
            _jsonBacked.Clear();
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

        // ── .ogham file I/O ───────────────────────────────────────────────────

        private void LoadOghamFile(string assetPath)
        {
            // Ignore if a file at this path is already open.
            foreach (var kv in _jsonBacked)
                if (kv.Value.Path == assetPath) return;

            string json;
            try { json = File.ReadAllText(assetPath); }
            catch (Exception e)
            {
                Debug.LogError($"[Ogham] Cannot read {assetPath}: {e.Message}");
                return;
            }

            var doc  = OghamJsonDocument.Parse(json);
            var data = doc.ToOghamData();
            var meta = doc.ToMetadata();

            data.name = Path.GetFileNameWithoutExtension(assetPath);
            meta.name = data.name + "_editor";
            data.BuildIndex(); // synthesise inline-link options for the graph editor

            _jsonBacked[data] = (doc, assetPath);
            _openAssets.Add(data);
            _canvas?.LoadSyntheticAsset(data, meta);
            _treePanel?.AddAsset(data);
        }

        // Writes all JSON-backed assets back to their source .ogham files and triggers reimport.
        private void SaveOghamFiles()
        {
            foreach (var kv in _jsonBacked)
            {
                var data = kv.Key;
                var doc  = kv.Value.Doc;
                var path = kv.Value.Path;
                var meta = _canvas?.GetMeta(data);
                doc.SyncFrom(data, meta);
                try
                {
                    File.WriteAllText(path, doc.ToJson());
                    AssetDatabase.ImportAsset(path); // refresh compiled sub-assets
                    EditorUtility.ClearDirty(data);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Ogham] Save failed for {path}: {e.Message}");
                }
            }
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
            // Guard: if the window was destroyed before delayCall fired, do nothing.
            if (this == null) return;

            // Restore any assets that survived an assembly reload (already in _openAssets
            // but not yet in the canvas or tree panel since those are rebuilt in CreateGUI).
            foreach (var asset in _openAssets)
            {
                if (asset == null) continue;
                _canvas?.LoadAsset(asset);
                _treePanel?.AddAsset(asset);
            }

            // Load .asset OghamData files.
            var guids = AssetDatabase.FindAssets("t:OghamData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<OghamData>(path);
                if (data != null) LoadAsset(data);
            }

            // Load .ogham source files (synthetic assets, not in AssetDatabase).
            var dataPath = Application.dataPath;
            if (!Directory.Exists(dataPath)) return;
            foreach (var absPath in Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories))
            {
                // Convert absolute filesystem path to AssetDatabase-relative path.
                var relPath = "Assets" + absPath.Substring(dataPath.Length).Replace('\\', '/');
                LoadOghamFile(relPath);
            }
        }

        // Detects .ogham files added or removed from the project and refreshes the window.
        private void OnProjectChanged()
        {
            if (this == null) return;

            var dataPath   = Application.dataPath;
            if (!Directory.Exists(dataPath)) return;
            var foundPaths = new HashSet<string>();
            foreach (var abs in Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories))
                foundPaths.Add("Assets" + abs.Substring(dataPath.Length).Replace('\\', '/'));

            var openPaths = new HashSet<string>(_jsonBacked.Values.Select(v => v.Path));

            // Load newly appeared files.
            foreach (var p in foundPaths)
                if (!openPaths.Contains(p)) LoadOghamFile(p);

            // Unload files that disappeared.
            var removed = _jsonBacked.Where(kv => !foundPaths.Contains(kv.Value.Path))
                                     .Select(kv => kv.Key).ToList();
            foreach (var key in removed) HandleAssetClosed(key);

            if (removed.Count > 0) _treePanel?.Rebuild();
        }

        // Right-click in the Project window: Assets/Create/Ogham/Story
        [MenuItem("Assets/Create/Ogham/Story")]
        private static void CreateOghamStoryFromMenu()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "New Ogham Story File", "MyStory", "ogham",
                "Create a new .ogham story source file.", "Assets");
            if (string.IsNullOrEmpty(path)) return;
            var doc = OghamJsonDocument.CreateNew();
            File.WriteAllText(path, doc.ToJson());
            AssetDatabase.ImportAsset(path);
            if (HasOpenInstances<OghamGraphEditorWindow>())
                GetWindow<OghamGraphEditorWindow>().LoadOghamFile(path);
        }

        // ── Toolbar ───────────────────────────────────────────────────────────

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button(new GUIContent("Support", "Join the Heathen community on Discord"),
                    EditorStyles.toolbarButton, GUILayout.Width(58)))
                    Application.OpenURL("https://discord.gg/tytrBwwHZe");

                if (GUILayout.Button(new GUIContent("Docs", "Open Heathen documentation"),
                    EditorStyles.toolbarButton, GUILayout.Width(40)))
                    Application.OpenURL("https://heathen.group/");

                if (_jsonBacked.Count > 0)
                {
                    GUILayout.Space(4f);
                    bool isDirty = _jsonBacked.Keys.Any(a => a != null && EditorUtility.IsDirty(a));
                    var savedBg = GUI.backgroundColor;
                    if (isDirty) GUI.backgroundColor = new Color(1f, 0.85f, 0.2f);
                    if (GUILayout.Button("Save .ogham", EditorStyles.toolbarButton, GUILayout.Width(84)))
                        SaveOghamFiles();
                    GUI.backgroundColor = savedBg;
                }

                GUILayout.Space(8f);

                var savedSnapBg = GUI.backgroundColor;
                if (_snapToGrid) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
                var newSnap = GUILayout.Toggle(_snapToGrid, "Snap", EditorStyles.toolbarButton, GUILayout.Width(48));
                GUI.backgroundColor = savedSnapBg;
                if (newSnap != _snapToGrid) { _snapToGrid = newSnap; if (_canvas != null) _canvas.SnapToGrid = newSnap; }

                GUILayout.FlexibleSpace();

                // Zoom slider: left = 100% (zoom=1), right = 15% (zoom=0.15). No value label.
                if (_canvas != null)
                {
                    float sliderVal = 1f - (_canvas.Zoom - 0.15f) / (1f - 0.15f);
                    float newSlider = GUILayout.HorizontalSlider(sliderVal, 0f, 1f, GUILayout.Width(120f));
                    if (!Mathf.Approximately(newSlider, sliderVal))
                        _canvas.SetZoom(Mathf.Lerp(1f, 0.15f, newSlider));
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

                if (GUILayout.Button("Export…",    EditorStyles.toolbarButton, GUILayout.Width(64)))
                    ShowExportWindow();

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

        // ── Export ────────────────────────────────────────────────────────────

        private void ShowExportWindow()
        {
            if (_openAssets.Count == 0)
            {
                EditorUtility.DisplayDialog("Export VO Script",
                    "No story assets are open. Open a story in the graph editor first.", "OK");
                return;
            }

            var metas = _openAssets
                .Select(a => _canvas.GetMeta(a))
                .ToList();

            var selectedTag  = _canvas.SelectedEntryTagPath;
            var selectedTags = string.IsNullOrEmpty(selectedTag)
                ? Enumerable.Empty<string>()
                : Enumerable.Repeat(selectedTag, 1);

            OghamExportWindow.Open(_openAssets, metas, selectedTags);
        }

        // ── New file ──────────────────────────────────────────────────────────

        private void ShowNewAssetDialog()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("New .ogham file"), false, ShowNewOghamFileDialog);
            menu.AddItem(new GUIContent("New .asset file (legacy)"), false, ShowNewLegacyAssetDialog);
            menu.ShowAsContext();
        }

        private void ShowNewOghamFileDialog()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "New Ogham Story File", "MyStory", "ogham",
                "Create a new .ogham story source file.", "Assets");
            if (string.IsNullOrEmpty(path)) return;
            var doc = OghamJsonDocument.CreateNew();
            File.WriteAllText(path, doc.ToJson());
            AssetDatabase.ImportAsset(path);
            LoadOghamFile(path);
        }

        private void ShowNewLegacyAssetDialog()
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
            bool wasSynthetic = _jsonBacked.ContainsKey(asset);
            var syntheticMeta = wasSynthetic ? _canvas?.GetMeta(asset) : null;

            _openAssets.Remove(asset);
            _jsonBacked.Remove(asset);
            _canvas?.UnloadAsset(asset);
            _treePanel?.RemoveAsset(asset);

            // Destroy synthetic ScriptableObjects that were never added to the AssetDatabase.
            if (wasSynthetic)
            {
                if (syntheticMeta != null) UnityEngine.Object.DestroyImmediate(syntheticMeta);
                if (asset         != null) UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}
