using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project-level VO export metadata for the Ogham Storyteller graph editor. Content labels define the
    /// human-readable name for each content key slot in order: the first label applies to content key 0,
    /// the second to key 1, and so on. These labels appear as read-only badges in the graph editor and
    /// populate the row headers in exported VO script files.
    /// </summary>
    [CreateAssetMenu(menuName = "Heathen/Ogham/Storyteller Metadata", fileName = "OghamStorytellerMetadata")]
    public class OghamStorytellerMetadata : ScriptableObject
    {
        /// <summary>
        /// Ordered list of human-readable labels for content key slots. Index 0 labels the first content key,
        /// index 1 labels the second, and so on.
        /// </summary>
        public List<string> ContentLabels = new();

        private const string DefaultAssetPath = "Assets/Settings/OghamStorytellerMetadata.asset";

        /// <summary>
        /// Returns the first <see cref="OghamStorytellerMetadata"/> asset found in the project, creating one at
        /// <c>Assets/Settings/OghamStorytellerMetadata.asset</c> when none exists. The asset creation is deferred
        /// via <c>EditorApplication.delayCall</c> to avoid triggering AssetPostprocessor mid-frame.
        /// </summary>
        /// <returns>The project's <see cref="OghamStorytellerMetadata"/> instance.</returns>
        public static OghamStorytellerMetadata GetOrCreate()
        {
            var guids = AssetDatabase.FindAssets("t:OghamStorytellerMetadata");
            if (guids.Length > 0)
            {
                var path     = AssetDatabase.GUIDToAssetPath(guids[0]);
                var existing = AssetDatabase.LoadAssetAtPath<OghamStorytellerMetadata>(path);
                if (existing != null) return existing;
            }

            var dir = Path.GetDirectoryName(DefaultAssetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = CreateInstance<OghamStorytellerMetadata>();
            AssetDatabase.CreateAsset(settings, DefaultAssetPath);
            // Defer SaveAssets — calling it synchronously here fires AssetPostprocessor
            // mid-frame and can disrupt in-memory ScriptableObjects (e.g. meta positions).
            EditorApplication.delayCall += AssetDatabase.SaveAssets;
            return settings;
        }

        /// <summary>
        /// Returns the label configured for the given content key index, or an empty string when the index is
        /// out of range or the label is null.
        /// </summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The label string, or an empty string.</returns>
        public string GetLabel(int index)
            => index >= 0 && index < ContentLabels.Count ? ContentLabels[index] ?? "" : "";
    }
}
