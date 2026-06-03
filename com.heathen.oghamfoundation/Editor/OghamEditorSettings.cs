using System.IO;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project-level editor preferences for the Ogham Storyteller graph editor. Create via
    /// <c>Tools &rarr; Heathen &rarr; Ogham &rarr; Settings</c>, or let <see cref="GetOrCreate"/> create one automatically.
    /// </summary>
    [CreateAssetMenu(menuName = "Heathen/Ogham/Editor Settings", fileName = "OghamEditorSettings")]
    public class OghamEditorSettings : ScriptableObject
    {
        /// <summary>Default colour applied to inline link markup when inserted via the link button in the content key editor.</summary>
        [Tooltip("Default color applied to inline link markup when inserted via the 🔗 button.")]
        public Color DefaultLinkColor     = new Color(0.20f, 0.60f, 1.00f, 1f);

        /// <summary>Whether inline links default to underlined text in the canvas preview. Runtime styling comes from the TMPro stylesheet.</summary>
        [Tooltip("Whether inline links default to underlined. Applies at canvas preview; runtime styling comes from the TMPro stylesheet.")]
        public bool  DefaultLinkUnderline = true;

        private const string DefaultAssetPath = "Assets/Settings/OghamEditorSettings.asset";

        /// <summary>
        /// Returns the first <see cref="OghamEditorSettings"/> asset found in the project, creating one at
        /// <c>Assets/Settings/OghamEditorSettings.asset</c> when none exists.
        /// </summary>
        /// <returns>The project's <see cref="OghamEditorSettings"/> instance.</returns>
        public static OghamEditorSettings GetOrCreate()
        {
            var guids = AssetDatabase.FindAssets("t:OghamEditorSettings");
            if (guids.Length > 0)
            {
                var path     = AssetDatabase.GUIDToAssetPath(guids[0]);
                var existing = AssetDatabase.LoadAssetAtPath<OghamEditorSettings>(path);
                if (existing != null) return existing;
            }

            var dir = Path.GetDirectoryName(DefaultAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = CreateInstance<OghamEditorSettings>();
            AssetDatabase.CreateAsset(settings, DefaultAssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}
