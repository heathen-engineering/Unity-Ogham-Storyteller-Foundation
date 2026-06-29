using System.IO;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Registers every tag authored in the project's <c>.ogham</c> JSON sources into the live
    /// <see cref="GameplayTagRegistry"/> on each domain reload, so the editor (pickers, validation, ancestry
    /// queries) sees Ogham's tags without a separate <c>.gptags</c> file. The <c>.ogham</c> JSON is the single
    /// source of truth for these tags; this is the edit-time mirror of the runtime story builder's
    /// registration and the build-time bake. Mirrors <c>GameplayTagsEditorRegistrar</c>, but sourced from
    /// Ogham's own data.
    /// </summary>
    [InitializeOnLoad]
    public static class OghamTagRegistrar
    {
        static OghamTagRegistrar() => EditorApplication.delayCall += Refresh;

        /// <summary>Re-read every <c>.ogham</c> source and register its tags into the live registry.</summary>
        public static void Refresh()
        {
            string[] files;
            try { files = Directory.GetFiles(Application.dataPath, "*.ogham", SearchOption.AllDirectories); }
            catch { return; }

            foreach (var file in files)
            {
                try
                {
                    var doc = OghamJsonDocument.Parse(File.ReadAllText(file));
                    foreach (var path in doc.GetAllTagPaths())
                        if (!string.IsNullOrWhiteSpace(path))
                            GameplayTagRegistry.Register(path);
                }
                catch
                {
                    // A malformed .ogham is surfaced by the importer's LogImportError; skip it here.
                }
            }
        }
    }
}
