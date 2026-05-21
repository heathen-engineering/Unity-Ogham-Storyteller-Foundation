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
    // Popup for editing a single OghamContentKey.
    //
    // The main editing surface is a single multiline TextField with rich-text
    // rendering enabled on its inner TextElement — i.e. the author always sees
    // and edits formatted text (WYSIWYG).  "Source" toggles that rendering off
    // so the raw markup is visible; it is an escape-hatch, not the primary view.
    //
    // Layout:
    //   [Type ▼] [Literal|Localised ▼]                   [Save] [X]
    //   [B] [I] [U] [■ color] [A] [size ▼] [🔗] [Source]
    //   [ formatted TextField — 160 px, scrolls                      ]
    //   (lexicon key row — Localised mode only)
    //   (link panel — when open)
    //   -- OR (non-text type) --
    //   [ ObjectField ]
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
        private bool _richTextActive = true;  // mirrors enableRichText on the inner TextElement
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

        private const string ColorPrefKey = "Ogham.LastTextColor";
        private const float  W            = 660f;
        private const float  RowH         = 24f;
        private const float  EditorH      = 320f;
        private const float  AssetFieldH  = 50f;
        private const float  Gap          = 4f;
        private const int    UndoLimit    = 200;

        private bool IsText    => _editType == OghamContentType.Text;
        private bool IsLiteral => _editMode == LexiconLocMode.Literal;


        // ── Open ──────────────────────────────────────────────────────────────

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

            toolbar.Add(MakeBtn("B", "Bold (Ctrl+B)",      () => ApplyFormatting("<b>",  "</b>")));
            toolbar.Add(MakeBtn("I", "Italic (Ctrl+I)",    () => ApplyFormatting("<i>",  "</i>")));
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
                _linkPanelOpen = !_linkPanelOpen;
                _linkPanel.style.display = _linkPanelOpen ? DisplayStyle.Flex : DisplayStyle.None;
                if (_linkPanelOpen)
                {
                    string raw    = _editorField.value;
                    int visMin    = Mathf.Min(_cachedCursorIndex, _cachedSelectIndex);
                    int visMax    = Mathf.Max(_cachedCursorIndex, _cachedSelectIndex);
                    int rawSelMin = Mathf.Clamp(_richTextActive ? VisualToRawIndex(raw, visMin) : visMin, 0, raw.Length);
                    int rawSelMax = Mathf.Clamp(_richTextActive ? VisualToRawIndex(raw, visMax) : visMax, 0, raw.Length);

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

            // "Source" toggle — flips rich-text rendering on the inner TextElement.
            // The field content (raw markup) never changes; only the display mode does.
            var sourceBtn = new Button { text = "Source", tooltip = "Show raw markup (editing still active)" };
            sourceBtn.style.width  = 60f;
            sourceBtn.style.height = RowH;
            sourceBtn.focusable    = false;
            sourceBtn.RegisterCallback<MouseDownEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);
            sourceBtn.clicked += () => {
                sourceMode      = !sourceMode;
                sourceBtn.text  = sourceMode ? "Formatted" : "Source";
                SetEditorRichText(!sourceMode);
            };
            toolbar.Add(sourceBtn);

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
            _editorField.style.whiteSpace = WhiteSpace.Normal;
            _editorField.selectAllOnFocus   = false;
            _editorField.selectAllOnMouseUp = false;

            _editorField.RegisterCallback<AttachToPanelEvent>(_ => {
                SetEditorRichText(true);
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
            });

            editorScroll.Add(_editorField);

            // Keyboard shortcuts — trickle-down so we handle before TextField default.
            _editorField.RegisterCallback<KeyDownEvent>(evt => {
                bool mod = evt.ctrlKey || evt.commandKey;
                if (!mod) return;
                switch (evt.keyCode)
                {
                    case KeyCode.B:
                        CacheSelection(); ApplyFormatting("<b>",  "</b>");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                    case KeyCode.I:
                        CacheSelection(); ApplyFormatting("<i>",  "</i>");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                    case KeyCode.U:
                        CacheSelection(); ApplyFormatting("<u>",  "</u>");
                        evt.StopPropagation(); evt.PreventDefault(); break;
                }
            }, TrickleDown.TrickleDown);

            // Cache continuously — covers every way the cursor/selection can change.
            _editorField.RegisterCallback<MouseUpEvent>(_ => CacheSelection());
            _editorField.RegisterCallback<KeyUpEvent>(_ => CacheSelection());
            // Trickle-down: fires before TextSelectingManipulator.OnFocusOut clears selectIndex.
            _editorField.RegisterCallback<FocusOutEvent>(_ => CacheSelection(), TrickleDown.TrickleDown);

            // Sync model + maintain undo stack on every change.
            // evt.previousValue gives us what to push; _suppressUndo prevents
            // loops when we set the value programmatically during undo/redo.
            _editorField.RegisterValueChangedCallback(evt => {
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

            string cur = _editorField.value;

            // textSelection reports VISUAL indices when rich text is active.
            // Convert to raw string indices before splicing markup.
            int visMin = Mathf.Min(_cachedCursorIndex, _cachedSelectIndex);
            int visMax = Mathf.Max(_cachedCursorIndex, _cachedSelectIndex);
            int start, end;
            if (_richTextActive)
            {
                start = Mathf.Clamp(VisualToRawIndex(cur, visMin), 0, cur.Length);
                end   = Mathf.Clamp(VisualToRawIndex(cur, visMax), 0, cur.Length);
            }
            else
            {
                start = Mathf.Clamp(visMin, 0, cur.Length);
                end   = Mathf.Clamp(visMax, 0, cur.Length);
            }

            string result = cur[..start] + open + cur[start..end] + close + cur[end..];

            // Push current state to undo BEFORE the change.
            _undoStack.Add((cur, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _suppressUndo = true;
            _editorField.value = result;   // fires RegisterValueChangedCallback to sync model
            _suppressUndo = false;

            // New cursor sits after the closing tag in raw coords; convert to visual.
            int newRawPos = end + open.Length + close.Length;
            int newVisPos = _richTextActive
                ? RawToVisualIndex(result, newRawPos)
                : Mathf.Clamp(newRawPos, 0, result.Length);
            RestoreFocusAndCursor(newVisPos);
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
        private void ApplyRestoredValue(string text, int cursor)
        {
            _editValue = text;

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
                _editorField.SetValueWithoutNotify(_editValue);
                _suppressUndo = false;
                _lexiconRow.style.display = isLoc ? DisplayStyle.Flex : DisplayStyle.None;
                if (isLoc) _lexiconKeyField.SetValueWithoutNotify(_editKey);
            }
            else
            {
                _assetField.objectType = _editType switch {
                    OghamContentType.Audio  => typeof(AudioClip),
                    OghamContentType.Prefab => typeof(GameObject),
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
            var tags  = new List<string>();
            var guids = AssetDatabase.FindAssets("t:OghamData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<OghamData>(path);
                if (data == null) continue;
                foreach (var entry in data.Entries)
                    if (!string.IsNullOrEmpty(entry.TagPath) && !tags.Contains(entry.TagPath))
                        tags.Add(entry.TagPath);
            }
            tags.Sort(StringComparer.Ordinal);

            var menu = new GenericMenu();
            foreach (var tag in tags)
            {
                var cap = tag;
                var uri = $"ogham://{cap}";
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
            // Apply formatting toggles to the display text — markup generated here,
            // not stored in the display field during editing.
            string display = string.IsNullOrWhiteSpace(_linkDisplayText) ? "link" : _linkDisplayText;
            if (_linkUnderline)   display = $"<u>{display}</u>";
            if (_linkItalic)      display = $"<i>{display}</i>";
            if (_linkBold)        display = $"<b>{display}</b>";
            if (_linkColorActive) display = $"<color=#{ColorUtility.ToHtmlStringRGB(_activeColor)}>{display}</color>";
            var snippet = $"<link=\"{_linkTarget}\">{display}</link>";

            string prev = _editValue;

            string next;
            int cursorAfter;

            if (_editingLinkRawStart >= 0)
            {
                // Replacing an existing link span (possibly expanded by selection).
                int rawStart = Mathf.Clamp(_editingLinkRawStart, 0, prev.Length);
                int rawEnd   = Mathf.Clamp(_editingLinkRawEnd,   0, prev.Length);
                next = prev[..rawStart] + snippet + prev[rawEnd..];
                int newRawPos = rawStart + snippet.Length;
                cursorAfter = _richTextActive ? RawToVisualIndex(next, newRawPos) : newRawPos;
            }
            else if (_cachedCursorIndex != _cachedSelectIndex)
            {
                // Replace selected range with new link.
                int visMin = Mathf.Min(_cachedCursorIndex, _cachedSelectIndex);
                int visMax = Mathf.Max(_cachedCursorIndex, _cachedSelectIndex);
                int rawStart = _richTextActive
                    ? Mathf.Clamp(VisualToRawIndex(prev, visMin), 0, prev.Length)
                    : Mathf.Clamp(visMin, 0, prev.Length);
                int rawEnd = _richTextActive
                    ? Mathf.Clamp(VisualToRawIndex(prev, visMax), 0, prev.Length)
                    : Mathf.Clamp(visMax, 0, prev.Length);
                next = prev[..rawStart] + snippet + prev[rawEnd..];
                int newRawPos = rawStart + snippet.Length;
                cursorAfter = _richTextActive
                    ? RawToVisualIndex(next, newRawPos)
                    : Mathf.Clamp(newRawPos, 0, next.Length);
            }
            else
            {
                // No selection — insert at cursor position.
                int rawCursor = _richTextActive
                    ? Mathf.Clamp(VisualToRawIndex(prev, _cachedCursorIndex), 0, prev.Length)
                    : Mathf.Clamp(_cachedCursorIndex, 0, prev.Length);
                next = prev[..rawCursor] + snippet + prev[rawCursor..];
                int newRawPos = rawCursor + snippet.Length;
                cursorAfter = _richTextActive
                    ? RawToVisualIndex(next, newRawPos)
                    : Mathf.Clamp(newRawPos, 0, next.Length);
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
            var fw = EditorWindow.focusedWindow;
            if (fw != null && fw.GetType().Name.Contains("Color")) return;
            Commit();
        }

        private void Commit()
        {
            if (_closing) return;
            _closing         = true;
            _item.Type       = _editType;
            _item.Mode       = _editMode;
            _item.KeyOrValue = IsText && _editMode == LexiconLocMode.Localised ? _editKey : _editValue;
            _item.AssetRef   = _editAsset;
            _item.InvalidateHash();

            if (IsText && _editMode == LexiconLocMode.Localised && !string.IsNullOrWhiteSpace(_editKey))
            {
                var current = LexiconRegistry.ResolveString(LexiconRegistry.Hash(_editKey));
                if (current == null || _editValue != current)
                    LexiconSettingsProvider.UpsertStringEntry(_editKey, _editValue);
            }

            if (IsText) SaveColor();
            EditorUtility.SetDirty(_asset);
            _onCommit?.Invoke();
            Close();
        }

        private void Cancel() { _closing = true; Close(); }

        // Strips the <link=...>...</link> wrapper from the editing span, leaving
        // the inner display markup (bold, colour, etc.) in place.
        private void RemoveLink()
        {
            if (_editingLinkRawStart < 0) return;

            string prev  = _editValue;
            int rawStart = Mathf.Clamp(_editingLinkRawStart, 0, prev.Length);
            int rawEnd   = Mathf.Clamp(_editingLinkRawEnd,   0, prev.Length);

            // Strip the link span completely — remove all markup, leave plain text.
            string span   = prev[rawStart..rawEnd];
            string result = StripAllTags(span);
            string next   = prev[..rawStart] + result + prev[rawEnd..];

            _editValue = next;

            _undoStack.Add((prev, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(next);
            _suppressUndo = false;

            int newRawPos   = rawStart + result.Length;
            int cursorAfter = _richTextActive ? RawToVisualIndex(next, newRawPos) : newRawPos;

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

        // Finds the first <link="target">...</link> whose raw span overlaps [rawSelMin, rawSelMax).
        private static bool TryFindOverlappingLink(string raw, int rawSelMin, int rawSelMax,
            out string linkTarget, out string linkDisplayRaw, out int linkRawStart, out int linkRawEnd)
        {
            linkTarget = ""; linkDisplayRaw = ""; linkRawStart = -1; linkRawEnd = -1;
            int i = 0;
            while (i < raw.Length)
            {
                int open = raw.IndexOf('<', i);
                if (open < 0) break;
                int tagEnd = raw.IndexOf('>', open);
                if (tagEnd < 0) break;
                string tagInner = raw.Substring(open + 1, tagEnd - open - 1);
                if (!tagInner.StartsWith("link=", StringComparison.OrdinalIgnoreCase))
                { i = open + 1; continue; }

                string attr = tagInner.Substring(5);
                string target = (attr.Length > 0 && attr[0] == '"')
                    ? attr.Substring(1, Mathf.Max(0, attr.IndexOf('"', 1) - 1))
                    : attr;

                int contentStart = tagEnd + 1;
                int closeIdx = raw.IndexOf("</link>", contentStart, StringComparison.OrdinalIgnoreCase);
                if (closeIdx < 0) { i = tagEnd + 1; continue; }

                int spanEnd = closeIdx + 7;
                if (open < rawSelMax && spanEnd > rawSelMin)
                {
                    linkTarget     = target;
                    linkDisplayRaw = raw.Substring(contentStart, closeIdx - contentStart);
                    linkRawStart   = open;
                    linkRawEnd     = spanEnd;
                    return true;
                }
                i = spanEnd;
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

        // Strips <color=...> and </color> from the current selection (or whole string if no selection).
        private void StripColorFromSelection()
        {
            string prev = _editValue;

            int visMin = Mathf.Min(_cachedCursorIndex, _cachedSelectIndex);
            int visMax = Mathf.Max(_cachedCursorIndex, _cachedSelectIndex);
            bool hasSelection = visMin != visMax;

            string next;
            int newRawPos;
            if (hasSelection)
            {
                int rawMin = Mathf.Clamp(_richTextActive ? VisualToRawIndex(prev, visMin) : visMin, 0, prev.Length);
                int rawMax = Mathf.Clamp(_richTextActive ? VisualToRawIndex(prev, visMax) : visMax, 0, prev.Length);
                string stripped = StripTagsByName(prev[rawMin..rawMax], "color");
                next     = prev[..rawMin] + stripped + prev[rawMax..];
                newRawPos = rawMin + stripped.Length;
            }
            else
            {
                next     = StripTagsByName(prev, "color");
                int rawCursor = Mathf.Clamp(_richTextActive ? VisualToRawIndex(prev, _cachedCursorIndex) : _cachedCursorIndex, 0, prev.Length);
                newRawPos = Mathf.Clamp(rawCursor, 0, next.Length);
            }

            if (next == prev) return;

            _undoStack.Add((prev, _cachedCursorIndex));
            if (_undoStack.Count > UndoLimit) _undoStack.RemoveAt(0);
            _redoStack.Clear();

            _editValue = next;

            _suppressUndo = true;
            _editorField.SetValueWithoutNotify(next);
            _suppressUndo = false;

            int cursorAfter = _richTextActive ? RawToVisualIndex(next, newRawPos) : newRawPos;
            RestoreFocusAndCursor(cursorAfter);
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
