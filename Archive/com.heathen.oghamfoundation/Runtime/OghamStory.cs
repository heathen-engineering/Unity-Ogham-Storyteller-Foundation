using System.Collections.Generic;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// The immutable <em>definition</em> of a story: its dialogue graph (entry and child index), built from
    /// registered <see cref="OghamData"/> assets and/or runtime <see cref="DialogueEntry"/> instances. A
    /// definition is stateless and reusable — it carries no narrative state, current node, or history.
    /// <para>
    /// Create one or more <see cref="OghamSession"/> instances with <see cref="CreateSession"/> (or
    /// <c>new OghamSession(this)</c>) to play the story. Each session scopes its own state independently, so the
    /// same definition can back a world-wide conversation, a per-character dialogue, a save-slot, or a throwaway
    /// one-off — Ogham never prescribes the scope. Baked definitions are built and cached by
    /// <see cref="OghamStoryCatalog"/>; the per-world default session is opened by <see cref="StorytellerSubsystem"/>.
    /// </para>
    /// </summary>
    public class OghamStory
    {
        private readonly List<OghamData>         _assets         = new();
        private readonly List<DialogueEntry>     _runtimeEntries = new();
        private readonly Dictionary<ulong, (int assetIdx, DialogueEntry entry)> _entryIndex = new();
        private readonly Dictionary<ulong, HashSet<ulong>>                       _childIndex = new();

        /// <summary>The GameplayTag that uniquely identifies this story (its "name" is a tag id).</summary>
        public GameplayTag Id { get; }

        /// <summary>
        /// Initialises a new, empty story definition with the given identity tag. Register data or entries to
        /// populate the graph, then call <see cref="CreateSession"/> to play it.
        /// </summary>
        /// <param name="id">The GameplayTag that uniquely identifies this story.</param>
        public OghamStory(GameplayTag id)
        {
            Id = id;
        }

        /// <summary>Creates a new, independent <see cref="OghamSession"/> that plays this definition.</summary>
        /// <returns>A fresh session with its own narrative state and history.</returns>
        public OghamSession CreateSession() => new OghamSession(this);

        /// <summary>
        /// Collects the GUID-addressed assets referenced by one entry's literal content keys (images, audio, VFX,
        /// prefabs, etc.). Used by the asset streamer to acquire and release a node's assets as it enters and
        /// leaves the streaming window. Text and Lexicon-localised content carry no GUID and are skipped.
        /// </summary>
        /// <param name="nodeId">The entry tag id to enumerate.</param>
        /// <param name="into">The list to append each (guid, subAssetName) pair to.</param>
        internal void CollectNodeAssets(ulong nodeId, List<(string guid, string subAssetName)> into)
        {
            var entry = FindEntryInternal(nodeId);
            if (entry == null) return;
            foreach (var key in entry.ContentKeys)
                if (key.Type != OghamContentType.Text && key.Mode == LexiconLocMode.Literal
                    && !string.IsNullOrEmpty(key.AssetGuid))
                    into.Add((key.AssetGuid, key.AssetName));
        }

        /// <summary>
        /// Collects <paramref name="start"/> plus every entry reachable from it within <paramref name="depth"/>
        /// option hops (a breadth-first window over the option graph). Used by the asset streamer to compute the
        /// look-ahead window of nodes whose assets should be resident. Depth 0 yields just <paramref name="start"/>.
        /// </summary>
        /// <param name="start">The entry tag id at the centre of the window.</param>
        /// <param name="depth">How many option hops to include ahead of <paramref name="start"/>.</param>
        /// <param name="results">The set to add reachable entry ids to.</param>
        internal void CollectWithinDepth(ulong start, int depth, HashSet<ulong> results)
        {
            if (start == 0 || !results.Add(start)) return;
            if (depth <= 0) return;

            var frontier = new List<ulong> { start };
            for (int d = 0; d < depth && frontier.Count > 0; d++)
            {
                var next = new List<ulong>();
                foreach (var node in frontier)
                {
                    if (!_childIndex.TryGetValue(node, out var children)) continue;
                    foreach (var child in children)
                        if (results.Add(child)) next.Add(child);
                }
                frontier = next;
            }
        }

        // ── Graph composition ──────────────────────────────────────────────────

        /// <summary>
        /// Registers an <see cref="OghamData"/> authoring asset with this definition and rebuilds the graph index.
        /// Duplicate registrations are silently ignored.
        /// </summary>
        /// <param name="data">The authoring asset to add; <c>null</c> is silently ignored.</param>
        public void RegisterData(OghamData data)
        {
            if (data == null || _assets.Contains(data)) return;
            _assets.Add(data);
            RebuildIndex();
        }

        /// <summary>
        /// Removes a previously registered <see cref="OghamData"/> authoring asset and rebuilds the graph index.
        /// </summary>
        /// <param name="data">The authoring asset to remove.</param>
        public void UnregisterData(OghamData data)
        {
            if (_assets.Remove(data)) RebuildIndex();
        }

        /// <summary>
        /// Removes all registered data assets and clears the graph index. The definition becomes empty until new data is registered.
        /// </summary>
        public void UnregisterAll()
        {
            _assets.Clear();
            _runtimeEntries.Clear();
            _entryIndex.Clear();
            _childIndex.Clear();
        }

        /// <summary>
        /// Registers a single runtime-created <see cref="DialogueEntry"/> (for example, from a mod or UGC manifest)
        /// and rebuilds the graph index. Entries with no valid tag or that are already registered are silently ignored.
        /// </summary>
        /// <param name="entry">The runtime entry to register.</param>
        public void RegisterEntry(DialogueEntry entry)
        {
            if (entry == null || entry.ResolvedTag.Id == 0) return;
            if (!_runtimeEntries.Contains(entry))
            {
                _runtimeEntries.Add(entry);
                RebuildIndex();
            }
        }

        /// <summary>
        /// Registers a collection of runtime-created <see cref="DialogueEntry"/> instances and rebuilds the graph
        /// index once if any new entries were added. Entries with no valid tag or that are already registered are skipped.
        /// </summary>
        /// <param name="entries">The entries to register; <c>null</c> is silently ignored.</param>
        public void RegisterEntries(IEnumerable<DialogueEntry> entries)
        {
            if (entries == null) return;
            bool changed = false;
            foreach (var e in entries)
            {
                if (e == null || e.ResolvedTag.Id == 0) continue;
                if (!_runtimeEntries.Contains(e)) { _runtimeEntries.Add(e); changed = true; }
            }
            if (changed) RebuildIndex();
        }

        /// <summary>
        /// Removes the runtime entry identified by <paramref name="tag"/> and rebuilds the graph index.
        /// </summary>
        /// <param name="tag">The tag whose corresponding runtime entry should be removed.</param>
        public void UnregisterEntry(GameplayTag tag)
        {
            int idx = _runtimeEntries.FindIndex(e => e.ResolvedTag.Id == tag.Id);
            if (idx >= 0) { _runtimeEntries.RemoveAt(idx); RebuildIndex(); }
        }

        /// <summary>
        /// Forces a full graph index rebuild. Call this after making external modifications to runtime entries
        /// that bypass the normal registration API.
        /// </summary>
        public void RefreshIndex() => RebuildIndex();

        /// <summary>
        /// Finds and returns the <see cref="DialogueEntry"/> with the given tag across all registered data sources,
        /// or <c>null</c> if not found.
        /// </summary>
        /// <param name="tag">The GameplayTag identifying the entry to look up.</param>
        /// <returns>The matching <see cref="DialogueEntry"/>, or <c>null</c>.</returns>
        public DialogueEntry FindEntry(GameplayTag tag) => FindEntryInternal(tag.Id);

        // ── Internal lookups used by OghamSession ──────────────────────────────

        internal DialogueEntry FindEntryInternal(ulong id) =>
            _entryIndex.TryGetValue(id, out var loc) ? loc.entry : null;

        internal void CollectDescendants(ulong entryId, HashSet<ulong> results)
        {
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

        // ── Index build ────────────────────────────────────────────────────────

        private void RebuildIndex()
        {
            _entryIndex.Clear();
            _childIndex.Clear();

            for (int i = 0; i < _assets.Count; i++)
                IndexEntries(_assets[i]?.Entries, i);

            IndexEntries(_runtimeEntries, -1);
        }

        private void IndexEntries(List<DialogueEntry> entries, int assetIdx)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var id = entry.ResolvedTag.Id;
                if (id == 0) continue;
                _entryIndex.TryAdd(id, (assetIdx, entry));
            }

            foreach (var entry in entries)
            {
                var parentId = entry.ResolvedTag.Id;
                if (parentId == 0) continue;
                foreach (var opt in entry.Options)
                {
                    var childId = opt.ResolvedTargetEntry.Id;
                    if (childId == 0) continue;
                    if (!_childIndex.TryGetValue(parentId, out var set))
                    {
                        set = new HashSet<ulong>();
                        _childIndex[parentId] = set;
                    }
                    set.Add(childId);
                }
            }
        }
    }
}
