using System.IO;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // Project-level editor preferences for Ogham Storyteller.
    // Create via Tools → Heathen → Ogham → Settings, or let GetOrCreate() do it automatically.
    [CreateAssetMenu(menuName = "Heathen/Ogham/Editor Settings", fileName = "OghamEditorSettings")]
    public class OghamEditorSettings : ScriptableObject
    {
        [Tooltip("Default color applied to inline link markup when inserted via the 🔗 button.")]
        public Color DefaultLinkColor     = new Color(0.20f, 0.60f, 1.00f, 1f);

        [Tooltip("Whether inline links default to underlined. Applies at canvas preview; runtime styling comes from the TMPro stylesheet.")]
        public bool  DefaultLinkUnderline = true;

        private const string DefaultAssetPath = "Assets/Settings/OghamEditorSettings.asset";

        // Returns the first OghamEditorSettings asset in the project,
        // creating one at DefaultAssetPath if none exists.
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
