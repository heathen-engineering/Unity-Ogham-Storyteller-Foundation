using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// <c>ScriptedImporter</c> for <c>.ogham</c> JSON source files. Compiles each file into an
    /// <see cref="OghamCompiledData"/> main sub-asset (runtime-ready, with TMPro markup and inline localisations)
    /// and an <see cref="OghamGraphMetadata"/> secondary sub-asset (graph editor layout from the <c>_editor</c> block).
    /// Tags referenced in the file are registered with <see cref="GameplayTags.GameplayTagRegistry"/> at import time
    /// so ancestry queries work without a separate <c>.gptags</c> file. Inline localisations are injected into
    /// <see cref="Heathen.Lexicon.LexiconRegistry"/> for editor-time string resolution.
    /// </summary>
    [ScriptedImporter(1, "ogham")]
    public class OghamImporter : ScriptedImporter
    {
        /// <summary>
        /// Imports the <c>.ogham</c> JSON file at <c>ctx.assetPath</c>, compiling it into an
        /// <see cref="OghamCompiledData"/> main asset and an <see cref="OghamGraphMetadata"/> secondary asset.
        /// </summary>
        /// <param name="ctx">The import context provided by Unity's asset import pipeline.</param>
        public override void OnImportAsset(AssetImportContext ctx)
        {
            string json;
            try
            {
                json = File.ReadAllText(ctx.assetPath);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"[Ogham] Cannot read {ctx.assetPath}: {e.Message}");
                return;
            }

            var doc = OghamJsonDocument.Parse(json);

            // Pre-register all tag paths so IsAncestor() works on narrative-state tags
            // that are only defined inside this dialogue file.
            foreach (var path in doc.GetAllTagPaths())
                GameplayTagRegistry.Register(path);

            // Build runtime-ready compiled data (inline links → TMPro markup).
            OghamCompiledData compiled;
            try
            {
                compiled = doc.ToCompiledData();
            }
            catch (Exception e)
            {
                ctx.LogImportError($"[Ogham] Compile failed for {ctx.assetPath}: {e.Message}");
                compiled = ScriptableObject.CreateInstance<OghamCompiledData>();
            }
            compiled.name = Path.GetFileNameWithoutExtension(ctx.assetPath);
            ctx.AddObjectToAsset("main", compiled);
            ctx.SetMainObject(compiled);

            // Validate Fork termination: a fork-to-fork cycle never resolves to a node or ends the
            // conversation, and would spin forever at runtime. Surface it on the asset at compile time.
            var cyclicForks = OghamForkValidator.FindCyclicForks(compiled.Entries);
            foreach (var forkId in cyclicForks)
            {
                var forkName = GameplayTagRegistry.GetName(forkId) ?? forkId.ToString("X16");
                ctx.LogImportError(
                    $"[Ogham] Fork '{forkName}' is part of a routing cycle. Every path out of a Fork must " +
                    $"resolve to a node or end the conversation. ({ctx.assetPath})");
            }

            // Build graph editor layout metadata from the _editor block.
            OghamGraphMetadata meta;
            try
            {
                meta = doc.ToMetadata();
            }
            catch (Exception e)
            {
                ctx.LogImportWarning($"[Ogham] Metadata parse failed for {ctx.assetPath}: {e.Message}");
                meta = ScriptableObject.CreateInstance<OghamGraphMetadata>();
            }
            meta.name = compiled.name + "_editor";
            ctx.AddObjectToAsset("meta", meta);

            // Inject inline localisations so the editor can resolve them without runtime play.
            foreach (var loc in compiled.Localisations)
                if (!string.IsNullOrWhiteSpace(loc.Key))
                    LexiconRegistry.SetString(loc.Key, loc.Value,
                        string.IsNullOrWhiteSpace(loc.Culture) ? null : loc.Culture);
        }
    }

    /// <summary>
    /// Registers GameplayTags and inline localisations from all <c>.ogham</c> files on editor load and after imports,
    /// ensuring that tags and string values are available in the editor without requiring a full play-mode session.
    /// </summary>
    [InitializeOnLoad]
    public static class OghamImporterRefresh
    {
        static OghamImporterRefresh() => EditorApplication.delayCall += Refresh;

        /// <summary>
        /// Scans all <c>.ogham</c> files in the project, registers their tags with
        /// <see cref="GameplayTags.GameplayTagRegistry"/>, and injects their inline localisations into
        /// <see cref="Heathen.Lexicon.LexiconRegistry"/>. Called automatically on editor load.
        /// </summary>
        public static void Refresh()
        {
            var dataPath = Application.dataPath;
            string[] files;
            try { files = Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories); }
            catch { return; }

            foreach (var file in files)
            {
                // Skip files inside hidden folders (Unity convention: FolderName~).
                if (OghamImporterUtils.IsInHiddenFolder(file)) continue;

                try
                {
                    var json = File.ReadAllText(file);
                    var doc  = OghamJsonDocument.Parse(json);

                    foreach (var path in doc.GetAllTagPaths())
                        GameplayTagRegistry.Register(path);

                    foreach (var loc in doc.GetLocalisations())
                        if (!string.IsNullOrWhiteSpace(loc.Key))
                            LexiconRegistry.SetString(loc.Key, loc.Value,
                                string.IsNullOrWhiteSpace(loc.Culture) ? null : loc.Culture);
                }
                catch { /* corrupt or inaccessible file — skip */ }
            }
        }
    }

    // When .gptags files are imported, the tag registry gains new entries that .ogham files
    // may depend on. If .ogham was imported first in the same batch its compiled data will be
    // empty. Re-importing all .ogham files after a .gptags change fixes the ordering problem.
    // The same guard catches .ogham files whose ScriptedImporter didn't fire (fresh install
    // timing) by checking for a null main asset and scheduling a force-reimport.
    internal class OghamAssetPostprocessor : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool gptagsChanged = false;
            var  oghamToCheck  = new System.Collections.Generic.List<string>();

            foreach (var path in importedAssets)
            {
                if (path.EndsWith(".gptags", StringComparison.OrdinalIgnoreCase))
                    gptagsChanged = true;
                else if (path.EndsWith(".ogham", StringComparison.OrdinalIgnoreCase))
                    oghamToCheck.Add(path);
            }

            if (gptagsChanged)
            {
                // Re-import every .ogham in the project after a delay so the tag registry
                // is fully populated before the importer runs.
                EditorApplication.delayCall += ReimportAllOgham;
            }
            else
            {
                // No .gptags change — only recheck .ogham files whose compiled data is missing.
                foreach (var path in oghamToCheck)
                {
                    var p = path;
                    var compiled = AssetDatabase.LoadAssetAtPath<OghamCompiledData>(p);
                    if (compiled == null)
                        EditorApplication.delayCall += () =>
                            AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceUpdate);
                }
            }
        }

        static void ReimportAllOgham()
        {
            var dataPath = Application.dataPath;
            string[] files;
            try { files = Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories); }
            catch { return; }

            foreach (var file in files)
            {
                if (OghamImporterUtils.IsInHiddenFolder(file)) continue;
                var assetPath = "Assets" + file.Substring(dataPath.Length).Replace('\\', '/');
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
        }
    }

    internal static class OghamImporterUtils
    {
        // Returns true if any segment of the path ends with '~'.
        // Unity treats FolderName~ directories as hidden and excludes them from the Asset Database.
        internal static bool IsInHiddenFolder(string path)
        {
            foreach (var segment in path.Split('/', '\\'))
                if (segment.EndsWith("~", System.StringComparison.Ordinal)) return true;
            return false;
        }
    }

    [CustomEditor(typeof(OghamImporter))]
    internal class OghamImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            var importer = (OghamImporter)target;
            var compiled = AssetDatabase.LoadAssetAtPath<OghamCompiledData>(importer.assetPath);

            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Path", importer.assetPath, EditorStyles.miniLabel);

            if (compiled != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Story", EditorStyles.boldLabel);
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.TextField("Story Tag", compiled.StoryTagPath);
                EditorGUILayout.IntField("Entries", compiled.Entries?.Count ?? 0);
                EditorGUILayout.IntField("Localisations", compiled.Localisations?.Length ?? 0);
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.Space();
                if (GUILayout.Button("Open in Graph Editor"))
                    OghamGraphEditorWindow.OpenOghamFile(importer.assetPath);
            }

            EditorGUILayout.Space();
            ApplyRevertGUI();
        }
    }
}
