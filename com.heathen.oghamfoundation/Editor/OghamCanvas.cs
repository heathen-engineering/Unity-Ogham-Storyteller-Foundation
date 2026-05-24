using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    internal static class NodeColors
    {
        public static readonly Color BodyBg       = new(0.145f, 0.145f, 0.145f);
        public static readonly Color SectionHdrBg = new(0.118f, 0.118f, 0.118f);
        public static readonly Color FieldBg      = new(0.200f, 0.200f, 0.200f);
        public static readonly Color FieldText    = new(0.878f, 0.878f, 0.878f);
        public static readonly Color SectionText  = new(0.600f, 0.600f, 0.600f);
        public static readonly Color OpText       = new(0.530f, 0.530f, 0.530f);
        public static readonly Color OptionText   = new(0.720f, 0.720f, 0.720f);
        public static readonly Color Divider      = new(0.250f, 0.250f, 0.250f);
    }

    internal class CanvasNode
    {
        public DialogueEntry       Entry;
        public OghamNodeMeta       Meta;
        public OghamData           Asset;
        public string              DisplayName;
        public Color               HeaderColor;
        public Rect                Rect;       // canvas coordinates
        public bool                IsSelected;
        public List<OghamLabelDef> LabelDefs;  // shared ref to graph meta Labels list

        // Cached label def lookup (id → def) — rebuilt when LabelDefs changes.
        public readonly Dictionary<int, OghamLabelDef> LabelDefById = new();

        // Cached section header strings: [section 0-2][0=collapsed, 1=expanded].
        // Rebuilt by RebuildNodeLabelCache when entry item counts change.
        public readonly string[,] SectionHeaders = new string[3, 2];

        // Cached row-label strings per section — rebuilt on data change only, not every frame.
        public string[] OpLabels  = System.Array.Empty<string>();
        public string[] KeyLabels = System.Array.Empty<string>();
        public string[] OptLabels = System.Array.Empty<string>();
        // Cached per-key row heights — avoids running StripMarkup regex every frame in OutputPinPos / DrawSection.
        public float[]  KeyHeights = System.Array.Empty<float>();
    }

    internal class CanvasEdge
    {
        public CanvasNode     Source;
        public DialogueOption Option;
        public int            OptionIndex;       // cached index in Source.Entry.Options (avoids per-frame IndexOf)
        public CanvasNode     Target;            // null = unresolved or closes conversation
        public List<Vector2>  Waypoints = new(); // canvas-space redirect points
    }

    internal class CanvasAlias
    {
        public CanvasNode     OwnerNode;  // node whose meta holds this alias
        public OghamAliasMeta Meta;
        public Rect           Rect;       // canvas coordinates
    }

    // IMGUI node-graph canvas. Drop-in replacement for the GraphView-based OghamGraphView.
    // Draw() is called from an IMGUIContainer whose coordinate origin is the container's top-left.
    public class OghamCanvas
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private readonly List<OghamData>          _assets = new();
        private readonly List<OghamGraphMetadata> _metas  = new();
        private readonly List<CanvasNode>         _nodes   = new();
        private readonly List<CanvasEdge>         _edges   = new();
        private readonly List<CanvasAlias>        _aliases = new();
        private readonly Dictionary<OghamData, Color> _assetColors = new();
        private readonly HashSet<OghamData>           _hiddenAssets = new();
        private int _colorIndex;

        // ── View state ────────────────────────────────────────────────────────
        private Vector2 _pan  = new Vector2(20f, 20f);
        private float   _zoom = 1f;
        private Rect    _canvasRect;

        // ── Drag / pan / zoom state ───────────────────────────────────────────
        private bool    _snapToGrid;
        private bool    _isPanning;
        private Vector2 _panStartMouse;
        private Vector2 _panStartOffset;

        private CanvasNode _dragNode;
        private Vector2    _dragOffset;
        private Vector2    _dragStartMouse;
        private bool       _isDragging;

        // ── Connection drag ───────────────────────────────────────────────────
        private bool           _isDraggingConn;
        private CanvasNode     _connSrcNode;
        private DialogueOption _connSrcOpt;
        private int            _connSrcOptIdx;
        private Vector2        _connDragEnd;

        // ── Rubber-band selection ─────────────────────────────────────────────
        private bool    _isRubberBanding;
        private Vector2 _rubberBandStart;
        private Vector2 _rubberBandEnd;

        // ── Alias drag ────────────────────────────────────────────────────────
        private CanvasAlias _dragAlias;
        private Vector2     _dragAliasOffset;
        private Vector2     _dragAliasStartMouse;
        private bool        _isDraggingAlias;

        // ── Waypoint drag ─────────────────────────────────────────────────────
        private CanvasEdge _dragWpEdge;
        private int        _dragWpIdx        = -1;
        private bool       _isDraggingWp;
        private Vector2    _dragWpStartMouse;

        // ── Multi-node drag ───────────────────────────────────────────────────
        private bool    _isDragMulti;
        private Vector2 _multiDragMouseStart;
        private readonly List<(CanvasNode node, Vector2 startPos)> _multiDragStarts = new();

        // ── Tab-flag hover ────────────────────────────────────────────────────
        private CanvasEdge _hoveredTabEdge;

        // ── Label strip ───────────────────────────────────────────────────────
        // Trello-style: click any pill to show names (expanded), click again to collapse to dots.
        private bool _labelsExpanded;

        // ── Tab label deferred rendering ──────────────────────────────────────
        // GUI.Label must not be called inside Handles.BeginGUI/EndGUI — collect labels here
        // and flush them after EndGUI to avoid corrupting the Handles GL state.
        private readonly List<(Rect rect, string label, Color textColor)> _pendingTabLabels = new();

        // ── Render cache ──────────────────────────────────────────────────────
        private bool _orderedNodesDirty = true;
        private readonly List<CanvasNode> _orderedNodes = new();

        // Capsule pill texture (white, 9-sliced via GUIStyle.border).
        private Texture2D _pillBaseTex;
        private GUIStyle  _pillBoxStyle;

        // ── Connection paint cache ────────────────────────────────────────────
        // Pre-built per-frame so DrawNodePins avoids O(edges) Any() per node.
        private readonly HashSet<CanvasNode> _connectedAsTarget = new();
        private readonly HashSet<(CanvasNode, DialogueOption)> _connectedAsSource = new();

        // Reusable buffer for EdgeScreenPoints — eliminates per-edge List allocation.
        private readonly List<Vector2> _edgePtsBuf = new();

        // ── Public ───────────────────────────────────────────────────────────
        public OghamData ActiveAsset { get; private set; }
        public bool SnapToGrid { get => _snapToGrid; set => _snapToGrid = value; }
        public string SelectedEntryTagPath => _nodes.FirstOrDefault(n => n.IsSelected)?.Entry.TagPath;
        public event System.Action OnGraphChanged;
        public event System.Action OnSaveRequested;
        public event System.Action OnActiveAssetChanged;

        private readonly EditorWindow _host;

        // ── Colors / constants ────────────────────────────────────────────────
        private static readonly Color[] HeaderColors = {
            new Color(0.176f, 0.353f, 0.557f),
            new Color(0.341f, 0.220f, 0.400f),
            new Color(0.220f, 0.392f, 0.282f),
            new Color(0.455f, 0.278f, 0.180f),
            new Color(0.278f, 0.380f, 0.200f),
            new Color(0.380f, 0.318f, 0.176f),
        };

        private const float NodeW        = 310f;
        private const float HeaderH      = 26f;
        private const float LabelStripH  = 22f;   // pill strip below header (only when labels assigned)
        private const float MetaH        = 22f;   // strip holding the input pin
        private const float ImageRowH    = 72f;   // taller row for image-type content keys
        private const float SectionHdrH  = 20f;
        private const float RowH         = 21f;
        private const float TextKeyLineH = 14f;   // actual rendered line height for 10pt Inter in IMGUI
        private const float PinR         = 5f;
        private const float PinColW      = 16f;   // right column reserved for output pins / button margin
        private const float RowIndent    = 8f;
        private const float DragThresh   = 4f;

        // Alias pin badge constants
        private const float AliasW = 100f;
        private const float AliasH = 22f;
        private static readonly Color AliasColor = new Color(1.0f, 0.72f, 0.15f);

        // Tab-flag constants (matches O3DE OghamConnectionItem)
        private const float TabH        = 18f;
        private const float TabArrow    = 7f;
        private const float TabDefaultW = 27f;
        private const float TabHoverW   = 160f;  // wide enough for a full tag path at 9pt
        private const float TabPadX     = 5f;

        // Zoom-level LOD thresholds.
        // Font sizes are fixed (10 pt rows, 12 pt header) and do NOT scale with zoom.
        // Instead we progressively hide content as it becomes illegible.
        //   LodRowsZoom    — below this: skip section row content (RowH*0.60 ≈ 12.6 px — illegible)
        //   LodSectionZoom — below this: skip section headers too (SectionHdrH*0.38 ≈ 7.6 px)
        //   LodLabelZoom   — below this: skip header label (HeaderH*0.25 ≈ 6.5 px)
        // Pins always render at least LodPinMinR screen-pixels so they stay clickable.
        private const float LodRowsZoom    = 0.60f;
        private const float LodSectionZoom = 0.38f;
        private const float LodLabelZoom   = 0.25f;
        private const float LodPinMinR     = 4f;

        public OghamCanvas(EditorWindow host) => _host = host;

        // ── Asset management ──────────────────────────────────────────────────

        public void LoadAsset(OghamData data)
        {
            if (data == null || _assets.Contains(data)) return;
            var meta = LoadOrCreateMeta(data);
            _assets.Add(data);
            _metas.Add(meta);
            if (!_assetColors.ContainsKey(data))
                _assetColors[data] = meta.HeaderColor.a > 0f
                    ? meta.HeaderColor
                    : HeaderColors[_colorIndex++ % HeaderColors.Length];
            if (ActiveAsset == null)
            {
                ActiveAsset = data;
                if (meta.ViewTransform.z > 0f)
                {
                    _pan  = new Vector2(meta.ViewTransform.x, meta.ViewTransform.y);
                    _zoom = meta.ViewTransform.z;
                }
            }
            RebuildCanvas();
        }

        // Loads a synthetic (not-in-AssetDatabase) OghamData + OghamGraphMetadata pair.
        // Used by the .ogham ScriptedImporter workflow where the source of truth is a JSON file.
        // Changes are NOT auto-saved — the owning window must serialize back to JSON explicitly.
        public void LoadSyntheticAsset(OghamData data, OghamGraphMetadata meta)
        {
            if (data == null || _assets.Contains(data)) return;
            if (meta == null) meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
            _assets.Add(data);
            _metas.Add(meta);
            if (!_assetColors.ContainsKey(data))
                _assetColors[data] = meta.HeaderColor.a > 0f
                    ? meta.HeaderColor
                    : HeaderColors[_colorIndex++ % HeaderColors.Length];
            if (ActiveAsset == null)
            {
                ActiveAsset = data;
                if (meta.ViewTransform.z > 0f)
                {
                    _pan  = new Vector2(meta.ViewTransform.x, meta.ViewTransform.y);
                    _zoom = meta.ViewTransform.z;
                }
            }
            RebuildCanvas();
        }

        public void UnloadAsset(OghamData data)
        {
            int i = _assets.IndexOf(data);
            if (i < 0) return;
            _assets.RemoveAt(i);
            _metas.RemoveAt(i);
            _assetColors.Remove(data);
            _hiddenAssets.Remove(data);
            if (ActiveAsset == data) ActiveAsset = _assets.Count > 0 ? _assets[0] : null;
            RebuildCanvas();
        }

        public OghamGraphMetadata GetMeta(OghamData data)
        {
            int i = _assets.IndexOf(data);
            return i >= 0 ? _metas[i] : null;
        }

        public void SetActiveAsset(OghamData data)
        {
            if (data == null || !_assets.Contains(data) || ActiveAsset == data) return;
            ActiveAsset = data;
            OnActiveAssetChanged?.Invoke();
        }

        public void SetAssetHidden(OghamData data, bool hidden)
        {
            if (hidden) _hiddenAssets.Add(data);
            else _hiddenAssets.Remove(data);
            _host?.Repaint();
        }

        public Color GetAssetColor(OghamData data)
            => _assetColors.TryGetValue(data, out var c) ? c : Color.white;

        public void SetAssetColor(OghamData data, Color color)
        {
            if (data == null) return;
            _assetColors[data] = color;
            int i = _assets.IndexOf(data);
            if (i >= 0)
            {
                _metas[i].HeaderColor = color;
                EditorUtility.SetDirty(data);
                SaveMeta(_metas[i]);
            }
            RebuildCanvas();
        }

        // Called by the editor window when it adds an entry programmatically.
        public void AddEntry(OghamData asset, DialogueEntry entry, Vector2 canvasPos)
        {
            int i = _assets.IndexOf(asset);
            if (i < 0) return;
            Undo.RecordObject(_metas[i], "Add Entry");
            var nm = _metas[i].GetOrCreateNode(entry.TagPath);
            nm.Position = new Rect(canvasPos, new Vector2(NodeW, 200f));
            RebuildCanvas();
            SaveMeta(_metas[i]);
        }

        public void FrameEntry(string tagPath)
        {
            var node = _nodes.FirstOrDefault(n => n.Entry.TagPath == tagPath);
            if (node == null) return;
            _pan = _canvasRect.center - node.Rect.center * _zoom;
            _host?.Repaint();
        }

        private void FrameNodes(List<CanvasNode> nodes)
        {
            if (nodes.Count == 0) return;
            if (nodes.Count == 1) { FrameEntry(nodes[0].Entry.TagPath); return; }
            var bounds = nodes[0].Rect;
            foreach (var n in nodes)
                bounds = Rect.MinMaxRect(
                    Mathf.Min(bounds.xMin, n.Rect.xMin), Mathf.Min(bounds.yMin, n.Rect.yMin),
                    Mathf.Max(bounds.xMax, n.Rect.xMax), Mathf.Max(bounds.yMax, n.Rect.yMax));
            const float pad = 60f;
            float zoomX = (_canvasRect.width  - pad * 2f) / bounds.width;
            float zoomY = (_canvasRect.height - pad * 2f) / bounds.height;
            _zoom = Mathf.Clamp(Mathf.Min(zoomX, zoomY), 0.15f, 1.0f);
            _pan  = _canvasRect.center - bounds.center * _zoom;
            SaveViewTransform();
            _host?.Repaint();
        }

        // BFS hierarchical layout — ported from O3DE OghamStoryteller::OnLayoutGraph().
        // Roots = nodes with no incoming edges. Each root starts a BFS tree laid out in
        // columns (depth) × rows (siblings). Separate trees are stacked vertically.
        // Cycles are handled by treating all nodes as roots on first pass and skipping visited ones.
        public void AutoLayout()
        {
            if (_nodes.Count == 0) return;

            for (int i = 0; i < _metas.Count; i++)
                Undo.RecordObject(_metas[i], "Auto Layout");

            const float colW    = NodeW + 80f;
            const float rowH    = 160f;
            const float treeGap = rowH * 0.5f;

            var tagIndex = new Dictionary<string, CanvasNode>();
            foreach (var n in _nodes)
                if (!string.IsNullOrEmpty(n.Entry.TagPath))
                    tagIndex[n.Entry.TagPath] = n;

            var hasIncoming = new HashSet<string>();
            foreach (var n in _nodes)
                foreach (var opt in n.Entry.Options)
                    if (!string.IsNullOrEmpty(opt.TargetEntryPath))
                        hasIncoming.Add(opt.TargetEntryPath);

            var roots = _nodes.Where(n =>
                !string.IsNullOrEmpty(n.Entry.TagPath) && !hasIncoming.Contains(n.Entry.TagPath)).ToList();
            if (roots.Count == 0)
                roots = _nodes.Where(n => !string.IsNullOrEmpty(n.Entry.TagPath)).ToList();

            var visited = new HashSet<string>();
            float baseY = 20f;

            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root.Entry.TagPath) || visited.Contains(root.Entry.TagPath))
                    continue;

                var queue  = new Queue<(CanvasNode node, int col)>();
                var queued = new HashSet<string>();
                queue.Enqueue((root, 0));
                queued.Add(root.Entry.TagPath);

                var nextRowPerCol = new Dictionary<int, int>();
                float treeMaxY = baseY;

                while (queue.Count > 0)
                {
                    var (node, col) = queue.Dequeue();
                    var tag = node.Entry.TagPath;
                    if (string.IsNullOrEmpty(tag) || visited.Contains(tag)) continue;
                    visited.Add(tag);

                    int row = nextRowPerCol.TryGetValue(col, out var r) ? r : 0;
                    nextRowPerCol[col] = row + 1;

                    float x = 20f + col * colW;
                    float y = baseY + row * rowH;
                    node.Rect          = new Rect(x, y, NodeW, node.Rect.height);
                    node.Meta.Position = node.Rect;
                    treeMaxY = Mathf.Max(treeMaxY, y + node.Rect.height);

                    foreach (var opt in node.Entry.Options)
                    {
                        if (string.IsNullOrEmpty(opt.TargetEntryPath) || queued.Contains(opt.TargetEntryPath)) continue;
                        if (!tagIndex.TryGetValue(opt.TargetEntryPath, out var child)) continue;
                        queued.Add(opt.TargetEntryPath);
                        queue.Enqueue((child, col + 1));
                    }
                }

                baseY = treeMaxY + treeGap;
            }

            // Place any orphan / unreachable nodes below the laid-out trees.
            foreach (var node in _nodes)
            {
                if (string.IsNullOrEmpty(node.Entry.TagPath) || visited.Contains(node.Entry.TagPath)) continue;
                node.Rect          = new Rect(20f, baseY, NodeW, node.Rect.height);
                node.Meta.Position = node.Rect;
                baseY += node.Rect.height + 20f;
            }

            for (int i = 0; i < _metas.Count; i++) SaveMeta(_metas[i]);
            FrameNodes(_nodes);
        }

        // BFS-layout only the nodes belonging to `asset`, placed below the bounding box
        // of all other currently loaded nodes.  Called after importing into an open window.
        public void AutoLayoutAsset(OghamData asset)
        {
            var assetNodes = _nodes.Where(n => n.Asset == asset).ToList();
            if (assetNodes.Count == 0) return;

            int mi = _assets.IndexOf(asset);
            if (mi >= 0) Undo.RecordObject(_metas[mi], "Auto Layout Import");

            float baseY = 20f;
            foreach (var n in _nodes)
            {
                if (n.Asset == asset) continue;
                if (n.Rect.yMax > baseY) baseY = n.Rect.yMax;
            }
            if (baseY > 20f) baseY += 80f;

            const float colW    = NodeW + 80f;
            const float rowH    = 160f;
            const float treeGap = rowH * 0.5f;

            var tagIndex = new Dictionary<string, CanvasNode>();
            foreach (var n in assetNodes)
                if (!string.IsNullOrEmpty(n.Entry.TagPath)) tagIndex[n.Entry.TagPath] = n;

            var hasIncoming = new HashSet<string>();
            foreach (var n in assetNodes)
                foreach (var opt in n.Entry.Options)
                    if (!string.IsNullOrEmpty(opt.TargetEntryPath)) hasIncoming.Add(opt.TargetEntryPath);

            var roots = assetNodes.Where(n =>
                !string.IsNullOrEmpty(n.Entry.TagPath) && !hasIncoming.Contains(n.Entry.TagPath)).ToList();
            if (roots.Count == 0)
                roots = assetNodes.Where(n => !string.IsNullOrEmpty(n.Entry.TagPath)).ToList();

            var visited = new HashSet<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root.Entry.TagPath) || visited.Contains(root.Entry.TagPath)) continue;

                var queue  = new Queue<(CanvasNode node, int col)>();
                var queued = new HashSet<string>();
                queue.Enqueue((root, 0));
                queued.Add(root.Entry.TagPath);

                var nextRowPerCol = new Dictionary<int, int>();
                float treeMaxY = baseY;

                while (queue.Count > 0)
                {
                    var (node, col) = queue.Dequeue();
                    var tag = node.Entry.TagPath;
                    if (string.IsNullOrEmpty(tag) || visited.Contains(tag)) continue;
                    visited.Add(tag);

                    int row = nextRowPerCol.TryGetValue(col, out var r) ? r : 0;
                    nextRowPerCol[col] = row + 1;

                    float x = 20f + col * colW;
                    float y = baseY + row * rowH;
                    node.Rect          = new Rect(x, y, NodeW, node.Rect.height);
                    node.Meta.Position = node.Rect;
                    treeMaxY = Mathf.Max(treeMaxY, y + node.Rect.height);

                    foreach (var opt in node.Entry.Options)
                    {
                        if (string.IsNullOrEmpty(opt.TargetEntryPath) || queued.Contains(opt.TargetEntryPath)) continue;
                        if (!tagIndex.TryGetValue(opt.TargetEntryPath, out var child)) continue;
                        queued.Add(opt.TargetEntryPath);
                        queue.Enqueue((child, col + 1));
                    }
                }

                baseY = treeMaxY + treeGap;
            }

            foreach (var node in assetNodes)
            {
                if (string.IsNullOrEmpty(node.Entry.TagPath) || visited.Contains(node.Entry.TagPath)) continue;
                node.Rect          = new Rect(20f, baseY, NodeW, node.Rect.height);
                node.Meta.Position = node.Rect;
                baseY += node.Rect.height + 20f;
            }

            if (mi >= 0) SaveMeta(_metas[mi]);
            FrameNodes(assetNodes);
            _host?.Repaint();
        }

        // Write BFS layout positions directly to the companion .graph.asset without requiring
        // an open canvas window.  Used by the Twee importer when no graph window is open.
        public static void LayoutMetaDirect(OghamData data)
        {
            if (data == null) return;

            var dataPath    = AssetDatabase.GetAssetPath(data);
            var ownMetaPath = Path.ChangeExtension(dataPath, null) + ".graph.asset";

            // Compute the bottom edge of all OTHER assets' graph metadata.
            float maxY = 20f;
            foreach (var guid in AssetDatabase.FindAssets("t:OghamGraphMetadata"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == ownMetaPath) continue;
                var m = AssetDatabase.LoadAssetAtPath<OghamGraphMetadata>(path);
                if (m == null) continue;
                foreach (var nm in m.Nodes)
                    if (nm.Position.yMax > maxY) maxY = nm.Position.yMax;
            }

            float baseY = maxY > 20f ? maxY + 80f : 20f;

            // Load or create the meta for this asset.
            var meta = AssetDatabase.LoadAssetAtPath<OghamGraphMetadata>(ownMetaPath);
            if (meta == null)
            {
                meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
                meta.SourceData = data;
                AssetDatabase.CreateAsset(meta, ownMetaPath);
            }

            const float colW    = NodeW + 80f;
            const float rowH    = 160f;
            const float treeGap = rowH * 0.5f;

            var tagIndex    = new Dictionary<string, DialogueEntry>();
            var hasIncoming = new HashSet<string>();
            foreach (var e in data.Entries)
            {
                if (!string.IsNullOrEmpty(e.TagPath)) tagIndex[e.TagPath] = e;
                foreach (var opt in e.Options)
                    if (!string.IsNullOrEmpty(opt.TargetEntryPath)) hasIncoming.Add(opt.TargetEntryPath);
            }

            var roots = data.Entries
                .Where(e => !string.IsNullOrEmpty(e.TagPath) && !hasIncoming.Contains(e.TagPath))
                .ToList();
            if (roots.Count == 0)
                roots = data.Entries.Where(e => !string.IsNullOrEmpty(e.TagPath)).ToList();

            var visited = new HashSet<string>();
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root.TagPath) || visited.Contains(root.TagPath)) continue;

                var queue  = new Queue<(DialogueEntry entry, int col)>();
                var queued = new HashSet<string>();
                queue.Enqueue((root, 0));
                queued.Add(root.TagPath);

                var nextRowPerCol = new Dictionary<int, int>();
                float treeMaxY = baseY;

                while (queue.Count > 0)
                {
                    var (entry, col) = queue.Dequeue();
                    var tag = entry.TagPath;
                    if (string.IsNullOrEmpty(tag) || visited.Contains(tag)) continue;
                    visited.Add(tag);

                    int row = nextRowPerCol.TryGetValue(col, out var r) ? r : 0;
                    nextRowPerCol[col] = row + 1;

                    float x  = 20f + col * colW;
                    float y  = baseY + row * rowH;
                    var nm   = meta.GetOrCreateNode(tag);
                    float h  = NodeHeightEstimate(entry, nm);
                    nm.Position = new Rect(x, y, NodeW, h);
                    treeMaxY = Mathf.Max(treeMaxY, y + h);

                    foreach (var opt in entry.Options)
                    {
                        if (string.IsNullOrEmpty(opt.TargetEntryPath) || queued.Contains(opt.TargetEntryPath)) continue;
                        if (!tagIndex.ContainsKey(opt.TargetEntryPath)) continue;
                        queued.Add(opt.TargetEntryPath);
                        queue.Enqueue((tagIndex[opt.TargetEntryPath], col + 1));
                    }
                }

                baseY = treeMaxY + treeGap;
            }

            foreach (var entry in data.Entries)
            {
                if (string.IsNullOrEmpty(entry.TagPath) || visited.Contains(entry.TagPath)) continue;
                var nm = meta.GetOrCreateNode(entry.TagPath);
                float h = NodeHeightEstimate(entry, nm);
                nm.Position = new Rect(20f, baseY, NodeW, h);
                baseY += h + 20f;
            }

            EditorUtility.SetDirty(meta);
            AssetDatabase.SaveAssetIfDirty(meta);
        }

        // Align/distribute the currently selected nodes — ported from O3DE AlignSelected().
        // mode: 0=Left 1=Right 2=CenterH 3=Top 4=Bottom 5=CenterV 6=DistributeH 7=DistributeV
        public void AlignSelected(int mode)
        {
            var sel = _nodes.Where(n => n.IsSelected).ToList();
            if (sel.Count < 2) return;

            var seenMetas = new HashSet<int>();
            foreach (var n in sel) { int mi = _assets.IndexOf(n.Asset); if (mi >= 0 && seenMetas.Add(mi)) Undo.RecordObject(_metas[mi], "Align Nodes"); }

            switch (mode)
            {
                case 0: // Left
                {
                    float x = sel.Min(n => n.Rect.x);
                    foreach (var n in sel) { n.Rect = new Rect(x, n.Rect.y, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 1: // Right
                {
                    float r = sel.Max(n => n.Rect.xMax);
                    foreach (var n in sel) { n.Rect = new Rect(r - n.Rect.width, n.Rect.y, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 2: // Center H
                {
                    float cx = sel.Average(n => n.Rect.center.x);
                    foreach (var n in sel) { n.Rect = new Rect(cx - n.Rect.width * 0.5f, n.Rect.y, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 3: // Top
                {
                    float y = sel.Min(n => n.Rect.y);
                    foreach (var n in sel) { n.Rect = new Rect(n.Rect.x, y, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 4: // Bottom
                {
                    float b = sel.Max(n => n.Rect.yMax);
                    foreach (var n in sel) { n.Rect = new Rect(n.Rect.x, b - n.Rect.height, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 5: // Center V
                {
                    float cy = sel.Average(n => n.Rect.center.y);
                    foreach (var n in sel) { n.Rect = new Rect(n.Rect.x, cy - n.Rect.height * 0.5f, n.Rect.width, n.Rect.height); n.Meta.Position = n.Rect; }
                    break;
                }
                case 6: // Distribute H
                {
                    if (sel.Count < 3) break;
                    var sorted = sel.OrderBy(n => n.Rect.x).ToList();
                    float x0 = sorted[0].Rect.x; float xN = sorted[^1].Rect.x; int N = sorted.Count;
                    for (int i = 1; i < N - 1; i++) { float x = x0 + i * (xN - x0) / (N - 1); sorted[i].Rect = new Rect(x, sorted[i].Rect.y, sorted[i].Rect.width, sorted[i].Rect.height); sorted[i].Meta.Position = sorted[i].Rect; }
                    break;
                }
                case 7: // Distribute V
                {
                    if (sel.Count < 3) break;
                    var sorted = sel.OrderBy(n => n.Rect.y).ToList();
                    float y0 = sorted[0].Rect.y; float yN = sorted[^1].Rect.y; int N = sorted.Count;
                    for (int i = 1; i < N - 1; i++) { float y = y0 + i * (yN - y0) / (N - 1); sorted[i].Rect = new Rect(sorted[i].Rect.x, y, sorted[i].Rect.width, sorted[i].Rect.height); sorted[i].Meta.Position = sorted[i].Rect; }
                    break;
                }
            }

            foreach (var mi in seenMetas) SaveMeta(_metas[mi]);
            _host?.Repaint();
        }

        public string ResolveEntryName(DialogueEntry entry)
        {
            var n = _nodes.FirstOrDefault(x => x.Entry == entry);
            if (n != null && !string.IsNullOrEmpty(n.DisplayName) && n.DisplayName != "(no tag)")
                return n.DisplayName;
            for (int i = 0; i < _assets.Count; i++)
            {
                if (!_assets[i].Entries.Contains(entry)) continue;
                var nm = _metas[i].GetOrCreateNode(entry.TagPath);
                if (!string.IsNullOrEmpty(nm.TagName)) return nm.TagName;
                break;
            }
            if (!string.IsNullOrEmpty(entry.TagPath)) return entry.TagPath;
            if (!entry.Tag.IsValid) return "(no tag)";
            return GameplayTagRegistry.GetName(entry.Tag.Id)
                ?? OghamTagHelper.GetTagName(entry.Tag.Id)
                ?? entry.Tag.Id.ToString("X16");
        }

        // ── RebuildCanvas ─────────────────────────────────────────────────────

        public void RebuildCanvas()
        {
            _dragWpEdge = null; _dragWpIdx = -1; _isDraggingWp = false;
            _dragAlias = null; _isDraggingAlias = false;
            _isRubberBanding = false;
            _isDragMulti = false; _multiDragStarts.Clear();
            _nodes.Clear();
            _edges.Clear();
            _aliases.Clear();

            for (int i = 0; i < _assets.Count; i++)
            {
                var asset = _assets[i];
                var meta  = _metas[i];
                var color = _assetColors.TryGetValue(asset, out var c) ? c : HeaderColors[0];

                foreach (var entry in asset.Entries)
                {
                    var nm = meta.GetOrCreateNode(entry.TagPath);
                    if (nm.Position.width < 10f)
                        nm.Position = new Rect(Random.Range(40f, 500f), Random.Range(40f, 360f), NodeW, 200f);

                    var name = ResolveDisplayName(entry, nm);
                    nm.Position = new Rect(nm.Position.x, nm.Position.y, NodeW, NodeHeight(entry, nm));

                    var cn = new CanvasNode {
                        Entry       = entry,
                        Meta        = nm,
                        Asset       = asset,
                        DisplayName = name,
                        HeaderColor = color,
                        Rect        = nm.Position,
                        LabelDefs   = meta.Labels,
                    };
                    RebuildNodeLabelCache(cn);
                    _nodes.Add(cn);
                }
            }

            foreach (var src in _nodes)
            {
                for (int oi = 0; oi < src.Entry.Options.Count; oi++)
                {
                    var opt    = src.Entry.Options[oi];
                    var target = !string.IsNullOrEmpty(opt.TargetEntryPath)
                        ? _nodes.FirstOrDefault(n => n.Entry.TagPath == opt.TargetEntryPath)
                        : null;
                    var edge = new CanvasEdge { Source = src, Option = opt, OptionIndex = oi, Target = target };
                    var wps  = src.Meta.EdgeWaypoints.FirstOrDefault(w => w.OptionTagPath == opt.TagPath);
                    if (wps != null) edge.Waypoints.AddRange(wps.Points);
                    _edges.Add(edge);
                }
                foreach (var am in src.Meta.AliasPins)
                    _aliases.Add(new CanvasAlias { OwnerNode = src, Meta = am,
                        Rect = new Rect(am.Position, new Vector2(AliasW, AliasH)) });
            }

            _orderedNodesDirty = true;
        }

        private string ResolveDisplayName(DialogueEntry entry, OghamNodeMeta nm)
        {
            if (!string.IsNullOrEmpty(nm.TagName)) return nm.TagName;
            if (!string.IsNullOrEmpty(entry.TagPath)) return entry.TagPath;
            if (!entry.Tag.IsValid) return "(no tag)";
            return GameplayTagRegistry.GetName(entry.Tag.Id)
                ?? OghamTagHelper.GetTagName(entry.Tag.Id)
                ?? entry.Tag.Id.ToString("X16");
        }

        private static float ContentKeyRowH(OghamContentKey key)
            => key.Type == OghamContentType.Image ? ImageRowH : RowH;

        // Char-based height estimate for text keys — used in NodeHeight and LayoutMetaDirect
        // (both called outside GUI events where CalcHeight is unavailable).
        // Content width ≈ NodeW minus indents, reorder buttons, remove button, pin column.
        private static float TextKeyHEstimate(OghamContentKey key)
        {
            if (key.Type != OghamContentType.Text)
                return key.Type == OghamContentType.Image ? ImageRowH : RowH;
            // Use stripped text for char counting — MD markers and tags are invisible when richText is on.
            var text = OghamInlineLinkParser.StripMarkup(key.ResolveText() ?? "");
            if (string.IsNullOrEmpty(text)) return RowH;
            const int charsPerLine = 52;
            int lines = 1, ll = 0;
            foreach (char c in text)
            {
                if (c == '\n') { lines++; ll = 0; }
                else if (++ll >= charsPerLine) { lines++; ll = 0; }
            }
            // First line uses full RowH (matches other row types for visual consistency).
            // Each additional wrapped line uses the font's actual line height, not the full control height.
            return lines <= 1 ? RowH : RowH + (lines - 1) * TextKeyLineH;
        }

        private static float NodeHeightEstimate(DialogueEntry entry, OghamNodeMeta nm)
        {
            float h = HeaderH + MetaH + 4f;
            if (nm.AssignedLabelIds.Count > 0) h += LabelStripH;
            h += SectionHdrH;
            if (nm.OpsExpanded) h += entry.EntryOperations.Count * RowH;
            h += SectionHdrH;
            if (nm.FieldsExpanded)
                foreach (var key in entry.ContentKeys) h += TextKeyHEstimate(key);
            h += SectionHdrH;
            if (nm.ChoicesExpanded) h += entry.Options.Count * RowH;
            h += 4f;
            return h;
        }

        private float NodeHeight(DialogueEntry entry, OghamNodeMeta nm)
            => NodeHeightEstimate(entry, nm);

        // ── Coordinate helpers ────────────────────────────────────────────────

        private Vector2 ToScreen(Vector2 canvas) => canvas * _zoom + _pan;
        private Rect    ToScreen(Rect canvas)    => new Rect(ToScreen(canvas.position), canvas.size * _zoom);
        private Vector2 ToCanvas(Vector2 screen) => (screen - _pan) / _zoom;

        // Input pin: inside the meta bar, near the left edge
        private static Vector2 InputPinPos(CanvasNode n)
        {
            float lblStrip = n.Meta.AssignedLabelIds.Count > 0 ? LabelStripH : 0f;
            return new Vector2(n.Rect.x + PinR + 4f, n.Rect.y + HeaderH + lblStrip + MetaH * 0.5f);
        }

        // Output pin: inside the right pin-column of each option row
        private static Vector2 OutputPinPos(CanvasNode n, int optIdx)
        {
            float lblStrip = n.Meta.AssignedLabelIds.Count > 0 ? LabelStripH : 0f;
            float opsRows  = n.Meta.OpsExpanded ? n.Entry.EntryOperations.Count : 0;
            float keyH     = 0f;
            if (n.Meta.FieldsExpanded)
                foreach (var h in n.KeyHeights) keyH += h;  // use cached heights — no regex per frame
            float top = n.Rect.y + HeaderH + lblStrip + MetaH + 4f
                      + SectionHdrH + opsRows * RowH
                      + SectionHdrH + keyH
                      + SectionHdrH;
            return new Vector2(n.Rect.xMax - PinColW * 0.5f, top + (optIdx + 0.5f) * RowH);
        }

        // ── Draw entry point ──────────────────────────────────────────────────

        public void Draw(Rect rect)
        {
            _canvasRect = rect;
            EnsureStyles();
            ProcessEvents();

            // Clip all rendering to the canvas container bounds — prevents IMGUI content
            // (including Handles-based beziers and pins) from bleeding into the tree panel.
            GUI.BeginClip(new Rect(0f, 0f, rect.width, rect.height));

            // Background
            EditorGUI.DrawRect(rect, new Color(0.165f, 0.165f, 0.165f));
            DrawGrid(rect);

            // Rebuild sorted-by-selection cache when needed (avoids per-frame LINQ alloc).
            if (_orderedNodesDirty)
            {
                _orderedNodes.Clear();
                foreach (var n in _nodes) if (!n.IsSelected) _orderedNodes.Add(n);
                foreach (var n in _nodes) if (n.IsSelected)  _orderedNodes.Add(n);
                _orderedNodesDirty = false;
            }

            // Layer 1: bezier connections (behind nodes).
            // _pendingTabLabels accumulates GUI.Label calls deferred out of the Handles scope.
            _pendingTabLabels.Clear();
            Handles.BeginGUI();
            DrawConnections();
            if (_isDraggingConn && _connSrcNode != null)
            {
                Vector3 s   = ToScreen(OutputPinPos(_connSrcNode, _connSrcOptIdx));
                Vector3 t   = _connDragEnd;
                Vector3 tan = new Vector3(Mathf.Max(60f, Mathf.Abs(t.x - s.x) * 0.5f) * _zoom, 0f, 0f);
                Handles.DrawBezier(s, t, s + tan, t - tan,
                    new Color(0.816f, 0.565f, 0.290f), null, 2f * _zoom);
            }
            Handles.EndGUI();

            // Flush deferred tab-flag labels — must be outside Handles scope to avoid GL state corruption.
            foreach (var (r, lbl, tc) in _pendingTabLabels)
            {
                _tabLabelStyle.normal.textColor = tc;
                GUI.Label(r, lbl, _tabLabelStyle);
            }

            // Layer 2: node bodies + interactive controls (frustum-culled).
            foreach (var node in _orderedNodes)
                DrawNode(node);
            foreach (var alias in _aliases)
                DrawAlias(alias);

            // Layer 3: pins + selection borders on top of everything.
            BuildConnectionCache();
            Handles.BeginGUI();
            foreach (var node in _orderedNodes)
                DrawNodePins(node);
            DrawAliasPins();
            Handles.EndGUI();

            // Layer 4: rubber-band selection rect
            if (_isRubberBanding)
            {
                var rbr    = RubberBandRect();
                var fill   = new Color(0.39f, 0.78f, 0.99f, 0.08f);
                var border = new Color(0.39f, 0.78f, 0.99f, 0.55f);
                EditorGUI.DrawRect(rbr, fill);
                EditorGUI.DrawRect(new Rect(rbr.x,         rbr.y,        rbr.width, 1f), border);
                EditorGUI.DrawRect(new Rect(rbr.x,         rbr.yMax - 1, rbr.width, 1f), border);
                EditorGUI.DrawRect(new Rect(rbr.x,         rbr.y,        1f, rbr.height), border);
                EditorGUI.DrawRect(new Rect(rbr.xMax - 1f, rbr.y,        1f, rbr.height), border);
            }

            GUI.EndClip();
        }

        // ── Event processing ──────────────────────────────────────────────────

        private void ProcessEvents()
        {
            var e = Event.current;
            if (e == null) return;
            var mp = e.mousePosition;

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Z && e.control)
            {
                Undo.PerformUndo();
                e.Use();
                _host?.Repaint();
                return;
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.S && e.control)
            {
                OnSaveRequested?.Invoke();
                e.Use();
                return;
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F)
            {
                var selected = _nodes.Where(n => n.IsSelected).ToList();
                if (selected.Count > 0) FrameNodes(selected);
                else if (_nodes.Count > 0) FrameNodes(_nodes);
                e.Use();
                return;
            }

            // Tab-flag hover tracking + left-click to frame target.
            // Only scan on mouse events — avoids iterating all edges on every Repaint/Layout pass.
            bool isMouseEvent = e.type == EventType.MouseMove  || e.type == EventType.MouseDrag
                             || e.type == EventType.MouseDown  || e.type == EventType.MouseUp;
            if (isMouseEvent)
            {
                CanvasEdge newHover = null;
                foreach (var edge in _edges)
                {
                    if (!IsTabMode(edge)) continue;
                    if (_hiddenAssets.Contains(edge.Source.Asset)) continue;
                    int oi = edge.OptionIndex; // cached — avoids O(n) IndexOf per edge
                    if (oi < 0) continue;
                    bool wasHovered = edge == _hoveredTabEdge;
                    if (TabFlagScreenRect(edge.Source, oi, wasHovered).Contains(mp))
                    { newHover = edge; break; }
                }
                if (newHover != _hoveredTabEdge) { _hoveredTabEdge = newHover; _host?.Repaint(); }
            }
            if (e.type == EventType.MouseDown && e.button == 0 && _hoveredTabEdge != null)
            {
                if (_hoveredTabEdge.Target != null)
                    FrameEntry(_hoveredTabEdge.Target.Entry.TagPath);
                e.Use();
                return;
            }

            if (e.type == EventType.ScrollWheel && _canvasRect.Contains(mp))
            {
                var pivot  = mp;
                var before = ToCanvas(pivot);
                _zoom      = Mathf.Clamp(_zoom - e.delta.y * 0.05f, 0.15f, 1.0f);
                _pan       = pivot - before * _zoom;
                SaveViewTransform();
                e.Use();
                _host?.Repaint();
                return;
            }

            bool panButton = e.button == 2 || (e.button == 0 && e.alt);
            if (e.type == EventType.MouseDown && panButton)
            {
                _isPanning = true; _panStartMouse = mp; _panStartOffset = _pan;
                e.Use(); return;
            }
            if (_isPanning)
            {
                if (e.type == EventType.MouseDrag) { _pan = _panStartOffset + (mp - _panStartMouse); e.Use(); _host?.Repaint(); }
                if (e.type == EventType.MouseUp)   { _isPanning = false; SaveViewTransform(); e.Use(); }
                return;
            }

            // ── Active waypoint drag ──────────────────────────────────────────────
            if (_dragWpEdge != null)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    if (!_isDraggingWp && Vector2.Distance(mp, _dragWpStartMouse) > DragThresh)
                        _isDraggingWp = true;
                    if (_isDraggingWp && _dragWpIdx >= 0 && _dragWpIdx < _dragWpEdge.Waypoints.Count)
                    {
                        _dragWpEdge.Waypoints[_dragWpIdx] = ToCanvas(mp);
                        e.Use(); _host?.Repaint();
                    }
                    return;
                }
                if (e.type == EventType.MouseUp && e.button == 0)
                {
                    if (_isDraggingWp) PersistWaypoints(_dragWpEdge);
                    _dragWpEdge = null; _dragWpIdx = -1; _isDraggingWp = false;
                    e.Use(); return;
                }
            }

            // ── Rubber-band drag ──────────────────────────────────────────────────
            if (_isRubberBanding)
            {
                if (e.type == EventType.MouseDrag)
                { _rubberBandEnd = mp; e.Use(); _host?.Repaint(); return; }
                if (e.type == EventType.MouseUp && e.button == 0)
                { SelectNodesInRubberBand(); _isRubberBanding = false; e.Use(); _host?.Repaint(); return; }
            }

            // ── Active alias drag ─────────────────────────────────────────────────
            if (_dragAlias != null)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    if (!_isDraggingAlias && Vector2.Distance(mp, _dragAliasStartMouse) > DragThresh)
                        _isDraggingAlias = true;
                    if (_isDraggingAlias)
                    {
                        var cp = ToCanvas(mp) - _dragAliasOffset;
                        _dragAlias.Rect          = new Rect(cp, _dragAlias.Rect.size);
                        _dragAlias.Meta.Position = cp;
                        e.Use(); _host?.Repaint();
                    }
                    return;
                }
                if (e.type == EventType.MouseUp && e.button == 0)
                {
                    if (_isDraggingAlias)
                    {
                        int mi = _assets.IndexOf(_dragAlias.OwnerNode.Asset);
                        if (mi >= 0) SaveMeta(_metas[mi]);
                    }
                    _dragAlias = null; _isDraggingAlias = false;
                    e.Use(); return;
                }
            }

            if (_isDraggingConn)
            {
                if (e.type == EventType.MouseDrag) { _connDragEnd = mp; e.Use(); _host?.Repaint(); }
                if (e.type == EventType.MouseUp && e.button == 0)
                {
                    CompleteConnectionDrag(mp);
                    _isDraggingConn = false; _connSrcNode = null;
                    e.Use(); _host?.Repaint();
                }
                return;
            }

            if (_dragNode != null)
            {
                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    if (!_isDragging && Vector2.Distance(mp, _dragStartMouse) > DragThresh)
                        _isDragging = true;
                    if (_isDragging)
                    {
                        if (_isDragMulti && _multiDragStarts.Count > 0)
                        {
                            var delta = ToCanvas(mp) - ToCanvas(_multiDragMouseStart);
                            foreach (var (mn, startPos) in _multiDragStarts)
                            {
                                var cp = startPos + delta;
                                if (_snapToGrid)
                                {
                                    const float grid = 20f;
                                    cp = new Vector2(Mathf.Round(cp.x / grid) * grid,
                                                     Mathf.Round(cp.y / grid) * grid);
                                }
                                mn.Rect = new Rect(cp, mn.Rect.size);
                                mn.Meta.Position = mn.Rect;
                            }
                        }
                        else
                        {
                            var cp = ToCanvas(mp) - _dragOffset;
                            if (_snapToGrid)
                            {
                                const float grid = 20f;
                                cp = new Vector2(Mathf.Round(cp.x / grid) * grid,
                                                 Mathf.Round(cp.y / grid) * grid);
                            }
                            _dragNode.Rect = new Rect(cp, _dragNode.Rect.size);
                            _dragNode.Meta.Position = _dragNode.Rect;
                        }
                        e.Use(); _host?.Repaint();
                    }
                    return;
                }
                if (e.type == EventType.MouseUp && e.button == 0)
                {
                    if (_isDragMulti)
                    {
                        if (_isDragging)
                        {
                            var savedMi = new HashSet<int>();
                            foreach (var (mn, _) in _multiDragStarts)
                            {
                                int mi = _assets.IndexOf(mn.Asset);
                                if (mi >= 0 && savedMi.Add(mi)) SaveMeta(_metas[mi]);
                            }
                        }
                        else if (_multiDragStarts.Count == 1)
                        {
                            OpenRenameDialog(_dragNode);
                        }
                        _isDragMulti = false; _multiDragStarts.Clear();
                    }
                    else
                    {
                        if (!_isDragging)
                            OpenRenameDialog(_dragNode);
                        else
                        {
                            int idx = _assets.IndexOf(_dragNode.Asset);
                            if (idx >= 0) SaveMeta(_metas[idx]);
                        }
                    }
                    _dragNode = null; _isDragging = false;
                    e.Use(); return;
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                // ── Waypoint hit (drag start) or double-click-to-insert ───────────
                float wpHitR = (PinR + 3f) * _zoom;
                foreach (var edge in _edges)
                {
                    if (IsTabMode(edge) || edge.Target == null) continue;
                    for (int wi = 0; wi < edge.Waypoints.Count; wi++)
                    {
                        if (Vector2.Distance(mp, ToScreen(edge.Waypoints[wi])) <= wpHitR)
                        {
                            int mi = _assets.IndexOf(edge.Source.Asset);
                            if (mi >= 0) Undo.RecordObject(_metas[mi], "Move Waypoint");
                            _dragWpEdge = edge; _dragWpIdx = wi; _isDraggingWp = false; _dragWpStartMouse = mp;
                            e.Use(); return;
                        }
                    }
                }
                if (e.clickCount == 2)
                {
                    foreach (var edge in _edges)
                    {
                        if (IsTabMode(edge) || edge.Target == null) continue;
                        if (!NearWire(edge, mp, 8f)) continue;
                        int mi = _assets.IndexOf(edge.Source.Asset);
                        if (mi >= 0) Undo.RecordObject(_metas[mi], "Add Waypoint");
                        edge.Waypoints.Insert(BestInsertIdx(edge, mp), ToCanvas(mp));
                        PersistWaypoints(edge);
                        e.Use(); _host?.Repaint(); return;
                    }
                }
                // ── Alias hit (drag start) ───────────────────────────────────────
                foreach (var alias in Enumerable.Reverse(_aliases.ToList()))
                {
                    if (!ToScreen(alias.Rect).Contains(mp)) continue;
                    int mi = _assets.IndexOf(alias.OwnerNode.Asset);
                    if (mi >= 0) Undo.RecordObject(_metas[mi], "Move Alias Pin");
                    _dragAlias = alias; _dragAliasOffset = ToCanvas(mp) - alias.Rect.position;
                    _dragAliasStartMouse = mp; _isDraggingAlias = false;
                    e.Use(); return;
                }
                // ─────────────────────────────────────────────────────────────────

                foreach (var node in Enumerable.Reverse(_nodes.ToList()))
                {
                    if (!node.Meta.ChoicesExpanded) continue;
                    if (!NodeHasFullDetail(node)) continue; // pins not rendered at low LOD
                    for (int i = 0; i < node.Entry.Options.Count; i++)
                    {
                        var pinS = ToScreen(OutputPinPos(node, i));
                        if (Vector2.Distance(mp, pinS) <= (PinR + 4f) * _zoom)
                        {
                            _isDraggingConn = true;
                            _connSrcNode    = node;
                            _connSrcOpt     = node.Entry.Options[i];
                            _connSrcOptIdx  = i;
                            _connDragEnd    = mp;
                            if (ActiveAsset != node.Asset)
                            {
                                ActiveAsset = node.Asset;
                                OnActiveAssetChanged?.Invoke();
                            }
                            e.Use(); return;
                        }
                    }
                }

                foreach (var node in Enumerable.Reverse(_nodes.ToList()))
                {
                    var sr = ToScreen(node.Rect);
                    if (!sr.Contains(mp)) continue;

                    bool alreadySelected = node.IsSelected;
                    if (!alreadySelected)
                    {
                        foreach (var n in _nodes) n.IsSelected = false;
                        node.IsSelected = true;
                        _orderedNodesDirty = true;
                    }

                    // Check for label-strip click → toggle expanded state.
                    if (node.Meta.AssignedLabelIds.Count > 0)
                    {
                        float stripH = LabelStripH * _zoom;
                        var   stripR = new Rect(sr.x, sr.y + HeaderH * _zoom, sr.width, stripH);
                        if (stripR.Contains(mp))
                        {
                            _labelsExpanded = !_labelsExpanded;
                            e.Use();
                            _host?.Repaint();
                            return;
                        }
                    }

                    var hdr = new Rect(sr.x, sr.y, sr.width, HeaderH * _zoom);
                    if (hdr.Contains(mp))
                    {
                        var selectedNodes = _nodes.Where(n => n.IsSelected).ToList();
                        if (selectedNodes.Count > 1)
                        {
                            var seenMetas = new HashSet<int>();
                            foreach (var sn in selectedNodes)
                            {
                                int mi = _assets.IndexOf(sn.Asset);
                                if (mi >= 0 && seenMetas.Add(mi)) Undo.RecordObject(_metas[mi], "Move Nodes");
                            }
                            _isDragMulti         = true;
                            _multiDragMouseStart = mp;
                            _multiDragStarts.Clear();
                            foreach (var sn in selectedNodes)
                                _multiDragStarts.Add((sn, sn.Rect.position));
                        }
                        else
                        {
                            int metaDragIdx = _assets.IndexOf(node.Asset);
                            if (metaDragIdx >= 0) Undo.RecordObject(_metas[metaDragIdx], "Move Node");
                            _isDragMulti = false;
                            _multiDragStarts.Clear();
                        }
                        _dragNode       = node;
                        _dragStartMouse = mp;
                        _dragOffset     = ToCanvas(mp) - node.Rect.position;
                        _isDragging     = false;
                        e.Use();
                    }
                    _host?.Repaint();
                    return;
                }

                foreach (var n in _nodes) n.IsSelected = false;
                _orderedNodesDirty = true;
                _isRubberBanding = true; _rubberBandStart = mp; _rubberBandEnd = mp;
                e.Use(); _host?.Repaint();
            }

            if (e.type == EventType.MouseUp && e.button == 1)
                ShowContextMenu(mp);

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete)
            {
                var sel = _nodes.Where(n => n.IsSelected).ToList();
                foreach (var n in sel) DeleteNode(n);
                if (sel.Count > 0) { e.Use(); _host?.Repaint(); }
            }
        }

        // ── Grid ──────────────────────────────────────────────────────────────

        private void DrawGrid(Rect r)
        {
            float step = 40f * _zoom;
            float ox = _pan.x % step;
            float oy = _pan.y % step;
            var minor = new Color(0.20f, 0.20f, 0.20f);
            var major = new Color(0.24f, 0.24f, 0.24f);
            for (float x = ox; x < r.width;  x += step)
            {
                bool maj = Mathf.RoundToInt((x - ox) / step) % 5 == 0;
                EditorGUI.DrawRect(new Rect(r.x + x, r.y, 1f, r.height), maj ? major : minor);
            }
            for (float y = oy; y < r.height; y += step)
            {
                bool maj = Mathf.RoundToInt((y - oy) / step) % 5 == 0;
                EditorGUI.DrawRect(new Rect(r.x, r.y + y, r.width, 1f), maj ? major : minor);
            }
        }

        // ── Connections ───────────────────────────────────────────────────────

        private static readonly string[] s_SectionTitles = { "On Enter", "Keys", "Options" };

        // Builds all string caches for a node. Call whenever entry data (items, labels) changes.
        // During Draw(), these cached strings are used directly — no per-frame allocations.
        private void RebuildNodeLabelCache(CanvasNode node)
        {
            // Label def dictionary (id → def) for O(1) pill lookups.
            node.LabelDefById.Clear();
            if (node.LabelDefs != null)
                foreach (var def in node.LabelDefs)
                    if (def != null) node.LabelDefById[def.Id] = def;

            // Section header strings — both collapsed and expanded forms.
            int[] counts = {
                node.Entry.EntryOperations.Count,
                node.Entry.ContentKeys.Count,
                node.Entry.Options.Count,
            };
            for (int s = 0; s < 3; s++)
            {
                node.SectionHeaders[s, 0] = $"▶ {s_SectionTitles[s]} ({counts[s]})";
                node.SectionHeaders[s, 1] = $"▼ {s_SectionTitles[s]} ({counts[s]})";
            }

            // Row label strings per section.
            int opCount = node.Entry.EntryOperations.Count;
            if (node.OpLabels.Length != opCount) node.OpLabels = new string[opCount];
            for (int i = 0; i < opCount; i++)
                node.OpLabels[i] = OpSummary(node.Entry.EntryOperations[i]);

            int keyCount = node.Entry.ContentKeys.Count;
            if (node.KeyLabels.Length != keyCount) node.KeyLabels = new string[keyCount];
            for (int i = 0; i < keyCount; i++)
                node.KeyLabels[i] = KeySummary(node.Entry.ContentKeys[i], i, keyCount);

            // Cache row heights — prevents TextKeyHEstimate (regex) from running every frame.
            if (node.KeyHeights.Length != keyCount) node.KeyHeights = new float[keyCount];
            for (int i = 0; i < keyCount; i++)
                node.KeyHeights[i] = TextKeyHEstimate(node.Entry.ContentKeys[i]);

            int optCount = node.Entry.Options.Count;
            if (node.OptLabels.Length != optCount) node.OptLabels = new string[optCount];
            for (int i = 0; i < optCount; i++)
                node.OptLabels[i] = OptSummary(node.Entry.Options[i]);
        }

        // Populate connection-cache HashSets used by DrawNodePins so it can skip O(edges) scans.
        private void BuildConnectionCache()
        {
            _connectedAsTarget.Clear();
            _connectedAsSource.Clear();
            foreach (var edge in _edges)
            {
                if (edge.Target == null) continue;
                _connectedAsTarget.Add(edge.Target);
                _connectedAsSource.Add((edge.Source, edge.Option));
            }
        }

        private void DrawConnections()
        {
            var wireColor = new Color(0.416f, 0.690f, 0.816f, 0.8f);
            float cw = _canvasRect.width;
            float ch = _canvasRect.height;

            foreach (var edge in _edges)
            {
                if (edge.Target == null) continue;
                // Skip nodes that are hidden or loop-back to themselves (drawn in DrawNodePins)
                if (_hiddenAssets.Contains(edge.Source.Asset)) continue;
                if (_hiddenAssets.Contains(edge.Target.Asset)) continue;
                if (edge.Source == edge.Target) continue;
                int optIdx = edge.OptionIndex;
                if (optIdx < 0) continue;

                // Frustum cull — skip if both endpoints are off the visible canvas area.
                var srcSR = ToScreen(edge.Source.Rect);
                var tgtSR = ToScreen(edge.Target.Rect);
                bool srcVis = srcSR.xMax >= 0 && srcSR.x <= cw && srcSR.yMax >= 0 && srcSR.y <= ch;
                bool tgtVis = tgtSR.xMax >= 0 && tgtSR.x <= cw && tgtSR.yMax >= 0 && tgtSR.y <= ch;
                if (!srcVis && !tgtVis) continue;

                if (IsTabMode(edge))
                {
                    DrawTabFlag(edge, optIdx, edge == _hoveredTabEdge);
                    continue;
                }

                DrawBezierEdge(edge, wireColor);

                foreach (var wp in edge.Waypoints)
                {
                    var sc = (Vector3)ToScreen(wp);
                    Handles.color = wireColor;
                    Handles.DrawSolidDisc(sc, Vector3.forward, (PinR - 1f) * _zoom);
                    Handles.color = new Color(0.10f, 0.10f, 0.10f, 0.8f);
                    Handles.DrawWireDisc(sc, Vector3.forward, (PinR - 1f) * _zoom);
                }
            }
        }

        // Returns the ordered screen-space points along an edge: source pin, waypoints, target pin.
        // Reuses _edgePtsBuf to avoid per-call List allocation — do not cache the returned reference.
        private List<Vector2> EdgeScreenPoints(CanvasEdge edge)
        {
            int oi = edge.OptionIndex >= 0 ? edge.OptionIndex : edge.Source.Entry.Options.IndexOf(edge.Option);
            _edgePtsBuf.Clear();
            _edgePtsBuf.Add(ToScreen(OutputPinPos(edge.Source, oi)));
            foreach (var wp in edge.Waypoints) _edgePtsBuf.Add(ToScreen(wp));
            if (edge.Target != null) _edgePtsBuf.Add(ToScreen(InputPinPos(edge.Target)));
            return _edgePtsBuf;
        }

        // Piecewise bezier through the edge's screen-space point list.
        // Must be called inside Handles.BeginGUI()/EndGUI().
        private void DrawBezierEdge(CanvasEdge edge, Color color)
        {
            var pts = EdgeScreenPoints(edge);
            if (pts.Count < 2) return;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                Vector3 s  = pts[i]; Vector3 t = pts[i + 1];
                float   dx = Mathf.Max(60f, Mathf.Abs(t.x - s.x) * 0.5f) * _zoom;
                Handles.DrawBezier(s, t, s + new Vector3(dx, 0f), t - new Vector3(dx, 0f),
                    color, null, 2f * _zoom);
            }
        }

        // True if mp (screen space) is within threshold pixels of any segment in the edge's polyline.
        private bool NearWire(CanvasEdge edge, Vector2 mp, float threshold)
        {
            var pts = EdgeScreenPoints(edge);
            for (int i = 0; i < pts.Count - 1; i++)
                if (DistToSegment(mp, pts[i], pts[i + 1]) <= threshold) return true;
            return false;
        }

        // Which waypoint list index to insert at (best segment index = insertion index).
        private int BestInsertIdx(CanvasEdge edge, Vector2 mp)
        {
            var   pts  = EdgeScreenPoints(edge);
            float best = float.MaxValue;
            int   idx  = edge.Waypoints.Count;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                float d = DistToSegment(mp, pts[i], pts[i + 1]);
                if (d < best) { best = d; idx = i; }
            }
            return idx;
        }

        private static float DistToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            var   ab = b - a;
            if (ab.sqrMagnitude < 0.001f) return Vector2.Distance(p, a);
            float t  = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
            return Vector2.Distance(p, a + t * ab);
        }

        // Writes edge.Waypoints back into the source node's meta and saves.
        private void PersistWaypoints(CanvasEdge edge)
        {
            int mi = _assets.IndexOf(edge.Source.Asset);
            if (mi < 0) return;
            var nm  = edge.Source.Meta;
            var wps = nm.EdgeWaypoints.FirstOrDefault(w => w.OptionTagPath == edge.Option.TagPath);
            if (wps == null)
            {
                wps = new OghamEdgeWaypoints { OptionTagPath = edge.Option.TagPath };
                nm.EdgeWaypoints.Add(wps);
            }
            wps.Points.Clear();
            wps.Points.AddRange(edge.Waypoints);
            if (wps.Points.Count == 0) nm.EdgeWaypoints.Remove(wps);
            SaveMeta(_metas[mi]);
        }

        private bool IsTabMode(CanvasEdge edge)
            => !string.IsNullOrEmpty(edge.Option.TagPath)
            && edge.Source.Meta.TabFlagOptions.Contains(edge.Option.TagPath);

        // Screen-space bounding rect for a tab flag (includes arrow tip width).
        private Rect TabFlagScreenRect(CanvasNode node, int optIdx, bool hovered)
        {
            var pin = ToScreen(OutputPinPos(node, optIdx));
            float w = ((hovered ? TabHoverW : TabDefaultW) + TabArrow) * _zoom;
            float h = TabH * _zoom;
            return new Rect(pin.x, pin.y - h * 0.5f, w, h);
        }

        // Draw a flag-shaped label at the output pin. Must be inside Handles.BeginGUI/EndGUI.
        private void DrawTabFlag(CanvasEdge edge, int optIdx, bool hovered)
        {
            var pin   = ToScreen(OutputPinPos(edge.Source, optIdx));
            float w   = (hovered ? TabHoverW : TabDefaultW) * _zoom;
            float h   = TabH * _zoom;
            float arr = TabArrow * _zoom;
            float px  = TabPadX * _zoom;

            float left   = pin.x;
            float top    = pin.y - h * 0.5f;
            float bottom = pin.y + h * 0.5f;
            float bodyR  = left + w;
            float tipX   = left + w + arr;

            var tl  = new Vector3(left,  top,    0f);
            var tr  = new Vector3(bodyR, top,    0f);
            var tip = new Vector3(tipX,  pin.y,  0f);
            var br  = new Vector3(bodyR, bottom, 0f);
            var bl  = new Vector3(left,  bottom, 0f);

            Color fill = (edge.Target != null && edge.Target.Meta.HighlightColor.a > 0f)
                ? edge.Target.Meta.HighlightColor
                : edge.Source.HeaderColor;
            fill.a = hovered ? 0.95f : 0.82f;
            if (hovered) fill = Color.Lerp(fill, Color.white, 0.15f);
            Handles.color = fill;
            Handles.DrawAAConvexPolygon(tl, tr, tip, br, bl);

            Handles.color = new Color(0f, 0f, 0f, 0.45f);
            Handles.DrawAAPolyLine(1.5f, tl, tr, tip, br, bl, tl);
            Handles.color = Color.white; // reset to avoid colour bleed into subsequent bezier draws

            // Defer the GUI.Label outside Handles.BeginGUI/EndGUI — calling GUI.Label inside
            // the Handles scope corrupts the GL state and causes bezier lines to render black.
            string label;
            if (hovered)
                label = edge.Target != null
                    ? (!string.IsNullOrEmpty(edge.Target.Entry.TagPath) ? edge.Target.Entry.TagPath : edge.Target.DisplayName)
                    : "?";
            else
            {
                label = edge.Target != null ? edge.Target.DisplayName : "?";
                if (label.Length > 4) label = label.Substring(0, 4);
            }
            _pendingTabLabels.Add((new Rect(left + px, top, w - px, h), label, AdaptiveTextColor(fill)));
        }

        // ── Alias badge drawing ───────────────────────────────────────────────

        private static Vector2 AliasPinPos(CanvasAlias alias)
            => new Vector2(alias.Rect.x + PinR + 4f, alias.Rect.y + alias.Rect.height * 0.5f);

        private void DrawAlias(CanvasAlias alias)
        {
            var   sr  = ToScreen(alias.Rect);
            EditorGUI.DrawRect(sr, NodeColors.BodyBg);
            float bt  = 1.5f;
            EditorGUI.DrawRect(new Rect(sr.x,         sr.y,         sr.width, bt),        AliasColor);
            EditorGUI.DrawRect(new Rect(sr.x,         sr.yMax - bt, sr.width, bt),        AliasColor);
            EditorGUI.DrawRect(new Rect(sr.x,         sr.y,         bt, sr.height),       AliasColor);
            EditorGUI.DrawRect(new Rect(sr.xMax - bt, sr.y,         bt, sr.height),       AliasColor);
            float pinZoneW = (PinR * 2f + 8f) * _zoom;
            string lbl = !string.IsNullOrEmpty(alias.Meta.Name)
                ? alias.Meta.Name : alias.Meta.TargetEntryTagName;
            GUI.Label(new Rect(sr.x + pinZoneW, sr.y, sr.width - pinZoneW, sr.height), lbl, _rowOptStyle);
        }

        // Must be called inside Handles.BeginGUI()/EndGUI().
        private void DrawAliasPins()
        {
            float pinSz = PinR * 2f * _zoom;
            foreach (var alias in _aliases)
            {
                var sc = (Vector3)ToScreen(AliasPinPos(alias));
                DrawTrianglePin(sc, pinSz, AliasColor, true);
            }
        }

        // ── Node drawing ──────────────────────────────────────────────────────

        // Returns true when zoom is high enough to render section row content legibly.
        private bool NodeHasFullDetail(CanvasNode node) => _zoom >= LodRowsZoom;

        private void DrawNode(CanvasNode node)
        {
            if (_hiddenAssets.Contains(node.Asset)) return;
            var sr = ToScreen(node.Rect);

            // Frustum cull — skip nodes entirely outside the visible area.
            if (sr.xMax < 0f || sr.x > _canvasRect.width || sr.yMax < 0f || sr.y > _canvasRect.height)
                return;

            bool showRows     = _zoom >= LodRowsZoom;
            bool showSections = _zoom >= LodSectionZoom;
            bool showLabel    = _zoom >= LodLabelZoom;

            // Body fill — inset 1px so the outer outline is always visible
            EditorGUI.DrawRect(new Rect(sr.x + 1f, sr.y + 1f, sr.width - 2f, sr.height - 2f),
                NodeColors.BodyBg);

            var outlineColor = new Color(0.10f, 0.10f, 0.10f);
            EditorGUI.DrawRect(new Rect(sr.x, sr.y,              sr.width,  1f),        outlineColor);
            EditorGUI.DrawRect(new Rect(sr.x, sr.yMax - 1f,      sr.width,  1f),        outlineColor);
            EditorGUI.DrawRect(new Rect(sr.x, sr.y,              1f,        sr.height), outlineColor);
            EditorGUI.DrawRect(new Rect(sr.xMax - 1f, sr.y,      1f,        sr.height), outlineColor);

            float hScaled = HeaderH * _zoom;
            var hdrR = new Rect(sr.x + 1f, sr.y + 1f, sr.width - 2f, hScaled);
            EditorGUI.DrawRect(hdrR, node.HeaderColor);

            // Header label — abbreviated to last tag segment when header is narrow; hidden below LodLabelZoom.
            if (showLabel)
            {
                float dotSz   = 8f * _zoom;
                float dotOffX = 4f * _zoom;
                var lblX = hdrR.x + dotOffX + dotSz + 3f * _zoom;
                float lblW = hdrR.xMax - lblX - 4f * _zoom;

                // Shorten display name to last tag segment when the header is too narrow on screen.
                string headerName = node.DisplayName;
                if (lblW < 110f)
                {
                    var lastDot = headerName.LastIndexOf('.');
                    if (lastDot >= 0) headerName = headerName.Substring(lastDot + 1);
                }

                _headerStyle.normal.textColor = AdaptiveTextColor(node.HeaderColor);
                GUI.Label(new Rect(lblX, hdrR.y, lblW, hScaled), headerName, _headerStyle);
            }

            // Below LodSectionZoom, only the colored header is useful — skip sections entirely.
            if (!showSections) return;

            // Label strip — collapsible pill strip below the header (only when labels assigned).
            // Click anywhere in the strip to toggle expanded/collapsed (handled in ProcessEvents).
            float labelStripScaled = 0f;
            if (node.LabelDefs != null && node.Meta.AssignedLabelIds.Count > 0)
            {
                labelStripScaled = LabelStripH * _zoom;
                var stripR = new Rect(sr.x + 1f, sr.y + 1f + hScaled, sr.width - 2f, labelStripScaled);
                EditorGUI.DrawRect(stripR, new Color(0.13f, 0.13f, 0.13f));
                EditorGUI.DrawRect(new Rect(stripR.x, stripR.y, stripR.width, 1f), NodeColors.Divider);

                float pillH    = labelStripScaled - 6f * _zoom;
                float pillY    = stripR.y + 3f * _zoom;
                float startX   = stripR.x + 6f * _zoom;
                float maxRight = stripR.xMax - 4f * _zoom;
                float pillX    = startX;

                EnsurePillStyle();
                var savedGUIColor = GUI.color;

                foreach (var id in node.Meta.AssignedLabelIds)
                {
                    if (!node.LabelDefById.TryGetValue(id, out var def)) continue;

                    float pillW = _labelsExpanded
                        ? Mathf.Min(maxRight - pillX, (def.Name.Length * 6f + 16f) * _zoom)
                        : Mathf.Min(maxRight - pillX, pillH * 2.5f); // collapsed: wide capsule, not a square dot

                    if (pillX + pillW > maxRight) break;
                    var pillR = new Rect(pillX, pillY, pillW, pillH);

                    GUI.color = def.Color;
                    GUI.Box(pillR, GUIContent.none, _pillBoxStyle);
                    GUI.color = savedGUIColor;

                    if (_labelsExpanded)
                        GUI.Label(pillR, def.Name, _pillStyle);

                    pillX += pillW + 3f * _zoom;
                }
            }

            // Meta bar (input pin strip)
            float mScaled = MetaH * _zoom;
            var metaR = new Rect(sr.x + 1f, sr.y + 1f + hScaled + labelStripScaled, sr.width - 2f, mScaled);
            EditorGUI.DrawRect(metaR, new Color(0.11f, 0.11f, 0.11f));
            EditorGUI.DrawRect(new Rect(metaR.x, metaR.y, metaR.width, 1f), NodeColors.Divider);

            float lblStrip = node.Meta.AssignedLabelIds.Count > 0 ? LabelStripH : 0f;
            float y  = sr.y + 1f + (HeaderH + lblStrip + MetaH + 2f) * _zoom;
            float sw = sr.width - 2f;
            y = DrawSection(node, sr.x + 1f, y, sw, "On Enter",
                node.Entry.EntryOperations.Count, ref node.Meta.OpsExpanded, 0);
            y = DrawSection(node, sr.x + 1f, y, sw, "Keys",
                node.Entry.ContentKeys.Count, ref node.Meta.FieldsExpanded, 1);
            DrawSection(node, sr.x + 1f, y, sw, "Options",
                node.Entry.Options.Count, ref node.Meta.ChoicesExpanded, 2);
        }

        private float DrawSection(CanvasNode node, float x, float y, float w,
            string title, int count, ref bool expanded, int sectionIdx)
        {
            float sh     = SectionHdrH * _zoom;
            float indent = RowIndent * _zoom;
            float rmW    = 16f * _zoom;
            float rmPad  = 2f * _zoom;
            // All sections use the same right margin so all buttons align vertically.
            // Options section additionally uses this column for the output pin.
            float pinCol = PinColW * _zoom;

            var hdrR = new Rect(x, y, w, sh);
            EditorGUI.DrawRect(hdrR, NodeColors.SectionHdrBg);
            EditorGUI.DrawRect(new Rect(x, y, w, 1f), NodeColors.Divider);

            // Toggle label — use pre-built cached string to avoid per-frame allocation.
            var lbl  = node.SectionHeaders[sectionIdx, expanded ? 1 : 0]
                       ?? $"{(expanded ? "▼" : "▶")} {title} ({count})"; // fallback before cache is ready
            var lblR = new Rect(hdrR.x + indent, hdrR.y,
                w - indent - rmW - rmPad - pinCol - 2f * _zoom, hdrR.height);
            if (GUI.Button(lblR, lbl, _sectionToggleStyle))
            {
                expanded = !expanded;
                RefreshNodeHeight(node);
                int mi = _assets.IndexOf(node.Asset);
                if (mi >= 0) SaveMeta(_metas[mi]);
            }

            // + button — aligned with × buttons below
            var addR = new Rect(x + w - pinCol - rmPad - rmW, hdrR.y + rmPad, rmW, sh - 2f * rmPad);
            if (GUI.Button(addR, "+", _addBtnStyle))
                AddItem(node, sectionIdx);

            y += sh;
            // Skip row content when zoom is too low to render 10pt text legibly.
            if (!expanded || _zoom < LodRowsZoom) return y;

            int rowCount = sectionIdx switch {
                0 => node.Entry.EntryOperations.Count,
                1 => node.Entry.ContentKeys.Count,
                2 => node.Entry.Options.Count,
                _ => 0,
            };

            // Scale key-row font proportionally with zoom so text remains the same relative size
            // as the node at any zoom level. Other row types stay fixed (ops/options are single-line).
            int savedKeyFontSize = _rowKeyStyle.fontSize;
            if (sectionIdx == 1)
                _rowKeyStyle.fontSize = Mathf.Max(4, Mathf.RoundToInt(10f * _zoom));

            for (int i = 0; i < rowCount; i++)
            {
                // Content keys: text keys use word-wrapped height estimate; image/audio/prefab use fixed heights.
                OghamContentKey contentKey = null;
                if (sectionIdx == 1 && i < node.Entry.ContentKeys.Count)
                    contentKey = node.Entry.ContentKeys[i];

                float rh = contentKey != null
                    ? (i < node.KeyHeights.Length ? node.KeyHeights[i] : TextKeyHEstimate(contentKey)) * _zoom
                    : RowH * _zoom;

                if (sectionIdx == 1)
                    EditorGUI.DrawRect(new Rect(x, y, w, rh), NodeColors.FieldBg);

                float rowIndent = indent + 8f * _zoom;
                float thumbW    = 0f;

                // Draw thumbnail for image-type content keys.
                if (contentKey?.Type == OghamContentType.Image)
                {
                    float thumbSize = (ImageRowH - 8f) * _zoom;
                    thumbW = thumbSize + 4f * _zoom;
                    if (contentKey.AssetRef != null)
                    {
                        var tex = UnityEditor.AssetPreview.GetAssetPreview(contentKey.AssetRef)
                               ?? UnityEditor.AssetPreview.GetMiniThumbnail(contentKey.AssetRef);
                        if (tex != null)
                            GUI.DrawTexture(
                                new Rect(x + rowIndent, y + 4f * _zoom, thumbSize, thumbSize),
                                tex, ScaleMode.ScaleToFit);
                    }
                }

                // Content indented an extra step past the section header label.
                float contentW  = w - rowIndent - thumbW - rmW - rmPad - pinCol;
                var   rowR      = new Rect(x + rowIndent + thumbW, y, contentW, rh);
                var   style    = sectionIdx == 0 ? _rowOpStyle
                               : sectionIdx == 1 ? _rowKeyStyle : _rowOptStyle;
                int captured = i;
                // Use cached label string — rebuilt on data change, not every frame.
                string rowLabel = sectionIdx == 0 && i < node.OpLabels.Length  ? node.OpLabels[i]
                                : sectionIdx == 1 && i < node.KeyLabels.Length ? node.KeyLabels[i]
                                : sectionIdx == 2 && i < node.OptLabels.Length ? node.OptLabels[i]
                                : GetRowLabel(node, sectionIdx, i);
                if (GUI.Button(rowR, rowLabel, style))
                    OpenRowPopup(node, sectionIdx, captured);

                // Reorder ▲▼ buttons — only visible when section has more than one item
                if (rowCount > 1)
                {
                    float reorderW = 14f * _zoom;
                    float reorderX = x + w - pinCol - rmPad - rmW - reorderW - rmPad;
                    float halfRh   = rh * 0.5f;
                    var upR   = new Rect(reorderX, y + rmPad,        reorderW, halfRh - rmPad);
                    var downR = new Rect(reorderX, y + halfRh,        reorderW, halfRh - rmPad);
                    if (GUI.Button(upR,   "▲", _reorderBtnStyle) && captured > 0)
                    { MoveItem(node, sectionIdx, captured, -1); break; }
                    if (GUI.Button(downR, "▼", _reorderBtnStyle) && captured < rowCount - 1)
                    { MoveItem(node, sectionIdx, captured, +1); break; }
                }

                // × remove button — fixed single-line height at the top of the row
                // so it stays consistent regardless of how much text wraps below it.
                var rmR = new Rect(x + w - pinCol - rmPad - rmW, y + rmPad, rmW, RowH * _zoom - 2f * rmPad);
                if (GUI.Button(rmR, "×", _removeBtnStyle))
                {
                    RemoveItem(node, sectionIdx, captured);
                    break;
                }
                y += rh;
            }

            if (sectionIdx == 1) _rowKeyStyle.fontSize = savedKeyFontSize;
            return y;
        }

        // Must be called inside Handles.BeginGUI() / EndGUI().
        private void DrawNodePins(CanvasNode node)
        {
            if (_hiddenAssets.Contains(node.Asset)) return;
            var sr = ToScreen(node.Rect);

            // Frustum cull — skip off-screen nodes (matches DrawNode early-out).
            if (sr.xMax < 0f || sr.x > _canvasRect.width || sr.yMax < 0f || sr.y > _canvasRect.height)
                return;

            float hScaled = HeaderH * _zoom;

            // Status dot (in header, left side)
            float dotSz   = 8f * _zoom;
            float dotOffX = 4f * _zoom;
            var dotCenter = new Vector3(
                sr.x + 1f + dotOffX + dotSz * 0.5f,
                sr.y + 1f + hScaled * 0.5f,
                0f);
            Handles.color = node.Entry.Tag.IsValid
                ? new Color(0.133f, 0.600f, 0.267f)
                : new Color(0.800f, 0.467f, 0.000f);
            Handles.DrawSolidDisc(dotCenter, Vector3.forward, dotSz * 0.5f);

            // Input pin — left-pointing triangle (flipped) for input; right-pointing for output.
            // Minimum screen size ensures pins remain clickable at low zoom.
            bool inputConnected = _connectedAsTarget.Contains(node);
            var  inputPos       = (Vector3)ToScreen(InputPinPos(node));
            float pinSz         = Mathf.Max(PinR * 2f * _zoom, LodPinMinR * 2f);
            DrawTrianglePin(inputPos, pinSz, new Color(0.416f, 0.690f, 0.816f), inputConnected);

            // Output pins (one per option) — right-pointing triangles, or loop icon for self-targeting options
            if (node.Meta.ChoicesExpanded)
            {
                for (int i = 0; i < node.Entry.Options.Count; i++)
                {
                    var opt = node.Entry.Options[i];
                    if (!string.IsNullOrEmpty(opt.TargetEntryPath) && opt.TargetEntryPath == node.Entry.TagPath)
                    {
                        DrawLoopbackSymbol(node, i, pinSz);
                        continue;
                    }
                    bool connected = _connectedAsSource.Contains((node, opt));
                    var  outPos    = (Vector3)ToScreen(OutputPinPos(node, i));
                    DrawTrianglePin(outPos, pinSz, new Color(0.816f, 0.565f, 0.290f), connected);
                }
            }

            // Highlight color accent border (2 px, inside the outline)
            if (node.Meta.HighlightColor.a > 0f)
            {
                var hc  = node.Meta.HighlightColor;
                float ht = 2f;
                EditorGUI.DrawRect(new Rect(sr.x,         sr.y,         sr.width, ht),        hc);
                EditorGUI.DrawRect(new Rect(sr.x,         sr.yMax - ht, sr.width, ht),        hc);
                EditorGUI.DrawRect(new Rect(sr.x,         sr.y,         ht, sr.height),       hc);
                EditorGUI.DrawRect(new Rect(sr.xMax - ht, sr.y,         ht, sr.height),       hc);
            }

            // Selection: corner-bracket markers so they're visually distinct from the highlight border.
            // Each corner shows an L-shaped bracket in the selection colour.
            if (!node.IsSelected) return;
            var sel  = new Color(0.98f, 0.95f, 0.35f); // bright yellow — different from highlight blues/reds
            float bt = 2f;
            float bk = Mathf.Min(sr.width * 0.25f, sr.height * 0.25f, 14f); // bracket arm length
            // Top-left
            EditorGUI.DrawRect(new Rect(sr.x, sr.y, bk, bt), sel);
            EditorGUI.DrawRect(new Rect(sr.x, sr.y, bt, bk), sel);
            // Top-right
            EditorGUI.DrawRect(new Rect(sr.xMax - bk, sr.y, bk, bt), sel);
            EditorGUI.DrawRect(new Rect(sr.xMax - bt, sr.y, bt, bk), sel);
            // Bottom-left
            EditorGUI.DrawRect(new Rect(sr.x, sr.yMax - bt, bk, bt), sel);
            EditorGUI.DrawRect(new Rect(sr.x, sr.yMax - bk, bt, bk), sel);
            // Bottom-right
            EditorGUI.DrawRect(new Rect(sr.xMax - bk, sr.yMax - bt, bk, bt), sel);
            EditorGUI.DrawRect(new Rect(sr.xMax - bt, sr.yMax - bk, bt, bk), sel);
        }

        // Right-pointing triangle execution pin matching O3DE OghamNodeItem::drawPinTriangle.
        // Filled = connected; outline-only = disconnected.
        // Must be called inside Handles.BeginGUI() / EndGUI().
        private static void DrawTrianglePin(Vector3 center, float size, Color color, bool connected)
        {
            float h = size;
            float w = size * 0.75f;
            var top   = new Vector3(center.x - w * 0.5f, center.y - h * 0.5f, 0f);
            var bot   = new Vector3(center.x - w * 0.5f, center.y + h * 0.5f, 0f);
            var tip   = new Vector3(center.x + w * 0.5f, center.y,             0f);

            if (connected)
            {
                Handles.color = color;
                Handles.DrawAAConvexPolygon(top, tip, bot);
            }
            else
            {
                Handles.color = new Color(0.12f, 0.12f, 0.12f);
                Handles.DrawAAConvexPolygon(top, tip, bot);
                Handles.color = color * 0.85f;
                Handles.DrawAAPolyLine(2f, top, tip, bot, top);
            }
        }

        // Draws a loop-back arc icon for options whose target is their own entry.
        // Arc travels: left → up → right → down (3/4 circle clockwise on screen), arrowhead at bottom pointing left.
        // Must be called inside Handles.BeginGUI() / EndGUI().
        private void DrawLoopbackSymbol(CanvasNode node, int optIdx, float pinSz)
        {
            var pinS = (Vector2)ToScreen(OutputPinPos(node, optIdx));
            float sz  = Mathf.Max(pinSz * 0.5f, 7f * _zoom);
            float cx  = pinS.x + sz;   // arc centre X, to the right of the pin
            float cy  = pinS.y;

            Color c = new Color(0.816f, 0.565f, 0.290f, 0.88f);
            Handles.color = c;

            // 3/4 arc: angle goes from π (left) to 2.5π (bottom), stepping through top and right.
            // In Unity screen-space (Y-down), increasing angle sweeps clockwise visually.
            const int segs = 12;
            var pts = new Vector3[segs + 1];
            for (int i = 0; i <= segs; i++)
            {
                float t     = (float)i / segs;
                float angle = Mathf.PI + Mathf.PI * 1.5f * t;
                pts[i] = new Vector3(cx + Mathf.Cos(angle) * sz, cy + Mathf.Sin(angle) * sz, 0f);
            }
            Handles.DrawAAPolyLine(Mathf.Max(1f, 1.5f * _zoom), pts);

            // Arrowhead at arc end (bottom of circle), pointing left — tangent at that point.
            var tip    = (Vector2)pts[segs];
            float arrSz = Mathf.Max(3f, 4f * _zoom);
            Handles.DrawAAConvexPolygon(
                new Vector3(tip.x,          tip.y,              0f),
                new Vector3(tip.x + arrSz,  tip.y - arrSz * 0.5f, 0f),
                new Vector3(tip.x + arrSz,  tip.y + arrSz * 0.5f, 0f));
        }

        // ── Row labels ────────────────────────────────────────────────────────

        private string GetRowLabel(CanvasNode node, int section, int idx)
        {
            return section switch {
                0 when idx < node.Entry.EntryOperations.Count
                    => OpSummary(node.Entry.EntryOperations[idx]),
                1 when idx < node.Entry.ContentKeys.Count
                    => KeySummary(node.Entry.ContentKeys[idx], idx, node.Entry.ContentKeys.Count),
                2 when idx < node.Entry.Options.Count
                    => OptSummary(node.Entry.Options[idx]),
                _ => "",
            };
        }

        private static string OpSummary(GameplayTagOperation op)
        {
            var tag = OghamTagHelper.GetTagName(op.Tag.Id);
            if (string.IsNullOrEmpty(tag)) tag = op.Tag.IsValid ? op.Tag.Id.ToString("X") : "?";
            var sym = op.Arithmetic switch {
                GameplayTagArithmetic.Set      => "=",
                GameplayTagArithmetic.Add      => "+=",
                GameplayTagArithmetic.Subtract => "-=",
                GameplayTagArithmetic.Multiply => "*=",
                GameplayTagArithmetic.Divide   => "/=",
                _                              => "?",
            };
            return $"{tag} {sym} {op.Value}"
                + (op.Conditions.Count > 0 ? $" (if {op.Conditions.Count})" : "");
        }

        // Prepare text for IMGUI rich text display.
        // IMGUI supports <b>, <i>, <color>, <size>; it shows <u> literally, so strip those.
        // Markdown-style links [display](target) are also not renderable — show display text only.
        // Prepare text for IMGUI richText rendering.
        // IMGUI supports <b>, <i>, <color>, <size>; all other tags (<u>, <link=...>)
        // would display literally, so strip them while preserving their inner content.
        private static string PrepareNodeText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var sb = new System.Text.StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '<')
                {
                    int end = raw.IndexOf('>', i);
                    if (end >= 0)
                    {
                        var inner = raw.Substring(i + 1, end - i - 1).TrimStart('/').ToLowerInvariant();
                        // Strip tags IMGUI would show literally instead of rendering.
                        if (inner == "u" || inner == "link" || inner.StartsWith("link=") || inner.StartsWith("link "))
                        { i = end + 1; continue; }
                        sb.Append(raw, i, end - i + 1);
                        i = end + 1;
                        continue;
                    }
                }
                sb.Append(raw[i++]);
            }
            return sb.ToString();
        }

        // Strip all markup (tags + links) to get pure visible character count for height estimation.
        // Strip all markup tags to get pure visible character count for height estimation.
        // Generic tag stripping handles <link=...>, </link>, <b>, etc.
        private static string StripMarkup(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            var sb = new System.Text.StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                if (raw[i] == '<')
                {
                    int end = raw.IndexOf('>', i);
                    if (end >= 0) { i = end + 1; continue; }
                }
                sb.Append(raw[i++]);
            }
            return sb.ToString();
        }

        private static string KeySummary(OghamContentKey key, int idx, int total)
        {
            string badge = key.Type switch {
                OghamContentType.Image  => "[IMG] ",
                OghamContentType.Audio  => "[AUD] ",
                OghamContentType.Prefab => "[PFB] ",
                _                       => "",
            };
            var s = key.Type == OghamContentType.Text
                ? PrepareNodeText(OghamInlineLinkParser.ToTMProMarkup(key.ResolveText()))
                : key.KeyOrValue;
            if (string.IsNullOrEmpty(s)) s = "(empty)";
            // Non-text keys: truncate for single-line display. Text keys: show in full (word wrap handles display).
            if (key.Type != OghamContentType.Text && s.Length > 38) s = s.Substring(0, 35) + "…";
            var body = badge + s;
            return total > 1 ? $"Key {idx + 1}: {body}" : body;
        }

        private static string OptSummary(DialogueOption opt)
        {
            var s = opt.TextKey.Resolve();
            if (string.IsNullOrEmpty(s))
                s = !string.IsNullOrEmpty(opt.TagPath) ? opt.TagPath : "Option";
            if (opt.Conditions.Count > 0) s += $" (if {opt.Conditions.Count})";
            if (opt.Operations.Count  > 0) s += $" [{opt.Operations.Count}]";
            return s;
        }

        // ── Add / remove items ────────────────────────────────────────────────

        private void AddItem(CanvasNode node, int section)
        {
            Undo.RecordObject(node.Asset, "Add Item");
            switch (section)
            {
                case 0:
                    node.Entry.EntryOperations.Add(new GameplayTagOperation());
                    break;
                case 1:
                    node.Entry.ContentKeys.Add(new OghamContentKey());
                    break;
                case 2:
                    var opt = new DialogueOption();
                    if (!string.IsNullOrEmpty(node.Entry.TagPath))
                    {
                        var baseName = node.DisplayName + ".Option";
                        int n = 1;
                        while (node.Entry.Options.Any(o =>
                            !string.IsNullOrEmpty(o.TagPath) && o.TagPath == baseName + n.ToString())) n++;
                        opt.TagPath = baseName + n.ToString();
                    }
                    node.Entry.Options.Add(opt);
                    node.Asset.BuildIndex();
                    break;
            }
            EditorUtility.SetDirty(node.Asset);
            RebuildNodeLabelCache(node);
            RefreshNodeHeight(node);
            RebuildEdgesForNode(node);
        }

        private void RemoveItem(CanvasNode node, int section, int idx)
        {
            Undo.RecordObject(node.Asset, "Remove Item");
            switch (section)
            {
                case 0:
                    if (idx < node.Entry.EntryOperations.Count)
                        node.Entry.EntryOperations.RemoveAt(idx);
                    break;
                case 1:
                    if (idx < node.Entry.ContentKeys.Count)
                        node.Entry.ContentKeys.RemoveAt(idx);
                    break;
                case 2:
                    if (idx < node.Entry.Options.Count)
                    {
                        node.Entry.Options.RemoveAt(idx);
                        node.Asset.BuildIndex();
                    }
                    break;
            }
            EditorUtility.SetDirty(node.Asset);
            RefreshNodeHeight(node);
            RebuildEdgesForNode(node);
        }

        private void MoveItem(CanvasNode node, int section, int idx, int delta)
        {
            Undo.RecordObject(node.Asset, "Reorder Item");
            switch (section)
            {
                case 0:
                {
                    var list = node.Entry.EntryOperations;
                    int to = idx + delta;
                    if (to < 0 || to >= list.Count) return;
                    (list[idx], list[to]) = (list[to], list[idx]);
                    break;
                }
                case 1:
                {
                    var list = node.Entry.ContentKeys;
                    int to = idx + delta;
                    if (to < 0 || to >= list.Count) return;
                    (list[idx], list[to]) = (list[to], list[idx]);
                    break;
                }
                case 2:
                {
                    var list = node.Entry.Options;
                    int to = idx + delta;
                    if (to < 0 || to >= list.Count) return;
                    (list[idx], list[to]) = (list[to], list[idx]);
                    node.Asset.BuildIndex();
                    break;
                }
            }
            EditorUtility.SetDirty(node.Asset);
            RebuildEdgesForNode(node);
            _host?.Repaint();
        }

        private void RefreshNodeHeight(CanvasNode node)
        {
            RebuildNodeLabelCache(node);
            node.Rect = new Rect(node.Rect.x, node.Rect.y, NodeW, NodeHeight(node.Entry, node.Meta));
            node.Meta.Position = node.Rect;
            _host?.Repaint();
        }

        private void RebuildEdgesForNode(CanvasNode node)
        {
            if (_dragWpEdge != null && _dragWpEdge.Source == node)
            { _dragWpEdge = null; _dragWpIdx = -1; _isDraggingWp = false; }
            _edges.RemoveAll(e => e.Source == node);
            for (int oi = 0; oi < node.Entry.Options.Count; oi++)
            {
                var opt    = node.Entry.Options[oi];
                var target = !string.IsNullOrEmpty(opt.TargetEntryPath)
                    ? _nodes.FirstOrDefault(n => n.Entry.TagPath == opt.TargetEntryPath)
                    : null;
                var edge = new CanvasEdge { Source = node, Option = opt, OptionIndex = oi, Target = target };
                var wps  = node.Meta.EdgeWaypoints.FirstOrDefault(w => w.OptionTagPath == opt.TagPath);
                if (wps != null) edge.Waypoints.AddRange(wps.Points);
                _edges.Add(edge);
            }
            RebuildNodeLabelCache(node);
        }

        // ── Open popup editors ────────────────────────────────────────────────

        private void OpenRowPopup(CanvasNode node, int section, int idx)
        {
            var screenLeft = GUIUtility.GUIToScreenPoint(
                new Vector2(ToScreen(node.Rect).x, 0f)).x;
            var anchor = new Vector2(screenLeft,
                GUIUtility.GUIToScreenPoint(Event.current.mousePosition).y);

            System.Action onRefresh = () => { RefreshNodeHeight(node); _host?.Repaint(); };

            switch (section)
            {
                case 0 when idx < node.Entry.EntryOperations.Count:
                    OghamOperationEditWindow.Open(node.Entry.EntryOperations[idx],
                        node.Asset, onRefresh, anchor);
                    break;
                case 1 when idx < node.Entry.ContentKeys.Count:
                    OghamKeyEditWindow.Open(node.Entry.ContentKeys[idx],
                        node.Asset, onRefresh, anchor);
                    break;
                case 2 when idx < node.Entry.Options.Count:
                    OghamOptionEditWindow.Open(node.Entry.Options[idx],
                        node.Asset, onRefresh, anchor);
                    break;
            }
        }

        // ── Cascade / Sequence ────────────────────────────────────────────────

        private void ShowCascadeDialog(CanvasNode node)
        {
            var anchor = GUIUtility.GUIToScreenPoint(ToScreen(node.Rect.position));
            OghamCascadeDialog.Open(node.Entry.TagPath, count => ExecuteCascade(node, count), anchor);
        }

        private void ExecuteCascade(CanvasNode node, int count)
        {
            if (count < 2) return;
            var asset    = node.Asset;
            var original = node.Entry;
            var basePath = original.TagPath;
            if (string.IsNullOrEmpty(basePath)) return;

            int mi = _assets.IndexOf(asset);
            Undo.RecordObject(asset, "Cascade");
            if (mi >= 0) Undo.RecordObject(_metas[mi], "Cascade");

            // Save operations — Seq1 gets them; all later Seqs (including renamed original) have none.
            var savedOps = new List<GameplayTagOperation>(original.EntryOperations);
            original.EntryOperations.Clear();

            // Rename original entry → SeqN (keeps its original options).
            var seqNPath = basePath + ".Seq" + count;
            original.TagPath = seqNPath;
            OghamTagHelper.EnsureRegistered(seqNPath);

            // Record where SeqN (original) sits so we can insert Seq1…Seq(N-1) before it in order.
            int baseInsertIdx = asset.Entries.IndexOf(original);
            if (baseInsertIdx < 0) baseInsertIdx = asset.Entries.Count - 1;

            // Create Seq1 … Seq(N-1), inserting each one after the previous so list order is sequential.
            for (int i = 1; i < count; i++)
            {
                var seqPath  = basePath + ".Seq" + i;
                var nextPath = i < count - 1 ? basePath + ".Seq" + (i + 1) : seqNPath;

                var entry = new DialogueEntry { TagPath = seqPath };
                OghamTagHelper.EnsureRegistered(seqPath);

                // Deep-copy content keys from original.
                foreach (var key in original.ContentKeys)
                    entry.ContentKeys.Add(new OghamContentKey {
                        Type       = key.Type,
                        Mode       = key.Mode,
                        KeyOrValue = key.KeyOrValue,
                        AssetRef   = key.AssetRef,
                    });

                // Seq1 carries the original operations.
                if (i == 1)
                    entry.EntryOperations.AddRange(savedOps);

                // Add a single "Continue" option pointing to the next node.
                var continueOpt = new DialogueOption {
                    TagPath         = seqPath + ".Continue",
                    TargetEntryPath = nextPath,
                };
                continueOpt.TextKey.Mode      = Heathen.Lexicon.LexiconLocMode.Literal;
                continueOpt.TextKey.KeyOrValue = "Continue";
                entry.Options.Add(continueOpt);
                OghamTagHelper.EnsureRegistered(seqPath + ".Continue");

                // baseInsertIdx + (i-1): each successive insert shifts by one, keeping order Seq1…Seq(N-1), SeqN.
                asset.Entries.Insert(baseInsertIdx + (i - 1), entry);
            }

            asset.BuildIndex();
            EditorUtility.SetDirty(asset);
            RebuildCanvas();
            AutoLayoutAsset(asset);
            OnGraphChanged?.Invoke();
            _host?.Repaint();
        }

        // ── Rename dialog ─────────────────────────────────────────────────────

        private void OpenRenameDialog(CanvasNode node)
        {
            var current = !string.IsNullOrEmpty(node.Meta.TagName) ? node.Meta.TagName
                : !string.IsNullOrEmpty(node.Entry.TagPath) ? node.Entry.TagPath : "";
            var anchor = GUIUtility.GUIToScreenPoint(ToScreen(node.Rect.position));
            OghamRenameWindow.Open(current, newPath =>
            {
                if (string.IsNullOrWhiteSpace(newPath)) return;
                var trimmed = newPath.Trim();
                if (trimmed == current) return;
                int renameMetaIdx = _assets.IndexOf(node.Asset);
                Undo.RecordObject(node.Asset, "Rename Entry");
                if (renameMetaIdx >= 0) Undo.RecordObject(_metas[renameMetaIdx], "Rename Entry");
                node.Entry.TagPath = trimmed;
                OghamTagHelper.EnsureRegistered(trimmed);
                PropagateTagRename(current, trimmed);
                node.Asset.BuildIndex();
                EditorUtility.SetDirty(node.Asset);
                node.DisplayName      = trimmed;
                node.Meta.TagName     = trimmed;
                if (renameMetaIdx >= 0) { _metas[renameMetaIdx].PruneOrphans(); SaveMeta(_metas[renameMetaIdx]); }
                RebuildCanvas();
                OnGraphChanged?.Invoke();
            }, anchor);
        }

        // ── Delete node ───────────────────────────────────────────────────────

        private void DeleteNode(CanvasNode node)
        {
            Undo.RecordObject(node.Asset, "Delete Node");
            int delMetaIdx = _assets.IndexOf(node.Asset);
            if (delMetaIdx >= 0) Undo.RecordObject(_metas[delMetaIdx], "Delete Node");
            foreach (var edge in _edges.Where(e => e.Target == node))
            {
                if (edge.Source.Asset != node.Asset)
                    Undo.RecordObject(edge.Source.Asset, "Delete Node");
                edge.Option.TargetEntryPath = "";
                EditorUtility.SetDirty(edge.Source.Asset);
            }
            node.Asset.Entries.Remove(node.Entry);
            node.Asset.BuildIndex();
            EditorUtility.SetDirty(node.Asset);
            if (delMetaIdx >= 0) { _metas[delMetaIdx].RemoveNode(node.Entry.TagPath); SaveMeta(_metas[delMetaIdx]); }
            RebuildCanvas();
            OnGraphChanged?.Invoke();
        }

        // ── Tag rename propagation ────────────────────────────────────────────

        // Scans all loaded assets and metas; updates any reference to oldPath → newPath.
        private void PropagateTagRename(string oldPath, string newPath)
        {
            if (string.IsNullOrEmpty(oldPath)) return;
            foreach (var asset in _assets)
            {
                bool dirty = false;
                foreach (var entry in asset.Entries)
                    foreach (var opt in entry.Options)
                        if (opt.TargetEntryPath == oldPath)
                        {
                            if (!dirty) { Undo.RecordObject(asset, "Rename Entry"); dirty = true; }
                            opt.TargetEntryPath = newPath;
                        }
                if (dirty) { asset.BuildIndex(); EditorUtility.SetDirty(asset); }
            }
            for (int i = 0; i < _metas.Count; i++)
            {
                bool dirty = false;
                foreach (var nm in _metas[i].Nodes)
                    foreach (var alias in nm.AliasPins)
                        if (alias.TargetEntryTagName == oldPath)
                        {
                            if (!dirty) { Undo.RecordObject(_metas[i], "Rename Entry"); dirty = true; }
                            alias.TargetEntryTagName = newPath;
                        }
                if (dirty) SaveMeta(_metas[i]);
            }
        }

        // ── Context menu ──────────────────────────────────────────────────────

        private void ShowContextMenu(Vector2 mp)
        {
            // Right-click on a waypoint → dedicated "Remove Waypoint" menu.
            float wpR = (PinR + 4f) * _zoom;
            foreach (var edge in _edges)
            {
                if (IsTabMode(edge) || edge.Target == null) continue;
                for (int wi = 0; wi < edge.Waypoints.Count; wi++)
                {
                    if (Vector2.Distance(mp, ToScreen(edge.Waypoints[wi])) > wpR) continue;
                    var capEdge = edge; var capWi = wi;
                    var wpMenu  = new GenericMenu();
                    wpMenu.AddItem(new GUIContent("Remove Waypoint"), false, () =>
                    {
                        int mi = _assets.IndexOf(capEdge.Source.Asset);
                        if (mi >= 0) Undo.RecordObject(_metas[mi], "Remove Waypoint");
                        capEdge.Waypoints.RemoveAt(capWi);
                        PersistWaypoints(capEdge);
                        _host?.Repaint();
                    });
                    wpMenu.ShowAsContext();
                    return;
                }
            }

            // Right-click on alias badge → Rename / Remove menu.
            foreach (var alias in _aliases)
            {
                if (!ToScreen(alias.Rect).Contains(mp)) continue;
                var capAlias = alias;
                var aMenu = new GenericMenu();
                aMenu.AddItem(new GUIContent("Rename…"), false, () =>
                {
                    var anchor = GUIUtility.GUIToScreenPoint(ToScreen(capAlias.Rect.position));
                    OghamRenameWindow.Open(capAlias.Meta.Name, newName =>
                    {
                        if (string.IsNullOrWhiteSpace(newName)) return;
                        int mi = _assets.IndexOf(capAlias.OwnerNode.Asset);
                        if (mi >= 0) Undo.RecordObject(_metas[mi], "Rename Alias Pin");
                        capAlias.Meta.Name = newName.Trim();
                        if (mi >= 0) SaveMeta(_metas[mi]);
                        _host?.Repaint();
                    }, anchor);
                });
                aMenu.AddItem(new GUIContent("Remove Alias Pin"), false, () =>
                {
                    int mi = _assets.IndexOf(capAlias.OwnerNode.Asset);
                    if (mi >= 0) Undo.RecordObject(_metas[mi], "Remove Alias Pin");
                    capAlias.OwnerNode.Meta.AliasPins.Remove(capAlias.Meta);
                    if (mi >= 0) SaveMeta(_metas[mi]);
                    RebuildCanvas();
                    _host?.Repaint();
                });
                aMenu.ShowAsContext();
                return;
            }

            var cp   = ToCanvas(mp);
            var node = _nodes.LastOrDefault(n => n.Rect.Contains(cp));
            var menu = new GenericMenu();

            if (node != null)
            {
                var cap = node;
                menu.AddItem(new GUIContent("Rename Tag…"), false, () => OpenRenameDialog(cap));
                if (!string.IsNullOrEmpty(cap.Entry.TagPath))
                    menu.AddItem(new GUIContent("Cascade…"), false, () => ShowCascadeDialog(cap));

                // Per-option tab-flag toggles
                if (node.Entry.Options.Count > 0 && node.Meta.ChoicesExpanded)
                {
                    menu.AddSeparator("");
                    for (int oi = 0; oi < node.Entry.Options.Count; oi++)
                    {
                        var opt     = node.Entry.Options[oi];
                        if (string.IsNullOrEmpty(opt.TagPath)) continue;
                        bool isTab  = node.Meta.TabFlagOptions.Contains(opt.TagPath);
                        var  label  = isTab
                            ? $"Show as Wire: {OptSummary(opt)}"
                            : $"Show as Tab: {OptSummary(opt)}";
                        var captOpt   = opt;
                        var captMeta  = node.Meta;
                        var captAsset = node.Asset;
                        int captMetaIdx = _assets.IndexOf(captAsset);
                        menu.AddItem(new GUIContent(label), isTab, () =>
                        {
                            if (captMetaIdx >= 0) Undo.RecordObject(_metas[captMetaIdx], "Toggle Tab Flag");
                            if (captMeta.TabFlagOptions.Contains(captOpt.TagPath))
                                captMeta.TabFlagOptions.Remove(captOpt.TagPath);
                            else
                                captMeta.TabFlagOptions.Add(captOpt.TagPath);
                            if (captMetaIdx >= 0) SaveMeta(_metas[captMetaIdx]);
                            _host?.Repaint();
                        });
                    }
                }

                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Add Alias Pin…"), false, () => ShowAddAliasPinDialog(cap, cp));

                // Highlight color submenu
                menu.AddSeparator("");
                var   hlMeta = node.Meta;
                int   hlMi   = _assets.IndexOf(node.Asset);
                foreach (var (hlLabel, hlCol) in new (string, Color)[] {
                    ("Highlight/Clear",  Color.clear),
                    ("Highlight/Red",    new Color(0.85f, 0.25f, 0.25f)),
                    ("Highlight/Green",  new Color(0.25f, 0.70f, 0.35f)),
                    ("Highlight/Blue",   new Color(0.25f, 0.55f, 0.90f)),
                    ("Highlight/Yellow", new Color(0.90f, 0.80f, 0.20f)),
                    ("Highlight/Purple", new Color(0.60f, 0.25f, 0.85f)),
                })
                {
                    var capCol = hlCol;
                    menu.AddItem(new GUIContent(hlLabel), node.Meta.HighlightColor == hlCol, () =>
                    {
                        if (hlMi >= 0) Undo.RecordObject(_metas[hlMi], "Set Highlight Color");
                        hlMeta.HighlightColor = capCol;
                        if (hlMi >= 0) SaveMeta(_metas[hlMi]);
                        _host?.Repaint();
                    });
                }

                // Label management
                menu.AddSeparator("");
                var labelNode   = node;
                var labelMi     = hlMi;
                var labelAnchor = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                menu.AddItem(new GUIContent("Labels…"), false, () =>
                {
                    if (labelMi < 0) return;
                    var meta     = _metas[labelMi];
                    var nodeMeta = labelNode.Meta;
                    var asset    = labelNode.Asset;
                    OghamLabelPickerPopup.OpenNodeMode(meta, nodeMeta, asset, () =>
                    {
                        SaveMeta(meta);
                        // Recalculate height in case labels were added/removed
                        RefreshNodeHeight(labelNode);
                        labelNode.LabelDefs = meta.Labels;
                        _host?.Repaint();
                    }, labelAnchor);
                });

                // Align / distribute — only shown when multiple nodes are selected
                var selCount = _nodes.Count(n => n.IsSelected);
                if (selCount >= 2)
                {
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Align/Left"),          false, () => AlignSelected(0));
                    menu.AddItem(new GUIContent("Align/Right"),         false, () => AlignSelected(1));
                    menu.AddItem(new GUIContent("Align/Center Horiz"),  false, () => AlignSelected(2));
                    menu.AddItem(new GUIContent("Align/Top"),           false, () => AlignSelected(3));
                    menu.AddItem(new GUIContent("Align/Bottom"),        false, () => AlignSelected(4));
                    menu.AddItem(new GUIContent("Align/Center Vert"),   false, () => AlignSelected(5));
                    if (selCount >= 3)
                    {
                        menu.AddItem(new GUIContent("Align/Distribute Horiz"), false, () => AlignSelected(6));
                        menu.AddItem(new GUIContent("Align/Distribute Vert"),  false, () => AlignSelected(7));
                    }
                }

                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete Node"), false, () => DeleteNode(cap));
            }
            else
            {
                var target = ActiveAsset ?? (_assets.Count == 1 ? _assets[0] : null);
                if (target != null)
                {
                    var a = target; var p = cp;
                    menu.AddItem(new GUIContent("Add Entry"), false, () => CreateEntry(a, p));
                }
                else
                {
                    foreach (var asset in _assets)
                    {
                        var a = asset; var p = cp;
                        menu.AddItem(new GUIContent($"Add Entry → '{a.name}'"), false,
                            () => CreateEntry(a, p));
                    }
                    if (_assets.Count == 0)
                        menu.AddDisabledItem(new GUIContent("No OghamData files loaded"));
                }

                // Label manager — overview mode
                if (_metas.Count > 0)
                {
                    menu.AddSeparator("");
                    var bgAnchor = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
                    if (_metas.Count == 1)
                    {
                        var bgMeta = _metas[0];
                        menu.AddItem(new GUIContent("Manage Labels…"), false, () =>
                            OghamLabelPickerPopup.OpenOverviewMode(bgMeta, () =>
                            {
                                SaveMeta(bgMeta);
                                _host?.Repaint();
                            }, bgAnchor));
                    }
                    else
                    {
                        foreach (var m in _metas)
                        {
                            var capMeta = m;
                            menu.AddItem(new GUIContent($"Manage Labels/{capMeta.name}…"), false, () =>
                                OghamLabelPickerPopup.OpenOverviewMode(capMeta, () =>
                                {
                                    SaveMeta(capMeta);
                                    _host?.Repaint();
                                }, bgAnchor));
                        }
                    }
                }
            }
            menu.ShowAsContext();
        }

        // ── Connection drag completion ─────────────────────────────────────────

        private void CompleteConnectionDrag(Vector2 mp)
        {
            if (_connSrcOpt == null) return;
            var cp     = ToCanvas(mp);
            var target = _nodes.LastOrDefault(n => n.Rect.Contains(cp));

            // Drop on an alias pin badge → connect to alias's target entry.
            var aliasTarget = _aliases.LastOrDefault(a => a.Rect.Contains(cp));
            if (aliasTarget != null && !string.IsNullOrEmpty(aliasTarget.Meta.TargetEntryTagName))
            {
                Undo.RecordObject(_connSrcNode.Asset, "Connect via Alias");
                _connSrcOpt.TargetEntryPath = aliasTarget.Meta.TargetEntryTagName;
                EditorUtility.SetDirty(_connSrcNode.Asset);
                RebuildCanvas();
                return;
            }

            if (target != null && target != _connSrcNode)
            {
                Undo.RecordObject(_connSrcNode.Asset, "Connect");
                _connSrcOpt.TargetEntryPath = target.Entry.TagPath;
                EditorUtility.SetDirty(_connSrcNode.Asset);
                RebuildCanvas();
            }
            else if (target == null)
            {
                var srcNode = _connSrcNode;
                var srcOpt  = _connSrcOpt;
                var pos     = cp;
                var suggested   = SuggestEntryTag(srcNode.Asset, srcOpt);
                var dropAnchor  = GUIUtility.GUIToScreenPoint(mp);
                OghamRenameWindow.Open(suggested, newPath =>
                {
                    if (string.IsNullOrWhiteSpace(newPath)) return;
                    var t     = newPath.Trim();
                    int connMetaIdx = _assets.IndexOf(srcNode.Asset);
                    Undo.RecordObject(srcNode.Asset, "Add Entry");
                    if (connMetaIdx >= 0) Undo.RecordObject(_metas[connMetaIdx], "Add Entry");
                    var entry = new DialogueEntry();
                    entry.TagPath = t;
                    OghamTagHelper.EnsureRegistered(t);
                    srcNode.Asset.Entries.Add(entry);
                    srcNode.Asset.BuildIndex();
                    srcOpt.TargetEntryPath = t;
                    EditorUtility.SetDirty(srcNode.Asset);
                    if (connMetaIdx >= 0)
                    {
                        var nm = _metas[connMetaIdx].GetOrCreateNode(t);
                        nm.Position = new Rect(pos, new Vector2(NodeW, 200f));
                        nm.TagName  = t;
                        SaveMeta(_metas[connMetaIdx]);
                    }
                    RebuildCanvas();
                    OnGraphChanged?.Invoke();
                }, dropAnchor);
            }
        }

        private string SuggestEntryTag(OghamData asset, DialogueOption srcOpt)
        {
            string ns = null;
            foreach (var e in asset.Entries)
            {
                if (!e.Options.Contains(srcOpt)) continue;
                var name = ResolveEntryName(e);
                var dot  = name.LastIndexOf('.');
                ns = dot > 0 ? name.Substring(0, dot) : name;
                break;
            }
            var baseName = (ns != null ? ns + "." : "") + "NewNode";
            for (int i = 0; i < 1000; i++)
            {
                var cand = i == 0 ? baseName : baseName + i;
                if (!asset.Entries.Any(e => e.TagPath == cand)) return cand;
            }
            return baseName + System.Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        public void CreateEntry(OghamData asset, Vector2 canvasPos)
        {
            int mi = _assets.IndexOf(asset);
            Undo.RecordObject(asset, "Add Entry");
            if (mi >= 0) Undo.RecordObject(_metas[mi], "Add Entry");
            var entry = new DialogueEntry();
            asset.Entries.Add(entry);
            asset.BuildIndex();
            EditorUtility.SetDirty(asset);
            if (mi >= 0)
            {
                var nm = _metas[mi].GetOrCreateNode(entry.TagPath);
                nm.Position = new Rect(canvasPos, new Vector2(NodeW, 200f));
                SaveMeta(_metas[mi]);
            }
            RebuildCanvas();
            OnGraphChanged?.Invoke();
        }

        public Vector2 CanvasCentre => ToCanvas(_canvasRect.center);

        public float Zoom => _zoom;

        public void SetZoom(float z)
        {
            _zoom = Mathf.Clamp(z, 0.15f, 1.0f);
            _pan  = _canvasRect.center - ToCanvas(_canvasRect.center) * _zoom;
            SaveViewTransform();
            _host?.Repaint();
        }

        // ── Styles ────────────────────────────────────────────────────────────

        private GUIStyle _headerStyle;
        private GUIStyle _sectionToggleStyle;
        private GUIStyle _rowOpStyle;
        private GUIStyle _rowKeyStyle;
        private GUIStyle _rowOptStyle;
        private GUIStyle _addBtnStyle;
        private GUIStyle _removeBtnStyle;
        private GUIStyle _reorderBtnStyle;
        private GUIStyle _tabLabelStyle;
        private GUIStyle _pillStyle;
        private bool     _stylesBuilt;

        private void EnsureStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _headerStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 12, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                clipping  = TextClipping.Clip,
                normal    = { textColor = Color.white },
            };
            _sectionToggleStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 10,
                alignment = TextAnchor.MiddleLeft,
                normal    = { textColor = NodeColors.SectionText, background = null },
                hover     = { textColor = Color.white,            background = null },
            };
            _rowOpStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 10, alignment = TextAnchor.MiddleLeft,
                clipping  = TextClipping.Clip, wordWrap = false,
                normal    = { textColor = NodeColors.OpText,     background = null },
                hover     = { textColor = NodeColors.OpText * 1.3f, background = MakeTex(new Color(0.27f, 0.27f, 0.27f)) },
            };
            _rowKeyStyle = new GUIStyle(_rowOpStyle) {
                wordWrap  = true,
                richText  = true,
                clipping  = TextClipping.Clip,
                alignment = TextAnchor.UpperLeft,
                normal    = { textColor = NodeColors.FieldText,  background = null },
                hover     = { textColor = NodeColors.FieldText * 1.3f, background = MakeTex(new Color(0.27f, 0.27f, 0.27f)) },
            };
            _rowOptStyle = new GUIStyle(_rowOpStyle) {
                normal    = { textColor = NodeColors.OptionText, background = null },
                hover     = { textColor = NodeColors.OptionText * 1.3f, background = MakeTex(new Color(0.27f, 0.27f, 0.27f)) },
            };
            // Transparent background — only the colored icon text is visible.
            _addBtnStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 14, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.35f, 0.90f, 0.35f), background = null },
                hover     = { textColor = new Color(0.55f, 1.00f, 0.55f), background = null },
            };
            _removeBtnStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 14, fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.90f, 0.35f, 0.35f), background = null },
                hover     = { textColor = new Color(1.00f, 0.55f, 0.55f), background = null },
            };
            _reorderBtnStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 8,
                alignment = TextAnchor.MiddleCenter,
                normal    = { textColor = new Color(0.55f, 0.55f, 0.55f), background = null },
                hover     = { textColor = new Color(0.85f, 0.85f, 0.85f), background = null },
            };
            _tabLabelStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 9,
                alignment = TextAnchor.MiddleLeft,
                clipping  = TextClipping.Clip,
                wordWrap  = false,
                normal    = { textColor = Color.white, background = null },
            };
            _pillStyle = new GUIStyle(GUI.skin.label) {
                fontSize  = 8,
                alignment = TextAnchor.MiddleCenter,
                clipping  = TextClipping.Clip,
                wordWrap  = false,
                normal    = { textColor = Color.white, background = null },
            };
        }

        // Returns dark or light text based on perceived luminance of the background.
        private static Color AdaptiveTextColor(Color bg)
            => (0.299f * bg.r + 0.587f * bg.g + 0.114f * bg.b) > 0.55f
                ? new Color(0.10f, 0.10f, 0.10f)
                : Color.white;

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        // Builds a white capsule Texture2D (32×16) and wires it into _pillBoxStyle with 9-slice borders.
        // GUI.color tinting makes each pill the right label colour without needing per-colour textures.
        private void EnsurePillStyle()
        {
            if (_pillBoxStyle != null) return;

            const int w = 32, h = 16;
            float r = h * 0.5f;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float dLeft  = Mathf.Sqrt((x - r) * (x - r) + (y - r) * (y - r));
                    float dRight = Mathf.Sqrt((x - (w - r)) * (x - (w - r)) + (y - r) * (y - r));
                    bool inside  = (x >= r && x <= w - r) || dLeft <= r || dRight <= r;
                    pixels[y * w + x] = inside ? Color.white : Color.clear;
                }
            tex.SetPixels(pixels);
            tex.Apply();
            _pillBaseTex = tex;

            _pillBoxStyle = new GUIStyle {
                border = new RectOffset(8, 8, 0, 0), // 9-slice: preserve the rounded ends
                normal = { background = _pillBaseTex },
            };
        }

        // ── Metadata helpers ──────────────────────────────────────────────────

        private static OghamGraphMetadata LoadOrCreateMeta(OghamData data)
        {
            var dataPath = AssetDatabase.GetAssetPath(data);
            var metaPath = Path.ChangeExtension(dataPath, null) + ".graph.asset";
            var existing = AssetDatabase.LoadAssetAtPath<OghamGraphMetadata>(metaPath);
            if (existing != null) return existing;
            var meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
            meta.SourceData = data;
            AssetDatabase.CreateAsset(meta, metaPath);
            AssetDatabase.SaveAssets();
            return meta;
        }

        private static void SaveMeta(OghamGraphMetadata meta)
        {
            EditorUtility.SetDirty(meta);
            AssetDatabase.SaveAssetIfDirty(meta);
        }

        private void SaveViewTransform()
        {
            if (ActiveAsset == null) return;
            int i = _assets.IndexOf(ActiveAsset);
            if (i < 0) return;
            _metas[i].ViewTransform = new Vector3(_pan.x, _pan.y, _zoom);
            SaveMeta(_metas[i]);
        }

        private Rect RubberBandRect()
        {
            float x = Mathf.Min(_rubberBandStart.x, _rubberBandEnd.x);
            float y = Mathf.Min(_rubberBandStart.y, _rubberBandEnd.y);
            return new Rect(x, y, Mathf.Abs(_rubberBandEnd.x - _rubberBandStart.x),
                Mathf.Abs(_rubberBandEnd.y - _rubberBandStart.y));
        }

        private void SelectNodesInRubberBand()
        {
            var r = RubberBandRect();
            foreach (var node in _nodes)
                node.IsSelected = ToScreen(node.Rect).Overlaps(r);
            _orderedNodesDirty = true;
        }

        private void ShowAddAliasPinDialog(CanvasNode node, Vector2 canvasPos)
        {
            OghamTagHelper.ShowTagPicker(targetTag =>
            {
                if (string.IsNullOrEmpty(targetTag)) return;
                int mi = _assets.IndexOf(node.Asset);
                if (mi >= 0) Undo.RecordObject(_metas[mi], "Add Alias Pin");
                var dot       = targetTag.LastIndexOf('.');
                var shortName = dot >= 0 ? targetTag.Substring(dot + 1) : targetTag;
                node.Meta.AliasPins.Add(new OghamAliasMeta {
                    Name               = shortName,
                    TargetEntryTagName = targetTag,
                    Position           = canvasPos + new Vector2(NodeW * 0.5f, -AliasH * 2f),
                });
                if (mi >= 0) SaveMeta(_metas[mi]);
                RebuildCanvas();
                _host?.Repaint();
            });
        }
    }
}
