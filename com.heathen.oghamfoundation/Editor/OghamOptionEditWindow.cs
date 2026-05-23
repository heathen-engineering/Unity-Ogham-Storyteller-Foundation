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
    //   │ [ ⊞ exact ][ Compare ▼ ][ Value                ][ − ] │
    //   └────────────────────────────────────────────────────────┘
    //   Operations (N)                                         [+]
    //   ┌────────────────────────────────────────────────────────┐
    //   │ [ TagPath ][ ▾ ]                                       │
    //   │ [ Op ▼ ][ Value                              ][ − ]    │
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
        private readonly List<OpRow>   _operations = new();

        private string  _keyDisplayValue;
        private bool    _keyExists;
        private bool    _closing;
        private Vector2 _anchor;

        private const float W     = 400f;
        private const float MaxH  = 600f;
        private const float Row   = 20f;
        private const float PickW = 26f;
        private const float OpW   = 100f;
        private const float ValW  = 80f;
        private const float ExW   = 22f;
        private const float ModeW = 90f;

        // Block heights: helpBox overhead (8) + tag row + fields row + spacing (2)
        private const float CondH = 8f + Row * 2f + 4f;
        private const float OpH   = 8f + Row * 2f + 4f;

        private bool ValueMismatch
        {
            get
            {
                if (_keyMode != LexiconLocMode.Localised || !_keyExists) return false;
                var current = LexiconRegistry.ResolveString(LexiconRegistry.Hash(_keyValue)) ?? "";
                return _keyDisplayValue != current;
            }
        }

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

        private struct OpRow
        {
            public string                TagName;
            public GameplayTagArithmetic Arithmetic;
            public long                  Value;
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

            w._operations.Clear();
            foreach (var op in item.Operations)
                w._operations.Add(new OpRow
                {
                    TagName    = op.Tag.IsValid ? OghamTagHelper.GetTagName(op.Tag.Id) : "",
                    Arithmetic = op.Arithmetic,
                    Value      = (long)op.Value,
                });

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

        private float ComputeHeight()
        {
            // tag row + key row(s) + target row + hint + cond header + conds + op header + ops + buttons
            float keyRows = _keyMode == LexiconLocMode.Localised ? Row * 2f : Row;
            float h = 4f
                    + Row                            // option tag
                    + 4f + keyRows                   // key mode + key value [+ resolved value]
                    + 4f + Row                       // target entry
                    + 4f + Row                       // hint (EditorGUILayout.HelpBox)
                    + 4f + Row                       // conditions header
                    + _conditions.Count * CondH
                    + 4f + Row                       // operations header
                    + _operations.Count * OpH
                    + 6f + Row + 6f;                 // buttons
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
                // Resolved value row: ⚠ indicator + editable value field
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

            // ── Conditions ────────────────────────────────────────────────────
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
            DrawConditionRows();

            // ── Operations ────────────────────────────────────────────────────
            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"Operations ({_operations.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("+", GUILayout.Width(22f)))
                {
                    _operations.Add(new OpRow { Arithmetic = GameplayTagArithmetic.Set, Value = 1 });
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

        private void DrawConditionRows()
        {
            for (int i = 0; i < _conditions.Count; i++)
            {
                var c = _conditions[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

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
        }

        private void DrawOperationRows()
        {
            for (int i = 0; i < _operations.Count; i++)
            {
                var op = _operations[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);

                using (new EditorGUILayout.HorizontalScope())
                {
                    var captI = i;
                    op.TagName = EditorGUILayout.TextField(op.TagName, GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("▾", GUILayout.Width(PickW)))
                        OghamTagHelper.ShowTagPicker(s =>
                        {
                            var r = _operations[captI]; r.TagName = s; _operations[captI] = r; Repaint();
                        });
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    op.Arithmetic = (GameplayTagArithmetic)EditorGUILayout.EnumPopup(op.Arithmetic,
                        GUILayout.Width(OpW));
                    op.Value = EditorGUILayout.LongField(op.Value, GUILayout.ExpandWidth(true));
                    if (op.Value < 0) op.Value = 0;
                    var captI = i;
                    if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        _operations.RemoveAt(captI);
                        ResizeToContent();
                        EditorGUILayout.EndVertical();
                        break;
                    }
                }

                _operations[i] = op;
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

            _item.Operations.Clear();
            foreach (var op in _operations)
            {
                OghamTagHelper.EnsureRegistered(op.TagName);
                _item.Operations.Add(new GameplayTagOperation
                {
                    Tag        = string.IsNullOrWhiteSpace(op.TagName)
                        ? default : GameplayTag.FromName(op.TagName.Trim()),
                    Arithmetic = op.Arithmetic,
                    Value      = (ulong)op.Value,
                });
            }

            EditorUtility.SetDirty(_asset);
            _onCommit?.Invoke();
            Close();
        }

        private void Cancel() { _closing = true; Close(); }
    }
}
