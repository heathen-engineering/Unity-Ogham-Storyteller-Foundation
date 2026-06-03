using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // VO Script Export window.
    // Open via the "Export…" button in the Ogham graph editor toolbar.
    internal class OghamExportWindow : EditorWindow
    {
        // ── Mode ──────────────────────────────────────────────────────────────
        private enum ExportMode { All, Selected, Pick }
        private ExportMode _mode = ExportMode.All;

        // ── Source data (injected on Open) ────────────────────────────────────
        private List<OghamData>          _assets;
        private List<OghamGraphMetadata> _metas;
        private HashSet<string>          _selectedTags;

        // ── Pick mode state ───────────────────────────────────────────────────
        private readonly Dictionary<string, bool> _pickState = new();
        private Vector2 _pickScroll;

        // ── Format + options ──────────────────────────────────────────────────
        private OghamExportFormat _format           = OghamExportFormat.CSV;
        private bool              _stripTrailing    = true;
        private bool              _listOptions      = true;
        private bool              _removeFormatting = true;

        // ── Culture / helex ───────────────────────────────────────────────────
        private string _helexPath = "";
        private List<(string path, string displayName)> _helexFiles = new();

        // ── ─────────────────────────────────────────────────────────────────

        internal static void Open(
            List<OghamData>          assets,
            List<OghamGraphMetadata> metas,
            IEnumerable<string>      selectedTags)
        {
            var w = GetWindow<OghamExportWindow>(true, "Export VO Script", true);
            w.minSize       = new Vector2(380f, 520f);
            w._assets       = assets  ?? new List<OghamData>();
            w._metas        = metas   ?? new List<OghamGraphMetadata>();
            w._selectedTags = new HashSet<string>(selectedTags ?? Enumerable.Empty<string>());
            w.RefreshHelexList();
            w.RebuildPickState();

            var defHelex = w._helexFiles.FirstOrDefault(h =>
                h.displayName.Equals("Default", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(defHelex.path))
                w._helexPath = defHelex.path;

            w.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Export VO Script", EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            DrawFormatSelector();
            EditorGUILayout.Space(6);
            DrawHelexPicker();
            EditorGUILayout.Space(6);
            DrawOptions();
            EditorGUILayout.Space(6);
            DrawModeSelector();
            EditorGUILayout.Space(4);
            if (_mode == ExportMode.Pick) DrawPickList();

            GUILayout.FlexibleSpace();
            DrawExportButton();
            EditorGUILayout.Space(6);
        }

        // ── Format selector ───────────────────────────────────────────────────

        private static readonly string[] s_FormatLabels = { "CSV (.csv)", "Markdown (.md)", "HTML (.html)", "Plain Text (.txt)" };
        private static readonly OghamExportFormat[] s_Formats =
        {
            OghamExportFormat.CSV, OghamExportFormat.Markdown,
            OghamExportFormat.HTML, OghamExportFormat.PlainText,
        };
        private static readonly string[] s_Extensions = { "csv", "md", "html", "txt" };

        private void DrawFormatSelector()
        {
            EditorGUILayout.LabelField("Output Format", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < s_Formats.Length; i++)
                {
                    var style = i == 0 ? EditorStyles.miniButtonLeft
                              : i == s_Formats.Length - 1 ? EditorStyles.miniButtonRight
                              : EditorStyles.miniButtonMid;
                    if (GUILayout.Toggle(_format == s_Formats[i], s_FormatLabels[i], style, GUILayout.Height(20)))
                        _format = s_Formats[i];
                }
            }

            if (_format == OghamExportFormat.Markdown)
                EditorGUILayout.HelpBox(
                    "Markdown preserves *italic* and **bold** from content keys — consider disabling \"Remove content formatting\" to keep them.",
                    MessageType.None);
        }

        // ── Culture / helex picker ────────────────────────────────────────────

        private void DrawHelexPicker()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Culture File", GUILayout.Width(90));
                int current = _helexFiles.FindIndex(h => h.path == _helexPath);
                var names   = _helexFiles.Select(h => h.displayName).Prepend("None").ToArray();
                int sel     = EditorGUILayout.Popup(current + 1, names) - 1;
                _helexPath  = sel >= 0 ? _helexFiles[sel].path : "";
            }
        }

        // ── Options ───────────────────────────────────────────────────────────

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("Options", EditorStyles.boldLabel);
            _stripTrailing = EditorGUILayout.ToggleLeft(
                new GUIContent("Strip trailing navigation links",
                    "Removes [← Back] and similar links that appear at the very end of a content key, " +
                    "after all spoken text. Links embedded inside spoken text are kept."),
                _stripTrailing);
            _listOptions = EditorGUILayout.ToggleLeft(
                new GUIContent("List player options",
                    "Appends an Options section to each node showing the player choices that follow. " +
                    "Useful for director context; disable for a narrator-only script."),
                _listOptions);
            _removeFormatting = EditorGUILayout.ToggleLeft(
                new GUIContent("Remove content formatting",
                    "Strips markdown marks (* _ ` ~~ **) and non-speakable characters (→ ← · — …) from content text. " +
                    "Recommended for CSV/HTML/TXT. Disable for Markdown to preserve rendering."),
                _removeFormatting);
        }

        // ── Mode selector ─────────────────────────────────────────────────────

        private void DrawModeSelector()
        {
            EditorGUILayout.LabelField("Nodes to export", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Toggle(_mode == ExportMode.All,      "All",      EditorStyles.miniButtonLeft,  GUILayout.Height(20))) _mode = ExportMode.All;
                if (GUILayout.Toggle(_mode == ExportMode.Selected, "Selected", EditorStyles.miniButtonMid,   GUILayout.Height(20))) _mode = ExportMode.Selected;
                if (GUILayout.Toggle(_mode == ExportMode.Pick,     "Pick",     EditorStyles.miniButtonRight, GUILayout.Height(20))) _mode = ExportMode.Pick;
            }

            if (_mode == ExportMode.Selected)
            {
                int count = _selectedTags?.Count ?? 0;
                EditorGUILayout.HelpBox(
                    count == 0
                        ? "No nodes are currently selected in the graph editor."
                        : $"{count} node{(count == 1 ? "" : "s")} selected.",
                    count == 0 ? MessageType.Warning : MessageType.None);
            }
        }

        // ── Pick list ─────────────────────────────────────────────────────────

        private void DrawPickList()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Choose nodes:", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("All",  EditorStyles.miniButton, GUILayout.Width(34)))
                    foreach (var k in _pickState.Keys.ToList()) _pickState[k] = true;
                if (GUILayout.Button("None", EditorStyles.miniButton, GUILayout.Width(38)))
                    foreach (var k in _pickState.Keys.ToList()) _pickState[k] = false;
            }

            _pickScroll = EditorGUILayout.BeginScrollView(_pickScroll, GUILayout.MaxHeight(180));
            foreach (var key in _pickState.Keys.ToList())
            {
                bool prev = _pickState[key];
                bool next = EditorGUILayout.ToggleLeft(key, prev);
                if (next != prev) _pickState[key] = next;
            }
            EditorGUILayout.EndScrollView();
        }

        // ── Export button ─────────────────────────────────────────────────────

        private void DrawExportButton()
        {
            var nodesToExport = GatherNodes();
            bool canExport    = nodesToExport.Count > 0;

            EditorGUI.BeginDisabledGroup(!canExport);
            int fmtLabelIdx = Array.IndexOf(s_Formats, _format);
            var fmtLabel    = fmtLabelIdx >= 0 ? s_FormatLabels[fmtLabelIdx] : _format.ToString();
            if (GUILayout.Button(
                canExport ? $"Export {fmtLabel}  ({nodesToExport.Count} nodes)" : "Export",
                GUILayout.Height(28)))
                DoExport(nodesToExport);
            EditorGUI.EndDisabledGroup();
        }

        // ── Core export ───────────────────────────────────────────────────────

        private void DoExport(List<(DialogueEntry entry, OghamNodeMeta meta)> nodes)
        {
            int fmtI = Array.IndexOf(s_Formats, _format);
            var ext  = fmtI >= 0 ? s_Extensions[fmtI] : "csv";
            var savePath = EditorUtility.SaveFilePanel("Export VO Script", "", "VOScript", ext);
            if (string.IsNullOrEmpty(savePath)) return;

            var metaLookup = new Dictionary<string, OghamNodeMeta>();
            foreach (var (_, meta) in nodes)
                if (meta != null && !string.IsNullOrEmpty(meta.TagName))
                    metaLookup[meta.TagName] = meta;

            Func<string, string> resolveKey = null;
            if (!string.IsNullOrEmpty(_helexPath) && File.Exists(_helexPath))
            {
                var lookup = BuildHelexLookup(_helexPath);
                resolveKey = key =>
                    lookup.TryGetValue(key ?? "", out var val) && !string.IsNullOrEmpty(val) ? val : key;
            }

            // <<>> is ambiguous in Markdown (parsed as HTML) and HTML (even with encoding it's fragile).
            // Use {} for those formats; << >> for plain text formats where it's unambiguous.
            bool useCurly = _format == OghamExportFormat.Markdown
                         || _format == OghamExportFormat.HTML;

            var opts = new OghamScriptExporter.ExportOptions
            {
                Format             = _format,
                StripTrailingLinks = _stripTrailing,
                ListOptions        = _listOptions,
                RemoveFormatting   = _removeFormatting,
                ContentLabels      = OghamStorytellerMetadata.GetOrCreate().ContentLabels,
                ResolveKey         = resolveKey,
                StateVarOpen       = useCurly ? "{" : "<<",
                StateVarClose      = useCurly ? "}" : ">>",
            };

            var output = OghamScriptExporter.Export(nodes.Select(n => n.entry), metaLookup, opts);
            File.WriteAllText(savePath, output, System.Text.Encoding.UTF8);
            EditorUtility.RevealInFinder(savePath);
            Debug.Log($"[Ogham] Exported VO script ({_format}): {savePath}  ({nodes.Count} nodes)");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private List<(DialogueEntry entry, OghamNodeMeta meta)> GatherNodes()
        {
            var result = new List<(DialogueEntry, OghamNodeMeta)>();
            for (int i = 0; i < _assets.Count; i++)
            {
                var asset = _assets[i];
                var meta  = i < _metas.Count ? _metas[i] : null;

                foreach (var entry in asset.Entries)
                {
                    if (entry.Mode == OghamNodeMode.Fork) continue;
                    switch (_mode)
                    {
                        case ExportMode.All: break;
                        case ExportMode.Selected:
                            if (!_selectedTags.Contains(entry.TagPath)) continue;
                            break;
                        case ExportMode.Pick:
                            if (!_pickState.TryGetValue(entry.TagPath, out var picked) || !picked) continue;
                            break;
                    }
                    var nm = meta?.Nodes.FirstOrDefault(n => n.TagName == entry.TagPath);
                    result.Add((entry, nm));
                }
            }
            return result;
        }

        private void RebuildPickState()
        {
            _pickState.Clear();
            if (_assets == null) return;
            foreach (var asset in _assets)
                foreach (var entry in asset.Entries)
                    if (entry.Mode != OghamNodeMode.Fork && !string.IsNullOrEmpty(entry.TagPath))
                        _pickState[entry.TagPath] = true;
        }

        private void RefreshHelexList()
        {
            _helexFiles = new List<(string, string)>();
            foreach (var guid in AssetDatabase.FindAssets("t:TextAsset"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".helex", StringComparison.OrdinalIgnoreCase))
                    _helexFiles.Add((path, Path.GetFileNameWithoutExtension(path)));
            }
            _helexFiles.Sort((a, b) => string.Compare(a.displayName, b.displayName, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, string> BuildHelexLookup(string helexPath)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                var root = JObject.Parse(File.ReadAllText(helexPath));
                if (root["entries"] is JObject entries)
                    foreach (var prop in entries.Properties())
                    {
                        var key = prop.Name?.Trim();
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (prop.Value.Type == JTokenType.String)
                            result[key] = prop.Value.Value<string>() ?? "";
                    }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Ogham] Could not read helex file '{helexPath}': {e.Message}");
            }
            return result;
        }
    }
}
