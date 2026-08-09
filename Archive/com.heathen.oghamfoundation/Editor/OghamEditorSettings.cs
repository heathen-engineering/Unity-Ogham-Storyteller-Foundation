using UnityEngine;
using Heathen;
using Heathen.Editor;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Project-level editor preferences for the Ogham Storyteller graph editor. Stored as JSON in
    /// <c>ProjectSettings/OghamEditorSettings.json</c> via the Game Framework settings store (no longer a
    /// ScriptableObject in <c>Assets/</c>). Edit via <c>Project Settings ▸ Ogham Storyteller</c>.
    /// </summary>
    [Settings(Location = SettingsLocation.ProjectSettings)]
    public class OghamEditorSettings
    {
        /// <summary>Default colour applied to inline link markup inserted via the link button in the content key editor.</summary>
        public Color DefaultLinkColor = new Color(0.20f, 0.60f, 1.00f, 1f);

        /// <summary>Whether inline links default to underlined text in the canvas preview.</summary>
        public bool DefaultLinkUnderline = true;

        private static OghamEditorSettings _instance;

        /// <summary>The project's settings, loaded once and cached (defaults when no file exists yet).</summary>
        public static OghamEditorSettings GetOrCreate() => _instance ??= SettingsStore.Load<OghamEditorSettings>();

        /// <summary>Persist changes back to ProjectSettings.</summary>
        public void Save() => SettingsStore.Save(this);
    }
}
