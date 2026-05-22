using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    // In-editor playback of the Ogham dialogue graph.
    public class OghamPlayWindow : EditorWindow
    {
        public static void Open(IEnumerable<OghamData> assets, string startTagPath = null)
        {
            var w = GetWindow<OghamPlayWindow>("Ogham Play");
            w._assets.Clear();
            w._assets.AddRange(assets);
            w.BuildTagList(startTagPath);
            w.ResetStory();
        }

        private readonly List<OghamData> _assets = new();
        private OghamStory _story;

        private StoryNode            _current;
        private Vector2              _stateScroll;
        private Vector2              _historyScroll;

        // Start-node dropdown
        private string[] _startTagDisplayNames = System.Array.Empty<string>();
        private string[] _startTagPaths        = System.Array.Empty<string>();
        private int      _startTagIdx          = 0;

        // Tag name lookup built from loaded assets (handles editor play mode where registry may be cold)
        private readonly Dictionary<ulong, string> _tagNames = new();

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

            _story   = new OghamStory(new GameplayTag(GameplayTagRegistry.Hash("Editor.Play")));
            _current = null;
            _tagNames.Clear();

            foreach (var asset in _assets)
            {
                if (asset == null) continue;
                _story.RegisterData(asset);
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

        private void HandleClosed(GameplayTag storyId, bool interrupted)
        {
            _current = null;
            Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (_startTagPaths.Length > 0)
                {
                    _startTagIdx = EditorGUILayout.Popup(_startTagIdx, _startTagDisplayNames,
                        EditorStyles.toolbarPopup);
                }
                else
                {
                    EditorGUILayout.LabelField("(no entries)", EditorStyles.toolbarButton);
                }

                if (GUILayout.Button("Start", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    TryStart();
                if (GUILayout.Button("Reset", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    ResetStory();
                if (GUILayout.Button("Close", EditorStyles.toolbarButton, GUILayout.Width(50)))
                    _story.Close(interrupted: true);
            }

            EditorGUILayout.Space(4f);

            if (_current == null)
            {
                EditorGUILayout.HelpBox("No active conversation. Select an entry and click Start.", MessageType.None);
            }
            else
            {
                DrawCurrentEntry();
                EditorGUILayout.Space(4f);
                DrawOptions();
            }

            EditorGUILayout.Space(6f);
            DrawStatePanel();
            EditorGUILayout.Space(4f);
            DrawHistoryPanel();
        }

        private void TryStart()
        {
            if (_startTagPaths.Length == 0) return;
            var path = _startTagPaths[_startTagIdx];
            var tag  = GameplayTag.FromName(path);
            if (!_story.Enter(tag))
                EditorUtility.DisplayDialog("Ogham Play", $"Entry tag not found: {path}", "OK");
        }

        private void DrawCurrentEntry()
        {
            EditorGUILayout.LabelField("Current Entry", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Tag", ResolveTagName(_current.Tag.Id));

            for (int i = 0; i < _current.ContentCount; i++)
            {
                var resolved = _current.GetText(i);
                var display  = string.IsNullOrEmpty(resolved) ? _current.GetRawKey(i) : resolved;
                if (!string.IsNullOrEmpty(display))
                    EditorGUILayout.LabelField(display, EditorStyles.wordWrappedLabel);
            }
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            if (_current.Options.Count == 0)
            {
                EditorGUILayout.LabelField("(no available options)", EditorStyles.miniLabel);
                return;
            }

            foreach (var opt in _current.Options)
            {
                var label = opt.GetText();
                if (string.IsNullOrEmpty(label))
                    label = opt.Tag.IsValid ? ResolveTagName(opt.Tag.Id) : "Option";

                if (GUILayout.Button(label))
                {
                    _story.Choose(opt.Tag);
                    GUIUtility.ExitGUI();
                }
            }
        }

        private void DrawStatePanel()
        {
            EditorGUILayout.LabelField("Narrative State", EditorStyles.boldLabel);
            _stateScroll = EditorGUILayout.BeginScrollView(_stateScroll, GUILayout.Height(80f));
            foreach (var (tag, value) in _story.NarrativeState.GetAll())
                EditorGUILayout.LabelField(ResolveTagName(tag.Id), value.ToString(), EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
        }

        private void DrawHistoryPanel()
        {
            EditorGUILayout.LabelField("History", EditorStyles.boldLabel);
            _historyScroll = EditorGUILayout.BeginScrollView(_historyScroll, GUILayout.Height(80f));
            var history = _story.History;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                var h       = history[i];
                var eName   = ResolveTagName(h.EntryId);
                var optName = h.SelectedOption == 0 ? "(closed)" : ResolveTagName(h.SelectedOption);

                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{eName} → {optName}", EditorStyles.miniLabel);
                    if (GUILayout.Button("Return To", EditorStyles.miniButton, GUILayout.Width(68)))
                    {
                        _story.ReturnTo(new GameplayTag(h.EntryId));
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private string ResolveTagName(ulong id)
        {
            if (_tagNames.TryGetValue(id, out var name)) return name;
            var reg = GameplayTagRegistry.GetName(id);
            return reg ?? id.ToString("X16");
        }
    }
}
