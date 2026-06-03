using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Heathen.Ogham.Editor
{
    // Project-level VO export metadata for Ogham Storyteller.
    // Content labels define the human-readable name for each content key slot in order —
    // the first label applies to content key 0, the second to key 1, etc.
    // These labels appear in the graph editor as read-only badges and populate the
    // row headers in the exported VO script CSV.
    [CreateAssetMenu(menuName = "Heathen/Ogham/Storyteller Metadata", fileName = "OghamStorytellerMetadata")]
    public class OghamStorytellerMetadata : ScriptableObject
    {
        public List<string> ContentLabels = new();

        private const string DefaultAssetPath = "Assets/Settings/OghamStorytellerMetadata.asset";

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

        // Returns the label for the given content key index, or an empty string if none is configured.
        public string GetLabel(int index)
            => index >= 0 && index < ContentLabels.Count ? ContentLabels[index] ?? "" : "";
    }
}
