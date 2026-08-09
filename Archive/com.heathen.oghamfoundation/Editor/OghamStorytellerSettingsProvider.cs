using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project Settings page for the Ogham Storyteller. Registered under <c>Project/Ogham Storyteller</c>.
    /// Edits the VO export content labels and the graph editor preferences, both persisted as JSON in
    /// ProjectSettings via the Game Framework settings store.
    /// </summary>
    public class OghamStorytellerSettingsProvider : SettingsProvider
    {
        private OghamStorytellerMetadata _meta;
        private OghamEditorSettings      _editor;
        private Vector2 _scroll;

        private static GUIStyle s_LabelStyle;
        private static GUIStyle LabelStyle => s_LabelStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
        };

        /// <summary>Registers this provider in the Project Settings window under <c>Project/Subsystems/Ogham Storyteller</c>.</summary>
        [SettingsProvider]
        public static SettingsProvider Create() =>
            new OghamStorytellerSettingsProvider("Project/Subsystems/Ogham Storyteller", SettingsScope.Project)
            {
                keywords = new HashSet<string>(
                    new[] { "ogham", "storyteller", "voice", "over", "script", "labels", "link", "heathen" }),
            };

        /// <summary>Initialises the settings provider at the given path and scope.</summary>
        public OghamStorytellerSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        /// <inheritdoc/>
        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            _meta   = OghamStorytellerMetadata.GetOrCreate();
            _editor = OghamEditorSettings.GetOrCreate();
        }

        /// <inheritdoc/>
        public override void OnGUI(string searchContext)
        {
            _meta   ??= OghamStorytellerMetadata.GetOrCreate();
            _editor ??= OghamEditorSettings.GetOrCreate();

            DrawContentLabels();
            EditorGUILayout.Space(12);
            DrawGraphEditor();
        }

        // ── VO export content labels ────────────────────────────────────────────

        private void DrawContentLabels()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Ogham Storyteller — VO Export Settings", LabelStyle);
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Content Labels define the row header for each content key slot in exported VO scripts.\n" +
                "Label 1 → Content Key 0,  Label 2 → Content Key 1, and so on.\n" +
                "These labels also appear as read-only badges in the graph editor node keys section.",
                MessageType.None);
            EditorGUILayout.Space(6);

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                EditorGUILayout.LabelField("Content Labels", EditorStyles.whiteLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(240));

            var labels   = _meta.ContentLabels;
            int removeAt = -1;
            int moveUp   = -1;
            int moveDown = -1;

            for (int i = 0; i < labels.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{i + 1}.", GUILayout.Width(22));
                    EditorGUI.BeginChangeCheck();
                    var newVal = EditorGUILayout.TextField(labels[i] ?? "", GUILayout.ExpandWidth(true));
                    if (EditorGUI.EndChangeCheck())
                    {
                        labels[i] = newVal;
                        _meta.Save();
                    }

                    EditorGUI.BeginDisabledGroup(i == 0);
                    if (GUILayout.Button("▲", EditorStyles.miniButton, GUILayout.Width(22))) moveUp = i;
                    EditorGUI.EndDisabledGroup();

                    EditorGUI.BeginDisabledGroup(i == labels.Count - 1);
                    if (GUILayout.Button("▼", EditorStyles.miniButton, GUILayout.Width(22))) moveDown = i;
                    EditorGUI.EndDisabledGroup();

                    if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(22))) removeAt = i;
                }
            }

            EditorGUILayout.EndScrollView();

            if (removeAt >= 0)
            {
                labels.RemoveAt(removeAt);
                _meta.Save();
            }
            if (moveUp > 0)
            {
                (labels[moveUp], labels[moveUp - 1]) = (labels[moveUp - 1], labels[moveUp]);
                _meta.Save();
            }
            if (moveDown >= 0 && moveDown < labels.Count - 1)
            {
                (labels[moveDown], labels[moveDown + 1]) = (labels[moveDown + 1], labels[moveDown]);
                _meta.Save();
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Label", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    labels.Add($"Key {labels.Count + 1}");
                    _meta.Save();
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Director Notes are per-node. Right-click any node in the graph editor and choose " +
                "\"Edit Director Notes…\" to add guidance for the VO director.",
                MessageType.None);
        }

        // ── Graph editor preferences ────────────────────────────────────────────

        private void DrawGraphEditor()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                EditorGUILayout.LabelField("Graph Editor", EditorStyles.whiteLabel);

            EditorGUI.BeginChangeCheck();
            var color     = EditorGUILayout.ColorField("Default Link Colour", _editor.DefaultLinkColor);
            var underline = EditorGUILayout.Toggle("Default Link Underline", _editor.DefaultLinkUnderline);
            if (EditorGUI.EndChangeCheck())
            {
                _editor.DefaultLinkColor     = color;
                _editor.DefaultLinkUnderline = underline;
                _editor.Save();
            }
        }
    }
}
