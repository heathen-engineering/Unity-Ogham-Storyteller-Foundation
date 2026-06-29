using System;
using System.Collections.Generic;
using Heathen.GameplayTags;
#if UNITY_ENTITIES
using Unity.Collections;
#endif

namespace Heathen.Ogham
{
    /// <summary>
    /// A single play <em>session</em> of an <see cref="OghamStory"/> definition: it owns the mutable narrative
    /// state (the session's variables, as a <see cref="GameplayTagCollection"/>), the conversation history, and
    /// the current node, and drives navigation. The backing definition is immutable and shared, so many sessions
    /// can play the same story independently.
    /// <para>
    /// The session's scope is the consumer's choice — Ogham does not prescribe it. Create one per world (the
    /// default the <see cref="StorytellerSubsystem"/> provides), per character, per save-slot, or a throwaway for
    /// a stateless one-off. A story can start, end, and restart freely within any scope; the session, not the
    /// story, is what carries and bounds state.
    /// </para>
    /// </summary>
    public class OghamSession
    {
        private readonly OghamStory             _story;
        private readonly GameplayTagCollection  _state   = new();
        private readonly List<HistoryEntry>     _history = new();

        private bool   _isActive;
        private ulong  _currentEntryId;
        private StoryNode                  _currentNode;
        private IReadOnlyList<StoryOption> _currentOptions    = Array.Empty<StoryOption>();
        private IReadOnlyList<StoryOption> _currentAllOptions = Array.Empty<StoryOption>();

        /// <summary>The immutable story definition this session plays.</summary>
        public OghamStory                      Story             => _story;
        /// <summary>The GameplayTag that identifies the story being played (the definition's <see cref="OghamStory.Id"/>).</summary>
        public GameplayTag                     Id                => _story.Id;
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
        /// <summary>
        /// The live per-session narrative state: the GameplayTag collection holding this session's variables,
        /// traversal markers, and option selections, updated by entry and option operations. This is the state
        /// that <see cref="OghamVariables"/> tokens read from.
        /// </summary>
        public GameplayTagCollection           NarrativeState    => _state;
        /// <summary>The ordered history of entries visited and options chosen during this session.</summary>
        public IReadOnlyList<HistoryEntry>     History           => _history;

        /// <summary>Raised when the session enters a new dialogue node. The first parameter is the story <see cref="Id"/>.</summary>
        public event Action<GameplayTag, StoryNode>   OnEntered;
        /// <summary>Raised when an option is selected, before navigation. The first parameter is the story <see cref="Id"/>.</summary>
        public event Action<GameplayTag, StoryOption> OnChoice;
        /// <summary>
        /// Raised when the conversation ends. The only way a story ends is a deliberate exit (an option with no
        /// target, or an explicit <see cref="Close"/>); there is no separate "interrupted" state. The parameter
        /// is the story <see cref="Id"/>.
        /// </summary>
        public event Action<GameplayTag>              OnClosed;

        /// <summary>Initialises a new session that plays the given story definition.</summary>
        /// <param name="story">The definition to play. Must not be <c>null</c>.</param>
        public OghamSession(OghamStory story)
        {
            _story = story ?? throw new ArgumentNullException(nameof(story));
        }

        /// <summary>Finds an entry in the backing definition. Convenience for <c>Story.FindEntry</c>.</summary>
        /// <param name="tag">The GameplayTag identifying the entry to look up.</param>
        /// <returns>The matching <see cref="DialogueEntry"/>, or <c>null</c>.</returns>
        public DialogueEntry FindEntry(GameplayTag tag) => _story.FindEntryInternal(tag.Id);

        // ── Conversation ───────────────────────────────────────────────────────

        /// <summary>
        /// Starts or restarts a conversation at the entry identified by <paramref name="nodeTag"/>.
        /// If a conversation is already active it is closed before the new one begins.
        /// </summary>
        /// <param name="nodeTag">The tag of the dialogue entry to enter.</param>
        /// <returns><c>true</c> when the entry was found and entered successfully; <c>false</c> if not found.</returns>
        public bool Enter(GameplayTag nodeTag)
        {
            var entry = _story.FindEntryInternal(nodeTag.Id);
            if (entry == null) return false;

            if (_isActive) CloseInternal();

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
        /// <returns>
        /// <c>true</c> whenever a valid, condition-passing option was accepted, regardless of whether it
        /// navigates to a target or ends the conversation. <c>false</c> only when no conversation is active
        /// or the option is not currently available (missing or gated by its conditions).
        /// </returns>
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
                // Deliberate exit: an option with no target ends the conversation.
                CloseInternal();
            }
            else
            {
                var target = _story.FindEntryInternal(chosenTarget.Id);
                // A dangling target is a graph error, not a rejected choice: the option was valid and
                // accepted, so report success and end the conversation cleanly.
                if (target == null) { CloseInternal(); return true; }
                _currentEntryId = chosenTarget.Id;
                EnterEntry(target);
            }

            return true;
        }

        /// <summary>Ends the current conversation and fires <see cref="OnClosed"/>.</summary>
        public void Close() => CloseInternal();

        /// <summary>
        /// Re-surfaces the current entry without re-running its On-Enter operations, rebuilding its options
        /// and firing <see cref="OnEntered"/>. Use this after <see cref="Restore"/> to resume a saved session
        /// and re-display the node the player was last on. Has no effect when there is no current entry,
        /// the entry is not found, or the current entry is a Fork (which is never a resting position).
        /// </summary>
        /// <returns><c>true</c> when the current entry was re-surfaced; otherwise <c>false</c>.</returns>
        public bool Resume()
        {
            if (_currentEntryId == 0) return false;
            var entry = _story.FindEntryInternal(_currentEntryId);
            if (entry == null || entry.Mode == OghamNodeMode.Fork) return false;

            _isActive = true;
            var (active, all) = BuildOptions(entry);
            _currentOptions    = active;
            _currentAllOptions = all;
            _currentNode       = new StoryNode(entry, active, all);
            OnEntered?.Invoke(Id, _currentNode);
            return true;
        }

        /// <summary>
        /// Navigates back to a previously-visited entry and clears narrative-state tags for all descendant entries
        /// (not general side-effect tags), then re-enters it through the normal entry path so On-Enter operations
        /// and Fork routing are honoured. Has no effect when no conversation is active or the entry is not found.
        /// </summary>
        /// <param name="entryTag">The tag of the entry to return to.</param>
        /// <returns><c>true</c> when the entry was found and re-entered; <c>false</c> otherwise.</returns>
        public bool ReturnTo(GameplayTag entryTag)
        {
            if (!_isActive) return false;
            var entry = _story.FindEntryInternal(entryTag.Id);
            if (entry == null) return false;

            var descendants = new HashSet<ulong>();
            _story.CollectDescendants(entryTag.Id, descendants);
            foreach (var id in descendants)
                _state.Apply(new GameplayTag(id), GameplayTagArithmetic.Set, 0);

            _currentEntryId = entryTag.Id;
            EnterEntry(entry);
            return true;
        }

        // ── Narrative state ────────────────────────────────────────────────────

        /// <summary>
        /// Applies one or more <see cref="GameplayTagOperation"/> instances directly to this session's narrative state.
        /// </summary>
        /// <param name="ops">The operations to apply in order.</param>
        public void Execute(params GameplayTagOperation[] ops)
        {
            foreach (var op in ops)
                op.Apply(_state);
        }

        /// <summary>Clears all narrative-state tags for this session. Does not clear the history.</summary>
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

        // ── Save / load ────────────────────────────────────────────────────────

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
        /// history, and current entry. The session is marked inactive after restore; call <see cref="Enter"/> or
        /// <see cref="Resume"/> to resume.
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

        // ── ECS / Burst ────────────────────────────────────────────────────────

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

        // ── Private ────────────────────────────────────────────────────────────

        private void CloseInternal()
        {
            if (!_isActive) return;
            _isActive          = false;
            _currentEntryId    = 0;
            _currentNode       = null;
            _currentOptions    = Array.Empty<StoryOption>();
            _currentAllOptions = Array.Empty<StoryOption>();
            OnClosed?.Invoke(Id);
        }

        // Iterative entry: walks through any chain of Fork nodes until it reaches a displayable
        // Content node, the conversation ends, or a fork cycle is detected. Iteration (rather than
        // recursion) plus a visited-set means a malformed graph can never blow the stack — the
        // editor validates fork termination at compile time, and this is the runtime safety net.
        private void EnterEntry(DialogueEntry entry)
        {
            HashSet<ulong> visitedForks = null;

            while (true)
            {
                foreach (var op in entry.EntryOperations)
                    op.Apply(_state);

                if (entry.Mode != OghamNodeMode.Fork)
                {
                    var (activeOpts, allOpts) = BuildOptions(entry);
                    _currentOptions    = activeOpts;
                    _currentAllOptions = allOpts;
                    _currentNode       = new StoryNode(entry, activeOpts, allOpts);
                    OnEntered?.Invoke(Id, _currentNode);
                    return;
                }

                // Fork nodes route silently: evaluate routes, pick the first passing one.
                // No OnEntered event. No history entry. Player never sees this node.
                visitedForks ??= new HashSet<ulong>();
                if (!visitedForks.Add(entry.ResolvedTag.Id))
                {
                    UnityEngine.Debug.LogError(
                        $"[Ogham] Fork cycle detected re-entering '{entry.ResolvedTag.Id}' in story '{Id.Id}'. " +
                        "Closing the conversation. Every path out of a Fork must resolve to a node or end the story.");
                    CloseInternal();
                    return;
                }

                var (active, _) = BuildOptions(entry);
                var route = active.Count > 0 ? active[0] : null;
                if (route == null) { CloseInternal(); return; }

                foreach (var op in route.RawOption.Operations)
                    op.Apply(_state);

                var dest = route.RawOption.ResolvedTargetEntry;
                if (dest.Id == 0) { CloseInternal(); return; }

                var target = _story.FindEntryInternal(dest.Id);
                if (target == null) { CloseInternal(); return; }

                _currentEntryId = dest.Id;
                entry           = target;
            }
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
