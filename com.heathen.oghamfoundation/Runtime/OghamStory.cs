using System;
using System.Collections.Generic;
using Heathen.GameplayTags;
#if UNITY_ENTITIES
using Unity.Collections;
#endif

namespace Heathen.Ogham
{
    // A named story instance: owns graph index, narrative state, and conversation history.
    // Multiple OghamStory instances can coexist; Storyteller manages the collection.
    // The graph (entry/child index) is immutable after data registration.
    // The session (state, history, current entry) is swappable via Restore().
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

        public GameplayTag                     Id                { get; }
        public bool                            IsActive          => _isActive;
        public StoryNode                       CurrentNode       => _currentNode;
        public IReadOnlyList<StoryOption>      CurrentOptions    => _currentOptions;
        // All options for the current node, including those whose conditions are not met.
        // IsActive is false on gated options. Use this to style inline Ogham:// links.
        public IReadOnlyList<StoryOption>      CurrentAllOptions => _currentAllOptions;
        public GameplayTagCollection           NarrativeState    => _state;
        public IReadOnlyList<HistoryEntry>     History           => _history;

        // StoryId is the first parameter on every event so listeners can distinguish origin.
        public event Action<GameplayTag, StoryNode>   OnEntered;
        public event Action<GameplayTag, StoryOption> OnChoice;
        public event Action<GameplayTag, bool>        OnClosed;

        public OghamStory(GameplayTag id)
        {
            Id = id;
        }

        // ── Asset registration ────────────────────────────────────────────────

        public void RegisterData(OghamData data)
        {
            if (data == null || _assets.Contains(data)) return;
            _assets.Add(data);
            RebuildIndex();
        }

        public void RegisterData(OghamCompiledData data)
        {
            if (data == null || _compiledAssets.Contains(data)) return;
            _compiledAssets.Add(data);
            RebuildIndex();
        }

        public void UnregisterData(OghamData data)
        {
            if (_assets.Remove(data)) RebuildIndex();
        }

        public void UnregisterData(OghamCompiledData data)
        {
            if (_compiledAssets.Remove(data)) RebuildIndex();
        }

        public void UnregisterAll()
        {
            _assets.Clear();
            _compiledAssets.Clear();
            _runtimeEntries.Clear();
            _entryIndex.Clear();
            _childIndex.Clear();
        }

        // ── Runtime entry registration ────────────────────────────────────────

        // Register a single entry created at runtime (e.g. from a mod or UGC manifest).
        public void RegisterEntry(DialogueEntry entry)
        {
            if (entry == null || entry.ResolvedTag.Id == 0) return;
            if (!_runtimeEntries.Contains(entry))
            {
                _runtimeEntries.Add(entry);
                RebuildIndex();
            }
        }

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

        public void UnregisterEntry(GameplayTag tag)
        {
            int idx = _runtimeEntries.FindIndex(e => e.ResolvedTag.Id == tag.Id);
            if (idx >= 0) { _runtimeEntries.RemoveAt(idx); RebuildIndex(); }
        }

        // Force a full index rebuild after external modifications to runtime entries.
        public void RefreshIndex() => RebuildIndex();

        // ── Conversation ──────────────────────────────────────────────────────

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

        public void Close(bool interrupted = false) => CloseInternal(interrupted);

        // Navigate back to a previously-visited entry.
        // Clears narrative-state tags for all descendant entries (not side-effect tags).
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

        // ── Query ─────────────────────────────────────────────────────────────

        public DialogueEntry FindEntry(GameplayTag tag) => FindEntryInternal(tag.Id);

        // ── State / History management ────────────────────────────────────────

        public void Execute(params GameplayTagOperation[] ops)
        {
            foreach (var op in ops)
                op.Apply(_state);
        }

        public void ClearNarrativeState() => _state.Clear();

        public void ClearNarrativeState(GameplayTag tag)
        {
            var toRemove = _state.GetMatchingTags(tag);
            foreach (var t in toRemove)
                _state.RemoveTag(t);
        }

        public void ClearHistory() => _history.Clear();

        public void ClearHistory(int steps)
        {
            int count = Math.Min(steps, _history.Count);
            if (count > 0)
                _history.RemoveRange(_history.Count - count, count);
        }

        // ── Persistence ───────────────────────────────────────────────────────

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
        // Caller-owned NativeHashMap for read-only job access. Caller must Dispose.
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
