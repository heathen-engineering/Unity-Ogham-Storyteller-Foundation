using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Heathen.Lexicon;
using Heathen.Lexicon.Editor;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Popup editor for a single <see cref="OghamContentKey"/>. Supports two display modes: Source mode
    /// (Markdown-like syntax, fully editable) and Formatted mode (TMPro preview, read-only). Toolbar buttons
    /// always edit the MD source; the formatted view is a live preview. Non-text types show an ObjectField.
    /// </summary>
    public class OghamKeyEditWindow : EditorWindow
    {
        private OghamContentKey    _item;
        private OghamData          _asset;
        private Action             _onCommit;
        private OghamContentType   _editType;
        private LexiconLocMode     _editMode;
        private string             _editKey;
        private string             _editValue;
        private UnityEngine.Object _editAsset;
        private bool               _closing;
        private Vector2            _anchor;
        private Color              _activeColor;
        private bool               _linkPanelOpen;
        private string             _linkDisplayText = "";
        private string             _linkTarget      = "";
        private bool               _linkBold;
        private bool               _linkItalic;
        private bool               _linkUnderline;
        private bool               _linkColorActive;
        private int                _editingLinkRawStart = -1;   // -1 = new link, ≥0 = editing existing
        private int                _editingLinkRawEnd   = -1;
        private Button             _linkBoldBtn;
        private Button             _linkItalBtn;
        private Button             _linkUndlBtn;
        private Button             _linkColorBtn;
        private Button             _unlinkBtn;
        private ColorField         _linkColorField;
        private string             _editingLinkDisplayRaw = "";

        // Cached selection — updated on every relevant event so formatting
        // buttons always know where the user's cursor was even after focus moves.
        private int _cachedCursorIndex;
        private int _cachedSelectIndex;

        // Undo/redo — covers both typed changes (via RegisterValueChangedCallback)
        // and programmatic formatting changes (explicit push before apply).
        // List used as a stack (index 0 = oldest, [^1] = top) so we can cap size.
        private readonly List<(string text, int cursor)> _undoStack = new();
        private readonly List<(string text, int cursor)> _redoStack = new();
        private bool _suppressUndo;   // set true during programmatic value changes
        private bool _windowReady;    // true after CreateGUI completes; guards Resize()
        private bool _richTextActive = false; // false = Source mode (MD, editable); true = Formatted preview (TMPro, read-only)
        private int  _lastCursorPos  = -1;    // used to detect intentional cursor moves vs focus-loss clears

        // UI element references
        private TextField     _editorField;   // the ONE editing surface
        private VisualElement _textSection;
        private VisualElement _assetSection;
        private VisualElement _lexiconRow;
        private TextField     _lexiconKeyField;
        private VisualElement _linkPanel;
        private TextField     _linkDisplayField;
        private TextField     _linkTargetField;
        private ObjectField   _assetField;
        private TextField     _assetKeyField;
        private VisualElement _assetKeyRow;
        private Button        _sourceBtn;

        private const string ColorPrefKey = "Ogham.LastTextColor";
        private const float  W            = 660f;
        private const float  RowH         = 24f;
        private const float  EditorH      = 320f;
        private const float  AssetFieldH  = 50f;
        private const float  Gap          = 4f;
        private const int    UndoLimit    = 200;

        private bool IsText    => _editType == OghamContentType.Text;
        private bool IsLiteral => _editMode == LexiconLocMode.Literal;


        /// <summary>
        /// Opens the content key editor popup anchored near the given screen position, pre-populated with the
        /// values of <paramref name="item"/>. Calls <paramref name="onRefresh"/> and marks the asset dirty on save.
        /// </summary>
        /// <param name="item">The content key to edit. Changes are written back to this instance on save.</param>
        /// <param name="asset">The owning <see cref="OghamData"/> asset, marked dirty when saved.</param>
        /// <param name="onRefresh">Callback invoked after the key is saved so callers can repaint.</param>
        /// <param name="anchor">The screen-space position near which the popup is anchored.</param>
        public static void Open(OghamContentKey item, OghamData asset, Action onRefresh, Vector2 anchor)
        {
            var w = CreateInstance<OghamKeyEditWindow>();
            w.titleContent   = new GUIContent("Edit Content Key");
            w._item          = item;
            w._asset         = asset;
            w._onCommit      = onRefresh;
            w._editType      = item.Type;
            w._editMode      = item.Mode;
            w._editAsset     = item.AssetRef;
            w._closing       = false;
            w._anchor        = anchor;
            w._activeColor   = LoadColor();
            w._linkPanelOpen = false;

            // _editValue = the text content the editor always works with.
            // _editKey   = the localization key (Localised mode only).
            if (item.Mode == LexiconLocMode.Localised)
            {
                w._editKey   = item.KeyOrValue ?? "";
                w._editValue = "";
                w.RefreshLexiconState(populateValue: true);
            }
            else
            {
                w._editKey   = "";
                w._editValue = item.KeyOrValue ?? "";
            }

            float h = w.ComputeHeight();
            w.minSize = new Vector2(W, h);
            w.maxSize = new Vector2(W, h);
            w.position = PlaceAtAnchor(anchor, W, h);
            w.ShowPopup();
            w.Focus();
        }

        // ── Height ────────────────────────────────────────────────────────────

        private float ComputeHeight()
        {
            float h = Gap + RowH;   // top row
            if (IsText)
            {
                h += Gap + RowH;    // toolbar
                h += Gap + EditorH; // text editor
                if (_editMode == LexiconLocMode.Localised)
                    h += Gap + RowH;
                if (_linkPanelOpen)
                    h += Gap + 1f + Gap + RowH + Gap + RowH + Gap + RowH;
            }
            else
            {
                h += Gap + AssetFieldH;
                if (_editMode == LexiconLocMode.Localised)
                    h += Gap + RowH;
            }
            h += Gap * 2f;
            return h;
        }

        // ── CreateGUI ────────────────────────────────────────────────────────

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop    = Gap;
            root.style.paddingLeft   = Gap;
            root.style.paddingRight  = Gap;
            root.style.paddingBottom = Gap;

            // Undo / redo intercepted at root so they fire before the focused
            // element (usually the TextField) would handle them.
            root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            // ── Row 1 ─────────────────────────────────────────────────────────
            var topRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = Gap } };
            root.Add(topRow);

            var typeField = new EnumField(_editType);
            typeField.label = "";
            typeField.labelElement.style.display = DisplayStyle.None;
            typeField.style.width = 80f;
            typeField.RegisterValueChangedCallback(evt => {
                _editType = (OghamContentType)evt.newValue;
                RefreshSections();
            });
            topRow.Add(typeField);

            var modeField = new EnumField(_editMode);
            modeField.label = "";
            modeField.labelElement.style.display = DisplayStyle.None;
            modeField.style.width      = 90f;
            modeField.style.marginLeft = 2f;
            modeField.RegisterValueChangedCallback(evt => {
                _editMode = (LexiconLocMode)evt.newValue;
                RefreshLexiconState(populateValue: true);
                RefreshSections();
            });
            topRow.Add(modeField);

            topRow.Add(new VisualElement { style = { flexGrow = 1f } });
            topRow.Add(new Button(Commit) { text = "Save", style = { width = 46f } });
            topRow.Add(new Button(Cancel) { text = "✕",   style = { width = 24f, marginLeft = 2f } });

            _textSection  = BuildTextSection();
            _assetSection = BuildAssetSection();
            root.Add(_textSection);
            root.Add(_assetSection);

            RefreshSections();
            _windowReady = true;   // Open() already sized the window; allow Resize() from now on
        }

        // ── Text section ──────────────────────────────────────────────────────

        private VisualElement BuildTextSection()
        {
            var section = new VisualElement();

            // ── Toolbar ───────────────────────────────────────────────────────
            var toolbar = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = Gap } };
            section.Add(toolbar);

            // sourceMode captured by closure: false = WYSIWYG (rich text on),
            //                                 true  = raw source (rich text off)
            bool sourceMode = false;

            // Compact non-focusable button.  MouseDown (trickle-down) caches the
            // cursor/selection before the focus system can transfer away.
            Button MakeBtn(string label, string tip, Action onClick)
            {
                var btn = new Button(onClick) { text = label, tooltip = tip };
                btn.focusable = false;
                btn.style.width     = 24f;
                btn.style.minWidth  = 24f;
                btn.style.height    = RowH;
                btn.style.marginRight = 1f;
                btn.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
                return btn;
            }

            toolbar.Add(MakeBtn("B", "Bold (Ctrl+B)",      () => ApplyFormatting("**",   "**")));
            toolbar.Add(MakeBtn("I", "Italic (Ctrl+I)",    () => ApplyFormatting("*",    "*")));
            toolbar.Add(MakeBtn("U", "Underline (Ctrl+U)", () => ApplyFormatting("<u>",  "</u>")));
            toolbar.Add(new VisualElement { style = { width = Gap } });

            var colorField = new ColorField { showAlpha = false, showEyeDropper = false, value = _activeColor };
            colorField.label = "";
            colorField.labelElement.style.display = DisplayStyle.None;
            colorField.style.width  = 36f;
            colorField.style.height = RowH;
            colorField.focusable = false;
            colorField.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
            colorField.RegisterValueChangedCallback(evt => _activeColor = evt.newValue);
            toolbar.Add(colorField);

            toolbar.Add(MakeBtn("A", "Apply color to selection",
                () => ApplyFormatting($"<color=#{ColorUtility.ToHtmlStringRGB(_activeColor)}>", "</color>")));
            var antiA = new Button(StripColorFromSelection) { text = "✕A", tooltip = "Remove color from selection" };
            antiA.focusable = false;
            antiA.style.width     = 30f;
            antiA.style.minWidth  = 30f;
            antiA.style.height    = RowH;
            antiA.style.marginRight = 1f;
            antiA.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
            toolbar.Add(antiA);
            toolbar.Add(new VisualElement { style = { width = Gap } });

            var sizes = new List<string> { "─ pt ─", "8", "10", "12", "14", "16", "18", "20", "24", "28", "32" };
            var sizeField = new PopupField<string>(sizes, 0);
            sizeField.label = "";
            sizeField.labelElement.style.display = DisplayStyle.None;
            sizeField.style.width  = 60f;
            sizeField.style.height = RowH;
            sizeField.focusable    = false;
            sizeField.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
            sizeField.RegisterValueChangedCallback(evt => {
                if (evt.newValue != sizes[0] && int.TryParse(evt.newValue, out int sz))
                {
                    ApplyFormatting($"<size={sz}>", "</size>");
                    sizeField.SetValueWithoutNotify(sizes[0]);
                }
            });
            toolbar.Add(sizeField);
            toolbar.Add(new VisualElement { style = { width = Gap } });

            toolbar.Add(MakeBtn("🔗", "Insert / edit link", () => {
                // Link editing only works in Source mode — the field holds the MD source there.
                if (_richTextActive) return;

                _linkPanelOpen = !_linkPanelOpen;
                _linkPanel.style.display = _linkPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
                if (_linkPanelOpen)
                {
                    // In Source mode, cursor/selection indices are raw (no rich-text offset).
                    string raw    = _editValue;
                    int rawSelMin = Mathf.Clamp(Mathf.Min(_cachedCursorIndex, _cachedSelectIndex), 0, raw.Length);
                    int rawSelMax = Mathf.Clamp(Mathf.Max(_cachedCursorIndex, _cachedSelectIndex), 0, raw.Length);

                    if (TryFindOverlappingLink(raw, rawSelMin, rawSelMax,
                            out string existingTarget, out string existingDisplayRaw,
                            out int linkRawStart, out int linkRawEnd))
                    {
                        // ── Editing an existing link ──────────────────────────────
                        _editingLinkDisplayRaw = existingDisplayRaw;
                        _linkTarget = existingTarget;
                        _linkTargetField?.SetValueWithoutNotify(existingTarget);

                        bool selContainedInLink = rawSelMin >= linkRawStart && rawSelMax <= linkRawEnd;
                        if (selContainedInLink)
                        {
                            // Restore formatting toggles from the link's stored markup.
                            ParseLinkFormatting(existingDisplayRaw,
                                out string plainText, out bool bold, out bool italic,
                                out bool underline,   out string colorHex);
                            _linkDisplayText = plainText;
                            _linkBold        = bold;
                            _linkItalic      = italic;
                            _linkUnderline   = underline;
                            _linkColorActive = !string.IsNullOrEmpty(colorHex);
                            if (_linkColorActive && ColorUtility.TryParseHtmlString("#" + colorHex, out var c))
                            {
                                _activeColor = c;
                                _linkColorField?.SetValueWithoutNotify(c);
                            }
                            _editingLinkRawStart = linkRawStart;
                            _editingLinkRawEnd   = linkRawEnd;
                        }
                        else
                        {
                            // Selection extends beyond the link — keep target, expand display text.
                            _editingLinkRawStart = Mathf.Min(rawSelMin, linkRawStart);
                            _editingLinkRawEnd   = Mathf.Max(rawSelMax, linkRawEnd);
                            _linkDisplayText     = StripAllTags(raw[_editingLinkRawStart.._editingLinkRawEnd]);
                            _linkBold = _linkItalic = _linkUnderline = _linkColorActive = false;
                        }

                        if (_unlinkBtn != null) _unlinkBtn.style.display = DisplayStyle.Flex;
                    }
                    else
                    {
                        // ── New link ──────────────────────────────────────────────
                        _editingLinkRawStart   = -1;
                        _editingLinkRawEnd     = -1;
                        _editingLinkDisplayRaw = "";
                        _linkDisplayText       = rawSelMin < rawSelMax ? raw[rawSelMin..rawSelMax] : "";
                        _linkBold = _linkItalic = _linkUnderline = _linkColorActive = false;
                        if (_unlinkBtn != null) _unlinkBtn.style.display = DisplayStyle.None;
                    }

                    _linkDisplayField?.SetValueWithoutNotify(_linkDisplayText);
                    SetLinkToggleActive(_linkBoldBtn,  _linkBold);
                    SetLinkToggleActive(_linkItalBtn,  _linkItalic);
                    SetLinkToggleActive(_linkUndlBtn,  _linkUnderline);
                    SetLinkToggleActive(_linkColorBtn, _linkColorActive);
                }
                else
                {
                    // Panel closed without committing — reset edit state.
                    _editingLinkRawStart   = -1;
                    _editingLinkRawEnd     = -1;
                    _editingLinkDisplayRaw = "";
                    if (_unlinkBtn != null) _unlinkBtn.style.display = DisplayStyle.None;
                }
                Resize();
            }));
            toolbar.Add(new VisualElement { style = { width = Gap } });

            // Source/Formatted toggle — switches between MD source (editable) and TMPro preview (read-only).
            // Default is Formatted (TMPro preview), so the button starts as "Source" (what clicking will switch TO).
            // The backing _editValue is always the MD string; Formatted view is smoke-and-mirrors.
            _sourceBtn = new Button { text = "Source", tooltip = "Show the raw Markdown source instead of the TMPro preview" };
            _sourceBtn.style.width  = 74f;
            _sourceBtn.style.height = RowH;
            _sourceBtn.focusable    = false;
            _sourceBtn.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
            _sourceBtn.clicked += () => {
                if (!_richTextActive)
                {
                    // Switch to Formatted (TMPro preview, read-only)
                    _suppressUndo = true;
                    _editorField.SetValueWithoutNotify(OghamInlineLinkParser.ToTMProMarkup(_editValue));
                    _suppressUndo = false;
                    _editorField.isReadOnly = true;
                    SetEditorRichText(true);
                    _sourceBtn.text = "Source";
                }
                else
                {
                    // Switch back to Source (MD, editable)
                    _suppressUndo = true;
                    _editorField.SetValueWithoutNotify(_editValue);
                    _suppressUndo = false;
                    _editorField.isReadOnly = false;
                    SetEditorRichText(false);
                    _sourceBtn.text = "Formatted";
                }
            };
            toolbar.Add(_sourceBtn);

            // ── Editor field ──────────────────────────────────────────────────
            // The TextField is placed inside an explicit ScrollView we control.
            // This is more reliable than trying to configure Unity's internal
            // ScrollView, which resets its own state after layout passes.
            //
            // The TextField itself has NO fixed height — it auto-grows with content.
            // The outer ScrollView provides the 160 px window and the scrollbar.
            // The TextField's internal ScrollView is configured to only constrain
            // the horizontal axis (enabling word-wrap) with no visible scrollers.
            var editorScroll = new ScrollView(ScrollViewMode.Vertical);
            editorScroll.style.height       = EditorH;
            editorScroll.style.minHeight    = EditorH;
            editorScroll.style.maxHeight    = EditorH;
            editorScroll.style.marginBottom = Gap;
            editorScroll.verticalScrollerVisibility   = ScrollerVisibility.Auto;
            editorScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

            _editorField = new TextField { multiline = true };
            _editorField.style.flexGrow   = 1f;
            _editorField.style.flexShrink = 0f;   // grow with content, don't collapse
            _editorField.style.minHeight  = RowH * 5f; // always tall enough to click into
            _editorField.style.whiteSpace = WhiteSpace.Normal;
            _editorField.selectAllOnFocus   = false;
            _editorField.selectAllOnMouseUp = false;

            _editorField.RegisterCallback<AttachToPanelEvent>(_ => {
                _editorField.schedule.Execute(() => {
                    // Internal ScrollView: Vertical mode constrains width (enables
                    // word-wrap); scrollers hidden because the outer ScrollView handles
                    // scrolling.
                    var sv = _editorField.Q<ScrollView>();
                    if (sv == null) return;
                    sv.mode = ScrollViewMode.Vertical;
                    sv.verticalScrollerVisibility   = ScrollerVisibility.Hidden;
                    sv.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
                }).StartingIn(50);

                if (string.IsNullOrEmpty(_editValue))
                {
                    // Empty key: open in Source mode so the cursor is immediately visible.
                    _editorField.isReadOnly = false;
                    SetEditorRichText(false);
                    if (_sourceBtn != null) _sourceBtn.text = "Formatted";
                }
                else
                {
                    // Non-empty key: Formatted mode — show the TMPro preview, read-only.
                    _suppressUndo = true;
                    _editorField.SetValueWithoutNotify(OghamInlineLinkParser.ToTMProMarkup(_editValue));
                    _suppressUndo = false;
                    _editorField.isReadOnly = true;
                    SetEditorRichText(true);
                }
            });

            editorScroll.Add(_editorField);

            // Keyboard shortcuts — trickle-down so we handle before TextField default.
            _editorField.RegisterCallback<KeyDownEvent>(evt => {
                bool mod = evt.ctrlKey || evt.commandKey;
                if (!mod) return;
                switch (evt.keyCode)
                {
                    case KeyCode.B:
                        CacheSelection(); ApplyFormatting("**",  "**");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                    case KeyCode.I:
                        CacheSelection(); ApplyFormatting("*",   "*");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                    case KeyCode.U:
                        CacheSelection(); ApplyFormatting("<u>", "</u>");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                }
            }, TrickleDown.TrickleDown);

            // Cache continuously — covers every way the cursor/selection can change.
            _editorField.RegisterCallback<MouseUpEvent>(_ => CacheSelection());
            _editorField.RegisterCallback<KeyUpEvent>(_ => CacheSelection());
            // Trickle-down: fires before TextSelectingManipulator.OnFocusOut clears selectIndex.
            _editorField.RegisterCallback<FocusOutEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);

            // Sync model + maintain undo stack on every change.
            // Skipped in Formatted mode — the field shows the TMPro preview string, not the MD source.
            _editorField.RegisterValueChangedCallback(evt => {
                if (_richTextActive) return;   // Formatted view is read-only; don't touch the MD backing store
                if (!_suppressUndo)
                {
                    _undoStack.Add((evt.previousValue ?? "", _cachedCursorIndex));
                    if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
                    _redoStack.Clear();
                }
                _editValue = evt.newValue ?? "";
            });

            section.Add(editorScroll);

            // ── Lexicon key row (Localised mode only) ─────────────────────────
            // Simple: key text field + picker. Type a new key to create it; pick
            // from the menu to reuse an existing one. Value is always what is in
            // the editor above — same as Literal, saved to the lexicon on commit.
            _lexiconRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = Gap } };
            section.Add(_lexiconRow);

            _lexiconKeyField = new TextField { style = { flexGrow = 1f } };
            _lexiconKeyField.tooltip = "Localization key — type a new key or pick an existing one with ▾";
            _lexiconKeyField.RegisterValueChangedCallback(evt => {
                _editKey = evt.newValue;
                RefreshLexiconState(populateValue: false);
            });
            _lexiconRow.Add(_lexiconKeyField);

            _lexiconRow.Add(new Button(ShowKeyPicker) { text = "▾", style = { width = 24f },
                tooltip = "Pick an existing localization key" });

            // ── Link panel ────────────────────────────────────────────────────
            _linkPanel = BuildLinkPanel();
            _linkPanel.style.display = DisplayStyle.None;
            section.Add(_linkPanel);

            return section;
        }

        // ── Link panel ────────────────────────────────────────────────────────

        private VisualElement BuildLinkPanel()
        {
            var panel = new VisualElement();
            panel.Add(new VisualElement
            {
                style = { height = 1f, backgroundColor = new Color(0.35f, 0.35f, 0.35f), marginBottom = Gap }
            });

            // Formatting toggles — author picks styles; markup applied on Insert.
            // No raw markup ever appears in the display field.
            var fmtRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = Gap } };
            panel.Add(fmtRow);

            Button MakeToggle(string label, string tip)
            {
                var btn = new Button { text = label, tooltip = tip };
                btn.style.width       = 24f;
                btn.style.height      = RowH;
                btn.style.marginRight = 1f;
                return btn;
            }

            _linkBoldBtn = MakeToggle("B", "Bold");
            _linkItalBtn = MakeToggle("I", "Italic");
            _linkUndlBtn = MakeToggle("U", "Underline");

            _linkBoldBtn.clicked += () => { _linkBold      = !_linkBold;      SetLinkToggleActive(_linkBoldBtn, _linkBold); };
            _linkItalBtn.clicked += () => { _linkItalic    = !_linkItalic;    SetLinkToggleActive(_linkItalBtn, _linkItalic); };
            _linkUndlBtn.clicked += () => { _linkUnderline = !_linkUnderline; SetLinkToggleActive(_linkUndlBtn, _linkUnderline); };

            fmtRow.Add(_linkBoldBtn);
            fmtRow.Add(_linkItalBtn);
            fmtRow.Add(_linkUndlBtn);
            fmtRow.Add(new VisualElement { style = { width = Gap } });

            _linkColorField = new ColorField { showAlpha = false, showEyeDropper = false, value = _activeColor };
            _linkColorField.label = "";
            _linkColorField.labelElement.style.display = DisplayStyle.None;
            _linkColorField.style.width  = 36f;
            _linkColorField.style.height = RowH;
            _linkColorField.RegisterValueChangedCallback(evt => _activeColor = evt.newValue);
            fmtRow.Add(_linkColorField);

            _linkColorBtn = MakeToggle("A", "Apply color to link text");
            _linkColorBtn.style.marginLeft = 1f;
            _linkColorBtn.clicked += () => { _linkColorActive = !_linkColorActive; SetLinkToggleActive(_linkColorBtn, _linkColorActive); };
            fmtRow.Add(_linkColorBtn);

            // Plain text field — author types display text with no markup visible.
            _linkDisplayField = new TextField { style = { marginBottom = Gap } };
            _linkDisplayField.RegisterValueChangedCallback(evt => _linkDisplayText = evt.newValue);
            panel.Add(_linkDisplayField);

            // Target URI — free-form; picker pre-fills ogham:// scheme for node targets.
            var targetRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            panel.Add(targetRow);

            _linkTargetField = new TextField { style = { flexGrow = 1f } };
            _linkTargetField.RegisterValueChangedCallback(evt => _linkTarget = evt.newValue);
            targetRow.Add(_linkTargetField);

            targetRow.Add(new Button(ShowLinkTargetPicker) { text = "▾", style = { width = 24f } });

            _unlinkBtn = new Button(RemoveLink) { text = "Unlink", tooltip = "Remove link wrapper, keep display text" };
            _unlinkBtn.style.width       = 50f;
            _unlinkBtn.style.marginLeft  = 2f;
            _unlinkBtn.style.display     = DisplayStyle.None;   // shown only when editing an existing link
            targetRow.Add(_unlinkBtn);

            targetRow.Add(new Button(CommitLink) { text = "Insert", style = { width = 52f, marginLeft = 2f } });

            return panel;
        }

        // ── Asset section ─────────────────────────────────────────────────────

        private VisualElement BuildAssetSection()
        {
            var section = new VisualElement();

            _assetField = new ObjectField { objectType = typeof(Texture2D), value = _editAsset };
            _assetField.style.height       = AssetFieldH;
            _assetField.style.marginBottom = Gap;
            _assetField.RegisterValueChangedCallback(evt => _editAsset = evt.newValue);
            section.Add(_assetField);

            _assetKeyRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginBottom = Gap } };
            section.Add(_assetKeyRow);

            _assetKeyField = new TextField { style = { flexGrow = 1f } };
            _assetKeyField.RegisterValueChangedCallback(evt => _editKey = evt.newValue);
            _assetKeyRow.Add(_assetKeyField);
            _assetKeyRow.Add(new Button(ShowKeyPicker) { text = "▾", style = { width = 24f } });

            return section;
        }

        // ── Rich-text display ─────────────────────────────────────────────────

        // Finds the inner editing TextElement (not the label) and toggles
        // rich-text rendering.  The raw markup is unchanged; only the display flips.
        private void SetEditorRichText(bool richText)
        {
            _richTextActive = richText;
            if (_editorField == null) return;
            // labelElement is itself a TextElement; we want the editing TextElement
            // which is inside the inner input container, not the label.
            var labelTE = _editorField.labelElement as TextElement;
            TextElement innerTE = null;
            _editorField.Query<TextElement>().ForEach(te => {
                if (innerTE == null && te != labelTE) innerTE = te;
            });
            if (innerTE == null) return;
            innerTE.enableRichText = richText;
            innerTE.style.whiteSpace = WhiteSpace.Normal;
        }

        // ── Selection / formatting ────────────────────────────────────────────

        // Snapshot cursor/selection from the live field.
        //
        // Smart update rule:
        //   • Always update when there is an actual selection (cursor ≠ anchor).
        //   • Update with cursor-only when the cursor has MOVED since the last
        //     cache — this distinguishes the user repositioning the cursor from a
        //     focus-loss that clears selectIndex to cursorIndex.
        //
        // This prevents the button's MouseDownEvent callback (which fires after
        // TextSelectingManipulator.OnFocusOut already cleared the selection) from
        // overwriting the good selection captured by FocusOutEvent TrickleDown.
        private void CacheSelection()
        {
            if (_editorField == null) return;
            var sel    = _editorField.textSelection;
            int cursor = sel.cursorIndex;
            int anchor = sel.selectIndex;

            if (cursor != anchor || cursor != _lastCursorPos)
            {
                _cachedCursorIndex = cursor;
                _cachedSelectIndex = anchor;
                _lastCursorPos     = cursor;
            }
            // else: cursor is at the same position and no selection — this is a
            // focus-loss selectIndex clear; keep the previously cached selection.
        }

        // Convert a visual character index (as reported by textSelection when
        // rich text is enabled) to the corresponding index in the raw markup string.
        // Tags are skipped — every visible character advances the visual counter by 1.
        private static int VisualToRawIndex(string raw, int visualIndex)
        {
            int r = 0, v = 0;
            while (r < raw.Length)
            {
                if (raw[r] == '<')
                {
                    int tagEnd = raw.IndexOf('>', r);
                    if (tagEnd >= 0) { r = tagEnd + 1; continue; }
                    // malformed '<' — treat as a visible character
                }
                if (v == visualIndex) return r;
                v++;
                r++;
            }
            return r;   // at or past end of string
        }

        // Inverse: convert a raw-string index to the visual index for SelectRange.
        private static int RawToVisualIndex(string raw, int rawIndex)
        {
            int r = 0, v = 0;
            while (r < raw.Length && r < rawIndex)
            {
                if (raw[r] == '<')
                {
                    int tagEnd = raw.IndexOf('>', r);
                    if (tagEnd >= 0) { r = tagEnd + 1; continue; }
                }
                v++;
                r++;
            }
            return v;
        }

        private void ApplyFormatting(string open, string close)
        {
            if (_editorField == null) return;

            if (_richTextActive)
            {
                // Formatted mode: operate on the MD source using the visual selection.
                string prev = _editValue;
                var (rawMin, rawMax) = GetSelectionMdRange(prev);

                // Binary formats (**, *, <u>) toggle: strip/split if selection is already formatted.
                // Parameterised tags (<color=...>, <size=...>) always apply.
                bool shouldToggle = open == "**" || open == "*" || open == "<u>"
                                 || open == "<b>" || open == "<i>";
                string next = (shouldToggle && rawMin != rawMax)
                    ? ToggleFormatInRange(prev, rawMin, rawMax, open, close)
                    : prev[..rawMin] + open + prev[rawMin..rawMax] + close + prev[rawMax..];

                if (next == prev) return;

                _undoStack.Add((prev, _cachedCursorIndex));
                if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
                _redoStack.Clear();

                _editValue = next;
                RefreshFormattedDisplay();
                return;
            }

            // Source mode: wrap selection with markers.
            string cur   = _editValue;
            int start    = Mathf.Clamp(Mathf.Min(_cachedCursorIndex, _cachedSelectIndex), 0, cur.Length);
            int end      = Mathf.Clamp(Mathf.Max(_cachedCursorIndex, _cachedSelectIndex), 0, cur.Length);
            string result = cur[..start] + open + cur[start..end] + close + cur[end..];

            _undoStack.Add((cur, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _suppressUndo = true;
            _editorField.value = result;   // fires RegisterValueChangedCallback → syncs _editValue
            _suppressUndo = false;

            int newPos = Mathf.Clamp(end + open.Length + close.Length, 0, result.Length);
            RestoreFocusAndCursor(newPos);
        }

        // ── Undo / redo ───────────────────────────────────────────────────────

        // Intercepted at the root in trickle-down so we handle undo/redo before
        // the focused element (TextField) would try to.  When our stacks are empty
        // we do NOT stop propagation — the TextField's own undo handles typing.
        private void OnRootKeyDown(KeyDownEvent evt)
        {
            bool mod = evt.ctrlKey || evt.commandKey;
            if (!mod) return;

            if (evt.keyCode == KeyCode.Z && !evt.shiftKey)
            {
                if (_undoStack.Count > 0)
                {
                    PerformUndo();
                    evt.StopPropagation();
                    evt.PreventDefault();
                }
                // else: fall through → TextField handles typing undo
            }
            else if ((evt.keyCode == KeyCode.Z && evt.shiftKey) || evt.keyCode == KeyCode.Y)
            {
                if (_redoStack.Count > 0)
                {
                    PerformRedo();
                    evt.StopPropagation();
                    evt.PreventDefault();
                }
            }
        }

        private void PerformUndo()
        {
            if (_undoStack.Count == 0) return;
            var item = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            _redoStack.Add((_editorField.value, _cachedCursorIndex));
            ApplyRestoredValue(item.text, item.cursor);
        }

        private void PerformRedo()
        {
            if (_redoStack.Count == 0) return;
            var item = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _undoStack.Add((_editorField.value, _cachedCursorIndex));
            ApplyRestoredValue(item.text, item.cursor);
        }

        // Restores a saved text+cursor state without pushing a new undo entry.
        // Always switches back to Source mode so the MD text is displayed and editable.
        private void ApplyRestoredValue(string text, int cursor)
        {
            _editValue = text;

            if (_richTextActive)
            {
                // Exit Formatted preview — restore editable Source mode.
                _editorField.isReadOnly = false;
                SetEditorRichText(false);
                if (_sourceBtn != null) _sourceBtn.text = "Formatted";
            }

            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(text);
            _suppressUndo = false;

            RestoreFocusAndCursor(Mathf.Clamp(cursor, 0, text.Length));
        }

        // pos is a VISUAL index (what SelectRange expects).
        // Focus() triggers SelectAll internally; the deferred schedule.Execute call
        // overrides it.  _lastCursorPos is updated so the next CacheSelection call
        // does not mistake this programmatic cursor move for a focus-loss clear.
        private void RestoreFocusAndCursor(int pos)
        {
            _editorField.Focus();
            _editorField.schedule.Execute(() => {
                int safePos = Mathf.Clamp(pos, 0, _editorField.value.Length);
                _editorField.textSelection.SelectRange(safePos, safePos);
                _cachedCursorIndex = _cachedSelectIndex = safePos;
                _lastCursorPos     = safePos;
            }).StartingIn(0);
        }

        // Programmatic value set (used by lexicon reload, key picker, etc.)
        // without creating an undo entry — callers that need undo push manually.
        private void SetEditorValue(string text, int cursorHint = 0)
        {
            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(text);
            _suppressUndo = false;
            RestoreFocusAndCursor(Mathf.Clamp(cursorHint, 0, text.Length));
        }

        // ── Refresh helpers ───────────────────────────────────────────────────

        private void RefreshSections()
        {
            if (_textSection == null) return;

            _textSection.style.display  = IsText ? DisplayStyle.Flex : DisplayStyle.None;
            _assetSection.style.display = IsText ? DisplayStyle.None : DisplayStyle.Flex;

            if (IsText)
            {
                bool isLoc = _editMode == LexiconLocMode.Localised;
                _suppressUndo = true;
                _editorField.SetValueWithoutNotify(_richTextActive
                    ? OghamInlineLinkParser.ToTMProMarkup(_editValue)
                    : _editValue);
                _suppressUndo = false;
                _lexiconRow.style.display = isLoc ? DisplayStyle.Flex : DisplayStyle.None;
                if (isLoc) _lexiconKeyField.SetValueWithoutNotify(_editKey);
            }
            else
            {
                _assetField.objectType = _editType switch {
                    OghamContentType.Audio  => typeof(AudioClip),
                    OghamContentType.Prefab => typeof(GameObject),
                    OghamContentType.Sprite => typeof(Sprite),
                    _                       => typeof(Texture2D),
                };
                _assetField.SetValueWithoutNotify(_editAsset);
                _assetKeyField.SetValueWithoutNotify(_editKey);
                _assetKeyRow.style.display = _editMode == LexiconLocMode.Localised
                    ? DisplayStyle.Flex : DisplayStyle.None;
            }

            Resize();
        }

        private void ShowLinkTargetPicker()
        {
            var tags = new List<string>();
            // OghamData is no longer an asset; gather node tags from the .ogham JSON sources.
            foreach (var full in System.IO.Directory.GetFiles(UnityEngine.Application.dataPath, "*.ogham", System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    var manifest = OghamJsonDocument.Parse(System.IO.File.ReadAllText(full)).ToManifest();
                    foreach (var e in manifest.Entries)
                        if (!string.IsNullOrEmpty(e.TagPath) && !tags.Contains(e.TagPath))
                            tags.Add(e.TagPath);
                }
                catch { /* skip unreadable / unparsable */ }
            }
            tags.Sort(StringComparer.Ordinal);

            var menu = new GenericMenu();
            foreach (var tag in tags)
            {
                var cap = tag;
                var uri = $"Ogham://{cap}";
                menu.AddItem(new GUIContent(cap.Replace('.', '/')), _linkTarget == uri, () => {
                    _linkTarget = uri;
                    _linkTargetField?.SetValueWithoutNotify(uri);
                });
            }
            if (tags.Count == 0)
                menu.AddDisabledItem(new GUIContent("No entries found in project"));
            menu.ShowAsContext();
        }

        private void CommitLink()
        {
            // Build MD link: [display](target)
            // Display text uses TMPro tags for bold/italic/underline/colour — they pass through
            // OghamInlineLinkParser.ToTMProMarkup unchanged when the formatted preview is shown.
            string display = string.IsNullOrWhiteSpace(_linkDisplayText) ? "link" : _linkDisplayText;
            if (_linkUnderline)   display = $"<u>{display}</u>";
            if (_linkItalic)      display = $"<i>{display}</i>";
            if (_linkBold)        display = $"<b>{display}</b>";
            if (_linkColorActive) display = $"<color=#{ColorUtility.ToHtmlStringRGB(_activeColor)}>{display}</color>";
            var snippet = $"[{display}]({_linkTarget})";

            // Always in Source mode here (link panel blocked in Formatted mode).
            // Cursor/selection indices are raw — no visual-to-raw conversion needed.
            string prev = _editValue;
            string next;
            int cursorAfter;

            if (_editingLinkRawStart >= 0)
            {
                // Replacing an existing link span (possibly expanded by selection).
                int rawStart = Mathf.Clamp(_editingLinkRawStart, 0, prev.Length);
                int rawEnd   = Mathf.Clamp(_editingLinkRawEnd,   0, prev.Length);
                next        = prev[..rawStart] + snippet + prev[rawEnd..];
                cursorAfter = Mathf.Clamp(rawStart + snippet.Length, 0, next.Length);
            }
            else if (_cachedCursorIndex != _cachedSelectIndex)
            {
                // Replace selected range with new link.
                int rawStart = Mathf.Clamp(Mathf.Min(_cachedCursorIndex, _cachedSelectIndex), 0, prev.Length);
                int rawEnd   = Mathf.Clamp(Mathf.Max(_cachedCursorIndex, _cachedSelectIndex), 0, prev.Length);
                next        = prev[..rawStart] + snippet + prev[rawEnd..];
                cursorAfter = Mathf.Clamp(rawStart + snippet.Length, 0, next.Length);
            }
            else
            {
                // No selection — insert at cursor position.
                int rawCursor = Mathf.Clamp(_cachedCursorIndex, 0, prev.Length);
                next        = prev[..rawCursor] + snippet + prev[rawCursor..];
                cursorAfter = Mathf.Clamp(rawCursor + snippet.Length, 0, next.Length);
            }

            _editValue = next;

            _undoStack.Add((prev, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(next);
            _suppressUndo = false;

            _linkPanelOpen       = false;
            _linkPanel.style.display = DisplayStyle.None;
            _editingLinkRawStart   = -1;
            _editingLinkRawEnd     = -1;
            _editingLinkDisplayRaw = "";
            if (_unlinkBtn != null) _unlinkBtn.style.display = DisplayStyle.None;
            _linkDisplayText     = "";
            _linkTarget          = "";
            _linkDisplayField?.SetValueWithoutNotify("");
            _linkTargetField?.SetValueWithoutNotify("");
            Resize();
            RestoreFocusAndCursor(cursorAfter);
        }

        // ── Lexicon helpers ───────────────────────────────────────────────────

        private void RefreshLexiconState(bool populateValue)
        {
            if (!IsText || _editMode != LexiconLocMode.Localised || string.IsNullOrWhiteSpace(_editKey))
                return;
            var resolved = LexiconRegistry.ResolveString(LexiconRegistry.Hash(_editKey));
            if (resolved != null && populateValue)
                _editValue = resolved;
        }

        private void ShowKeyPicker()
        {
            var keys = LexiconSettingsProvider.GetAllLexiconKeys()?.ToList() ?? new List<string>();
            var menu = new GenericMenu();
            if (keys.Count == 0)
            {
                menu.AddDisabledItem(new GUIContent("No existing keys — type a new key in the field"));
            }
            else
            {
                foreach (var k in keys)
                {
                    var captured = k;
                    menu.AddItem(new GUIContent(captured.Replace('.', '/')), _editKey == captured, () => {
                        _editKey = captured;
                        _lexiconKeyField?.SetValueWithoutNotify(captured);
                        _assetKeyField?.SetValueWithoutNotify(captured);
                        RefreshLexiconState(populateValue: true);
                        SetEditorValue(_editValue);
                    });
                }
            }
            menu.ShowAsContext();
        }

        // ── Resize / placement ────────────────────────────────────────────────

        private void Resize()
        {
            if (!_windowReady) return;
            float h = ComputeHeight();
            minSize = new Vector2(W, h);
            maxSize = new Vector2(W, h);
            position = PlaceAtAnchor(_anchor, W, h);
        }

        private static Rect PlaceAtAnchor(Vector2 anchor, float w, float h)
        {
            var r   = new Rect(anchor.x, anchor.y - 26f, w, h);
            var res = Screen.currentResolution;
            if (r.xMax > res.width)  r.x = res.width  - w - 4f;
            if (r.x    < 0f)         r.x = 0f;
            if (r.yMax > res.height) r.y = res.height - h - 4f;
            if (r.y    < 0f)         r.y = 0f;
            return r;
        }

        // ── Color persistence ─────────────────────────────────────────────────

        private static Color LoadColor()
        {
            if (EditorPrefs.HasKey(ColorPrefKey))
            {
                var hex = EditorPrefs.GetString(ColorPrefKey, "");
                if (!string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString("#" + hex, out var c))
                    return c;
            }
            return OghamEditorSettings.GetOrCreate().DefaultLinkColor;
        }

        private void SaveColor()
            => EditorPrefs.SetString(ColorPrefKey, ColorUtility.ToHtmlStringRGB(_activeColor));

        // ── Commit / cancel ───────────────────────────────────────────────────

        private void OnLostFocus()
        {
            if (_closing) return;
            // Defer by one frame: the color picker (and other auxiliary windows) claim focus
            // AFTER this event fires, so an immediate focusedWindow check always misses them.
            EditorApplication.delayCall += () =>
            {
                if (_closing || this == null) return;
                var fw = EditorWindow.focusedWindow;
                if (fw == this) return;   // we regained focus
                if (fw != null)
                {
                    var n = fw.GetType().Name;
                    // Covers: ColorPicker, GradientEditor, ObjectPickerWindow,
                    // ObjectSelectorWindow, ObjectSelector, any modal Popup.
                    if (n.Contains("Color")    || n.Contains("Gradient") ||
                        n.Contains("Picker")   || n.Contains("Selector") ||
                        n.Contains("Popup")    || n.Contains("Browser")  ||
                        n.Contains("Inspector"))
                        return;
                }
                Commit();
            };
        }

        private void Commit()
        {
            if (_closing) return;
            _closing         = true;
            _item.Type       = _editType;
            _item.Mode       = _editMode;
            _item.KeyOrValue = !IsText && _editMode == LexiconLocMode.Literal && _editAsset != null
                ? _editAsset.name
                : IsText && _editMode == LexiconLocMode.Localised ? _editKey : _editValue;
            _item.AssetRef   = _editAsset;
            _item.InvalidateHash();

            if (IsText && _editMode == LexiconLocMode.Localised && !string.IsNullOrWhiteSpace(_editKey))
            {
                var current = LexiconRegistry.ResolveString(LexiconRegistry.Hash(_editKey));
                if (current == null || _editValue != current)
                    LexiconSettingsProvider.UpsertStringEntry(_editKey, _editValue);
            }

            if (IsText) SaveColor();
            // _asset (OghamData) persists via the .ogham JSON on save; nothing to dirty.
            _onCommit?.Invoke();
            Close();
        }

        private void Cancel() { _closing = true; Close(); }

        // Strips the MD link wrapper [display](target) from the editing span, leaving
        // the inner display text (with any TMPro formatting tags) in place.
        private void RemoveLink()
        {
            if (_editingLinkRawStart < 0) return;

            string prev  = _editValue;
            int rawStart = Mathf.Clamp(_editingLinkRawStart, 0, prev.Length);
            int rawEnd   = Mathf.Clamp(_editingLinkRawEnd,   0, prev.Length);

            // Extract just the display text from [display](target) — keep formatting markup.
            string span = prev[rawStart..rawEnd];
            var m = OghamInlineLinkParser.LinkRx.Match(span);
            string result = m.Success ? m.Groups[1].Value : StripAllTags(span);
            string next   = prev[..rawStart] + result + prev[rawEnd..];

            _editValue = next;

            _undoStack.Add((prev, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(next);
            _suppressUndo = false;

            int cursorAfter = Mathf.Clamp(rawStart + result.Length, 0, next.Length);

            _linkPanelOpen         = false;
            _linkPanel.style.display = DisplayStyle.None;
            _editingLinkRawStart   = -1;
            _editingLinkRawEnd     = -1;
            _editingLinkDisplayRaw = "";
            _linkDisplayText       = "";
            _linkTarget            = "";
            _linkDisplayField?.SetValueWithoutNotify("");
            _linkTargetField?.SetValueWithoutNotify("");
            if (_unlinkBtn != null) _unlinkBtn.style.display = DisplayStyle.None;
            Resize();
            RestoreFocusAndCursor(cursorAfter);
        }

        // Removes only <link=...> and </link> tags, preserving all other markup.
        private static string StripLinkTagsOnly(string raw)
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
                        string inner = raw.Substring(i + 1, end - i - 1).TrimStart('/');
                        // Match "link" or "link=..." or "link ..."
                        if (inner.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                            (inner.Length > 4 &&
                             inner.StartsWith("link", StringComparison.OrdinalIgnoreCase) &&
                             (inner[4] == '=' || inner[4] == ' ')))
                        { i = end + 1; continue; }
                    }
                }
                sb.Append(raw[i++]);
            }
            return sb.ToString();
        }

        // ── Link-panel helpers ────────────────────────────────────────────────

        private static void SetLinkToggleActive(Button btn, bool on)
        {
            if (btn == null) return;
            btn.style.backgroundColor = on
                ? new Color(0.25f, 0.5f, 0.25f)
                : new Color(0f, 0f, 0f, 0f);
        }

        // Finds the first MD-style link [display](target) whose raw span overlaps [rawSelMin, rawSelMax).
        // The source is always the MD string (_editValue) so we use OghamInlineLinkParser.LinkRx.
        private static bool TryFindOverlappingLink(string raw, int rawSelMin, int rawSelMax,
            out string linkTarget, out string linkDisplayRaw, out int linkRawStart, out int linkRawEnd)
        {
            linkTarget = ""; linkDisplayRaw = ""; linkRawStart = -1; linkRawEnd = -1;
            if (string.IsNullOrEmpty(raw)) return false;

            foreach (System.Text.RegularExpressions.Match m in OghamInlineLinkParser.LinkRx.Matches(raw))
            {
                int spanStart = m.Index;
                int spanEnd   = m.Index + m.Length;
                if (spanStart >= rawSelMax || spanEnd <= rawSelMin) continue;

                linkDisplayRaw = m.Groups[1].Value;
                linkTarget     = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
                linkRawStart   = spanStart;
                linkRawEnd     = spanEnd;
                return true;
            }
            return false;
        }

        // Peels outer formatting wrappers (<b>, <i>, <u>, <color=...>) from link display markup,
        // returning the plain text and which toggles were set.
        private static void ParseLinkFormatting(string displayRaw,
            out string plainText, out bool bold, out bool italic, out bool underline, out string colorHex)
        {
            bold = false; italic = false; underline = false; colorHex = null;
            string s = displayRaw;
            bool changed = true;
            while (changed && s.Length > 0)
            {
                changed = false;
                if (s.StartsWith("<b>", StringComparison.OrdinalIgnoreCase) &&
                    s.EndsWith("</b>", StringComparison.OrdinalIgnoreCase))
                { bold = true; s = s[3..^4]; changed = true; continue; }
                if (s.StartsWith("<i>", StringComparison.OrdinalIgnoreCase) &&
                    s.EndsWith("</i>", StringComparison.OrdinalIgnoreCase))
                { italic = true; s = s[3..^4]; changed = true; continue; }
                if (s.StartsWith("<u>", StringComparison.OrdinalIgnoreCase) &&
                    s.EndsWith("</u>", StringComparison.OrdinalIgnoreCase))
                { underline = true; s = s[3..^4]; changed = true; continue; }
                if (s.StartsWith("<color=", StringComparison.OrdinalIgnoreCase) &&
                    s.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
                {
                    int gt = s.IndexOf('>');
                    if (gt > 0) { colorHex = s.Substring(7, gt - 7).TrimStart('#'); s = s[(gt + 1)..^8]; changed = true; }
                }
            }
            plainText = s;
        }

        // Strips / splits <color=...>...</color> tags wrapping the current selection.
        // Works in both Source mode (raw indices) and Formatted mode (visual → MD raw).
        // If there is no selection, strips all color tags from the whole string.
        private void StripColorFromSelection()
        {
            string prev = _editValue;
            int rawMin, rawMax;

            if (_richTextActive)
                (rawMin, rawMax) = GetSelectionMdRange(prev);
            else
            {
                rawMin = Mathf.Clamp(Mathf.Min(_cachedCursorIndex, _cachedSelectIndex), 0, prev.Length);
                rawMax = Mathf.Clamp(Mathf.Max(_cachedCursorIndex, _cachedSelectIndex), 0, prev.Length);
            }

            // With a selection: find color tags that surround / overlap the range and strip/split.
            // Without a selection: strip all color tags from the whole string (emergency full-strip).
            string next = rawMin != rawMax
                ? StripTagTypeInRange(prev, rawMin, rawMax, "color")
                : StripTagsByName(prev, "color");

            if (next == prev) return;

            _undoStack.Add((prev, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _editValue = next;

            if (_richTextActive)
                RefreshFormattedDisplay();
            else
            {
                _suppressUndo = true;
                _editorField.SetValueWithoutNotify(next);
                _suppressUndo = false;
                RestoreFocusAndCursor(Mathf.Clamp(rawMin, 0, next.Length));
            }
        }

        // ── Formatted-mode editing helpers ────────────────────────────────────────

        // Maps a visual character index (as reported by textSelection in Formatted/rich-text mode) to
        // its raw position in the MD source string.  Characters invisible in the MD source:
        //   • TMPro pass-through tags <...>
        //   • Bold markers ** (opening and closing pairs)
        //   • Italic markers *  (opening and closing, skipping those inside bold spans)
        //   • Link structure [...](...) — brackets and URL invisible; display text inside [] visible.
        private static int MdVisualToRawIndex(string md, int visualIdx)
        {
            if (string.IsNullOrEmpty(md) || visualIdx <= 0) return visualIdx <= 0 ? 0 : md.Length;

            var invisible = new HashSet<int>();

            // TMPro pass-through tags
            for (int i = 0; i < md.Length; )
            {
                if (md[i] == '<') { int end = md.IndexOf('>', i); if (end >= 0) { for (int k = i; k <= end; k++) invisible.Add(k); i = end + 1; continue; } }
                i++;
            }

            // Bold markers ** (each opening/closing pair contributes 2 invisible chars)
            var boldMatches = OghamInlineLinkParser.BoldRx.Matches(md);
            foreach (System.Text.RegularExpressions.Match m in boldMatches)
            {
                invisible.Add(m.Index); invisible.Add(m.Index + 1);
                int e = m.Index + m.Length - 2; invisible.Add(e); invisible.Add(e + 1);
            }

            // Italic markers * — skip any that are entirely inside a bold span (false positives)
            foreach (System.Text.RegularExpressions.Match m in OghamInlineLinkParser.ItalicRx.Matches(md))
            {
                bool inBold = false;
                foreach (System.Text.RegularExpressions.Match b in boldMatches)
                    if (m.Index >= b.Index && m.Index + m.Length <= b.Index + b.Length) { inBold = true; break; }
                if (inBold) continue;
                invisible.Add(m.Index); invisible.Add(m.Index + m.Length - 1);
            }

            // Link brackets and URL: [display](url) → '[', ']', '(' to ')' are invisible
            foreach (System.Text.RegularExpressions.Match m in OghamInlineLinkParser.LinkRx.Matches(md))
            {
                invisible.Add(m.Index);                                             // '['
                int cb = m.Index + 1 + m.Groups[1].Length; invisible.Add(cb);      // ']'
                for (int k = cb + 1; k < m.Index + m.Length; k++) invisible.Add(k); // (url)
            }

            int v = 0;
            for (int r = 0; r < md.Length; r++)
            {
                if (invisible.Contains(r)) continue;
                if (v == visualIdx) return r;
                v++;
            }
            return md.Length;
        }

        // Converts the cached visual selection to MD source raw indices.  Used in Formatted mode.
        private (int rawMin, int rawMax) GetSelectionMdRange(string md)
        {
            int visMin = Mathf.Min(_cachedCursorIndex, _cachedSelectIndex);
            int visMax = Mathf.Max(_cachedCursorIndex, _cachedSelectIndex);
            return (Mathf.Clamp(MdVisualToRawIndex(md, visMin), 0, md.Length),
                    Mathf.Clamp(MdVisualToRawIndex(md, visMax), 0, md.Length));
        }

        // Recomputes the Formatted (TMPro preview) display from the current _editValue.
        private void RefreshFormattedDisplay()
        {
            if (_editorField == null || !_richTextActive) return;
            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(OghamInlineLinkParser.ToTMProMarkup(_editValue));
            _suppressUndo = false;
        }

        // True when s contains at least one non-whitespace character that is not inside a <tag>.
        private static bool HasVisibleChars(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') { int end = s.IndexOf('>', i); if (end >= 0) { i = end; continue; } }
                if (!char.IsWhiteSpace(s[i])) return true;
            }
            return false;
        }

        // Strip or split a single format span [spanStart, spanEnd) around the selection [rawMin, rawMax).
        // contentStart / contentEnd delimit the span's inner content (excluding markers).
        // openTag / closeTag are the format markers (e.g. "<color=#FF>" / "</color>" or "**" / "**").
        private static string StripOrSplitFormatSpan(string md,
            int spanStart, int spanEnd, int contentStart, int contentEnd,
            string openTag, string closeTag, int rawMin, int rawMax)
        {
            int selMin = Mathf.Max(rawMin, contentStart);
            int selMax = Mathf.Min(rawMax, contentEnd);
            if (selMin >= contentEnd) return md; // selection doesn't reach content

            string before = md[..spanStart];
            string after  = md[spanEnd..];
            string leftC  = md[contentStart..selMin];
            string midC   = md[selMin..selMax];
            string rightC = md[selMax..contentEnd];

            bool lv = HasVisibleChars(leftC);
            bool rv = HasVisibleChars(rightC);

            if (!lv && !rv) return before + md[contentStart..contentEnd] + after;                             // remove markers
            if (lv && !rv)  return before + openTag + leftC + closeTag + midC + after;                        // trim end
            if (!lv)        return before + midC + openTag + rightC + closeTag + after;                       // trim start
            return          before + openTag + leftC + closeTag + midC + openTag + rightC + closeTag + after; // split
        }

        // Enumerates all <tagName...>...</tagName> spans in md (non-nested).
        private static IEnumerable<(int spanStart, int spanEnd, int contentStart, int contentEnd, string openTag)>
            FindTagSpans(string md, string tagName)
        {
            int i = 0;
            while (i < md.Length)
            {
                if (md[i] != '<') { i++; continue; }
                int gt = md.IndexOf('>', i);
                if (gt < 0) break;
                string inner = md.Substring(i + 1, gt - i - 1);
                if (!inner.StartsWith("/") &&
                    (inner.Equals(tagName, StringComparison.OrdinalIgnoreCase)
                    || (inner.Length > tagName.Length
                        && inner.StartsWith(tagName, StringComparison.OrdinalIgnoreCase)
                        && (inner[tagName.Length] == '=' || inner[tagName.Length] == ' '))))
                {
                    string openTag = md.Substring(i, gt - i + 1);
                    int contentStart = gt + 1;
                    string closeTag = $"</{tagName}>";
                    int closeIdx = md.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        yield return (i, closeIdx + closeTag.Length, contentStart, closeIdx, openTag);
                        i = closeIdx + closeTag.Length;
                        continue;
                    }
                }
                i = gt + 1;
            }
        }

        // Strips / splits all spans of the given TMPro tag type that overlap [rawMin, rawMax].
        // Processes right-to-left so earlier indices are unaffected by later modifications.
        private static string StripTagTypeInRange(string md, int rawMin, int rawMax, string tagName)
        {
            var spans = new List<(int spanStart, int spanEnd, int contentStart, int contentEnd, string openTag)>();
            foreach (var s in FindTagSpans(md, tagName))
                if (s.spanStart < rawMax && s.spanEnd > rawMin) spans.Add(s);

            for (int i = spans.Count - 1; i >= 0; i--)
            {
                var (spanStart, spanEnd, contentStart, contentEnd, openTag) = spans[i];
                md = StripOrSplitFormatSpan(md, spanStart, spanEnd, contentStart, contentEnd,
                    openTag, $"</{tagName}>", rawMin, rawMax);
            }
            return md;
        }

        // Strips / splits MD marker spans (bold ** or italic *) that overlap [rawMin, rawMax].
        private static string StripMdMarkerInRange(string md, int rawMin, int rawMax,
            System.Text.RegularExpressions.Regex rx, string marker)
        {
            var matches = new List<System.Text.RegularExpressions.Match>();
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(md))
                if (m.Index < rawMax && m.Index + m.Length > rawMin) matches.Add(m);

            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                md = StripOrSplitFormatSpan(md, m.Index, m.Index + m.Length,
                    m.Index + marker.Length, m.Index + m.Length - marker.Length,
                    marker, marker, rawMin, rawMax);
            }
            return md;
        }

        // Returns true if any format span of the given type overlaps [rawMin, rawMax] in md.
        private static bool HasFormatOverlap(string md, int rawMin, int rawMax, string open, string close)
        {
            if (open == "**")
            {
                foreach (System.Text.RegularExpressions.Match m in OghamInlineLinkParser.BoldRx.Matches(md))
                    if (m.Index < rawMax && m.Index + m.Length > rawMin) return true;
                return false;
            }
            if (open == "*")
            {
                var boldMs = OghamInlineLinkParser.BoldRx.Matches(md);
                foreach (System.Text.RegularExpressions.Match m in OghamInlineLinkParser.ItalicRx.Matches(md))
                {
                    if (m.Index >= rawMax || m.Index + m.Length <= rawMin) continue;
                    bool inBold = false;
                    foreach (System.Text.RegularExpressions.Match b in boldMs)
                        if (m.Index >= b.Index && m.Index + m.Length <= b.Index + b.Length) { inBold = true; break; }
                    if (!inBold) return true;
                }
                return false;
            }
            if (open.StartsWith("<") && open.EndsWith(">"))
            {
                string inner   = open[1..^1];
                string tagName = inner.Contains('=') ? inner[..inner.IndexOf('=')] : inner;
                foreach (var (spanStart, spanEnd, _, _, _) in FindTagSpans(md, tagName))
                    if (spanStart < rawMax && spanEnd > rawMin) return true;
            }
            return false;
        }

        // Toggles format on [rawMin, rawMax]: strips/splits if already formatted, applies if not.
        private static string ToggleFormatInRange(string md, int rawMin, int rawMax, string open, string close)
        {
            if (HasFormatOverlap(md, rawMin, rawMax, open, close))
            {
                if (open == "**") return StripMdMarkerInRange(md, rawMin, rawMax, OghamInlineLinkParser.BoldRx,   "**");
                if (open == "*")  return StripMdMarkerInRange(md, rawMin, rawMax, OghamInlineLinkParser.ItalicRx, "*");
                if (open.StartsWith("<") && open.EndsWith(">"))
                {
                    string inner   = open[1..^1];
                    string tagName = inner.Contains('=') ? inner[..inner.IndexOf('=')] : inner;
                    return StripTagTypeInRange(md, rawMin, rawMax, tagName);
                }
                return md;
            }
            return md[..rawMin] + open + md[rawMin..rawMax] + close + md[rawMax..];
        }

        // Strips <tagName=...> / <tagName> and </tagName> pairs from raw, leaving all other markup.
        private static string StripTagsByName(string raw, string tagName)
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
                        string inner = raw.Substring(i + 1, end - i - 1).TrimStart('/');
                        if (inner.Equals(tagName, StringComparison.OrdinalIgnoreCase) ||
                            (inner.Length > tagName.Length &&
                             inner.StartsWith(tagName, StringComparison.OrdinalIgnoreCase) &&
                             (inner[tagName.Length] == '=' || inner[tagName.Length] == ' ')))
                        { i = end + 1; continue; }
                    }
                }
                sb.Append(raw[i++]);
            }
            return sb.ToString();
        }

        // Strip all <tags> — used to extract plain visible text from a raw string range.
        private static string StripAllTags(string raw)
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
    }
}
