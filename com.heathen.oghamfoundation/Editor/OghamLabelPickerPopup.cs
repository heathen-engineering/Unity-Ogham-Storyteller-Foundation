using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Trello-style label picker and manager popup. In node mode (opened from a node's Labels button) it shows
    /// assign checkboxes; in overview mode (opened from Manage Labels) it shows only label definitions.
    /// </summary>
    public class OghamLabelPickerPopup : EditorWindow
    {
        private OghamGraphMetadata _meta;
        private OghamNodeMeta      _nodeMeta;    // null = overview mode
        private OghamData          _asset;
        private Action             _onChanged;

        // Inline edit state
        private int    _editingId  = -1;
        private string _editName   = "";
        private Color  _editColor;

        // New-label row
        private string _newLabelName = "";

        private Vector2 _scroll;
        private bool    _closing;

        private const float W          = 300f;
        private const float BaseRowH   = 24f;
        private const float EditRowH   = 52f;
        private const float NewRowH    = 26f;
        private const float Pad        = 6f;
        private const float DotSz      = 14f;
        private const float BtnW       = 22f;
        private const float FieldCentreOffset = 20f;

        /// <summary>
        /// Opens the label picker in node mode, showing assignment checkboxes for the given node.
        /// </summary>
        /// <param name="meta">The graph metadata asset that owns the label definitions.</param>
        /// <param name="nodeMeta">The node whose label assignments are edited.</param>
        /// <param name="asset">The owning data asset, marked dirty on changes.</param>
        /// <param name="onChanged">Callback invoked whenever labels are assigned, removed, or modified.</param>
        /// <param name="anchor">The screen-space position near which the popup is anchored.</param>
        public static void OpenNodeMode(OghamGraphMetadata meta, OghamNodeMeta nodeMeta,
            OghamData asset, Action onChanged, Vector2 anchor)
        {
            var w = CreateInstance<OghamLabelPickerPopup>();
            w.titleContent = new GUIContent("Labels");
            w._meta        = meta;
            w._nodeMeta    = nodeMeta;
            w._asset       = asset;
            w._onChanged   = onChanged;
            w._closing     = false;
            w.ApplySize(anchor);
            w.ShowPopup();
            w.Focus();
        }

        /// <summary>
        /// Opens the label picker in overview mode, showing only the global label definitions without node-assignment checkboxes.
        /// </summary>
        /// <param name="meta">The graph metadata asset whose label definitions are managed.</param>
        /// <param name="onChanged">Callback invoked whenever labels are created, deleted, or renamed.</param>
        /// <param name="anchor">The screen-space position near which the popup is anchored.</param>
        public static void OpenOverviewMode(OghamGraphMetadata meta,
            Action onChanged, Vector2 anchor)
        {
            var w = CreateInstance<OghamLabelPickerPopup>();
            w.titleContent = new GUIContent("Manage Labels");
            w._meta        = meta;
            w._nodeMeta    = null;
            w._asset       = null;
            w._onChanged   = onChanged;
            w._closing     = false;
            w.ApplySize(anchor);
            w.ShowPopup();
            w.Focus();
        }

        // ── Sizing ────────────────────────────────────────────────────────────

        private float ComputeHeight()
        {
            int n = _meta?.Labels.Count ?? 0;
            float h = Pad + n * BaseRowH + (_editingId >= 0 ? EditRowH : 0f) + NewRowH + Pad;
            return Mathf.Max(h, 80f);
        }

        private void ApplySize(Vector2 anchor)
        {
            float h = ComputeHeight();
            minSize = new Vector2(W, h);
            maxSize = new Vector2(W, h);
            var r = new Rect(anchor.x, anchor.y - FieldCentreOffset, W, h);
            var res = Screen.currentResolution;
            if (r.xMax > res.width)  r.x = res.width  - W - 4f;
            if (r.x    < 0f)         r.x = 0f;
            if (r.yMax > res.height) r.y = res.height - h - 4f;
            if (r.y    < 0f)         r.y = 0f;
            position = r;
        }

        private void ResizeToContent()
        {
            float h = ComputeHeight();
            minSize = new Vector2(W, h);
            maxSize = new Vector2(W, h);
            position = new Rect(position.x, position.y, W, h);
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_meta == null) { Close(); return; }

            _scroll = EditorGUILayout.BeginScrollView(_scroll,
                GUILayout.Width(W), GUILayout.Height(ComputeHeight()));

            EditorGUILayout.Space(Pad);
            DrawLabelList();
            EditorGUILayout.Space(4f);
            DrawNewLabelRow();
            EditorGUILayout.Space(Pad);

            EditorGUILayout.EndScrollView();
        }

        private void DrawLabelList()
        {
            if (_meta.Labels.Count == 0)
            {
                EditorGUILayout.LabelField("No labels defined yet.", EditorStyles.miniLabel);
                return;
            }

            foreach (var def in _meta.Labels.ToList())
            {
                DrawLabelRow(def);
                if (_editingId == def.Id)
                    DrawEditRow(def);
            }
        }

        private void DrawLabelRow(OghamLabelDef def)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(BaseRowH)))
            {
                // Checkbox (node mode only)
                if (_nodeMeta != null)
                {
                    bool assigned = _nodeMeta.AssignedLabelIds.Contains(def.Id);
                    bool newVal   = EditorGUILayout.Toggle(assigned, GUILayout.Width(18f));
                    if (newVal != assigned)
                    {
                        RecordMeta("Toggle Label");
                        if (newVal) _nodeMeta.AssignedLabelIds.Add(def.Id);
                        else        _nodeMeta.AssignedLabelIds.Remove(def.Id);
                        Commit();
                    }
                }

                // Color dot
                var dotRect = GUILayoutUtility.GetRect(DotSz, DotSz, GUILayout.Width(DotSz));
                dotRect.y += (BaseRowH - DotSz) * 0.5f - 2f;
                EditorGUI.DrawRect(dotRect, def.Color);

                GUILayout.Space(4f);

                // Name label
                GUILayout.Label(def.Name, GUILayout.ExpandWidth(true));

                // Edit button
                if (GUILayout.Button("✎", EditorStyles.miniButton, GUILayout.Width(BtnW)))
                {
                    if (_editingId == def.Id)
                    {
                        _editingId = -1;
                    }
                    else
                    {
                        _editingId  = def.Id;
                        _editName   = def.Name;
                        _editColor  = def.Color;
                    }
                    ResizeToContent();
                    Repaint();
                }

                // Delete button
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(BtnW)))
                {
                    int usageCount = _meta.Nodes.Count(n => n.AssignedLabelIds.Contains(def.Id));
                    bool doDelete = usageCount == 0 || EditorUtility.DisplayDialog(
                        "Delete Label",
                        $"'{def.Name}' is assigned to {usageCount} node(s). Delete anyway?",
                        "Delete", "Cancel");

                    if (doDelete)
                    {
                        RecordMeta("Delete Label");
                        _meta.Labels.Remove(def);
                        foreach (var n in _meta.Nodes)
                            n.AssignedLabelIds.Remove(def.Id);
                        if (_editingId == def.Id) _editingId = -1;
                        Commit();
                        ResizeToContent();
                        Repaint();
                    }
                }
            }
        }

        private void DrawEditRow(OghamLabelDef def)
        {
            EditorGUI.indentLevel++;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Name", GUILayout.Width(42f));
                _editName = EditorGUILayout.TextField(_editName);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Color", GUILayout.Width(42f));
                _editColor = EditorGUILayout.ColorField(_editColor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Apply", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    RecordMeta("Rename Label");
                    def.Name  = _editName.Trim();
                    def.Color = _editColor;
                    _editingId = -1;
                    Commit();
                    ResizeToContent();
                    Repaint();
                }
                if (GUILayout.Button("Cancel", EditorStyles.miniButton, GUILayout.Width(50f)))
                {
                    _editingId = -1;
                    ResizeToContent();
                    Repaint();
                }
                GUILayout.Space(4f);
            }

            EditorGUI.indentLevel--;
        }

        private void DrawNewLabelRow()
        {
            EditorGUILayout.LabelField("New label", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _newLabelName = EditorGUILayout.TextField(_newLabelName);
                if (GUILayout.Button("Add", EditorStyles.miniButton, GUILayout.Width(36f))
                    && !string.IsNullOrWhiteSpace(_newLabelName))
                {
                    RecordMeta("Add Label");
                    var labels = _meta.Labels;
                    int newId  = labels.Count > 0 ? labels.Max(l => l.Id) + 1 : 1;
                    var col    = DefaultLabelColor(labels.Count);
                    labels.Add(new OghamLabelDef { Id = newId, Color = col, Name = _newLabelName.Trim() });

                    // In node mode, immediately assign to current node
                    if (_nodeMeta != null)
                        _nodeMeta.AssignedLabelIds.Add(newId);

                    _newLabelName = "";
                    Commit();
                    ResizeToContent();
                    Repaint();
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static readonly Color[] DefaultColors = {
            new Color(0.176f, 0.353f, 0.557f),
            new Color(0.341f, 0.220f, 0.400f),
            new Color(0.220f, 0.392f, 0.282f),
            new Color(0.455f, 0.278f, 0.180f),
            new Color(0.278f, 0.380f, 0.200f),
            new Color(0.380f, 0.318f, 0.176f),
        };

        private static Color DefaultLabelColor(int idx)
        {
            var c = DefaultColors[idx % DefaultColors.Length];
            c.a = 1f;
            return c;
        }

        // No-op for now: OghamGraphMetadata is a plain class (not a UnityEngine.Object), so Unity Undo
        // cannot record it; wired to the framework UndoHistory in the final Stage D increment.
        private void RecordMeta(string undoLabel) { }

        private void Commit()
        {
            // _meta (layout) and _asset (OghamData) both persist via the .ogham JSON on save; nothing to dirty.
            _onChanged?.Invoke();
        }

        private void OnLostFocus() { if (!_closing) { _closing = true; Close(); } }
    }
}
