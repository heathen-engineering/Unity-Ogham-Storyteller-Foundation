using System;
using System.Collections.Generic;
using Heathen.GameplayTags;
#if UNITY_ENTITIES
using Unity.Collections;
#endif

namespace Heathen.Ogham
{
    /// <summary>
    /// A named story instance that owns the graph index, narrative state, and conversation history for a single story.
    /// Multiple <see cref="OghamStory"/> instances can coexist; <see cref="Storyteller"/> manages the collection.
    /// The graph (entry and child index) is rebuilt on data registration. The session state, history, and
    /// current entry can be swapped atomically via <see cref="Restore"/>.
    /// </summary>
    public class OghamStory
    {
        private readonly List<OghamData>         _assets         = new();
        private readonly List<OghamCompiledData> _compiledAssets = new();
        private readonly List<DialogueEntry>     _runtimeEntries = new();
        private readonly Dictionary<ulong, (int assetIdx, DialogueEntry entry)> _entryIndex = new();
        private readonly Dictionary<ulong, HashSet<ulong>>                       _childIndex = new();

        private bool   _isActive;
        private ulong  _currentEntryId;
        private readonly GameplayTagCollection _state   = new();
        private readonly List<HistoryEntry>    _history = new();

        private StoryNode                  _currentNode;
        private IReadOnlyList<StoryOption> _currentOptions    = Array.Empty<StoryOption>();
        private IReadOnlyList<StoryOption> _currentAllOptions = Array.Empty<StoryOption>();

        /// <summary>The GameplayTag that uniquely identifies this story within the <see cref="Storyteller"/> registry.</summary>
        public GameplayTag                     Id                { get; }
        /// <summary>Returns <c>true</c> when a conversation is in progress and an entry has been entered.</summary>
        public bool                            IsActive          => _isActive;
        /// <summary>The <see cref="StoryNode"/> for the currently active dialogue entry, or <c>null</c> when no conversation is active.</summary>
        public StoryNode                       CurrentNode       => _currentNode;
        /// <summary>Options for the current node whose conditions are satisfied. Use this to populate button lists.</summary>
        public IReadOnlyList<StoryOption>      CurrentOptions    => _currentOptions;
        /// <summary>
        /// All options for the current node, including those whose conditions are not met.
        /// <see cref="StoryOption.IsActive"/> is <c>false</c> on gated options. Use this to style inline <c>Ogham://</c> links.
        /// </summary>
        public IReadOnlyList<StoryOption>      CurrentAllOptions => _currentAllOptions;
        /// <summary>The live narrative-state collection for this story, updated by entry and option operations.</summary>
        public GameplayTagCollection           NarrativeState    => _state;
        /// <summary>The ordered history of entries visited and options chosen during this session.</summary>
        public IReadOnlyList<HistoryEntry>     History           => _history;

        /// <summary>
        /// Raised when the story enters a new dialogue node. The first parameter is this story's <see cref="Id"/>
        /// so listeners subscribed to multiple stories can distinguish the origin.
        /// </summary>
        public event Action<GameplayTag, StoryNode>   OnEntered;
        /// <summary>
        /// Raised when an option is selected, before navigation to the next node.
        /// The first parameter is this story's <see cref="Id"/>.
        /// </summary>
        public event Action<GameplayTag, StoryOption> OnChoice;
        /// <summary>
        /// Raised when the conversation ends, whether normally or interrupted.
        /// The first parameter is this story's <see cref="Id"/>; the second indicates whether it was interrupted.
        /// </summary>
        public event Action<GameplayTag, bool>        OnClosed;

        /// <summary>
        /// Initialises a new story with the given identity tag. Register this instance with
        /// <see cref="Storyteller.RegisterStory(OghamStory, bool)"/> to expose it to the global event system.
        /// </summary>
        /// <param name="id">The GameplayTag that uniquely identifies this story.</param>
        public OghamStory(GameplayTag id)
        {
            Id = id;
        }

        /// <summary>
        /// Registers an <see cref="OghamData"/> authoring asset with this story and rebuilds the graph index.
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
        /// Registers an <see cref="OghamCompiledData"/> runtime asset with this story and rebuilds the graph index.
        /// Duplicate registrations are silently ignored.
        /// </summary>
        /// <param name="data">The compiled asset to add; <c>null</c> is silently ignored.</param>
        public void RegisterData(OghamCompiledData data)
        {
            if (data == null || _compiledAssets.Contains(data)) return;
            _compiledAssets.Add(data);
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
        /// Removes a previously registered <see cref="OghamCompiledData"/> runtime asset and rebuilds the graph index.
        /// </summary>
        /// <param name="data">The compiled asset to remove.</param>
        public void UnregisterData(OghamCompiledData data)
        {
            if (_compiledAssets.Remove(data)) RebuildIndex();
        }

        /// <summary>
        /// Removes all registered data assets and clears the graph index. The story becomes empty until new data is registered.
        /// </summary>
        public void UnregisterAll()
        {
            _assets.Clear();
            _compiledAssets.Clear();
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
        /// Starts or restarts a conversation at the entry identified by <paramref name="nodeTag"/>.
        /// If a conversation is already active it is closed (interrupted) before the new one begins.
        /// </summary>
        /// <param name="nodeTag">The tag of the dialogue entry to enter.</param>
        /// <returns><c>true</c> when the entry was found and entered successfully; <c>false</c> if not found.</returns>
        public bool Enter(GameplayTag nodeTag)
        {
            var entry = FindEntryInternal(nodeTag.Id);
            if (entry == null) return false;

            if (_isActive) CloseInternal(interrupted: true);

            _isActive       = true;
            _currentEntryId = nodeTag.Id;
            EnterEntry(entry);
            return true;
        }

        /// <summary>
        /// Selects the active option identified by <paramref name="optionTag"/>, applies its operations, fires
        /// <see cref="OnChoice"/>, and navigates to the target entry (or closes if no target is set).
        /// </summary>
        /// <param name="optionTag">The tag of the option to select. Must be in <see cref="CurrentOptions"/>.</param>
        /// <returns><c>true</c> when the option was found and applied; <c>false</c> if no conversation is active or the option was not found.</returns>
        public bool Choose(GameplayTag optionTag)
        {
            if (!_isActive) return false;

            var storyOption = FindCurrentOption(optionTag);
            if (storyOption == null) return false;

            var chosen = storyOption.RawOption;

            // Fire before navigation so listeners can react to the selection itself.
            OnChoice?.Invoke(Id, storyOption);

            foreach (var op in chosen.Operations)
                op.Apply(_state);

            var chosenTag    = chosen.ResolvedTag;
            var chosenTarget = chosen.ResolvedTargetEntry;

            _state.Apply(new GameplayTag(_currentEntryId), GameplayTagArithmetic.Set, chosenTag.Id);
            _history.Add(new HistoryEntry { EntryId = _currentEntryId, SelectedOption = chosenTag.Id });

            if (chosenTarget.Id == 0)
            {
                CloseInternal(interrupted: false);
            }
            else
            {
                var target = FindEntryInternal(chosenTarget.Id);
                if (target == null) { CloseInternal(interrupted: false); return false; }
                _currentEntryId = chosenTarget.Id;
                EnterEntry(target);
            }

            return true;
        }

        /// <summary>
        /// Ends the current conversation. Fires <see cref="OnClosed"/> with the interrupted flag.
        /// </summary>
        /// <param name="interrupted"><c>true</c> when the conversation was closed externally rather than by option selection.</param>
        public void Close(bool interrupted = false) => CloseInternal(interrupted);

        /// <summary>
        /// Navigates back to a previously-visited entry and clears narrative-state tags for all descendant entries
        /// (not general side-effect tags). Has no effect when no conversation is active or the entry is not found.
        /// </summary>
        /// <param name="entryTag">The tag of the entry to return to.</param>
        /// <returns><c>true</c> when the entry was found and re-entered; <c>false</c> otherwise.</returns>
        public bool ReturnTo(GameplayTag entryTag)
        {
            if (!_isActive) return false;
            var entry = FindEntryInternal(entryTag.Id);
            if (entry == null) return false;

            var descendants = new HashSet<ulong>();
            CollectDescendants(entryTag.Id, descendants);
            foreach (var id in descendants)
                _state.Apply(new GameplayTag(id), GameplayTagArithmetic.Set, 0);

            _currentEntryId = entryTag.Id;
            var (active, all) = BuildOptions(entry);
            _currentOptions    = active;
            _currentAllOptions = all;
            _currentNode       = new StoryNode(entry, active, all);
            OnEntered?.Invoke(Id, _currentNode);
            return true;
        }

        /// <summary>
        /// Finds and returns the <see cref="DialogueEntry"/> with the given tag across all registered data sources,
        /// or <c>null</c> if not found.
        /// </summary>
        /// <param name="tag">The GameplayTag identifying the entry to look up.</param>
        /// <returns>The matching <see cref="DialogueEntry"/>, or <c>null</c>.</returns>
        public DialogueEntry FindEntry(GameplayTag tag) => FindEntryInternal(tag.Id);

        /// <summary>
        /// Applies one or more <see cref="GameplayTagOperation"/> instances directly to this story's narrative state.
        /// </summary>
        /// <param name="ops">The operations to apply in order.</param>
        public void Execute(params GameplayTagOperation[] ops)
        {
            foreach (var op in ops)
                op.Apply(_state);
        }

        /// <summary>Clears all narrative-state tags for this story. Does not clear the history.</summary>
        public void ClearNarrativeState() => _state.Clear();

        /// <summary>
        /// Clears all narrative-state tags that match or are beneath <paramref name="tag"/> in the tag hierarchy.
        /// </summary>
        /// <param name="tag">The root tag whose subtree of state values should be removed.</param>
        public void ClearNarrativeState(GameplayTag tag)
        {
            var toRemove = _state.GetMatchingTags(tag);
            foreach (var t in toRemove)
                _state.RemoveTag(t);
        }

        /// <summary>Removes all entries from the conversation history.</summary>
        public void ClearHistory() => _history.Clear();

        /// <summary>
        /// Removes the most recent <paramref name="steps"/> entries from the conversation history.
        /// </summary>
        /// <param name="steps">The number of recent history entries to remove. Clamped to the history count.</param>
        public void ClearHistory(int steps)
        {
            int count = Math.Min(steps, _history.Count);
            if (count > 0)
                _history.RemoveRange(_history.Count - count, count);
        }

        /// <summary>
        /// Creates a deep snapshot of the current session (narrative state, history, current entry) as an
        /// <see cref="OghamSaveState"/> that can be serialised and later restored via <see cref="Restore"/>.
        /// </summary>
        /// <param name="name">A human-readable label for the save state; defaults to "snapshot".</param>
        /// <returns>A new <see cref="OghamSaveState"/> representing the current session.</returns>
        public OghamSaveState Snapshot(string name = "snapshot")
        {
            var snap = new OghamSaveState
            {
                Uuid           = Guid.NewGuid().ToString(),
                Name           = name,
                StoryId        = Id.Id,
                CurrentEntryId = _currentEntryId,
                History        = new List<HistoryEntry>(_history),
            };
            foreach (var (tag, value) in _state.GetAll())
                snap.State.Apply(tag, GameplayTagArithmetic.Set, value);
            return snap;
        }

        /// <summary>
        /// Restores the session from a previously created <see cref="OghamSaveState"/>, replacing narrative state,
        /// history, and current entry. The story is marked inactive after restore; call <see cref="Enter"/> to resume.
        /// </summary>
        /// <param name="state">The save state to restore from.</param>
        public void Restore(OghamSaveState state)
        {
            _isActive       = false;
            _currentEntryId = state.CurrentEntryId;
            _state.Clear();
            foreach (var (tag, value) in state.State.GetAll())
                _state.Apply(tag, GameplayTagArithmetic.Set, value);
            _history.Clear();
            _history.AddRange(state.History);
            _currentNode       = null;
            _currentOptions    = Array.Empty<StoryOption>();
            _currentAllOptions = Array.Empty<StoryOption>();
        }

        // ── ECS / Burst ───────────────────────────────────────────────────────

#if UNITY_ENTITIES
        /// <summary>
        /// Returns a caller-owned <c>NativeHashMap</c> copy of the current narrative state for read-only access
        /// from Burst jobs. The caller is responsible for disposing the returned map.
        /// </summary>
        /// <param name="allocator">The allocator to use for the returned <c>NativeHashMap</c>.</param>
        /// <returns>A <c>NativeHashMap&lt;ulong, ulong&gt;</c> mapping tag IDs to state values.</returns>
        public Unity.Collections.NativeHashMap<ulong, ulong> GetStateSnapshot(Unity.Collections.Allocator allocator) =>
            _state.GetSnapshot(allocator);
#endif

        // ── Private ───────────────────────────────────────────────────────────

        private void CloseInternal(bool interrupted)
        {
            if (!_isActive) return;
            _isActive          = false;
            _currentEntryId    = 0;
            _currentNode       = null;
            _currentOptions    = Array.Empty<StoryOption>();
            _currentAllOptions = Array.Empty<StoryOption>();
            OnClosed?.Invoke(Id, interrupted);
        }

        private void RebuildIndex()
        {
            _entryIndex.Clear();
            _childIndex.Clear();

            for (int i = 0; i < _assets.Count; i++)
                IndexEntries(_assets[i]?.Entries, i);

            for (int i = 0; i < _compiledAssets.Count; i++)
                IndexEntries(_compiledAssets[i]?.Entries, _assets.Count + i);

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

        private DialogueEntry FindEntryInternal(ulong id) =>
            _entryIndex.TryGetValue(id, out var loc) ? loc.entry : null;

        private void CollectDescendants(ulong entryId, HashSet<ulong> results)
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

        private void EnterEntry(DialogueEntry entry)
        {
            foreach (var op in entry.EntryOperations)
                op.Apply(_state);

            if (entry.Mode == OghamNodeMode.Fork)
            {
                // Fork nodes route silently: evaluate routes, pick the first passing one.
                // No OnEntered event. No history entry. Player never sees this node.
                var (active, _) = BuildOptions(entry);
                var route = active.Count > 0 ? active[0] : null;
                if (route == null) { CloseInternal(interrupted: false); return; }

                foreach (var op in route.RawOption.Operations)
                    op.Apply(_state);

                var dest = route.RawOption.ResolvedTargetEntry;
                if (dest.Id == 0)
                {
                    CloseInternal(interrupted: false);
                    return;
                }
                var target = FindEntryInternal(dest.Id);
                if (target == null) { CloseInternal(interrupted: false); return; }
                _currentEntryId = dest.Id;
                EnterEntry(target);
                return;
            }

            var (activeOpts, allOpts) = BuildOptions(entry);
            _currentOptions    = activeOpts;
            _currentAllOptions = allOpts;
            _currentNode       = new StoryNode(entry, activeOpts, allOpts);
            OnEntered?.Invoke(Id, _currentNode);
        }

        // Returns (active, all): active contains only condition-passing options;
        // all contains every option with IsActive reflecting whether its conditions passed.
        private (IReadOnlyList<StoryOption> active, IReadOnlyList<StoryOption> all) BuildOptions(DialogueEntry entry)
        {
            var all    = new List<StoryOption>(entry.Options.Count);
            var active = new List<StoryOption>();
            foreach (var opt in entry.Options)
            {
                var passes = GameplayTagCondition.EvaluateAll(opt.Conditions, _state);
                var so     = new StoryOption(opt, this) { IsActive = passes };
                all.Add(so);
                if (passes) active.Add(so);
            }
            return (active.AsReadOnly(), all.AsReadOnly());
        }

        private StoryOption FindCurrentOption(GameplayTag tag)
        {
            foreach (var opt in _currentOptions)
                if (opt.Tag.Id == tag.Id) return opt;
            return null;
        }
    }
}
