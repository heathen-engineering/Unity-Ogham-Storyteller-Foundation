using UnityEditor;
using UnityEngine;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    // Keeps all open OghamGraphEditorWindow instances in sync with the AssetDatabase.
    // - Newly created/imported OghamData assets are automatically loaded into every open window.
    // - Deleted OghamData assets are automatically unloaded.
    // This fires for any asset import, including Twee importer "New…" and "New File…" from the toolbar.
    internal class OghamAssetWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            var windows = Resources.FindObjectsOfTypeAll<OghamGraphEditorWindow>();
            if (windows.Length == 0) return;

            foreach (var path in importedAssets)
            {
                var asset = AssetDatabase.LoadAssetAtPath<OghamData>(path);
                if (asset == null) continue;
                foreach (var w in windows)
                    w.LoadAsset(asset);
            }

            if (deletedAssets.Length > 0)
                foreach (var w in windows)
                    w.UnloadDeletedAssets(deletedAssets);
        }
    }
}
