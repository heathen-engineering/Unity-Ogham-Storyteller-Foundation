using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using Heathen.GameplayTags;

namespace Heathen.Ogham.Editor
{
    internal static class OghamTagHelper
    {
        // Returns the friendly name for a tag ID from the live registry.
        public static string GetTagName(ulong id)
        {
            if (id == 0) return "";
            return GameplayTagRegistry.GetName(id) ?? "";
        }

        // All registered tag names, alphabetically sorted.
        public static List<string> GetAllTagNames()
        {
            var names = GameplayTagRegistry.GetAllNames();
            return names != null
                ? names.OrderBy(t => t).ToList()
                : new List<string>();
        }

        // Ensures tagName is present in a .gptags source file and registered in the live registry.
        // Safe to call with null/empty — returns immediately.
        public static void EnsureRegistered(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return;
            var trimmed = tagName.Trim();
            var id = GameplayTag.FromName(trimmed).Id;
            if (id != 0 && GameplayTagRegistry.GetName(id) != null) return;

            var filePath = FindOrCreateGpTagsFile();
            if (string.IsNullOrEmpty(filePath)) return;

            var (tags, registered) = ReadGpTagsSource(filePath);
            if (tags.Contains(trimmed)) return;
            tags.Add(trimmed);
            WriteGpTagsFile(filePath, tags, registered);
        }

        public static bool IsValidTagPath(string s) => GameplayTagRegistry.ValidateTag(s);

        // Shows a GenericMenu of all registered tags; calls onPick(name) on selection.
        public static void ShowTagPicker(System.Action<string> onPick)
        {
            var names = GetAllTagNames();
            if (names.Count == 0)
            {
                EditorUtility.DisplayDialog("Gameplay Tags",
                    "No tags registered. Add tags via Project Settings > Gameplay Tags.", "OK");
                return;
            }
            var menu = new UnityEditor.GenericMenu();
            foreach (var n in names)
            {
                var captured = n;
                menu.AddItem(new UnityEngine.GUIContent(captured), false, () => onPick(captured));
            }
            menu.ShowAsContext();
        }

        // Finds the first .gptags file in the project, or prompts the user to create one.
        private static string FindOrCreateGpTagsFile()
        {
            var guids = AssetDatabase.FindAssets("t:GameplayTagsCompiledData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(".gptags", System.StringComparison.OrdinalIgnoreCase))
                    return path;
            }

            var savePath = EditorUtility.SaveFilePanelInProject(
                "Create Tag Source", "TagSource", "gptags",
                "No .gptags file found. Choose where to create one.");
            if (string.IsNullOrEmpty(savePath)) return null;

            var root = new JObject { ["registered"] = true, ["tags"] = new JArray() };
            File.WriteAllText(savePath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(savePath);
            return savePath;
        }

        private static (List<string> tags, bool registered) ReadGpTagsSource(string assetPath)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(assetPath));
                return (root["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        root["registered"]?.Value<bool>() ?? true);
            }
            catch { return (new List<string>(), true); }
        }

        private static void WriteGpTagsFile(string assetPath, List<string> tags, bool registered)
        {
            var root = new JObject
            {
                ["registered"] = registered,
                ["tags"]       = JArray.FromObject(tags)
            };
            File.WriteAllText(assetPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }
    }
}
