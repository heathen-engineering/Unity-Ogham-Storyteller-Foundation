using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Heathen.Editor;              // ISettingsMetadataProvider
using Heathen.GameplayTags.Editor; // ITagSource

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Publishes Ogham's authored tags (every dot-path referenced by the project's <c>.ogham</c> stories) to the
    /// Gameplay Tags settings panel and other tooling through the framework metadata seam, so a developer can see
    /// which tags Ogham contributes and where they come from. These tags are owned by Ogham — registered live by
    /// <see cref="OghamTagRegistrar"/> and baked at runtime by the story generator — so this is read-only
    /// provenance, not an editable source. Discovered by type via <c>SettingsMetadata</c>.
    /// </summary>
    public sealed class OghamTagSourceProvider : ISettingsMetadataProvider, ITagSource
    {
        /// <inheritdoc/>
        public string SourceName => "Ogham Storyteller";

        /// <inheritdoc/>
        public bool Registered => true; // .ogham tags are registered live and baked to runtime

        /// <inheritdoc/>
        public IEnumerable<string> Tags
        {
            get
            {
                var set = new SortedSet<string>(StringComparer.Ordinal);
                string[] files;
                try { files = Directory.GetFiles(Application.dataPath, "*.ogham", SearchOption.AllDirectories); }
                catch { return set; }

                foreach (var file in files)
                {
                    // Skip Unity hidden folders (Samples~ etc.): never compiled or active.
                    if (file.Replace('\\', '/').Contains("~/")) continue;
                    try
                    {
                        var doc = OghamJsonDocument.Parse(File.ReadAllText(file));
                        foreach (var path in doc.GetAllTagPaths())
                            if (!string.IsNullOrWhiteSpace(path)) set.Add(path.Trim());
                    }
                    catch { /* malformed .ogham is surfaced by the importer; skip here */ }
                }
                return set;
            }
        }
    }
}
