using System;
using System.Collections.Generic;
using Heathen;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// World-scoped owner of the Ogham Storyteller system: manages the per-world default <see cref="OghamSession"/>
    /// instances for one <see cref="World"/>, forwards their events, and tracks the main session and per-story
    /// presenters. Each world has its own isolated set of sessions (a pause world and a gameplay world, or
    /// per-player worlds, never share narrative state).
    ///
    /// <para>This provides the convenient default scope (one session per story tag per world). Consumers that need
    /// other scopes — per character, per save-slot, or a stateless one-off — create their own
    /// <see cref="OghamSession"/> from a definition (<see cref="OghamStoryCatalog.GetDefinition"/>) and optionally
    /// register it with <see cref="RegisterSession"/> for global addressing and events.</para>
    ///
    /// <para>The static <see cref="Storyteller"/> facade routes to the main world's instance for the common
    /// single-world case; multi-world code resolves the instance with <c>world.Get&lt;StorytellerSubsystem&gt;()</c>.</para>
    /// </summary>
    [Subsystem(SubsystemScope.World)]
    public sealed class StorytellerSubsystem : Subsystem, ISubsystemDebug
    {
        private readonly Dictionary<ulong, OghamSession>    _sessions   = new();
        private readonly Dictionary<ulong, IStoryProcessor> _processors = new();
        private OghamSession _mainSession;

        private static readonly IReadOnlyList<StoryOption>  _emptyOptions = Array.Empty<StoryOption>();
        private static readonly IReadOnlyList<HistoryEntry> _emptyHistory = Array.Empty<HistoryEntry>();

        /// <summary>Raised when a registered session enters a node. The first parameter identifies which story fired.</summary>
        public event Action<GameplayTag, StoryNode>   OnEntered;
        /// <summary>Raised when an option is selected, before navigation. The first parameter identifies the story.</summary>
        public event Action<GameplayTag, StoryOption> OnChoice;
        /// <summary>Raised when a session's conversation ends. The first parameter identifies which story fired.</summary>
        public event Action<GameplayTag>              OnClosed;

        // ── Debug ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public IEnumerable<(string label, string value)> GetDebugInfo()
        {
            yield return ("Sessions", _sessions.Count.ToString());
            var main = _mainSession;
            yield return ("Main story", main == null
                ? "(none)"
                : GameplayTagRegistry.GetName(main.Id.Id) ?? main.Id.Id.ToString());
            yield return ("Active", IsActive ? "yes" : "no");
        }

        // ── Sessions ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the default session for <paramref name="definition"/> in this world, creating and registering it
        /// on first request. This is the per-world default scope; for other scopes create an <see cref="OghamSession"/>
        /// directly.
        /// </summary>
        public OghamSession OpenSession(OghamStory definition, bool setAsMain = false)
        {
            if (definition == null) return null;
            if (_sessions.TryGetValue(definition.Id.Id, out var existing))
            {
                if (setAsMain) _mainSession = existing;
                return existing;
            }
            return AddSession(new OghamSession(definition), setAsMain);
        }

        /// <summary>
        /// Returns the default session for the story addressed by <paramref name="storyTag"/>, building its baked
        /// definition (via <see cref="OghamStoryCatalog"/>) as needed. Returns <c>null</c> when no story is registered.
        /// </summary>
        public OghamSession OpenSession(GameplayTag storyTag, bool setAsMain = false)
        {
            var def = OghamStoryCatalog.GetDefinition(storyTag);
            return def == null ? null : OpenSession(def, setAsMain);
        }

        /// <summary>
        /// Registers a pre-created session (for a custom scope) so it receives global addressing and event
        /// forwarding. No effect if a session for the same story id is already registered.
        /// </summary>
        public void RegisterSession(OghamSession session, bool setAsMain = false)
        {
            if (session == null || _sessions.ContainsKey(session.Id.Id)) return;
            AddSession(session, setAsMain);
        }

        /// <summary>
        /// Back-compat: registers a definition by opening (and returning) its default session.
        /// Prefer <see cref="OpenSession(OghamStory, bool)"/>.
        /// </summary>
        public void RegisterStory(OghamStory definition, bool setAsMain = false) => OpenSession(definition, setAsMain);

        /// <summary>Unregisters a session and releases its event subscriptions. The instance is not destroyed.</summary>
        public void UnregisterStory(GameplayTag storyId)
        {
            if (!_sessions.TryGetValue(storyId.Id, out var session)) return;
            session.OnEntered -= ForwardEntered;
            session.OnChoice  -= ForwardChoice;
            session.OnClosed  -= ForwardClosed;
            _sessions.Remove(storyId.Id);
            _processors.Remove(storyId.Id);

            if (_mainSession == session)
            {
                _mainSession = null;
                foreach (var s in _sessions.Values) { _mainSession = s; break; }
            }
        }

        /// <summary>The registered session with the given story id, or null.</summary>
        public OghamSession GetStory(GameplayTag storyId) =>
            _sessions.TryGetValue(storyId.Id, out var session) ? session : null;

        /// <summary>True when a session with the given story id is registered.</summary>
        public bool HasStory(GameplayTag storyId) => _sessions.ContainsKey(storyId.Id);

        /// <summary>Promotes a registered session to main. No effect when the story is not registered.</summary>
        public void SetMain(GameplayTag storyId)
        {
            if (_sessions.TryGetValue(storyId.Id, out var session))
                _mainSession = session;
        }

        /// <summary>The tag id of the current main story, or default when none is registered.</summary>
        public GameplayTag MainStoryId => _mainSession?.Id ?? default;

        // ── Processor ownership ───────────────────────────────────────────────

        /// <summary>Registers the single active presenter for a story; supersedes any previous one.</summary>
        public void AcquireStory(GameplayTag storyId, IStoryProcessor processor)
        {
            if (processor == null) return;
            if (_processors.TryGetValue(storyId.Id, out var prev) && prev != null && !ReferenceEquals(prev, processor))
                prev.OnSuperseded(storyId);
            _processors[storyId.Id] = processor;
        }

        /// <summary>Releases presentation rights, only when the processor is still the active presenter.</summary>
        public void ReleaseStory(GameplayTag storyId, IStoryProcessor processor)
        {
            if (_processors.TryGetValue(storyId.Id, out var cur) && ReferenceEquals(cur, processor))
                _processors.Remove(storyId.Id);
        }

        /// <summary>True when the processor is the active presenter for the given story.</summary>
        public bool IsProcessor(GameplayTag storyId, IStoryProcessor processor) =>
            _processors.TryGetValue(storyId.Id, out var cur) && ReferenceEquals(cur, processor);

        // ── Main-story conversation ───────────────────────────────────────────

        /// <summary>Starts a conversation at the given node in the main story.</summary>
        public bool Enter(GameplayTag nodeTag)   => _mainSession?.Enter(nodeTag)    ?? false;
        /// <summary>Selects an option in the main story.</summary>
        public bool Choose(GameplayTag optionTag)=> _mainSession?.Choose(optionTag) ?? false;
        /// <summary>Closes the main story's active conversation.</summary>
        public void Close()                      => _mainSession?.Close();
        /// <summary>Re-surfaces the main story's current node after a restore, without re-running On-Enter operations.</summary>
        public bool Resume()                     => _mainSession?.Resume()          ?? false;

        /// <summary>Starts a conversation at the given node in the specified story.</summary>
        public bool Enter(GameplayTag storyId, GameplayTag nodeTag)    => GetStory(storyId)?.Enter(nodeTag)    ?? false;
        /// <summary>Selects an option in the specified story.</summary>
        public bool Choose(GameplayTag storyId, GameplayTag optionTag) => GetStory(storyId)?.Choose(optionTag) ?? false;
        /// <summary>Closes the active conversation in the specified story.</summary>
        public void Close(GameplayTag storyId)                         => GetStory(storyId)?.Close();
        /// <summary>Re-surfaces the specified story's current node after a restore, without re-running On-Enter operations.</summary>
        public bool Resume(GameplayTag storyId)                        => GetStory(storyId)?.Resume()    ?? false;

        /// <summary>True when the main story has an active conversation.</summary>
        public bool                        IsActive   => _mainSession?.IsActive          ?? false;
        /// <summary>The current node for the main story, or null when no conversation is active.</summary>
        public StoryNode                   Data       => _mainSession?.CurrentNode;
        /// <summary>Active (condition-passing) options for the current node of the main story.</summary>
        public IReadOnlyList<StoryOption>  Options    => _mainSession?.CurrentOptions    ?? _emptyOptions;
        /// <summary>All options for the current node of the main story, including gated ones.</summary>
        public IReadOnlyList<StoryOption>  AllOptions => _mainSession?.CurrentAllOptions ?? _emptyOptions;
        /// <summary>The conversation history for the main story.</summary>
        public IReadOnlyList<HistoryEntry> History    => _mainSession?.History           ?? _emptyHistory;

        // ── Narrative state ───────────────────────────────────────────────────

        /// <summary>Narrative-state tags at or beneath the given path in the main story.</summary>
        public GameplayTagCollection ReadState(GameplayTag tag)
        {
            var result = new GameplayTagCollection();
            if (_mainSession == null) return result;
            CopyMatchingState(_mainSession.NarrativeState, tag, result);
            return result;
        }

        /// <summary>Applies operations to the main story's narrative state.</summary>
        public void Execute(params GameplayTagOperation[] ops) => _mainSession?.Execute(ops);

        /// <summary>Clears all narrative-state tags for the main story.</summary>
        public void ClearState() => _mainSession?.ClearNarrativeState();

        /// <summary>Clears narrative-state tags at or beneath the given path in the main story.</summary>
        public void ClearState(GameplayTag tag) => _mainSession?.ClearNarrativeState(tag);

        /// <summary>Narrative-state tags at or beneath the given path in the specified story.</summary>
        public GameplayTagCollection ReadState(GameplayTag storyId, GameplayTag tag)
        {
            var session = GetStory(storyId);
            var result  = new GameplayTagCollection();
            if (session == null) return result;
            CopyMatchingState(session.NarrativeState, tag, result);
            return result;
        }

        /// <summary>Applies operations to the narrative state of the specified story.</summary>
        public void Execute(GameplayTag storyId, params GameplayTagOperation[] ops) =>
            GetStory(storyId)?.Execute(ops);

        /// <summary>Clears narrative-state tags at or beneath the given path in the specified story.</summary>
        public void ClearState(GameplayTag storyId, GameplayTag tag) =>
            GetStory(storyId)?.ClearNarrativeState(tag);

        // ── History & save/load ───────────────────────────────────────────────

        /// <summary>Removes all entries from the main story's history.</summary>
        public void ClearHistory()          => _mainSession?.ClearHistory();
        /// <summary>Removes the most recent entries from the main story's history.</summary>
        public void ClearHistory(int steps) => _mainSession?.ClearHistory(steps);

        /// <summary>Snapshots the main story's session.</summary>
        public OghamSaveState Snapshot(string name = "snapshot") => _mainSession?.Snapshot(name);
        /// <summary>Snapshots the specified story's session.</summary>
        public OghamSaveState Snapshot(GameplayTag storyId, string name = "snapshot") =>
            GetStory(storyId)?.Snapshot(name);

        /// <summary>Restores a save state, routing to its session when registered, else the main session.</summary>
        public void Restore(OghamSaveState state)
        {
            if (state == null) return;
            var session = state.StoryId != 0 ? GetStory(new GameplayTag(state.StoryId)) : null;
            (session ?? _mainSession)?.Restore(state);
        }

        // ── Private ───────────────────────────────────────────────────────────

        private OghamSession AddSession(OghamSession session, bool setAsMain)
        {
            _sessions[session.Id.Id] = session;
            session.OnEntered += ForwardEntered;
            session.OnChoice  += ForwardChoice;
            session.OnClosed  += ForwardClosed;

            if (setAsMain || _mainSession == null)
                _mainSession = session;

            return session;
        }

        private static void CopyMatchingState(GameplayTagCollection source, GameplayTag tag, GameplayTagCollection dest)
        {
            var matches = source.GetMatchingTags(tag);
            foreach (var t in matches)
                dest.Apply(t, GameplayTagArithmetic.Set, source.GetValue(t));
        }

        private void ForwardEntered(GameplayTag storyId, StoryNode node)   => OnEntered?.Invoke(storyId, node);
        private void ForwardChoice(GameplayTag storyId, StoryOption option)=> OnChoice?.Invoke(storyId, option);
        private void ForwardClosed(GameplayTag storyId)                    => OnClosed?.Invoke(storyId);
    }
}
