using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Popup window for editing a single <see cref="GameplayTagOperation"/>, including its tag, arithmetic,
    /// value type, value, and any associated conditions. Writes changes back to the operation on save.
    /// </summary>
    public class OghamOperationEditWindow : EditorWindow
    {
        private GameplayTagOperation  _item;
        private OghamData             _asset;
        private Action                _onCommit;

        // Operation fields
        private string                _tagName;
        private GameplayTagArithmetic _arithmetic;
        private GameplayTagValueType  _valueType;
        private long                  _value;        // Unsigned (clamped ≥ 0) or Signed
        private double                _dValue;       // Decimal
        private string                _valueTagName; // Tag

        private readonly List<CondRow> _conditions = new();
        private bool    _closing;
        private Vector2 _anchor;

        private const float W     = 380f;
        private const float MaxH  = 500f;
        private const float Row   = 20f;
        private const float LogicH = Row;  // height of the logic-op connector row between conditions
        private const float PickW  = 26f;
        private const float OpW    = 110f;
        private const float TypeW  = 30f;
        private const float ValW   = 70f;
        private const float ExW    = 22f;
        private const float ReordW = 18f;  // ▲ / ▼ button width

        // Condition block: helpBox overhead + tag row + fields row + inner spacing
        private const float CondH = 8f + Row * 2f + 4f;

        private static readonly string[] s_TypeLabels = { "#", "+/-", "0.0", "T" };
        private static readonly string[] s_TypeTips   = {
            "Unsigned integer",
            "Signed integer",
            "Decimal (double)",
            "Tag — resolved from collection at runtime",
        };

        private struct CondRow
        {
            public string                  TagName;
            public GameplayTagComparisonOp Comparison;
            public long                    Value;         // Unsigned (≥ 0) or Signed
            public double                  DValue;        // Decimal
            public bool                    ExactMatch;
            public GameplayTagLogicOp      Logic;
            public GameplayTagValueType    CompareValueType;
            public string                  CompareTagName;
        }

        // Total rendered height for a block of N condition rows (including logic separators).
        private static float CondBlockHeight(int count)
            => count > 0 ? count * CondH + (count - 1) * LogicH : 0f;

        /// <summary>
        /// Opens the operation editor popup anchored near the given screen position, pre-populated with the
        /// values of <paramref name="item"/>. Changes are written back to the operation and the asset is marked dirty on save.
        /// </summary>
        /// <param name="item">The operation to edit. Changes are written back to this instance on save.</param>
        /// <param name="asset">The owning <see cref="OghamData"/> asset, marked dirty when saved.</param>
        /// <param name="onRefresh">Callback invoked after the operation is saved so callers can repaint.</param>
        /// <param name="anchor">The screen-space position near which the popup is anchored.</param>
        public static void Open(GameplayTagOperation item, OghamData asset, Action onRefresh, Vector2 anchor)
        {
            var w = CreateInstance<OghamOperationEditWindow>();
            w.titleContent = new GUIContent("Edit Operation");
            w._item        = item;
            w._asset       = asset;
            w._onCommit    = onRefresh;
            w._tagName     = item.Tag.IsValid ? OghamTagHelper.GetTagName(item.Tag.Id) : "";
            w._arithmetic  = item.Arithmetic;
            w._valueType   = item.ValueType;
            w._anchor      = anchor;

            switch (item.ValueType)
            {
                case GameplayTagValueType.Signed:
                    w._value = (long)item.Value;
                    break;
                case GameplayTagValueType.Decimal:
                    w._dValue = System.BitConverter.Int64BitsToDouble((long)item.Value);
                    break;
                case GameplayTagValueType.Tag:
                    w._value        = 0;
                    w._valueTagName = item.ValueTag.IsValid ? OghamTagHelper.GetTagName(item.ValueTag.Id) : "";
                    break;
                default: // Unsigned
                    w._value = (long)item.Value;
                    break;
            }

            w._conditions.Clear();
            foreach (var c in item.Conditions)
            {
                var cvt = c.CompareTag.Id != 0 ? GameplayTagValueType.Tag : c.CompareValueType;
                w._conditions.Add(new CondRow
                {
                    TagName          = c.Tag.IsValid ? OghamTagHelper.GetTagName(c.Tag.Id) : "",
                    Comparison       = c.Comparison,
                    Value            = cvt == GameplayTagValueType.Decimal ? 0L : (long)c.CompareValue,
                    DValue           = cvt == GameplayTagValueType.Decimal
                                       ? System.BitConverter.Int64BitsToDouble((long)c.CompareValue) : 0.0,
                    ExactMatch       = c.ExactMatch,
                    Logic            = c.LogicOp,
                    CompareValueType = cvt,
                    CompareTagName   = c.CompareTag.Id != 0 ? OghamTagHelper.GetTagName(c.CompareTag.Id) : "",
                });
            }

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
            // Space(4) + tag row + op+value row + Space(4) + header row + cond block + Space(6) + buttons
            float h = 4f + Row + Row + 4f + Row + CondBlockHeight(condCount) + 6f + Row + 6f;
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

            // ── Operation + value type + value row ───────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                _arithmetic = (GameplayTagArithmetic)EditorGUILayout.EnumPopup(_arithmetic, GUILayout.Width(OpW));

                if (GUILayout.Button(new GUIContent(s_TypeLabels[(int)_valueType],
                        s_TypeTips[(int)_valueType]),
                        EditorStyles.miniButton, GUILayout.Width(TypeW)))
                {
                    _valueType = (GameplayTagValueType)(((int)_valueType + 1) % 4);
                    if (_valueType != GameplayTagValueType.Tag) _valueTagName = "";
                }

                switch (_valueType)
                {
                    case GameplayTagValueType.Signed:
                        _value = EditorGUILayout.LongField(_value, GUILayout.ExpandWidth(true));
                        break;

                    case GameplayTagValueType.Decimal:
                        _dValue = EditorGUILayout.DoubleField(_dValue, GUILayout.ExpandWidth(true));
                        break;

                    case GameplayTagValueType.Tag:
                    {
                        var prevBg = GUI.backgroundColor;
                        if (!OghamTagHelper.IsValidTagPath(_valueTagName) && !string.IsNullOrEmpty(_valueTagName))
                            GUI.backgroundColor = Color.red;
                        _valueTagName = EditorGUILayout.TextField(_valueTagName ?? "", GUILayout.ExpandWidth(true));
                        GUI.backgroundColor = prevBg;
                        if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                            OghamTagHelper.ShowTagPicker(s => { _valueTagName = s; Repaint(); });
                        break;
                    }

                    default: // Unsigned
                        _value = EditorGUILayout.LongField(_value, GUILayout.ExpandWidth(true));
                        if (_value < 0) _value = 0;
                        break;
                }
            }

            // ── Conditions header ─────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Conditions ({_conditions.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("+", GUILayout.Width(22f)))
                {
                    _conditions.Add(new CondRow {
                        Comparison = GameplayTagComparisonOp.Exists,
                        Logic      = GameplayTagLogicOp.And,
                    });
                    ResizeToContent();
                }
            }

            // ── Condition rows ────────────────────────────────────────────────
            for (int i = 0; i < _conditions.Count; i++)
            {
                var c     = _conditions[i];
                var captI = i;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Tag + picker
                using (new EditorGUILayout.HorizontalScope())
                {
                    c.TagName = EditorGUILayout.TextField(c.TagName, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        OghamTagHelper.ShowTagPicker(s =>
                        {
                            var r = _conditions[captI]; r.TagName = s; _conditions[captI] = r; Repaint();
                        });
                }

                // Exact + comparison + type + value/tag + reorder + remove
                using (new EditorGUILayout.HorizontalScope())
                {
                    c.ExactMatch = GUILayout.Toggle(c.ExactMatch, new GUIContent("⊞", "Exact match"),
                        EditorStyles.miniButton, GUILayout.Width(ExW));
                    c.Comparison = (GameplayTagComparisonOp)EditorGUILayout.EnumPopup(c.Comparison,
                        GUILayout.ExpandWidth(true));

                    int cvtIdx = (int)c.CompareValueType;
                    if (GUILayout.Button(new GUIContent(s_TypeLabels[cvtIdx], s_TypeTips[cvtIdx]),
                            EditorStyles.miniButton, GUILayout.Width(TypeW)))
                    {
                        c.CompareValueType = (GameplayTagValueType)(((int)c.CompareValueType + 1) % 4);
                        if (c.CompareValueType != GameplayTagValueType.Tag) c.CompareTagName = "";
                    }

                    if (c.CompareValueType == GameplayTagValueType.Tag)
                    {
                        var prevBg = GUI.backgroundColor;
                        if (!OghamTagHelper.IsValidTagPath(c.CompareTagName) && !string.IsNullOrEmpty(c.CompareTagName))
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
                        switch (c.CompareValueType)
                        {
                            case GameplayTagValueType.Signed:
                                c.Value = EditorGUILayout.LongField(c.Value, GUILayout.Width(ValW));
                                break;
                            case GameplayTagValueType.Decimal:
                                c.DValue = EditorGUILayout.DoubleField(c.DValue, GUILayout.Width(ValW));
                                break;
                            default: // Unsigned
                                c.Value = EditorGUILayout.LongField(c.Value, GUILayout.Width(ValW));
                                if (c.Value < 0) c.Value = 0;
                                break;
                        }
                    }

                    // ▲ / ▼ reorder buttons
                    var prevEnabled = GUI.enabled;
                    GUI.enabled = prevEnabled && captI > 0;
                    if (GUILayout.Button("▲", EditorStyles.miniButtonLeft, GUILayout.Width(ReordW)))
                    {
                        GUI.enabled = prevEnabled;
                        _conditions[captI] = c;
                        (_conditions[captI], _conditions[captI - 1]) = (_conditions[captI - 1], _conditions[captI]);
                        EditorGUILayout.EndVertical();
                        break;
                    }
                    GUI.enabled = prevEnabled && captI < _conditions.Count - 1;
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(ReordW)))
                    {
                        GUI.enabled = prevEnabled;
                        _conditions[captI] = c;
                        (_conditions[captI], _conditions[captI + 1]) = (_conditions[captI + 1], _conditions[captI]);
                        EditorGUILayout.EndVertical();
                        break;
                    }
                    GUI.enabled = prevEnabled;

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

                // Logic op connector between conditions
                if (_conditions.Count > 1 && i < _conditions.Count - 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(20f);
                        var cur   = _conditions[i];
                        cur.Logic = (GameplayTagLogicOp)EditorGUILayout.EnumPopup(cur.Logic, GUILayout.Width(70f));
                        _conditions[i] = cur;
                        GUILayout.FlexibleSpace();
                    }
                }
                else
                {
                    EditorGUILayout.Space(2f);
                }
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
            _item.ValueType  = _valueType;
            switch (_valueType)
            {
                case GameplayTagValueType.Signed:
                    _item.Value    = (ulong)_value;
                    _item.ValueTag = default;
                    break;
                case GameplayTagValueType.Decimal:
                    _item.Value    = (ulong)System.BitConverter.DoubleToInt64Bits(_dValue);
                    _item.ValueTag = default;
                    break;
                case GameplayTagValueType.Tag:
                    _item.Value = 0;
                    OghamTagHelper.EnsureRegistered(_valueTagName);
                    _item.ValueTag = string.IsNullOrWhiteSpace(_valueTagName)
                        ? default : GameplayTag.FromName(_valueTagName.Trim());
                    break;
                default: // Unsigned
                    _item.Value    = (ulong)(_value < 0 ? 0 : _value);
                    _item.ValueTag = default;
                    break;
            }

            _item.Conditions.Clear();
            foreach (var c in _conditions)
            {
                OghamTagHelper.EnsureRegistered(c.TagName);
                var cond = new GameplayTagCondition
                {
                    Tag              = string.IsNullOrWhiteSpace(c.TagName)
                        ? default : GameplayTag.FromName(c.TagName.Trim()),
                    Comparison       = c.Comparison,
                    ExactMatch       = c.ExactMatch,
                    LogicOp          = c.Logic,
                    CompareValueType = c.CompareValueType,
                };
                switch (c.CompareValueType)
                {
                    case GameplayTagValueType.Signed:
                        cond.CompareValue = (ulong)c.Value;
                        break;
                    case GameplayTagValueType.Decimal:
                        cond.CompareValue = (ulong)System.BitConverter.DoubleToInt64Bits(c.DValue);
                        break;
                    case GameplayTagValueType.Tag:
                        cond.CompareValue = 0;
                        if (OghamTagHelper.IsValidTagPath(c.CompareTagName))
                        {
                            OghamTagHelper.EnsureRegistered(c.CompareTagName);
                            cond.CompareTag = GameplayTag.FromName(c.CompareTagName.Trim());
                        }
                        break;
                    default: // Unsigned
                        cond.CompareValue = (ulong)(c.Value < 0 ? 0 : c.Value);
                        break;
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
