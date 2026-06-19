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
        /// <summary>
        /// Returns the dot-path tag name for the given ID from the live <see cref="GameplayTagRegistry"/>,
        /// or an empty string when the ID is zero or not registered.
        /// </summary>
        /// <param name="id">The tag hash ID to look up.</param>
        /// <returns>The registered name string, or an empty string.</returns>
        public static string GetTagName(ulong id)
        {
            if (id == 0) return "";
            return GameplayTagRegistry.GetName(id) ?? "";
        }

        /// <summary>Returns all registered GameplayTag names, sorted alphabetically.</summary>
        /// <returns>A sorted list of dot-path tag name strings.</returns>
        public static List<string> GetAllTagNames()
        {
            var names = GameplayTagRegistry.GetAllNames();
            return names != null
                ? names.OrderBy(t => t).ToList()
                : new List<string>();
        }

        /// <summary>
        /// Ensures <paramref name="tagName"/> is present in a <c>.gptags</c> source file and registered in the live
        /// <see cref="GameplayTagRegistry"/>. If no <c>.gptags</c> file exists the user is prompted to create one.
        /// Safe to call with null or empty input; returns immediately in those cases.
        /// </summary>
        /// <param name="tagName">The dot-path tag name to register. Null or empty is silently ignored.</param>
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

        /// <summary>Returns <c>true</c> when <paramref name="s"/> is a valid dot-path GameplayTag string according to <see cref="GameplayTagRegistry.ValidateTag"/>.</summary>
        /// <param name="s">The tag path string to validate.</param>
        /// <returns><c>true</c> if the string is a valid registered tag path; otherwise <c>false</c>.</returns>
        public static bool IsValidTagPath(string s) => GameplayTagRegistry.ValidateTag(s);

        /// <summary>
        /// Shows a <see cref="GenericMenu"/> listing all registered GameplayTag names. Calls
        /// <paramref name="onPick"/> with the selected name when the user makes a selection.
        /// Displays a dialog when no tags are registered.
        /// </summary>
        /// <param name="onPick">Callback invoked with the selected tag name string.</param>
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
            // .gptags is no longer a typed asset (it imports to a TextAsset now); discover by file scan.
            var existing = Heathen.GameplayTags.Editor.GameplayTagsSources.FindAll();
            if (existing.Count > 0) return existing[0];

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
