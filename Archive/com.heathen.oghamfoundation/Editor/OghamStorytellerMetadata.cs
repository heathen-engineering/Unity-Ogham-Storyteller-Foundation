using System.Collections.Generic;
using Heathen;
using Heathen.Editor;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project-level VO export metadata for the Ogham Storyteller graph editor. Stored as JSON in
    /// <c>ProjectSettings/OghamStorytellerMetadata.json</c> via the Game Framework settings store (no
    /// longer a ScriptableObject in <c>Assets/</c>). Content labels define the human-readable name for
    /// each content key slot in order: the first label applies to content key 0, the second to key 1, and
    /// so on. They appear as read-only badges in the graph editor and as row headers in exported VO scripts.
    /// </summary>
    [Settings(Location = SettingsLocation.ProjectSettings)]
    public class OghamStorytellerMetadata
    {
        /// <summary>Ordered labels for content key slots (index 0 labels the first content key, and so on).</summary>
        public List<string> ContentLabels = new();

        private static OghamStorytellerMetadata _instance;

        /// <summary>The project's metadata, loaded once and cached (defaults when no file exists yet).</summary>
        public static OghamStorytellerMetadata GetOrCreate() => _instance ??= SettingsStore.Load<OghamStorytellerMetadata>();

        /// <summary>Persist changes back to ProjectSettings.</summary>
        public void Save() => SettingsStore.Save(this);

        /// <summary>The label for the given content key index, or an empty string when out of range or null.</summary>
        public string GetLabel(int index)
            => index >= 0 && index < ContentLabels.Count ? ContentLabels[index] ?? "" : "";
    }
}
