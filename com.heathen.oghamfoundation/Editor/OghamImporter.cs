using System;
using System.IO;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham.Editor
{
    // Compiles .ogham JSON source files into:
    //   OghamCompiledData  (main sub-asset) — runtime-ready, TMPro markup, inline localisations
    //   OghamGraphMetadata (secondary)      — graph editor layout, stored in _editor block
    //
    // Tags referenced in the file are registered with GameplayTagRegistry at import time
    // so ancestry queries work without a separate .gptags file for dialogue-internal tags.
    // Inline localisations are injected into LexiconRegistry for editor-time string resolution.
    [ScriptedImporter(1, "ogham")]
    public class OghamImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext ctx)
        {
            Debug.Log($"[OghamImporter] OnImportAsset running: {ctx.assetPath}");
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

    // Registers tags and localisations from all .ogham files on editor load and after imports.
    [InitializeOnLoad]
    internal static class OghamImporterRefresh
    {
        static OghamImporterRefresh() => EditorApplication.delayCall += Refresh;

        internal static void Refresh()
        {
            var dataPath = Application.dataPath;
            string[] files;
            try { files = Directory.GetFiles(dataPath, "*.ogham", SearchOption.AllDirectories); }
            catch { return; }

            foreach (var file in files)
            {
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
