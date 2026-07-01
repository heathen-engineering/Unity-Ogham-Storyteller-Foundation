using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Heathen.Ogham;
using Heathen.Editor; // framework UndoHistory

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// The main Ogham Storyteller graph editor window. Open via <c>Window &rarr; Ogham Storyteller</c>.
    /// Manages loading, saving, auto-layout, playback, import, and export of dialogue story assets.
    /// </summary>
    public class OghamGraphEditorWindow : EditorWindow
    {
        /// <summary>Opens the Ogham Storyteller graph editor window, docked next to the Scene view.</summary>
        [MenuItem("Window/Ogham Storyteller")]
        public static void Open() => GetWindow<OghamGraphEditorWindow>(typeof(SceneView));

        /// <summary>Opens the Ogham Storyteller graph editor window and loads the given <see cref="OghamData"/> asset.</summary>
        /// <param name="data">The asset to open and display in the graph editor.</param>
        public static void OpenAsset(OghamData data)
        {
            var w = GetWindow<OghamGraphEditorWindow>(typeof(SceneView));
            w.LoadAsset(data);
        }

        /// <summary>
        /// Opens a <c>.ogham</c> source file in the graph editor. Called from <c>OghamImporterEditor</c>
        /// and from <c>LoadAllAssets</c> when the window first opens.
        /// </summary>
        /// <param name="assetPath">The AssetDatabase-relative path to the <c>.ogham</c> file.</param>
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

        // Per-asset undo history of .ogham JSON snapshots (the framework's serialise-based undo). Edits are
        // debounced into one snapshot once activity settles, so a drag becomes a single undo step.
        private readonly Dictionary<OghamData, UndoHistory<string>> _undo = new();
        private readonly Dictionary<OghamData, double> _pendingSnapshots = new();
        private bool _snapshotTickHooked;
        private bool _restoring;
        // True when authored changes have not been built (code generated) since the last build. Drives the
        // amber Build button and the build-on-close prompt. "Save" writes the source; "Build" generates code.
        private bool _needsBuild;
        private const double SnapshotDebounceSeconds = 0.3;

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
            _canvas.AssetEdited          += OnAssetEdited;
            _canvas.OnUndoRequested      += UndoActiveAsset;
            _canvas.OnRedoRequested      += RedoActiveAsset;
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

            // Offer to build on a genuine window close (not on a domain reload or entering Play, which the
            // framework play-guard handles). Save synchronously while the canvas is alive, then defer the
            // generate + recompile to a clean tick.
            bool genuineClose = !EditorApplication.isCompiling
                              && !EditorApplication.isUpdating
                              && !EditorApplication.isPlayingOrWillChangePlaymode;
            if (_needsBuild && genuineClose && _jsonBacked.Count > 0 &&
                EditorUtility.DisplayDialog("Ogham — unbuilt changes",
                    "Story changes haven't been built. Build now so they take effect in Play and in builds?",
                    "Build", "Don't Build"))
            {
                SaveOghamFiles();
                var paths = _jsonBacked.Values.Select(v => v.Path).ToList();
                _needsBuild = false;
                EditorApplication.delayCall += () =>
                {
                    foreach (var p in paths) OghamStoryGenerator.Generate(p);
                    Heathen.Lexicon.Editor.LexiconAddressables.Save();
                    AssetDatabase.Refresh();
                };
            }

            if (_canvas != null)
            {
                _canvas.OnSaveRequested      -= SaveOghamFiles;
                _canvas.OnActiveAssetChanged -= _treePanel.Rebuild;
                _canvas.AssetEdited          -= OnAssetEdited;
                _canvas.OnUndoRequested      -= UndoActiveAsset;
                _canvas.OnRedoRequested      -= RedoActiveAsset;
            }

            if (_snapshotTickHooked) { EditorApplication.update -= SnapshotTick; _snapshotTickHooked = false; }
            _undo.Clear();
            _pendingSnapshots.Clear();

            if (_treePanel != null)
            {
                _treePanel.OnEntrySelected -= HandleEntrySelected;
                _treePanel.OnAssetSelected -= HandleAssetSelected;
                _treePanel.OnAssetClosed   -= HandleAssetClosed;
            }

            // Synthetic in-memory models (built from .ogham JSON, not assets) are plain objects now —
            // just drop the references and let the GC reclaim them.
            foreach (var kv in _jsonBacked)
                _openAssets.Remove(kv.Key);
            _jsonBacked.Clear();
        }

        // ── Undo (framework UndoHistory over .ogham JSON snapshots) ──────────────

        private void OnAssetEdited(OghamData asset)
        {
            if (_restoring || asset == null) return;
            _needsBuild = true; // an edit means the generated code is now behind the source
            _pendingSnapshots[asset] = EditorApplication.timeSinceStartup;
            if (!_snapshotTickHooked) { EditorApplication.update += SnapshotTick; _snapshotTickHooked = true; }
        }

        // Debounce: snapshot an asset once its edits settle, so a drag becomes a single undo step.
        private void SnapshotTick()
        {
            if (_pendingSnapshots.Count == 0)
            {
                EditorApplication.update -= SnapshotTick;
                _snapshotTickHooked = false;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            List<OghamData> ready = null;
            foreach (var kv in _pendingSnapshots)
                if (now - kv.Value >= SnapshotDebounceSeconds)
                    (ready ??= new List<OghamData>()).Add(kv.Key);

            if (ready != null)
                foreach (var asset in ready)
                {
                    _pendingSnapshots.Remove(asset);
                    SnapshotForUndo(asset);
                }
        }

        private void SnapshotForUndo(OghamData asset)
        {
            if (!_jsonBacked.TryGetValue(asset, out var jb) || !_undo.TryGetValue(asset, out var history)) return;
            jb.Doc.SyncFrom(asset, _canvas?.GetMeta(asset));
            history.Push(jb.Doc.ToJson());
        }

        // Commit any debounced snapshot for the active asset before an undo/redo so the latest edit is captured.
        private void FlushPending(OghamData asset)
        {
            if (asset != null && _pendingSnapshots.Remove(asset)) SnapshotForUndo(asset);
        }

        private void UndoActiveAsset()
        {
            var asset = _canvas?.ActiveAsset;
            FlushPending(asset);
            if (asset == null || !_undo.TryGetValue(asset, out var history) || !history.CanUndo) return;
            RestoreSnapshot(asset, history.Undo());
        }

        private void RedoActiveAsset()
        {
            var asset = _canvas?.ActiveAsset;
            FlushPending(asset);
            if (asset == null || !_undo.TryGetValue(asset, out var history) || !history.CanRedo) return;
            RestoreSnapshot(asset, history.Redo());
        }

        private void RestoreSnapshot(OghamData asset, string json)
        {
            if (asset == null || string.IsNullOrEmpty(json) || !_jsonBacked.TryGetValue(asset, out var jb)) return;

            _restoring = true;
            try
            {
                var doc      = OghamJsonDocument.Parse(json);
                var restored = doc.ToOghamData();
                var meta     = _canvas?.GetMeta(asset);

                // Mutate in place to preserve the object identities used as dictionary keys.
                asset.Entries.Clear();
                asset.Entries.AddRange(restored.Entries);
                asset.BuildIndex();

                if (meta != null)
                {
                    var restoredMeta = doc.ToMetadata();
                    meta.Nodes.Clear();
                    meta.Nodes.AddRange(restoredMeta.Nodes);
                    meta.ViewTransform = restoredMeta.ViewTransform;
                }

                _jsonBacked[asset] = (doc, jb.Path);

                // Rebuild the canvas view for this asset from the restored data + meta.
                _canvas?.UnloadAsset(asset);
                _canvas?.LoadSyntheticAsset(asset, meta);
                _treePanel?.Rebuild();
                Repaint();
            }
            finally { _restoring = false; }
        }

        /// <summary>
        /// Loads an <see cref="OghamData"/> asset into the open graph editor window. Duplicate loads are silently ignored.
        /// </summary>
        /// <param name="data">The asset to load. <c>null</c> is silently ignored.</param>
        public void LoadAsset(OghamData data)
        {
            if (data == null || _openAssets.Contains(data)) return;
            _openAssets.Add(data);
            _canvas?.LoadAsset(data);
            _treePanel?.AddAsset(data);
        }

        /// <summary>
        /// Rebuilds the canvas and auto-layouts the nodes belonging to <paramref name="data"/>.
        /// Called by the Twee import window after writing new content to an already-open asset.
        /// </summary>
        /// <param name="data">The asset whose nodes are rebuilt and laid out.</param>
        public void RefreshAndLayoutAsset(OghamData data)
        {
            if (data == null || !_openAssets.Contains(data)) return;
            _canvas?.RebuildCanvas();

            _canvas?.AutoLayoutAsset(data);
            _treePanel?.Rebuild();
        }

        /// <summary>
        /// Reloads a <c>.ogham</c> source file that may already be open in the editor, closing the old instance
        /// and re-opening from the updated file. Called by the Twee import window after writing imported content.
        /// </summary>
        /// <param name="assetPath">The AssetDatabase-relative path of the <c>.ogham</c> file to reload.</param>
        public void RefreshOghamFile(string assetPath)
        {
            // If the file is currently open, close and destroy it first.
            var existing = _jsonBacked.FirstOrDefault(kv => kv.Value.Path == assetPath);
            if (existing.Key != null)
                HandleAssetClosed(existing.Key);

            // Re-open from the freshly written file.
            LoadOghamFile(assetPath);

            // Auto-layout the newly imported nodes.
            var newEntry = _jsonBacked.FirstOrDefault(kv => kv.Value.Path == assetPath);
            if (newEntry.Key != null)
                _canvas?.AutoLayoutAsset(newEntry.Key);

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

            data.Name = Path.GetFileNameWithoutExtension(assetPath);
            meta.Name = data.Name + "_editor";
            data.BuildIndex(); // synthesise inline-link options for the graph editor

            _jsonBacked[data] = (doc, assetPath);
            if (OghamStoryGenerator.IsStale(assetPath)) _needsBuild = true; // opened with unbuilt changes
            var history = new UndoHistory<string>();
            history.Push(doc.ToJson()); // initial state
            _undo[data] = history;
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
                    AssetDatabase.ImportAsset(path); // re-import the .ogham TextAsset
                }
                catch (Exception e)
                {
                    Debug.LogError($"[Ogham] Save failed for {path}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Unloads any open assets whose paths appear in <paramref name="deletedPaths"/>.
        /// Called by <see cref="OghamAssetWatcher"/> when assets are deleted from the project.
        /// </summary>
        /// <param name="deletedPaths">The AssetDatabase-relative paths of the deleted assets.</param>
        public void UnloadDeletedAssets(string[] deletedPaths)
        {
            var pathSet = new System.Collections.Generic.HashSet<string>(deletedPaths);

            // Path-based match — works in the same postprocess frame while refs are still valid.
            var toClose = new System.Collections.Generic.List<OghamData>();
            foreach (var a in _openAssets)
                if (a != null && _jsonBacked.TryGetValue(a, out var jb) && pathSet.Contains(jb.Path))
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

            // Load .ogham source files (the in-memory authoring model; OghamData is no longer an asset).
            var dataPath = Application.dataPath;
            if (!Directory.Exists(dataPath)) return;
            foreach (var absPath in Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories))
            {
                // Convert absolute filesystem path to AssetDatabase-relative path.
                var relPath = "Assets" + absPath.Substring(dataPath.Length).Replace('\\', '/');
                if (IsInHiddenFolder(relPath)) continue;
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
            {
                var rel = "Assets" + abs.Substring(dataPath.Length).Replace('\\', '/');
                if (!IsInHiddenFolder(rel)) foundPaths.Add(rel);
            }

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

        private const float TbBtn = 30f;

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                // ── Left: story identity, save, build ──
                var active = _canvas?.ActiveAsset;
                if (active != null && _jsonBacked.TryGetValue(active, out var activeJb))
                {
                    GUILayout.Label("Story Tag:", EditorStyles.miniLabel, GUILayout.Width(58));
                    EditorGUI.BeginChangeCheck();
                    string newTag = EditorGUILayout.DelayedTextField(activeJb.Doc.StoryTag,
                        EditorStyles.toolbarTextField, GUILayout.Width(170));
                    if (EditorGUI.EndChangeCheck())
                    {
                        var trimmed = newTag?.Trim() ?? string.Empty;
                        if (trimmed != activeJb.Doc.StoryTag)
                        {
                            activeJb.Doc.SetStoryTag(trimmed);
                            SaveOghamFiles();   // persist the tag (with any pending edits)
                            _needsBuild = true; // identity changed → code is behind
                        }
                    }
                }

                if (_jsonBacked.Count > 0)
                {
                    if (GUILayout.Button(ToolbarIcon("SaveActive", "Save the .ogham source (does not build)", "Save"),
                        EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                        SaveOghamFiles();

                    // Shared Heathen Build/status button: amber "Update" when there are unbuilt changes,
                    // green "Ready" when the baked code is current.
                    var status = _needsBuild ? HeathenEditorStyles.BuildStatus.Dirty : HeathenEditorStyles.BuildStatus.UpToDate;
                    if (HeathenEditorStyles.BuildStatusButton(status, 74f))
                        BuildStories();
                }

                GUILayout.FlexibleSpace();

                // ── Centre: zoom + in-graph test-play (runs on source, no build) ──
                if (_canvas != null)
                {
                    float sliderVal = 1f - (_canvas.Zoom - 0.15f) / (1f - 0.15f);
                    float newSlider = GUILayout.HorizontalSlider(sliderVal, 0f, 1f, GUILayout.Width(120f));
                    if (!Mathf.Approximately(newSlider, sliderVal))
                        _canvas.SetZoom(Mathf.Lerp(1f, 0.15f, newSlider));
                }

                bool testPlaying = OghamPlayWindow.IsOpen;
                var playContent  = testPlaying
                    ? new GUIContent("■", "Stop the test-play runner")
                    : ToolbarIcon("PlayButton", "Test-play the story logic on the source (no build, no game simulation)", "▶");
                if (GUILayout.Button(playContent, EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                {
                    if (testPlaying) OghamPlayWindow.CloseIfOpen();
                    else             OghamPlayWindow.Open(_openAssets, _canvas?.SelectedEntryTagPath);
                }

                GUILayout.FlexibleSpace();

                // ── Right: view, IO, help ──
                var savedSnapBg = GUI.backgroundColor;
                if (_snapToGrid) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
                var newSnap = GUILayout.Toggle(_snapToGrid,
                    ToolbarIcon("d_SceneViewSnap", "Snap nodes to grid", "Snap"),
                    EditorStyles.toolbarButton, GUILayout.Width(TbBtn));
                GUI.backgroundColor = savedSnapBg;
                if (newSnap != _snapToGrid) { _snapToGrid = newSnap; if (_canvas != null) _canvas.SnapToGrid = newSnap; }

                if (GUILayout.Button(ToolbarIcon("d_Grid.Default", "Auto-layout the graph", "Layout"),
                    EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                    _canvas.AutoLayout();

                if (GUILayout.Button(ToolbarIcon("Import", "Import…", "Import"),
                    EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                    ShowImportMenu();

                if (GUILayout.Button(ToolbarIcon("SaveAs", "Export…", "Export"),
                    EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                    ShowExportWindow();

                if (GUILayout.Button(ToolbarIcon("_Help", "Help, documentation & settings", "Help"),
                    EditorStyles.toolbarButton, GUILayout.Width(TbBtn)))
                    ShowHelpMenu();
            }
        }

        // Returns a GUIContent using the named built-in editor icon, falling back to text when the icon name
        // is not present in this Unity version (so the toolbar is always usable).
        private static GUIContent ToolbarIcon(string iconName, string tooltip, string fallbackText)
        {
            var c = EditorGUIUtility.IconContent(iconName);
            return (c != null && c.image != null)
                ? new GUIContent(c.image, tooltip)
                : new GUIContent(fallbackText, tooltip);
        }

        // Build = generate code for the open stories (needed for Play mode). Saves first so the build reflects
        // the current source, then regenerates and triggers a recompile.
        private void BuildStories()
        {
            SaveOghamFiles();
            int built = 0;
            foreach (var kv in _jsonBacked)
                if (OghamStoryGenerator.Generate(kv.Value.Path)) built++;
            Heathen.Lexicon.Editor.LexiconAddressables.Save(); // persist addressable marking for baked assets
            AssetDatabase.Refresh(); // compile the generated code
            _needsBuild = false;
            Debug.Log($"[Ogham] Built {built} story file(s).");
        }

        private void ShowHelpMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Settings"), false,
                () => SettingsService.OpenProjectSettings("Project/Subsystems/Ogham Storyteller"));
            menu.AddSeparator(string.Empty);
            menu.AddItem(new GUIContent("Documentation"), false, () => Application.OpenURL("https://heathen.group/"));
            menu.AddItem(new GUIContent("Support (Discord)"), false, () => Application.OpenURL("https://discord.gg/tytrBwwHZe"));
            menu.ShowAsContext();
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

        private void ShowNewAssetDialog() => ShowNewOghamFileDialog();

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
                menu.AddItem(new GUIContent(asset.Name), false, () => AddRootEntry(cap));
            }
            menu.ShowAsContext();
        }

        private void AddRootEntry(OghamData asset)
        {
            var entry = new DialogueEntry();
            asset.Entries.Add(entry);
            asset.BuildIndex();
            // asset (OghamData) persists via the .ogham JSON on save; undo lands with UndoHistory (increment 3).
            _canvas.AddEntry(asset, entry, _canvas.CanvasCentre);
            _treePanel.Rebuild();
        }

        // ── Tree panel callbacks ──────────────────────────────────────────────

        private void HandleEntrySelected(OghamData asset, DialogueEntry entry)
            => _canvas.FrameEntry(entry.TagPath);

        private void HandleAssetSelected(OghamData asset)
        {
            _canvas.SetActiveAsset(asset);
        }

        private void HandleAssetClosed(OghamData asset)
        {
            // Models are plain objects (built from .ogham JSON), so just drop references; GC reclaims them.
            _openAssets.Remove(asset);
            _jsonBacked.Remove(asset);
            _canvas?.UnloadAsset(asset);
            _treePanel?.RemoveAsset(asset);
        }

        private static bool IsInHiddenFolder(string path) => OghamImporterUtils.IsInHiddenFolder(path);
    }
}
