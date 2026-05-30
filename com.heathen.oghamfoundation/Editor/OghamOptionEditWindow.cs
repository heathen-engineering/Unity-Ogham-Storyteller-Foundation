using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;
using Heathen.Lexicon.Editor;

namespace Heathen.Ogham.Editor
{
    // Popup window for editing a single DialogueOption.
    // Compact layout — no labels on tag/op/value controls; positions are self-evident.
    //
    // Layout:
    //   [ Option tag path text field                ][ ▾ picker ]
    //   [ Literal|Localised ▼ ] [ text / key value field       ][ ▾ ]
    //   [ Target entry tag                          ][ ▾ picker ]
    //   (hint text)
    //   Conditions (N)                                         [+]
    //   ┌────────────────────────────────────────────────────────┐
    //   │ [ TagPath ][ ▾ ]                                       │
    //   │ [ ⊞ exact ][ Compare ▼ ][ type ][ Value/tag ][ − ]    │
    //   └────────────────────────────────────────────────────────┘
    //   Operations (N)                                         [+]
    //   ┌────────────────────────────────────────────────────────┐
    //   │ [ TagPath ][ ▾ ]                                       │
    //   │ [ Op ▼ ][ type ][ Value / tag field         ][ − ]     │
    //   │   Conditions (N)                              [+]      │
    //   │   ┌──────────────────────────────────────────────────┐ │
    //   │   │ [ TagPath ][ ▾ ]                                 │ │
    //   │   │ [ ⊞ ][ Compare ▼ ][ type ][ Value/tag ][ − ]    │ │
    //   │   └──────────────────────────────────────────────────┘ │
    //   └────────────────────────────────────────────────────────┘
    //   [ Save ][ Cancel ]
    public class OghamOptionEditWindow : EditorWindow
    {
        private DialogueOption _item;
        private OghamData      _asset;
        private Action         _onCommit;

        private string            _tagName;
        private LexiconLocMode    _keyMode;
        private string            _keyValue;
        private string            _targetEntry;
        private readonly List<CondRow> _conditions = new();
        private readonly List<OpRowData> _operations = new();

        private string  _keyDisplayValue;
        private bool    _keyExists;
        private bool    _closing;
        private Vector2 _anchor;

        private const float W      = 400f;
        private const float MaxH   = 600f;
        private const float Row    = 20f;
        private const float LogicH = Row;   // height of the logic-op connector row between conditions
        private const float PickW  = 26f;
        private const float OpW    = 100f;
        private const float TypeW  = 30f;
        private const float ValW   = 70f;
        private const float ExW    = 22f;
        private const float ModeW  = 90f;
        private const float HelpH  = 26f;
        private const float ReordW = 18f;   // ▲ / ▼ button width

        // Per-condition block height: helpBox overhead + tag row + fields row + inner spacing
        private const float CondH = 8f + Row * 2f + 4f;

        // Total rendered height for a block of N conditions (includes logic-op connectors).
        private static float CondBlockHeight(int count)
            => count > 0 ? count * CondH + (count - 1) * LogicH : 0f;

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

        // Operations can have their own conditions; use a class so the List is heap-allocated.
        private sealed class OpRowData
        {
            public string                TagName;
            public GameplayTagArithmetic Arithmetic;
            public GameplayTagValueType  ValueType;
            public long                  Value;         // Unsigned (≥ 0) or Signed
            public double                DValue;        // Decimal
            public string                ValueTagName;  // Tag
            public readonly List<CondRow> Conditions = new();
        }

        private bool ValueMismatch
        {
            get
            {
                if (_keyMode != LexiconLocMode.Localised || !_keyExists) return false;
                var current = LexiconRegistry.ResolveString(LexiconRegistry.Hash(_keyValue)) ?? "";
                return _keyDisplayValue != current;
            }
        }

        public static void Open(DialogueOption item, OghamData asset, Action onRefresh, Vector2 anchor)
        {
            var w = CreateInstance<OghamOptionEditWindow>();
            w.titleContent = new GUIContent("Edit Option");
            w._item        = item;
            w._asset       = asset;
            w._onCommit    = onRefresh;
            w._tagName     = item.TagPath;
            w._keyMode     = item.TextKey.Mode;
            w._keyValue    = item.TextKey.KeyOrValue ?? "";
            w._targetEntry = item.TargetEntryPath;
            w._anchor      = anchor;

            w._conditions.Clear();
            foreach (var c in item.Conditions)
                w._conditions.Add(MakeCondRow(c));

            w._operations.Clear();
            foreach (var op in item.Operations)
            {
                var row = new OpRowData
                {
                    TagName      = op.Tag.IsValid ? OghamTagHelper.GetTagName(op.Tag.Id) : "",
                    Arithmetic   = op.Arithmetic,
                    ValueType    = op.ValueType,
                    Value        = op.ValueType == GameplayTagValueType.Decimal ? 0L : (long)op.Value,
                    DValue       = op.ValueType == GameplayTagValueType.Decimal
                                   ? System.BitConverter.Int64BitsToDouble((long)op.Value) : 0.0,
                    ValueTagName = op.ValueType == GameplayTagValueType.Tag
                                   ? (op.ValueTag.IsValid ? OghamTagHelper.GetTagName(op.ValueTag.Id) : "") : "",
                };
                foreach (var c in op.Conditions)
                    row.Conditions.Add(MakeCondRow(c));
                w._operations.Add(row);
            }

            w._keyDisplayValue = "";
            w._keyExists       = false;
            w._closing         = false;
            w.RefreshKeyState(populateValue: true);

            var h = w.ComputeHeight();
            w.minSize = new Vector2(W, h);
            w.maxSize = new Vector2(W, h);
            w.position = PlaceAtAnchor(anchor, W, h);
            w.ShowPopup();
            w.Focus();
        }

        private static CondRow MakeCondRow(GameplayTagCondition c)
        {
            // Back-compat: if a CompareTag was saved without CompareValueType, infer Tag type.
            var cvt = c.CompareTag.Id != 0 ? GameplayTagValueType.Tag : c.CompareValueType;
            return new CondRow
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
            };
        }

        private float ComputeHeight()
        {
            float keyRows = _keyMode == LexiconLocMode.Localised ? Row * 2f : Row;
            float h = 4f
                    + Row                            // option tag
                    + 4f + keyRows                   // key mode + key value [+ resolved value]
                    + 4f + Row                       // target entry
                    + 4f + HelpH                     // hint (HelpBox — taller than a bare Row)
                    + 4f + Row                       // conditions header
                    + CondBlockHeight(_conditions.Count)
                    + 4f + Row                       // operations header
                    + TotalOperationHeight()
                    + 6f + Row + 6f;                 // buttons
            return Mathf.Min(h, MaxH);
        }

        private float TotalOperationHeight()
        {
            float total = 0;
            foreach (var op in _operations)
                total += OpBaseHeight(op) + 2f;
            return total;
        }

        private static float OpBaseHeight(OpRowData op) =>
            8f + Row * 2f + 4f          // helpBox overhead + tag row + arithmetic+value row
            + Row                       // sub-conditions header
            + CondBlockHeight(op.Conditions.Count);

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

            // ── Option tag ────────────────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                _tagName = EditorGUILayout.TextField(_tagName, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                    OghamTagHelper.ShowTagPicker(s => { _tagName = s; Repaint(); });
            }

            // ── Text key ──────────────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            var prevMode = _keyMode;
            using (new EditorGUILayout.HorizontalScope())
            {
                _keyMode = (LexiconLocMode)EditorGUILayout.EnumPopup(_keyMode, GUILayout.Width(ModeW));
                if (_keyMode == LexiconLocMode.Literal)
                {
                    _keyValue = EditorGUILayout.TextField(_keyValue, GUILayout.ExpandWidth(true));
                }
                else
                {
                    _keyValue = EditorGUILayout.TextField(_keyValue, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        ShowKeyPicker();
                }
            }

            if (_keyMode == LexiconLocMode.Localised)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (ValueMismatch)
                        GUILayout.Label(new GUIContent("⚠", "Differs from Lexicon. Save will update it."),
                            GUILayout.Width(PickW));
                    else
                        GUILayout.Space(PickW + EditorGUIUtility.standardVerticalSpacing);
                    _keyDisplayValue = EditorGUILayout.TextField(_keyDisplayValue, GUILayout.ExpandWidth(true));
                }
            }

            if (_keyMode != prevMode) { RefreshKeyState(populateValue: true); ResizeToContent(); }

            // ── Target entry ──────────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                _targetEntry = EditorGUILayout.TextField(_targetEntry, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                    OghamTagHelper.ShowTagPicker(s => { _targetEntry = s; Repaint(); });
            }
            EditorGUILayout.HelpBox("Leave empty to close the conversation.", MessageType.None);

            // ── Option Conditions ─────────────────────────────────────────────
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
            DrawConditionRows(_conditions);

            // ── Operations ────────────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Operations ({_operations.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("+", GUILayout.Width(22f)))
                {
                    _operations.Add(new OpRowData { Arithmetic = GameplayTagArithmetic.Set, Value = 1 });
                    ResizeToContent();
                }
            }
            DrawOperationRows();

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

        private void DrawConditionRows(List<CondRow> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var c     = list[i];
                var captI = i;

                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // Tag + picker
                using (new EditorGUILayout.HorizontalScope())
                {
                    c.TagName = EditorGUILayout.TextField(c.TagName, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        OghamTagHelper.ShowTagPicker(s =>
                        {
                            var r = list[captI]; r.TagName = s; list[captI] = r; Repaint();
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
                                var r = list[captI]; r.CompareTagName = s; list[captI] = r; Repaint();
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
                        list[captI] = c;
                        (list[captI], list[captI - 1]) = (list[captI - 1], list[captI]);
                        EditorGUILayout.EndVertical();
                        break;
                    }
                    GUI.enabled = prevEnabled && captI < list.Count - 1;
                    if (GUILayout.Button("▼", EditorStyles.miniButtonRight, GUILayout.Width(ReordW)))
                    {
                        GUI.enabled = prevEnabled;
                        list[captI] = c;
                        (list[captI], list[captI + 1]) = (list[captI + 1], list[captI]);
                        EditorGUILayout.EndVertical();
                        break;
                    }
                    GUI.enabled = prevEnabled;

                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        list.RemoveAt(captI);
                        ResizeToContent();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                }

                list[i] = c;
                EditorGUILayout.EndVertical();

                // Logic op connector between conditions
                if (list.Count > 1 && i < list.Count - 1)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(20f);
                        var cur   = list[i];
                        cur.Logic = (GameplayTagLogicOp)EditorGUILayout.EnumPopup(cur.Logic, GUILayout.Width(70f));
                        list[i]   = cur;
                        GUILayout.FlexibleSpace();
                    }
                }
                else
                {
                    EditorGUILayout.Space(2f);
                }
            }
        }

        private void DrawOperationRows()
        {
            for (int i = 0; i < _operations.Count; i++)
            {
                var op = _operations[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                // ── Tag + picker ──────────────────────────────────────────────
                using (new EditorGUILayout.HorizontalScope())
                {
                    var captI = i;
                    op.TagName = EditorGUILayout.TextField(op.TagName, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        OghamTagHelper.ShowTagPicker(s =>
                        {
                            _operations[captI].TagName = s; Repaint();
                        });
                }

                // ── Arithmetic + type + value + remove ────────────────────────
                bool removed = false;
                using (new EditorGUILayout.HorizontalScope())
                {
                    op.Arithmetic = (GameplayTagArithmetic)EditorGUILayout.EnumPopup(op.Arithmetic,
                        GUILayout.Width(OpW));

                    // Type cycle
                    if (GUILayout.Button(new GUIContent(s_TypeLabels[(int)op.ValueType],
                            s_TypeTips[(int)op.ValueType]),
                            EditorStyles.miniButton, GUILayout.Width(TypeW)))
                    {
                        op.ValueType = (GameplayTagValueType)(((int)op.ValueType + 1) % 4);
                        if (op.ValueType != GameplayTagValueType.Tag) op.ValueTagName = "";
                    }

                    switch (op.ValueType)
                    {
                        case GameplayTagValueType.Signed:
                            op.Value = EditorGUILayout.LongField(op.Value, GUILayout.ExpandWidth(true));
                            break;

                        case GameplayTagValueType.Decimal:
                            op.DValue = EditorGUILayout.DoubleField(op.DValue, GUILayout.ExpandWidth(true));
                            break;

                        case GameplayTagValueType.Tag:
                        {
                            var prevBg = GUI.backgroundColor;
                            var valid  = OghamTagHelper.IsValidTagPath(op.ValueTagName);
                            if (!valid && !string.IsNullOrEmpty(op.ValueTagName))
                                GUI.backgroundColor = Color.red;
                            op.ValueTagName = EditorGUILayout.TextField(op.ValueTagName ?? "", GUILayout.ExpandWidth(true));
                            GUI.backgroundColor = prevBg;
                            if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                            {
                                var captI2 = i;
                                OghamTagHelper.ShowTagPicker(s =>
                                {
                                    _operations[captI2].ValueTagName = s; Repaint();
                                });
                            }
                            break;
                        }

                        default: // Unsigned
                            op.Value = EditorGUILayout.LongField(op.Value, GUILayout.ExpandWidth(true));
                            if (op.Value < 0) op.Value = 0;
                            break;
                    }

                    var captRemove = i;
                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        _operations.RemoveAt(captRemove);
                        ResizeToContent();
                        removed = true;
                    }
                }

                if (removed) { EditorGUILayout.EndVertical(); break; }

                // ── Per-operation conditions ──────────────────────────────────
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField($"Conditions ({op.Conditions.Count})", EditorStyles.miniLabel);
                    EditorGUI.indentLevel--;
                    if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        op.Conditions.Add(new CondRow { Comparison = GameplayTagComparisonOp.Exists, Logic = GameplayTagLogicOp.And });
                        ResizeToContent();
                    }
                }
                DrawConditionRows(op.Conditions);

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2f);
            }
        }

        // ── Lexicon helpers ───────────────────────────────────────────────────

        private void RefreshKeyState(bool populateValue = false)
        {
            if (_keyMode != LexiconLocMode.Localised || string.IsNullOrWhiteSpace(_keyValue))
            {
                _keyExists = false;
                return;
            }
            var hash     = LexiconRegistry.Hash(_keyValue);
            var resolved = LexiconRegistry.ResolveString(hash);
            _keyExists   = resolved != null;
            if (_keyExists && populateValue)
                _keyDisplayValue = resolved ?? "";
        }

        private void ShowKeyPicker()
        {
            var keys = LexiconSettingsProvider.GetAllLexiconKeys()?.ToList()
                       ?? new List<string>();
            if (keys.Count == 0)
            {
                EditorUtility.DisplayDialog("Lexicon", "No Lexicon keys found.", "OK");
                return;
            }
            var menu = new GenericMenu();
            foreach (var k in keys)
            {
                var captured = k;
                menu.AddItem(new GUIContent(captured), _keyValue == captured, () =>
                {
                    _keyValue = captured;
                    RefreshKeyState(populateValue: true);
                    Repaint();
                });
            }
            menu.ShowAsContext();
        }

        private void ResizeToContent()
        {
            var h = ComputeHeight();
            minSize = new Vector2(W, h);
            maxSize = new Vector2(W, h);
            position = PlaceAtAnchor(_anchor, W, h);
        }

        // ── Commit / cancel ───────────────────────────────────────────────────

        private void OnLostFocus() { if (!_closing) Commit(); }

        private void Commit()
        {
            if (_closing) return;
            _closing = true;

            OghamTagHelper.EnsureRegistered(_tagName);
            _item.TagPath = string.IsNullOrWhiteSpace(_tagName) ? "" : _tagName.Trim();

            _item.TextKey.Mode       = _keyMode;
            _item.TextKey.KeyOrValue = _keyValue;
            _item.TextKey.InvalidateHash();

            if (_keyMode == LexiconLocMode.Localised && !string.IsNullOrWhiteSpace(_keyValue))
            {
                var current = _keyExists
                    ? (LexiconRegistry.ResolveString(LexiconRegistry.Hash(_keyValue)) ?? "")
                    : null;
                if (current == null || _keyDisplayValue != current)
                    LexiconSettingsProvider.UpsertStringEntry(_keyValue, _keyDisplayValue);
            }

            OghamTagHelper.EnsureRegistered(_targetEntry);
            _item.TargetEntryPath = string.IsNullOrWhiteSpace(_targetEntry) ? "" : _targetEntry.Trim();

            _item.Conditions.Clear();
            foreach (var c in _conditions)
                _item.Conditions.Add(BuildCondition(c));

            _item.Operations.Clear();
            foreach (var op in _operations)
            {
                OghamTagHelper.EnsureRegistered(op.TagName);
                var operation = new GameplayTagOperation
                {
                    Tag        = string.IsNullOrWhiteSpace(op.TagName)
                        ? default : GameplayTag.FromName(op.TagName.Trim()),
                    Arithmetic = op.Arithmetic,
                    ValueType  = op.ValueType,
                };
                switch (op.ValueType)
                {
                    case GameplayTagValueType.Signed:
                        operation.Value    = (ulong)op.Value;
                        operation.ValueTag = default;
                        break;
                    case GameplayTagValueType.Decimal:
                        operation.Value    = (ulong)System.BitConverter.DoubleToInt64Bits(op.DValue);
                        operation.ValueTag = default;
                        break;
                    case GameplayTagValueType.Tag:
                        operation.Value = 0;
                        OghamTagHelper.EnsureRegistered(op.ValueTagName);
                        operation.ValueTag = string.IsNullOrWhiteSpace(op.ValueTagName)
                            ? default : GameplayTag.FromName(op.ValueTagName.Trim());
                        break;
                    default: // Unsigned
                        operation.Value    = (ulong)(op.Value < 0 ? 0 : op.Value);
                        operation.ValueTag = default;
                        break;
                }
                foreach (var c in op.Conditions)
                    operation.Conditions.Add(BuildCondition(c));
                _item.Operations.Add(operation);
            }

            EditorUtility.SetDirty(_asset);
            _onCommit?.Invoke();
            Close();
        }

        private static GameplayTagCondition BuildCondition(CondRow c)
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
            return cond;
        }

        private void Cancel() { _closing = true; Close(); }
    }
}
