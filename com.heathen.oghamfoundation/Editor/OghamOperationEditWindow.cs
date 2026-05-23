using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham.Editor
{
    // Popup window for editing a single GameplayTagOperation.
    // Compact layout — no labels; controls are self-evident by position and icon.
    //
    // Layout:
    //   [ TagPath text field                        ][ ▾ picker ]
    //   [ Operation ▼ ][ Value                                  ]
    //   Conditions (N)                                         [+]
    //   ┌──────────────────────────────────────────────────────┐
    //   │ [ TagPath ][ ▾ ]                                     │
    //   │ [ ⊞ exact ][ Compare ▼ ][ Value              ][ - ] │
    //   └──────────────────────────────────────────────────────┘
    //   [ Save ][ Cancel ]
    public class OghamOperationEditWindow : EditorWindow
    {
        private GameplayTagOperation  _item;
        private OghamData             _asset;
        private Action                _onCommit;

        private string                _tagName;
        private GameplayTagArithmetic _arithmetic;
        private long                  _value;
        private readonly List<CondRow> _conditions = new();

        private bool    _closing;
        private Vector2 _anchor;

        private const float W    = 380f;
        private const float MaxH = 500f;
        private const float Row  = 20f;   // singleLineHeight + spacing
        private const float PickW = 26f;
        private const float OpW   = 110f;
        private const float ValW  = 90f;
        private const float ExW   = 22f;   // exact-match toggle width

        // Condition block: helpBox overhead + tag row + fields row + spacing
        private const float CondH = 8f + Row * 2f + 4f;

        private struct CondRow
        {
            public string                  TagName;
            public GameplayTagComparisonOp Comparison;
            public long                    Value;
            public bool                    ExactMatch;
            public GameplayTagLogicOp      Logic;
            public bool                    UseCompareTag;
            public string                  CompareTagName;
        }

        public static void Open(GameplayTagOperation item, OghamData asset, Action onRefresh, Vector2 anchor)
        {
            var w = CreateInstance<OghamOperationEditWindow>();
            w.titleContent = new GUIContent("Edit Operation");
            w._item        = item;
            w._asset       = asset;
            w._onCommit    = onRefresh;
            w._tagName     = item.Tag.IsValid ? OghamTagHelper.GetTagName(item.Tag.Id) : "";
            w._arithmetic  = item.Arithmetic;
            w._value       = (long)item.Value;
            w._anchor      = anchor;

            w._conditions.Clear();
            foreach (var c in item.Conditions)
                w._conditions.Add(new CondRow
                {
                    TagName        = c.Tag.IsValid ? OghamTagHelper.GetTagName(c.Tag.Id) : "",
                    Comparison     = c.Comparison,
                    Value          = (long)c.CompareValue,
                    ExactMatch     = c.ExactMatch,
                    Logic          = c.LogicOp,
                    UseCompareTag  = c.CompareTag.Id != 0,
                    CompareTagName = c.CompareTag.Id != 0 ? OghamTagHelper.GetTagName(c.CompareTag.Id) : "",
                });

            w._closing = false;
            var h = ComputeHeight(w._conditions.Count);
            w.minSize = new Vector2(W, h);
            w.maxSize = new Vector2(W, h);
            w.position = PlaceAtAnchor(anchor, W, h);
            w.ShowPopup();
            w.Focus();
        }

        private static float ComputeHeight(int condCount)
        {
            // Space(4) + tag row + op+value row + Space(4) + header row + conditions + Space(6) + buttons
            float h = 4f + Row + Row + 4f + Row + condCount * CondH + 6f + Row + 6f;
            return Mathf.Min(h, MaxH);
        }

        private static Rect PlaceAtAnchor(Vector2 anchor, float w, float h)
        {
            const float fieldCentreOffset = 13f;
            var r   = new Rect(anchor.x, anchor.y - fieldCentreOffset, w, h);
            var res = Screen.currentResolution;
            if (r.xMax > res.width)  r.x = res.width  - w - 4f;
            if (r.x    < 0)          r.x = 0;
            if (r.yMax > res.height) r.y = res.height - h - 4f;
            if (r.y    < 0)          r.y = 0;
            return r;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4f);

            // ── Tag row ───────────────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                _tagName = EditorGUILayout.TextField(_tagName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                    OghamTagHelper.ShowTagPicker(s => { _tagName = s; Repaint(); });
            }

            // ── Operation + value row (no labels) ─────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                _arithmetic = (GameplayTagArithmetic)EditorGUILayout.EnumPopup(_arithmetic, GUILayout.Width(OpW));
                _value      = EditorGUILayout.LongField(_value, GUILayout.ExpandWidth(true));
                if (_value < 0) _value = 0;
            }

            // ── Conditions header ─────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Conditions ({_conditions.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("+", GUILayout.Width(22f)))
                {
                    _conditions.Add(new CondRow { Comparison = GameplayTagComparisonOp.Exists, Logic = GameplayTagLogicOp.And });
                    ResizeToContent();
                }
            }

            // ── Condition rows ────────────────────────────────────────────────
            for (int i = 0; i < _conditions.Count; i++)
            {
                var c = _conditions[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Tag + picker
                using (new EditorGUILayout.HorizontalScope())
                {
                    var captI = i;
                    c.TagName = EditorGUILayout.TextField(c.TagName, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        OghamTagHelper.ShowTagPicker(s =>
                        {
                            var r = _conditions[captI]; r.TagName = s; _conditions[captI] = r; Repaint();
                        });
                }

                // Exact match toggle + compare mode + T/# toggle + value/tag + remove button
                using (new EditorGUILayout.HorizontalScope())
                {
                    var captI = i;
                    c.ExactMatch = GUILayout.Toggle(c.ExactMatch, new GUIContent("⊞", "Exact match"),
                        EditorStyles.miniButton, GUILayout.Width(ExW));
                    c.Comparison = (GameplayTagComparisonOp)EditorGUILayout.EnumPopup(c.Comparison,
                        GUILayout.ExpandWidth(true));

                    var newUse = GUILayout.Toggle(c.UseCompareTag,
                        new GUIContent(c.UseCompareTag ? "T" : "#",
                            c.UseCompareTag ? "Compare against tag value" : "Compare against constant"),
                        EditorStyles.miniButton, GUILayout.Width(22f));
                    if (newUse != c.UseCompareTag) { c.UseCompareTag = newUse; if (!newUse) c.CompareTagName = ""; }

                    if (c.UseCompareTag)
                    {
                        var valid  = OghamTagHelper.IsValidTagPath(c.CompareTagName);
                        var prevBg = GUI.backgroundColor;
                        if (!valid && !string.IsNullOrEmpty(c.CompareTagName))
                            GUI.backgroundColor = Color.red;
                        c.CompareTagName = EditorGUILayout.TextField(c.CompareTagName ?? "", GUILayout.ExpandWidth(true));
                        GUI.backgroundColor = prevBg;
                        if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                            OghamTagHelper.ShowTagPicker(s =>
                            {
                                var r = _conditions[captI]; r.CompareTagName = s; _conditions[captI] = r; Repaint();
                            });
                    }
                    else
                    {
                        c.Value = EditorGUILayout.LongField(c.Value, GUILayout.Width(ValW));
                        if (c.Value < 0) c.Value = 0;
                    }

                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        _conditions.RemoveAt(captI);
                        ResizeToContent();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                }

                _conditions[i] = c;
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2f);
            }

            // ── Buttons ───────────────────────────────────────────────────────
            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Save") ||
                    (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
                    Commit();
                if (GUILayout.Button("Cancel"))
                    Cancel();
            }
        }

        private void ResizeToContent()
        {
            var h = ComputeHeight(_conditions.Count);
            minSize = new Vector2(W, h);
            maxSize = new Vector2(W, h);
            position = PlaceAtAnchor(_anchor, W, h);
        }

        private void OnLostFocus() { if (!_closing) Commit(); }

        private void Commit()
        {
            if (_closing) return;
            _closing = true;

            OghamTagHelper.EnsureRegistered(_tagName);
            _item.Tag = string.IsNullOrWhiteSpace(_tagName)
                ? default : GameplayTag.FromName(_tagName.Trim());

            _item.Arithmetic = _arithmetic;
            _item.Value      = (ulong)_value;

            _item.Conditions.Clear();
            foreach (var c in _conditions)
            {
                OghamTagHelper.EnsureRegistered(c.TagName);
                var cond = new GameplayTagCondition
                {
                    Tag          = string.IsNullOrWhiteSpace(c.TagName)
                        ? default : GameplayTag.FromName(c.TagName.Trim()),
                    Comparison   = c.Comparison,
                    CompareValue = (ulong)c.Value,
                    ExactMatch   = c.ExactMatch,
                    LogicOp      = c.Logic,
                };
                if (c.UseCompareTag && OghamTagHelper.IsValidTagPath(c.CompareTagName))
                {
                    OghamTagHelper.EnsureRegistered(c.CompareTagName);
                    cond.CompareTag = GameplayTag.FromName(c.CompareTagName.Trim());
                }
                _item.Conditions.Add(cond);
            }

            EditorUtility.SetDirty(_asset);
            _onCommit?.Invoke();
            Close();
        }

        private void Cancel() { _closing = true; Close(); }
    }
}
