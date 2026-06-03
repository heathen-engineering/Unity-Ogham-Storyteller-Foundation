using System;
using System.Collections.Generic;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// Primary static entry point for the Ogham Storyteller system. Manages named <see cref="OghamStory"/>
    /// instances and forwards their events as static events for global listeners. Provides single-story overloads
    /// for the main story and explicit-ID overloads for multi-story setups.
    /// Use <see cref="StorytellerRegistry"/> to create and register stories from the Unity Inspector.
    /// </summary>
    public static class Storyteller
    {
        private static readonly Dictionary<ulong, OghamStory> _stories = new();
        private static OghamStory _mainStory;

        private static readonly IReadOnlyList<StoryOption>  _emptyOptions = Array.Empty<StoryOption>();
        private static readonly IReadOnlyList<HistoryEntry> _emptyHistory = Array.Empty<HistoryEntry>();

        /// <summary>
        /// Raised when any registered story enters a dialogue node. The first parameter identifies which story fired.
        /// </summary>
        public static event Action<GameplayTag, StoryNode>   OnEntered;

        /// <summary>
        /// Raised when an option is selected in any registered story, before navigation to the next node.
        /// The first parameter identifies the story.
        /// </summary>
        public static event Action<GameplayTag, StoryOption> OnChoice;

        /// <summary>
        /// Raised when any registered story's conversation ends. The first parameter identifies which story fired.
        /// </summary>
        public static event Action<GameplayTag>              OnClosed;

        /// <summary>
        /// Creates a new <see cref="OghamStory"/>, registers it, and optionally sets it as the main story.
        /// Returns the existing instance when a story with the same ID is already registered.
        /// Inline localisations from <paramref name="compiled"/> are injected before the story is created.
        /// </summary>
        /// <param name="storyId">The GameplayTag that uniquely identifies the story.</param>
        /// <param name="compiled">Optional compiled story asset; takes priority as the primary data source.</param>
        /// <param name="additionalData">Optional additional authoring data assets to merge with the compiled story.</param>
        /// <param name="setAsMain">When <c>true</c>, sets this story as the main story.</param>
        /// <returns>The newly created or existing <see cref="OghamStory"/>.</returns>
        public static OghamStory RegisterStory(GameplayTag storyId,
                                               OghamCompiledData compiled = null,
                                               IEnumerable<OghamData> additionalData = null,
                                               bool setAsMain = false)
        {
            if (_stories.TryGetValue(storyId.Id, out var existing))
            {
                if (setAsMain) _mainStory = existing;
                return existing;
            }

            // Inject inline localisations before building story nodes so content resolves correctly.
            if (compiled?.Localisations != null)
                foreach (var loc in compiled.Localisations)
                    if (!string.IsNullOrWhiteSpace(loc.Key))
                        LexiconRegistry.SetString(loc.Key, loc.Value,
                            string.IsNullOrWhiteSpace(loc.Culture) ? null : loc.Culture);

            var story = new OghamStory(storyId);
            if (compiled != null) story.RegisterData(compiled);
            if (additionalData != null)
                foreach (var d in additionalData)
                    story.RegisterData(d);

            return AddStory(story, setAsMain);
        }

        /// <summary>
        /// Registers a pre-created <see cref="OghamStory"/> and subscribes it to the global event forwarding system.
        /// Has no effect when a story with the same ID is already registered.
        /// </summary>
        /// <param name="story">The story to register. <c>null</c> is silently ignored.</param>
        /// <param name="setAsMain">When <c>true</c>, sets this story as the main story.</param>
        public static void RegisterStory(OghamStory story, bool setAsMain = false)
        {
            if (story == null || _stories.ContainsKey(story.Id.Id)) return;
            AddStory(story, setAsMain);
        }

        /// <summary>
        /// Unregisters the story with the given ID and releases its event subscriptions.
        /// The <see cref="OghamStory"/> instance is not destroyed and can still be used directly.
        /// Use this when content such as a mod is unloaded and the story should be freed.
        /// </summary>
        /// <param name="storyId">The GameplayTag identifying the story to unregister.</param>
        public static void UnregisterStory(GameplayTag storyId)
        {
            if (!_stories.TryGetValue(storyId.Id, out var story)) return;
            story.OnEntered -= ForwardEntered;
            story.OnChoice  -= ForwardChoice;
            story.OnClosed  -= ForwardClosed;
            _stories.Remove(storyId.Id);

            if (_mainStory == story)
            {
                _mainStory = null;
                foreach (var s in _stories.Values) { _mainStory = s; break; }
            }
        }

        /// <summary>
        /// Returns the registered <see cref="OghamStory"/> with the given ID, or <c>null</c> if not registered.
        /// </summary>
        /// <param name="storyId">The GameplayTag identifying the story.</param>
        /// <returns>The registered story, or <c>null</c>.</returns>
        public static OghamStory GetStory(GameplayTag storyId) =>
            _stories.TryGetValue(storyId.Id, out var story) ? story : null;

        /// <summary>Returns <c>true</c> when a story with the given ID is registered.</summary>
        /// <param name="storyId">The GameplayTag identifying the story.</param>
        /// <returns><c>true</c> if the story is registered; otherwise <c>false</c>.</returns>
        public static bool HasStory(GameplayTag storyId) =>
            _stories.ContainsKey(storyId.Id);

        /// <summary>
        /// Sets the registered story with the given ID as the main story. Has no effect when the story is not registered.
        /// </summary>
        /// <param name="storyId">The GameplayTag of the story to promote to main.</param>
        public static void SetMain(GameplayTag storyId)
        {
            if (_stories.TryGetValue(storyId.Id, out var story))
                _mainStory = story;
        }

        /// <summary>The tag ID of the current main story, or <c>default(GameplayTag)</c> when no story is registered.</summary>
        public static GameplayTag MainStoryId => _mainStory?.Id ?? default;

        /// <summary>Starts a conversation at the given node in the main story. Returns <c>false</c> when the entry is not found.</summary>
        /// <param name="nodeTag">The tag of the dialogue entry to enter.</param>
        /// <returns><c>true</c> on success; <c>false</c> when no main story is set or the entry is not found.</returns>
        public static bool Enter(GameplayTag nodeTag)          => _mainStory?.Enter(nodeTag)     ?? false;
        /// <summary>Selects an option in the main story. Returns <c>false</c> when the option is not available.</summary>
        /// <param name="optionTag">The tag of the option to select.</param>
        /// <returns><c>true</c> on success; <c>false</c> when no main story is set or the option is not found.</returns>
        public static bool Choose(GameplayTag optionTag)       => _mainStory?.Choose(optionTag)  ?? false;
        /// <summary>Closes the main story's active conversation, if any.</summary>
        public static void Close()                             => _mainStory?.Close();

        /// <summary>Starts a conversation at the given node in the specified story.</summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        /// <param name="nodeTag">The tag of the dialogue entry to enter.</param>
        /// <returns><c>true</c> on success; <c>false</c> when the story or entry is not found.</returns>
        public static bool Enter(GameplayTag storyId, GameplayTag nodeTag)         => GetStory(storyId)?.Enter(nodeTag)    ?? false;
        /// <summary>Selects an option in the specified story.</summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        /// <param name="optionTag">The tag of the option to select.</param>
        /// <returns><c>true</c> on success; <c>false</c> when the story or option is not found.</returns>
        public static bool Choose(GameplayTag storyId, GameplayTag optionTag)      => GetStory(storyId)?.Choose(optionTag) ?? false;
        /// <summary>Closes the active conversation in the specified story, if any.</summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        public static void Close(GameplayTag storyId)                              => GetStory(storyId)?.Close();

        /// <summary>Returns <c>true</c> when the main story has an active conversation.</summary>
        public static bool                        IsActive   => _mainStory?.IsActive          ?? false;
        /// <summary>The current <see cref="StoryNode"/> for the main story, or <c>null</c> when no conversation is active.</summary>
        public static StoryNode                   Data       => _mainStory?.CurrentNode;
        /// <summary>Active (condition-passing) options for the current node of the main story.</summary>
        public static IReadOnlyList<StoryOption>  Options    => _mainStory?.CurrentOptions    ?? _emptyOptions;
        /// <summary>All options for the current node of the main story, including gated ones. Use with <see cref="OghamLinkFormatter"/> to style inline links.</summary>
        public static IReadOnlyList<StoryOption>  AllOptions => _mainStory?.CurrentAllOptions ?? _emptyOptions;
        /// <summary>The conversation history for the main story.</summary>
        public static IReadOnlyList<HistoryEntry> History    => _mainStory?.History           ?? _emptyHistory;

        /// <summary>
        /// Returns a new <see cref="GameplayTagCollection"/> containing only narrative-state tags at or beneath
        /// the given path in the main story.
        /// </summary>
        /// <param name="tag">The root tag whose subtree of state values is returned.</param>
        /// <returns>A new collection with the matching state tags and their values.</returns>
        public static GameplayTagCollection ReadState(GameplayTag tag)
        {
            var result = new GameplayTagCollection();
            if (_mainStory == null) return result;
            CopyMatchingState(_mainStory.NarrativeState, tag, result);
            return result;
        }

        /// <summary>Applies one or more operations to the main story's narrative state.</summary>
        /// <param name="ops">The operations to apply in order.</param>
        public static void Execute(params GameplayTagOperation[] ops) =>
            _mainStory?.Execute(ops);

        /// <summary>Clears all narrative-state tags for the main story. Does not clear the history.</summary>
        public static void ClearState() =>
            _mainStory?.ClearNarrativeState();

        /// <summary>Clears narrative-state tags at or beneath the given path in the main story.</summary>
        /// <param name="tag">The root tag whose subtree of state values is removed.</param>
        public static void ClearState(GameplayTag tag) =>
            _mainStory?.ClearNarrativeState(tag);

        /// <summary>
        /// Returns a new <see cref="GameplayTagCollection"/> containing only narrative-state tags at or beneath
        /// the given path in the specified story.
        /// </summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        /// <param name="tag">The root tag whose subtree of state values is returned.</param>
        /// <returns>A new collection with the matching state tags and their values.</returns>
        public static GameplayTagCollection ReadState(GameplayTag storyId, GameplayTag tag)
        {
            var story  = GetStory(storyId);
            var result = new GameplayTagCollection();
            if (story == null) return result;
            CopyMatchingState(story.NarrativeState, tag, result);
            return result;
        }

        /// <summary>Applies one or more operations to the narrative state of the specified story.</summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        /// <param name="ops">The operations to apply in order.</param>
        public static void Execute(GameplayTag storyId, params GameplayTagOperation[] ops) =>
            GetStory(storyId)?.Execute(ops);

        /// <summary>Clears narrative-state tags at or beneath the given path in the specified story.</summary>
        /// <param name="storyId">The tag identifying the target story.</param>
        /// <param name="tag">The root tag whose subtree of state values is removed.</param>
        public static void ClearState(GameplayTag storyId, GameplayTag tag) =>
            GetStory(storyId)?.ClearNarrativeState(tag);

        /// <summary>Removes all entries from the main story's conversation history.</summary>
        public static void ClearHistory()          => _mainStory?.ClearHistory();
        /// <summary>Removes the most recent <paramref name="steps"/> entries from the main story's history.</summary>
        /// <param name="steps">The number of recent history entries to remove.</param>
        public static void ClearHistory(int steps) => _mainStory?.ClearHistory(steps);

        /// <summary>Creates and returns a save-state snapshot of the main story's current session.</summary>
        /// <param name="name">A human-readable label for the save state. Defaults to "snapshot".</param>
        /// <returns>An <see cref="OghamSaveState"/>, or <c>null</c> when no main story is set.</returns>
        public static OghamSaveState Snapshot(string name = "snapshot") =>
            _mainStory?.Snapshot(name);

        /// <summary>Creates and returns a save-state snapshot of the specified story's current session.</summary>
        /// <param name="storyId">The tag identifying the story to snapshot.</param>
        /// <param name="name">A human-readable label for the save state. Defaults to "snapshot".</param>
        /// <returns>An <see cref="OghamSaveState"/>, or <c>null</c> when the story is not registered.</returns>
        public static OghamSaveState Snapshot(GameplayTag storyId, string name = "snapshot") =>
            GetStory(storyId)?.Snapshot(name);

        /// <summary>
        /// Restores a previously created <see cref="OghamSaveState"/>. Routes to the story matching
        /// <c>state.StoryId</c> when registered; falls back to the main story otherwise.
        /// </summary>
        /// <param name="state">The save state to restore from. <c>null</c> is silently ignored.</param>
        public static void Restore(OghamSaveState state)
        {
            if (state == null) return;
            var story = state.StoryId != 0 ? GetStory(new GameplayTag(state.StoryId)) : null;
            (story ?? _mainStory)?.Restore(state);
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static OghamStory AddStory(OghamStory story, bool setAsMain)
        {
            _stories[story.Id.Id] = story;
            story.OnEntered += ForwardEntered;
            story.OnChoice  += ForwardChoice;
            story.OnClosed  += ForwardClosed;

            if (setAsMain || _mainStory == null)
                _mainStory = story;

            return story;
        }

        private static void CopyMatchingState(GameplayTagCollection source, GameplayTag tag, GameplayTagCollection dest)
        {
            var matches = source.GetMatchingTags(tag);
            foreach (var t in matches)
                dest.Apply(t, GameplayTagArithmetic.Set, source.GetValue(t));
        }

        private static void ForwardEntered(GameplayTag storyId, StoryNode node) =>
            OnEntered?.Invoke(storyId, node);

        private static void ForwardChoice(GameplayTag storyId, StoryOption option) =>
            OnChoice?.Invoke(storyId, option);

        private static void ForwardClosed(GameplayTag storyId, bool _) =>
            OnClosed?.Invoke(storyId);
    }
}
