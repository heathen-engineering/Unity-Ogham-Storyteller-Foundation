using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project Settings page for the Ogham Storyteller VO export configuration. Registered under
    /// <c>Project/Ogham Storyteller</c>. Allows the user to define and reorder content label names and
    /// access director notes guidance.
    /// </summary>
    public class OghamStorytellerSettingsProvider : SettingsProvider
    {
        private OghamStorytellerMetadata _meta;
        private Vector2 _scroll;

        private static GUIStyle s_LabelStyle;
        private static GUIStyle LabelStyle => s_LabelStyle ??= new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize  = 11,
            alignment = TextAnchor.MiddleLeft,
        };

        /// <summary>Registers this provider in the Project Settings window under <c>Project/Ogham Storyteller</c>.</summary>
        /// <returns>A new <see cref="OghamStorytellerSettingsProvider"/> registered at the expected path.</returns>
        [SettingsProvider]
        public static SettingsProvider Create() =>
            new OghamStorytellerSettingsProvider("Project/Ogham Storyteller", SettingsScope.Project)
            {
                keywords = new System.Collections.Generic.HashSet<string>(
                    new[] { "ogham", "storyteller", "voice", "over", "script", "labels", "heathen" }),
            };

        /// <summary>
        /// Initialises the settings provider at the given path and scope.
        /// </summary>
        /// <param name="path">The settings window path, e.g. "Project/Ogham Storyteller".</param>
        /// <param name="scope">The scope of these settings (Project or User).</param>
        public OghamStorytellerSettingsProvider(string path, SettingsScope scope)
            : base(path, scope) { }

        /// <inheritdoc/>
        public override void OnActivate(string searchContext, UnityEngine.UIElements.VisualElement rootElement)
        {
            _meta = OghamStorytellerMetadata.GetOrCreate();
        }

        /// <inheritdoc/>
        public override void OnGUI(string searchContext)
        {
            if (_meta == null) _meta = OghamStorytellerMetadata.GetOrCreate();

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

            var labels = _meta.ContentLabels;
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
                        Undo.RecordObject(_meta, "Edit Content Label");
                        labels[i] = newVal;
                        EditorUtility.SetDirty(_meta);
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
                Undo.RecordObject(_meta, "Remove Content Label");
                labels.RemoveAt(removeAt);
                EditorUtility.SetDirty(_meta);
            }
            if (moveUp > 0)
            {
                Undo.RecordObject(_meta, "Reorder Content Label");
                (labels[moveUp], labels[moveUp - 1]) = (labels[moveUp - 1], labels[moveUp]);
                EditorUtility.SetDirty(_meta);
            }
            if (moveDown >= 0 && moveDown < labels.Count - 1)
            {
                Undo.RecordObject(_meta, "Reorder Content Label");
                (labels[moveDown], labels[moveDown + 1]) = (labels[moveDown + 1], labels[moveDown]);
                EditorUtility.SetDirty(_meta);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Add Label", EditorStyles.miniButton, GUILayout.Width(80)))
                {
                    Undo.RecordObject(_meta, "Add Content Label");
                    labels.Add($"Key {labels.Count + 1}");
                    EditorUtility.SetDirty(_meta);
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Director Notes are per-node. Right-click any node in the graph editor and choose " +
                "\"Edit Director Notes…\" to add guidance for the VO director.",
                MessageType.None);
        }
    }
}
