using System.Collections.Generic;
using Heathen.GameplayTags;
using UnityEngine;

namespace Heathen.Ogham
{
    /// <summary>
    /// Global registry of baked story manifests, keyed by their story <see cref="GameplayTag"/> id. A story's
    /// "name" is a GameplayTag, so it is addressed by that tag's <c>ulong</c> id and looked up here. Baked
    /// story code (generated from each <c>.ogham</c>) registers its <see cref="OghamStoryManifest"/> at load,
    /// so there is no ScriptableObject and no runtime file read. Build a registered story by tag with
    /// <see cref="Build"/>, or fetch its manifest with <see cref="TryGet"/>.
    /// </summary>
    public static class OghamStoryCatalog
    {
        private static readonly Dictionary<ulong, OghamStoryManifest> _byTag       = new();
        // Built definitions are immutable and shareable, so cache one per story tag and reuse it across sessions.
        private static readonly Dictionary<ulong, OghamStory>         _definitions = new();

        // Cleared each session (incl. enter-play-without-domain-reload) before the baked Register() calls,
        // which run later at BeforeSceneLoad, so stale entries never carry over.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _byTag.Clear();
            _definitions.Clear();
        }

        /// <summary>Registers (or replaces) a baked manifest under its story tag id. Null/untagged manifests are ignored.</summary>
        public static void Register(OghamStoryManifest manifest)
        {
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.StoryTagPath)) return;
            var id = GameplayTag.FromName(manifest.StoryTagPath).Id;
            _byTag[id] = manifest;
            _definitions.Remove(id); // a re-registered manifest invalidates any cached definition
        }

        /// <summary>True when a story is registered under <paramref name="storyTag"/>, returning its manifest.</summary>
        public static bool TryGet(GameplayTag storyTag, out OghamStoryManifest manifest) =>
            _byTag.TryGetValue(storyTag.Id, out manifest);

        /// <summary>True when a story is registered under <paramref name="storyTag"/>.</summary>
        public static bool Has(GameplayTag storyTag) => _byTag.ContainsKey(storyTag.Id);

        /// <summary>The tag ids of all registered baked stories.</summary>
        public static IEnumerable<ulong> StoryTags => _byTag.Keys;

        /// <summary>
        /// Returns the immutable <see cref="OghamStory"/> definition addressed by <paramref name="storyTag"/>,
        /// building it from the baked manifest on first request and caching it thereafter. Returns <c>null</c>
        /// when no story is registered under that tag. Use this when you want to scope sessions yourself
        /// (<c>new OghamSession(definition)</c>) rather than use the per-world default.
        /// </summary>
        public static OghamStory GetDefinition(GameplayTag storyTag)
        {
            if (_definitions.TryGetValue(storyTag.Id, out var def)) return def;
            if (!_byTag.TryGetValue(storyTag.Id, out var m))        return null;
            def = OghamStoryBuilder.BuildDefinition(m);
            _definitions[storyTag.Id] = def;
            return def;
        }

        /// <summary>
        /// Opens the per-world default play session for the story addressed by <paramref name="storyTag"/>,
        /// building (and caching) its definition from the baked manifest as needed. Returns the live
        /// <see cref="OghamSession"/>, or <c>null</c> when no story is registered under that tag.
        /// </summary>
        public static OghamSession Build(GameplayTag storyTag, bool setAsMain = false)
        {
            var def = GetDefinition(storyTag);
            if (def == null) return null;
            // Prefer the per-world default session; fall back to a standalone session if the framework's
            // world/subsystem is not resolvable at this moment (e.g. early script execution order), so a
            // reader is never left without a playable session.
            return Storyteller.OpenSession(def, setAsMain) ?? def.CreateSession();
        }
    }
}
