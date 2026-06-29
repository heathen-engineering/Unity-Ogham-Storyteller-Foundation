using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Modal in-editor playback window for the Ogham dialogue graph. Blocks interaction with other editor windows
    /// via <c>ShowModal()</c>. Allows the author to traverse the story graph, inspect narrative state, and review
    /// conversation history without entering Play mode.
    /// </summary>
    public class OghamPlayWindow : EditorWindow
    {
        /// <summary>True when a test-play window is currently open.</summary>
        public static bool IsOpen => Resources.FindObjectsOfTypeAll<OghamPlayWindow>().Length > 0;

        /// <summary>Closes any open test-play window.</summary>
        public static void CloseIfOpen()
        {
            foreach (var w in Resources.FindObjectsOfTypeAll<OghamPlayWindow>())
                w.Close();
        }

        /// <summary>
        /// Opens the test-play window — a unit-test-style runner that steps the story logic on the live source
        /// (no build, no game simulation) — loading the given assets and optionally starting at
        /// <paramref name="startTagPath"/>. Non-modal, so it runs alongside the graph.
        /// </summary>
        /// <param name="assets">The authoring assets to load into the play session.</param>
        /// <param name="startTagPath">The dot-path tag of the entry to pre-select. <c>null</c> means no pre-selection.</param>
        public static void Open(IEnumerable<OghamData> assets, string startTagPath = null)
        {
            CloseIfOpen(); // one runner at a time
            var w = CreateInstance<OghamPlayWindow>();
            w.titleContent = new GUIContent("Ogham Test Play");
            w._assets.Clear();
            w._assets.AddRange(assets);
            w.BuildTagList(startTagPath);
            w.ResetStory();
            w.minSize = new Vector2(700f, 520f);
            w.Show();
        }

        private readonly List<OghamData> _assets = new();
        private OghamStory   _definition;
        private OghamSession _story;

        private StoryNode _current;
        private Vector2   _stateScroll;
        private Vector2   _historyScroll;
        private Vector2   _contentScroll;

        // Option tag paths that appear as inline links in the current content —
        // populated during DrawCurrentEntry, consumed by DrawOptions to avoid duplication.
        private readonly HashSet<string> _inlineLinkedOptions = new();

        private string[] _startTagDisplayNames = System.Array.Empty<string>();
        private string[] _startTagPaths        = System.Array.Empty<string>();
        private int      _startTagIdx;

        private readonly Dictionary<ulong, string> _tagNames = new();

        // ── Styles ─────────────────────────────────────────────────────────────
        private GUIStyle _richTextStyle;
        private GUIStyle _linkStyle;
        private GUIStyle _panelHeaderStyle;
        private bool     _stylesBuilt;

        private void EnsureStyles()
        {
            if (_stylesBuilt) return;
            _stylesBuilt = true;

            _richTextStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 13,
                richText  = true,
                wordWrap  = true,
                alignment = TextAnchor.UpperLeft,
                normal    = { textColor = EditorStyles.label.normal.textColor },
            };
            // Link style — no background, coloured bold text used as a Button style.
            _linkStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize  = 13,
                richText  = false,
                wordWrap  = false,
                fontStyle = FontStyle.Bold,
                normal    = { textColor = new Color(0.35f, 0.75f, 1.00f), background = null },
                hover     = { textColor = new Color(0.60f, 0.90f, 1.00f), background = null },
                active    = { textColor = Color.white,                     background = null },
                focused   = { textColor = new Color(0.35f, 0.75f, 1.00f), background = null },
            };
            _panelHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            BuildTagList(null);
            ResetStory();
        }

        private void OnDisable()
        {
            if (_story != null)
            {
                _story.OnEntered -= HandleEntered;
                _story.OnClosed  -= HandleClosed;
                _story = null;
            }
        }

        private void BuildTagList(string preselect)
        {
            var paths = new List<string>();
            foreach (var asset in _assets)
                if (asset != null)
                    foreach (var entry in asset.Entries)
                        if (!string.IsNullOrEmpty(entry.TagPath) && !paths.Contains(entry.TagPath))
                            paths.Add(entry.TagPath);

            paths.Sort(System.StringComparer.Ordinal);
            _startTagPaths        = paths.ToArray();
            _startTagDisplayNames = paths.Select(p => {
                var dot = p.LastIndexOf('.');
                return dot >= 0 ? p.Substring(dot + 1) + $"  ({p})" : p;
            }).ToArray();

            _startTagIdx = 0;
            if (!string.IsNullOrEmpty(preselect))
            {
                int idx = System.Array.IndexOf(_startTagPaths, preselect);
                if (idx >= 0) _startTagIdx = idx;
            }
        }

        private void ResetStory()
        {
            if (_story != null)
            {
                _story.OnEntered -= HandleEntered;
                _story.OnClosed  -= HandleClosed;
            }
            _definition = new OghamStory(new GameplayTag(GameplayTagRegistry.Hash("Editor.Play")));
            _story      = _definition.CreateSession();
            _current    = null;
            _tagNames.Clear();

            foreach (var asset in _assets)
            {
                if (asset == null) continue;
                _definition.RegisterData(asset);
                foreach (var entry in asset.Entries)
                {
                    if (entry.Tag.IsValid) _tagNames[entry.Tag.Id] = entry.TagPath;
                    foreach (var opt in entry.Options)
                        if (opt.Tag.IsValid) _tagNames[opt.Tag.Id] = opt.TagPath;
                }
            }
            _story.OnEntered += HandleEntered;
            _story.OnClosed  += HandleClosed;
        }

        private void HandleEntered(GameplayTag storyId, StoryNode node)
        {
            _current = node;
            Repaint();
        }

        private void HandleClosed(GameplayTag storyId)
        {
            _current = null;
            Repaint();
        }

        // ── OnGUI ──────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EnsureStyles();

            // Toolbar
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_startTagPaths.Length > 0)
                    _startTagIdx = EditorGUILayout.Popup(_startTagIdx, _startTagDisplayNames,
                        EditorStyles.toolbarPopup);
                else
                    EditorGUILayout.LabelField("(no entries)", EditorStyles.toolbarButton);

                if (GUILayout.Button("Start", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    TryStart();
                if (GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    { ResetStory(); Repaint(); }
                if (GUILayout.Button("Close", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    { _story?.Close(); Close(); }
            }

            // Three-column body: [State | Content | History]
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            DrawStatePanel();
            DrawVerticalSep();

            // ── Center: current dialogue ──────────────────────────────────────
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            _contentScroll = GUILayout.BeginScrollView(_contentScroll,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.Space(8f);

            if (_current == null)
            {
                GUILayout.FlexibleSpace();
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    "No active conversation.\nSelect an entry above and press Start.",
                    _richTextStyle, GUILayout.MaxWidth(320f));
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }
            else
            {
                _inlineLinkedOptions.Clear();
                DrawCurrentEntry();
                GUILayout.Space(12f);
                DrawOptions();
                GUILayout.Space(8f);
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            // ─────────────────────────────────────────────────────────────────

            DrawVerticalSep();
            DrawHistoryPanel();

            GUILayout.EndHorizontal();
        }

        private void TryStart()
        {
            if (_startTagPaths.Length == 0) return;
            var tag = GameplayTag.FromName(_startTagPaths[_startTagIdx]);
            if (!_story.Enter(tag))
                EditorUtility.DisplayDialog("Ogham Play",
                    $"Entry tag not found: {_startTagPaths[_startTagIdx]}", "OK");
        }

        // ── Content rendering ──────────────────────────────────────────────────

        private void DrawCurrentEntry()
        {
            GUILayout.Label(ResolveTagName(_current.Tag.Id), _panelHeaderStyle);
            GUILayout.Space(4f);

            for (int i = 0; i < _current.ContentCount; i++)
            {
                var text = _current.GetText(i) ?? _current.GetRawKey(i) ?? "";
                text = OghamLinkFormatter.InterpolateState(text, _story.NarrativeState);
                if (!string.IsNullOrEmpty(text))
                    DrawFormattedContent(text);
            }
        }

        private void DrawFormattedContent(string rawText)
        {
            foreach (var rawLine in rawText.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) { GUILayout.Space(6f); continue; }
                DrawFormattedLine(line);
            }
        }

        private void DrawFormattedLine(string line)
        {
            var matches = OghamInlineLinkParser.LinkRx.Matches(line);

            // Quick check: any Ogham scheme links on this line?
            bool hasOgham = false;
            foreach (Match m in matches)
                if (m.Groups[2].Success && OghamInlineLinkParser.IsOghamLink(m.Groups[2].Value.Trim()))
                { hasOgham = true; break; }

            if (!hasOgham)
            {
                // Plain formatted text — convert **bold** / *italic* and strip link wrappers.
                GUILayout.Label(FormatForIMGUI(line), _richTextStyle);
                return;
            }

            // Mixed line with inline Ogham links — render segment by segment.
            GUILayout.BeginHorizontal();
            int pos = 0;

            foreach (Match m in matches)
            {
                if (m.Index > pos)
                {
                    var before = FormatForIMGUI(line.Substring(pos, m.Index - pos));
                    if (!string.IsNullOrEmpty(before))
                        GUILayout.Label(before, _richTextStyle, GUILayout.ExpandWidth(false));
                }

                var display = m.Groups[1].Value;
                var rawTag  = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";

                if (OghamInlineLinkParser.IsOghamLink(rawTag))
                {
                    var tagPath = OghamInlineLinkParser.GetTagPath(rawTag);
                    _inlineLinkedOptions.Add(tagPath); // tell DrawOptions to skip this one

                    var opt = FindOptionByTagPath(tagPath);
                    if (opt != null)
                    {
                        // Available option — render as a clickable coloured link.
                        if (GUILayout.Button(display, _linkStyle, GUILayout.ExpandWidth(false)))
                        {
                            _story.Choose(opt.Tag);
                            GUIUtility.ExitGUI();
                        }
                    }
                    else
                    {
                        // Option exists in text but isn't available (conditions not met).
                        var saved = GUI.color;
                        GUI.color = new Color(saved.r, saved.g, saved.b, saved.a * 0.35f);
                        GUILayout.Label(display, _linkStyle, GUILayout.ExpandWidth(false));
                        GUI.color = saved;
                    }
                }
                else
                {
                    // Non-Ogham link — show display text only.
                    GUILayout.Label(display, _richTextStyle, GUILayout.ExpandWidth(false));
                }

                pos = m.Index + m.Length;
            }

            if (pos < line.Length)
            {
                var after = FormatForIMGUI(line.Substring(pos));
                if (!string.IsNullOrEmpty(after))
                    GUILayout.Label(after, _richTextStyle, GUILayout.ExpandWidth(false));
            }

            GUILayout.EndHorizontal();
        }

        // Converts Ogham authoring markdown to IMGUI-compatible rich text.
        // **bold** → <b>bold</b>, *italic* → <i>italic</i>
        // Non-Ogham [display](url) links are stripped to display text.
        private static string FormatForIMGUI(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = OghamInlineLinkParser.BoldRx.Replace(text,   "<b>$1</b>");
            text = OghamInlineLinkParser.ItalicRx.Replace(text, "<i>$1</i>");
            text = OghamInlineLinkParser.LinkRx.Replace(text, m => m.Groups[1].Value);
            return text;
        }

        private StoryOption FindOptionByTagPath(string tagPath)
        {
            if (_current == null) return null;
            foreach (var opt in _current.Options)
            {
                if (!opt.Tag.IsValid) continue;
                if (_tagNames.TryGetValue(opt.Tag.Id, out var tp) && tp == tagPath) return opt;
                if (GameplayTagRegistry.GetName(opt.Tag.Id) == tagPath) return opt;
            }
            return null;
        }

        private void DrawOptions()
        {
            // Skip options already rendered as clickable inline links to avoid duplication.
            var remaining = _current.Options
                .Where(o => {
                    if (!o.Tag.IsValid) return true;
                    return !(_tagNames.TryGetValue(o.Tag.Id, out var tp) &&
                             _inlineLinkedOptions.Contains(tp));
                })
                .ToList();

            if (remaining.Count == 0) return;

            GUILayout.Label("Options", _panelHeaderStyle);
            GUILayout.Space(2f);
            foreach (var opt in remaining)
            {
                var label = OghamLinkFormatter.InterpolateState(opt.GetText(), _story.NarrativeState);
                if (string.IsNullOrEmpty(label))
                    label = opt.Tag.IsValid ? ResolveTagName(opt.Tag.Id) : "Option";

                if (GUILayout.Button(label))
                {
                    _story.Choose(opt.Tag);
                    GUIUtility.ExitGUI();
                }
            }
        }

        // ── Side panels ────────────────────────────────────────────────────────

        private void DrawStatePanel()
        {
            GUILayout.BeginVertical(GUILayout.Width(180f), GUILayout.ExpandHeight(true));
            GUILayout.Label("Narrative State", _panelHeaderStyle);
            DrawHorizontalSep();
            _stateScroll = GUILayout.BeginScrollView(_stateScroll, GUILayout.ExpandHeight(true));
            if (_story != null)
                foreach (var (tag, value) in _story.NarrativeState.GetAll())
                    EditorGUILayout.LabelField(ResolveTagName(tag.Id), value.ToString(),
                        EditorStyles.miniLabel);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawHistoryPanel()
        {
            GUILayout.BeginVertical(GUILayout.Width(210f), GUILayout.ExpandHeight(true));
            GUILayout.Label("History", _panelHeaderStyle);
            DrawHorizontalSep();
            _historyScroll = GUILayout.BeginScrollView(_historyScroll, GUILayout.ExpandHeight(true));
            if (_story != null)
            {
                var history = _story.History;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    var h       = history[i];
                    var eName   = ResolveTagName(h.EntryId);
                    var optName = h.SelectedOption == 0 ? "(closed)" : ResolveTagName(h.SelectedOption);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"{eName} → {optName}", EditorStyles.miniLabel,
                        GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("↩", EditorStyles.miniButton, GUILayout.Width(22f)))
                    {
                        _story.ReturnTo(new GameplayTag(h.EntryId));
                        GUIUtility.ExitGUI();
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        // ── Separators ─────────────────────────────────────────────────────────

        private static void DrawVerticalSep()
        {
            var r = GUILayoutUtility.GetRect(1f, 1f,
                GUILayout.Width(1f), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(r, new Color(0.22f, 0.22f, 0.22f));
        }

        private static void DrawHorizontalSep()
        {
            var r = GUILayoutUtility.GetRect(1f, 1f,
                GUILayout.ExpandWidth(true), GUILayout.Height(1f));
            EditorGUI.DrawRect(r, new Color(0.28f, 0.28f, 0.28f));
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private string ResolveTagName(ulong id)
        {
            if (_tagNames.TryGetValue(id, out var name)) return name;
            var reg = GameplayTagRegistry.GetName(id);
            return reg ?? id.ToString("X16");
        }
    }
}
