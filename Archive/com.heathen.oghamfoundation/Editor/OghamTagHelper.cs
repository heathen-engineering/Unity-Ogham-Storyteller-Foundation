using System.Collections.Generic;
using System.Linq;
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
        public static string GetTagName(ulong id)
        {
            if (id == 0) return "";
            return GameplayTagRegistry.GetName(id) ?? "";
        }

        /// <summary>Returns all registered GameplayTag names, sorted alphabetically.</summary>
        public static List<string> GetAllTagNames()
        {
            var names = GameplayTagRegistry.GetAllNames();
            return names != null ? names.OrderBy(t => t).ToList() : new List<string>();
        }

        /// <summary>
        /// Registers <paramref name="tagName"/> into the live <see cref="GameplayTagRegistry"/> so the editor
        /// (pickers, validation, ancestry queries) sees it immediately. Ogham does NOT write a shared
        /// <c>.gptags</c> file: its tags are persisted in its own <c>.ogham</c> JSON source, re-registered on
        /// domain reload by <see cref="OghamTagRegistrar"/>, and baked into code on build. Null/empty/invalid
        /// input is ignored.
        /// </summary>
        public static void EnsureRegistered(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName)) return;
            var trimmed = tagName.Trim();
            if (!GameplayTagRegistry.ValidateTag(trimmed)) return;
            if (GameplayTagRegistry.GetName(GameplayTag.FromName(trimmed).Id) != null) return; // already live
            GameplayTagRegistry.Register(trimmed);
        }

        /// <summary>Returns <c>true</c> when <paramref name="s"/> is a valid dot-path GameplayTag string.</summary>
        public static bool IsValidTagPath(string s) => GameplayTagRegistry.ValidateTag(s);

        /// <summary>
        /// Shows a <see cref="GenericMenu"/> listing all registered GameplayTag names, invoking
        /// <paramref name="onPick"/> with the selection. Shows a dialog when none are registered.
        /// </summary>
        public static void ShowTagPicker(System.Action<string> onPick)
        {
            var names = GetAllTagNames();
            if (names.Count == 0)
            {
                EditorUtility.DisplayDialog("Gameplay Tags",
                    "No tags registered yet. Tags are created as you author nodes, options and conditions " +
                    "in the Ogham graph editor.", "OK");
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
    }
}
