using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham.Editor
{
    internal static class OghamTagHelper
    {
        // Returns the friendly name for a tag ID.
        // Checks in-memory registry first, then scans all GameplayTagsData assets as fallback.
        public static string GetTagName(ulong id)
        {
            if (id == 0) return "";
            var name = GameplayTagRegistry.GetName(id);
            if (name != null) return name;
            foreach (var tag in ScanAllTagAssets())
                if (!string.IsNullOrEmpty(tag) && GameplayTag.FromName(tag).Id == id) return tag;
            return "";
        }

        // All known tag names: registry union all GameplayTagsData assets, alphabetically sorted.
        public static List<string> GetAllTagNames()
        {
            var registered = GameplayTagRegistry.GetAllNames();
            var set = registered != null
                ? new HashSet<string>(registered)
                : new HashSet<string>();
            foreach (var tag in ScanAllTagAssets())
                set.Add(tag);
            return set.OrderBy(t => t).ToList();
        }

        // Ensures tagName is present in a GameplayTagsData asset and in the live registry.
        // Safe to call with null/empty — returns immediately.
        public static void EnsureRegistered(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return;
            var trimmed = tagName.Trim();
            var id = GameplayTag.FromName(trimmed).Id;
            if (id == 0 || GameplayTagRegistry.GetName(id) != null) return;

            var target = FindOrCreateTagsData();
            if (target == null) return;  // user cancelled the save dialog
            if (!target.tags.Contains(trimmed))
            {
                target.tags.Add(trimmed);
                EditorUtility.SetDirty(target);
                AssetDatabase.SaveAssetIfDirty(target);
            }
            GameplayTagRegistry.RegisterDefaults(target);
        }

        // Returns true when s is a valid dot-path tag name: non-empty, segments of alphanumeric
        // characters only, no leading/trailing dots, no consecutive dots.
        public static bool IsValidTagPath(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            if (s[0] == '.' || s[s.Length - 1] == '.') return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!char.IsLetterOrDigit(c) && c != '.') return false;
                if (c == '.' && i + 1 < s.Length && s[i + 1] == '.') return false;
            }
            return true;
        }

        // Shows a GenericMenu of all known tags; calls onPick(name) when the user selects one.
        public static void ShowTagPicker(System.Action<string> onPick)
        {
            var names = GetAllTagNames();
            if (names.Count == 0)
            {
                EditorUtility.DisplayDialog("Gameplay Tags", "No tags found. Add tags via a GameplayTagsData asset.", "OK");
                return;
            }
            var menu = new GenericMenu();
            foreach (var n in names)
            {
                var captured = n;
                menu.AddItem(new GUIContent(captured), false, () => onPick(captured));
            }
            menu.ShowAsContext();
        }

        private static IEnumerable<string> ScanAllTagAssets()
        {
            var guids = AssetDatabase.FindAssets("t:GameplayTagsData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<GameplayTagsData>(path);
                if (data == null) continue;
                foreach (var tag in data.tags)
                    if (!string.IsNullOrWhiteSpace(tag)) yield return tag;
            }
        }

        private static GameplayTagsData FindOrCreateTagsData()
        {
            var guids = AssetDatabase.FindAssets("t:GameplayTagsData");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadAssetAtPath<GameplayTagsData>(path);
                if (data != null && data.autoRegister) return data;
            }
            var savePath = EditorUtility.SaveFilePanelInProject(
                "Create Gameplay Tags Data",
                "GameplayTagsData", "asset",
                "Choose where to save the new GameplayTagsData asset.",
                "Assets");
            if (string.IsNullOrEmpty(savePath)) return null;
            var created = ScriptableObject.CreateInstance<GameplayTagsData>();
            AssetDatabase.CreateAsset(created, savePath);
            AssetDatabase.SaveAssets();
            return created;
        }
    }
}
