using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    [CreateAssetMenu(menuName = "Heathen/Ogham/Dialogue Data", fileName = "OghamData")]
    public class OghamData : ScriptableObject
    {
        public List<DialogueEntry> Entries = new();

        private Dictionary<ulong, DialogueEntry> _index;
        private Dictionary<ulong, HashSet<ulong>> _childIndex;

        private void OnEnable() => BuildIndex();

        public DialogueEntry FindEntry(GameplayTag tag) => FindEntry(tag.Id);

        internal DialogueEntry FindEntry(ulong tagId)
        {
            if (_index == null) BuildIndex();
            return _index.TryGetValue(tagId, out var e) ? e : null;
        }

        public IEnumerable<ulong> GetChildren(ulong parentId)
        {
            if (_childIndex == null) BuildIndex();
            return _childIndex.TryGetValue(parentId, out var set) ? set : System.Array.Empty<ulong>();
        }

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
                        // Pure link: the ContentKey becomes a standalone option; remove the key.
                        FindOrCreateInlineLinkOption(entry, pureDisplay, pureTag,
                            string.Empty, ref dirty);
                        entry.ContentKeys.RemoveAt(keyIdx);
                        dirty = true;
                        continue;
                    }

                    var links = OghamInlineLinkParser.ExtractLinks(text);
                    for (int linkIdx = 0; linkIdx < links.Count; linkIdx++)
                    {
                        var (display, tag) = links[linkIdx];
                        var src = $"{entry.TagPath}.Keys[{keyIdx}].Links[{linkIdx}]";
                        FindOrCreateInlineLinkOption(entry, display, tag, src, ref dirty);
                    }
                }
            }

            if (dirty)
                UnityEditor.EditorUtility.SetDirty(this);
        }

        private void FindOrCreateInlineLinkOption(DialogueEntry entry, string display, string tag,
            string sourcePath, ref bool dirty)
        {
            // 1. Match by InlineLinkSourceKeyPath
            if (!string.IsNullOrEmpty(sourcePath))
            {
                var existing = entry.Options.Find(o => o.InlineLinkSourceKeyPath == sourcePath);
                if (existing != null)
                {
                    if (existing.TextKey.KeyOrValue != display)
                    { existing.TextKey.KeyOrValue = display; dirty = true; }
                    if (!string.IsNullOrEmpty(tag) && existing.TargetEntryPath != tag)
                    { existing.TargetEntryPath = tag; dirty = true; }
                    return;
                }
            }

            // 2. Fallback: match by normalised display text suffix
            var norm = OghamInlineLinkParser.NormaliseForTag(display);
            var fallback = entry.Options.Find(o =>
                !string.IsNullOrEmpty(o.TagPath) &&
                (o.TagPath.EndsWith("." + norm, System.StringComparison.Ordinal)
                 || o.TagPath == norm));
            if (fallback != null) return;

            // 3. Create a new synthesized option
            var optTagPath = $"{entry.TagPath}.{norm}";
            var newOpt = new DialogueOption
            {
                TagPath                   = optTagPath,
                TargetEntryPath           = tag ?? "",
                SynthesizedFromInlineLink = true,
                InlineLinkSourceKeyPath   = sourcePath,
            };
            newOpt.TextKey.KeyOrValue = display;
            entry.Options.Add(newOpt);
            dirty = true;

            // 4. Create a stub target entry if the tag isn't in any loaded data
            if (!string.IsNullOrEmpty(tag))
            {
                var targetTag = GameplayTag.FromName(tag);
                if (targetTag.IsValid && !_index.ContainsKey(targetTag.Id))
                {
                    var newEntry = new DialogueEntry { TagPath = tag };
                    Entries.Add(newEntry);
                    _index[targetTag.Id] = newEntry;
                    dirty = true;
                }
            }
        }
#endif
    }
}
