using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Authoring asset that holds the raw <see cref="DialogueEntry"/> list for a portion of a story.
    /// Used directly in the graph editor and during editor iteration; compiled into <see cref="OghamCompiledData"/>
    /// for player builds. Register with an <see cref="OghamStory"/> via <see cref="OghamStory.RegisterData(OghamData)"/>.
    /// </summary>
    [CreateAssetMenu(menuName = "Heathen/Ogham/Dialogue Data", fileName = "OghamData")]
    public class OghamData : ScriptableObject
    {
        /// <summary>All dialogue entries authored in this data asset.</summary>
        public List<DialogueEntry> Entries = new();

        private Dictionary<ulong, DialogueEntry> _index;
        private Dictionary<ulong, HashSet<ulong>> _childIndex;

        private void OnEnable() => BuildIndex();

        /// <summary>
        /// Finds and returns the dialogue entry whose tag matches <paramref name="tag"/>, or <c>null</c> if not found.
        /// </summary>
        /// <param name="tag">The GameplayTag whose ID is used to look up the entry.</param>
        /// <returns>The matching <see cref="DialogueEntry"/>, or <c>null</c>.</returns>
        public DialogueEntry FindEntry(GameplayTag tag) => FindEntry(tag.Id);

        internal DialogueEntry FindEntry(ulong tagId)
        {
            if (_index == null) BuildIndex();
            return _index.TryGetValue(tagId, out var e) ? e : null;
        }

        /// <summary>
        /// Returns the tag IDs of all entries that are direct navigation targets of options on the entry with the given ID.
        /// </summary>
        /// <param name="parentId">The tag ID of the parent entry.</param>
        /// <returns>An enumerable of child entry tag IDs, or an empty sequence when none exist.</returns>
        public IEnumerable<ulong> GetChildren(ulong parentId)
        {
            if (_childIndex == null) BuildIndex();
            return _childIndex.TryGetValue(parentId, out var set) ? set : System.Array.Empty<ulong>();
        }

        /// <summary>
        /// Performs a breadth-first traversal from <paramref name="entryId"/> and adds all reachable descendant
        /// entry IDs to <paramref name="results"/>. Already-visited IDs are not added twice.
        /// </summary>
        /// <param name="entryId">The tag ID of the entry to start traversal from.</param>
        /// <param name="results">The set to populate with discovered descendant IDs.</param>
        public void CollectDescendants(ulong entryId, HashSet<ulong> results)
        {
            if (_childIndex == null) BuildIndex();
            var queue = new Queue<ulong>();
            queue.Enqueue(entryId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!_childIndex.TryGetValue(current, out var children)) continue;
                foreach (var child in children)
                    if (results.Add(child)) queue.Enqueue(child);
            }
        }

        /// <summary>
        /// Rebuilds the internal tag-ID-to-entry and parent-to-child lookup dictionaries from <see cref="Entries"/>.
        /// Called automatically on <c>OnEnable</c> and <c>OnValidate</c>; call manually after modifying
        /// <see cref="Entries"/> at runtime or in the editor.
        /// </summary>
        public void BuildIndex()
        {
            _index = new Dictionary<ulong, DialogueEntry>(Entries.Count);
            _childIndex = new Dictionary<ulong, HashSet<ulong>>();

            foreach (var entry in Entries)
            {
                var id = entry.Tag.Id;
                if (id == 0) continue;
                _index.TryAdd(id, entry);
            }

            // Child relationships are derived from connections (options → target entries),
            // not from explicit parent pointers — this is a graph, not a tree.
            foreach (var entry in Entries)
            {
                var parentId = entry.Tag.Id;
                if (parentId == 0) continue;
                foreach (var opt in entry.Options)
                {
                    var childId = opt.TargetEntry.Id;
                    if (childId == 0) continue;
                    if (!_childIndex.TryGetValue(parentId, out var set))
                    {
                        set = new HashSet<ulong>();
                        _childIndex[parentId] = set;
                    }
                    set.Add(childId);
                }
            }

#if UNITY_EDITOR
            SyncInlineLinkOptions();
#endif
        }

#if UNITY_EDITOR
        private void OnValidate() => BuildIndex();

        private void SyncInlineLinkOptions()
        {
            bool dirty = false;
            // Snapshot to allow adding new entries during iteration
            var snapshot = new System.Collections.Generic.List<DialogueEntry>(Entries);

            foreach (var entry in snapshot)
            {
                if (string.IsNullOrEmpty(entry.TagPath)) continue;

                for (int keyIdx = entry.ContentKeys.Count - 1; keyIdx >= 0; keyIdx--)
                {
                    var key = entry.ContentKeys[keyIdx];
                    if (key.Type != OghamContentType.Text) continue;

                    var text = key.KeyOrValue ?? "";

                    if (OghamInlineLinkParser.IsPureLink(text, out var pureDisplay, out var pureTag))
                    {
                        // Only Ogham:// pure links become standalone options; http:// etc. are plain hyperlinks.
                        if (!OghamInlineLinkParser.IsOghamLink(pureTag)) continue;
                        FindOrCreateInlineLinkOption(entry, pureDisplay,
                            OghamInlineLinkParser.GetTagPath(pureTag), string.Empty, ref dirty);
                        entry.ContentKeys.RemoveAt(keyIdx);
                        dirty = true;
                        continue;
                    }

                    var links = OghamInlineLinkParser.ExtractLinks(text);
                    for (int linkIdx = 0; linkIdx < links.Count; linkIdx++)
                    {
                        var (display, rawUrl) = links[linkIdx];
                        // Non-Ogham links (http, https, …) are plain hyperlinks — not story options.
                        if (!OghamInlineLinkParser.IsOghamLink(rawUrl)) continue;
                        var optionTagPath = OghamInlineLinkParser.GetTagPath(rawUrl);
                        var src = $"{entry.TagPath}.Keys[{keyIdx}].Links[{linkIdx}]";
                        FindOrCreateInlineLinkOption(entry, display, optionTagPath, src, ref dirty);
                    }
                }
            }

            if (dirty)
                UnityEditor.EditorUtility.SetDirty(this);
        }

        // optionTagPath is the dot-path of the option this inline link refers to (Ogham:// prefix already stripped).
        private void FindOrCreateInlineLinkOption(DialogueEntry entry, string display, string optionTagPath,
            string sourcePath, ref bool dirty)
        {
            // 1. Match by InlineLinkSourceKeyPath (stable across display-text renames)
            if (!string.IsNullOrEmpty(sourcePath))
            {
                var existing = entry.Options.Find(o => o.InlineLinkSourceKeyPath == sourcePath);
                if (existing != null)
                {
                    if (existing.TextKey.KeyOrValue != display)
                    { existing.TextKey.KeyOrValue = display; dirty = true; }
                    return;
                }
            }

            // 2. Match by explicit option tag path — the Ogham:// URL IS the option's tag, not a target entry.
            //    This prevents duplicating options that are already explicitly declared in the entry.
            if (!string.IsNullOrEmpty(optionTagPath))
            {
                var byTag = entry.Options.Find(o =>
                    string.Equals(o.TagPath, optionTagPath, System.StringComparison.Ordinal));
                if (byTag != null)
                {
                    if (!string.IsNullOrEmpty(sourcePath) && byTag.InlineLinkSourceKeyPath != sourcePath)
                    { byTag.InlineLinkSourceKeyPath = sourcePath; dirty = true; }
                    return;
                }
            }

            // 3. Fallback: match by normalised display text suffix (legacy authoring without explicit tag)
            var norm = OghamInlineLinkParser.NormaliseForTag(display);
            var fallback = entry.Options.Find(o =>
                !string.IsNullOrEmpty(o.TagPath) &&
                (o.TagPath.EndsWith("." + norm, System.StringComparison.Ordinal)
                 || o.TagPath == norm));
            if (fallback != null)
            {
                if (!string.IsNullOrEmpty(sourcePath) && fallback.InlineLinkSourceKeyPath != sourcePath)
                { fallback.InlineLinkSourceKeyPath = sourcePath; dirty = true; }
                return;
            }

            // 4. Create a new synthesized option using the explicit option tag when available.
            var newTagPath = !string.IsNullOrEmpty(optionTagPath)
                ? optionTagPath
                : $"{entry.TagPath}.{norm}";
            var newOpt = new DialogueOption
            {
                TagPath                   = newTagPath,
                TargetEntryPath           = string.Empty,
                SynthesizedFromInlineLink = true,
                InlineLinkSourceKeyPath   = sourcePath,
            };
            newOpt.TextKey.KeyOrValue = display;
            entry.Options.Add(newOpt);
            dirty = true;
        }
#endif
    }
}
