using System;
using System.Collections.Generic;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // All narrative logic — no MonoBehaviour, no ScriptableObject dependency.
    // OghamProcessor (MonoBehaviour) and any ECS system both call into this class.
    public class OghamProcessorCore
    {
        private readonly List<OghamData>         _assets         = new();
        private readonly List<OghamCompiledData> _compiledAssets = new();
        // tagId -> (asset index, entry reference) — rebuilt whenever assets change.
        private readonly Dictionary<ulong, (int assetIdx, DialogueEntry entry)> _entryIndex = new();
        // parentId -> set of child IDs across all registered assets.
        private readonly Dictionary<ulong, HashSet<ulong>> _childIndex = new();

        private bool _isActive;
        private ulong _currentEntryId;
        private readonly GameplayTagCollection _state = new();
        private readonly List<HistoryEntry> _history = new();

        public bool IsConversationActive => _isActive;
        public DialogueEntry CurrentEntry => _isActive ? FindEntry(_currentEntryId) : null;
        public IReadOnlyList<HistoryEntry> History => _history;
        public GameplayTagCollection NarrativeState => _state;

        public event Action<DialogueEntry, List<DialogueOption>> OnDialogueEntered;
        public event Action<bool> OnDialogueClosed;

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
            _entryIndex.Clear();
            _childIndex.Clear();
        }

        // ── Conversation ──────────────────────────────────────────────────────

        public bool StartConversation(GameplayTag entryTag)
        {
            var entry = FindEntry(entryTag.Id);
            if (entry == null) return false;

            if (_isActive) CloseConversation(interrupted: true);

            _isActive = true;
            _currentEntryId = entryTag.Id;
            EnterEntry(entry);
            return true;
        }

        public bool SelectOption(GameplayTag optionTag)
        {
            if (!_isActive) return false;
            var entry = FindEntry(_currentEntryId);
            if (entry == null) return false;

            DialogueOption chosen = null;
            foreach (var opt in entry.Options)
            {
                if (opt.Tag.Id != optionTag.Id) continue;
                if (GameplayTagCondition.EvaluateAll(opt.Conditions, _state))
                    chosen = opt;
                break;
            }
            if (chosen == null) return false;

            foreach (var op in chosen.Operations)
                op.Apply(_state);

            // Record which option was chosen for this entry in the narrative state.
            _state.Apply(new GameplayTag(_currentEntryId), GameplayTagArithmetic.Set, chosen.Tag.Id);
            _history.Add(new HistoryEntry { EntryId = _currentEntryId, SelectedOption = chosen.Tag.Id });

            if (chosen.TargetEntry.Id == 0)
            {
                CloseConversation(interrupted: false);
            }
            else
            {
                var target = FindEntry(chosen.TargetEntry.Id);
                if (target == null) { CloseConversation(interrupted: false); return false; }
                _currentEntryId = chosen.TargetEntry.Id;
                EnterEntry(target);
            }

            return true;
        }

        // SelectOptionByTag and SelectOption are identical — the tag IS the option identifier.
        // Provided as a distinct method for protocol-link call sites (ogham://Option.Tag URLs).
        public bool SelectOptionByTag(GameplayTag optionTag) => SelectOption(optionTag);

        public void CloseConversation(bool interrupted = false)
        {
            if (!_isActive) return;
            _isActive = false;
            _currentEntryId = 0;
            OnDialogueClosed?.Invoke(interrupted);
        }

        // Navigate back to a previously-visited entry. Clears narrative-state tags for all
        // descendant entries (not side-effect tags — see ReturnTo design note in spec).
        public bool ReturnTo(GameplayTag entryTag)
        {
            if (!_isActive) return false;
            var entry = FindEntry(entryTag.Id);
            if (entry == null) return false;

            var descendants = new HashSet<ulong>();
            CollectDescendants(entryTag.Id, descendants);
            foreach (var id in descendants)
                _state.Apply(new GameplayTag(id), GameplayTagArithmetic.Set, 0);

            _currentEntryId = entryTag.Id;
            OnDialogueEntered?.Invoke(entry, GetAvailableOptions(entry));
            return true;
        }

        // ── Query ─────────────────────────────────────────────────────────────

        public List<DialogueOption> GetAvailableOptions()
        {
            var entry = CurrentEntry;
            return entry != null ? GetAvailableOptions(entry) : new List<DialogueOption>();
        }

        public DialogueEntry FindEntry(GameplayTag tag) => FindEntry(tag.Id);

        // ── Save / Load ───────────────────────────────────────────────────────

        public OghamSaveState CreateSaveState(string name)
        {
            var snap = new OghamSaveState
            {
                Uuid           = Guid.NewGuid().ToString(),
                Name           = name,
                CurrentEntryId = _currentEntryId,
                History        = new List<HistoryEntry>(_history),
            };
            foreach (var (tag, value) in _state.GetAll())
                snap.State.Apply(tag, GameplayTagArithmetic.Set, value);
            return snap;
        }

        public void LoadSaveState(OghamSaveState state)
        {
            _currentEntryId = state.CurrentEntryId;
            _state.Clear();
            foreach (var (tag, value) in state.State.GetAll())
                _state.Apply(tag, GameplayTagArithmetic.Set, value);
            _history.Clear();
            _history.AddRange(state.History);
        }

        public void ClearState()
        {
            _state.Clear();
            _history.Clear();
        }

        public void ApplyOperation(GameplayTagOperation op) => op.Apply(_state);

        // ── Private ───────────────────────────────────────────────────────────

        private void RebuildIndex()
        {
            _entryIndex.Clear();
            _childIndex.Clear();

            for (int i = 0; i < _assets.Count; i++)
                IndexEntries(_assets[i]?.Entries, i);

            for (int i = 0; i < _compiledAssets.Count; i++)
                IndexEntries(_compiledAssets[i]?.Entries, _assets.Count + i);
        }

        private void IndexEntries(List<DialogueEntry> entries, int assetIdx)
        {
            if (entries == null) return;
            foreach (var entry in entries)
            {
                var id = entry.Tag.Id;
                if (id == 0) continue;
                _entryIndex.TryAdd(id, (assetIdx, entry));
            }

            // Derive child relationships from connections so ReturnTo knows
            // which entries to clear when stepping back in the graph.
            foreach (var entry in entries)
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
        }

        private DialogueEntry FindEntry(ulong id) =>
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
            OnDialogueEntered?.Invoke(entry, GetAvailableOptions(entry));
        }

        private List<DialogueOption> GetAvailableOptions(DialogueEntry entry)
        {
            var result = new List<DialogueOption>(entry.Options.Count);
            foreach (var opt in entry.Options)
                if (GameplayTagCondition.EvaluateAll(opt.Conditions, _state))
                    result.Add(opt);
            return result;
        }
    }
}
