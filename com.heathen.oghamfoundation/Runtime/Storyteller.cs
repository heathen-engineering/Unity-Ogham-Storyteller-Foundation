using System;
using System.Collections.Generic;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    // Primary entry point for the Ogham Storyteller system.
    // Manages named OghamStory instances and forwards their events as static events.
    //
    // StorytellerRegistry creates and registers stories from the Unity Inspector.
    // All navigation and state methods have single-story overloads (main story) and
    // dual-story overloads (explicit story id) so multi-story setups need no boilerplate.
    public static class Storyteller
    {
        private static readonly Dictionary<ulong, OghamStory> _stories = new();
        private static OghamStory _mainStory;

        private static readonly IReadOnlyList<StoryOption>  _emptyOptions = Array.Empty<StoryOption>();
        private static readonly IReadOnlyList<HistoryEntry> _emptyHistory = Array.Empty<HistoryEntry>();

        // ── Events ────────────────────────────────────────────────────────────

        // Fired when any registered story enters a node. First param identifies which story.
        public static event Action<GameplayTag, StoryNode>   OnEntered;

        // Fired when an option is selected, before navigation to the next node.
        public static event Action<GameplayTag, StoryOption> OnChoice;

        // Fired when a story conversation ends. First param identifies which story.
        public static event Action<GameplayTag>              OnClosed;

        // ── Registration ──────────────────────────────────────────────────────

        // Create, register, and optionally set as main story. Returns the OghamStory.
        // If a story with the same id is already registered, returns the existing instance.
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

        // Register a pre-created OghamStory. No-op if a story with the same id is already registered.
        public static void RegisterStory(OghamStory story, bool setAsMain = false)
        {
            if (story == null || _stories.ContainsKey(story.Id.Id)) return;
            AddStory(story, setAsMain);
        }

        // Unregister a story and release its event subscriptions.
        // The OghamStory instance is NOT destroyed — it can still be used directly.
        // Useful when content (e.g. a mod) is unloaded and the story should be freed from memory.
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

        public static OghamStory GetStory(GameplayTag storyId) =>
            _stories.TryGetValue(storyId.Id, out var story) ? story : null;

        public static bool HasStory(GameplayTag storyId) =>
            _stories.ContainsKey(storyId.Id);

        public static void SetMain(GameplayTag storyId)
        {
            if (_stories.TryGetValue(storyId.Id, out var story))
                _mainStory = story;
        }

        // The id of the current main story; default(GameplayTag) when no story is registered.
        public static GameplayTag MainStoryId => _mainStory?.Id ?? default;

        // ── Navigation — main story ───────────────────────────────────────────

        public static bool Enter(GameplayTag nodeTag)          => _mainStory?.Enter(nodeTag)     ?? false;
        public static bool Choose(GameplayTag optionTag)       => _mainStory?.Choose(optionTag)  ?? false;
        public static void Close()                             => _mainStory?.Close();

        // ── Navigation — specific story ───────────────────────────────────────

        public static bool Enter(GameplayTag storyId, GameplayTag nodeTag)         => GetStory(storyId)?.Enter(nodeTag)    ?? false;
        public static bool Choose(GameplayTag storyId, GameplayTag optionTag)      => GetStory(storyId)?.Choose(optionTag) ?? false;
        public static void Close(GameplayTag storyId)                              => GetStory(storyId)?.Close();

        // ── Query — main story ────────────────────────────────────────────────

        public static bool                        IsActive   => _mainStory?.IsActive          ?? false;
        public static StoryNode                   Data       => _mainStory?.CurrentNode;
        public static IReadOnlyList<StoryOption>  Options    => _mainStory?.CurrentOptions    ?? _emptyOptions;
        // All options including gated ones. Use with OghamLinkFormatter to style inline links.
        public static IReadOnlyList<StoryOption>  AllOptions => _mainStory?.CurrentAllOptions ?? _emptyOptions;
        public static IReadOnlyList<HistoryEntry> History    => _mainStory?.History           ?? _emptyHistory;

        // ── State interface — main story ──────────────────────────────────────

        // Returns a new GameplayTagCollection containing only tags at or below the given path.
        public static GameplayTagCollection ReadState(GameplayTag tag)
        {
            var result = new GameplayTagCollection();
            if (_mainStory == null) return result;
            CopyMatchingState(_mainStory.NarrativeState, tag, result);
            return result;
        }

        // Apply one or more operations to the main story's narrative state.
        public static void Execute(params GameplayTagOperation[] ops) =>
            _mainStory?.Execute(ops);

        // Clear all narrative state for the main story (does not clear history).
        public static void ClearState() =>
            _mainStory?.ClearNarrativeState();

        // Clear narrative state tags at or below the given path on the main story.
        public static void ClearState(GameplayTag tag) =>
            _mainStory?.ClearNarrativeState(tag);

        // ── State interface — specific story ──────────────────────────────────

        public static GameplayTagCollection ReadState(GameplayTag storyId, GameplayTag tag)
        {
            var story  = GetStory(storyId);
            var result = new GameplayTagCollection();
            if (story == null) return result;
            CopyMatchingState(story.NarrativeState, tag, result);
            return result;
        }

        public static void Execute(GameplayTag storyId, params GameplayTagOperation[] ops) =>
            GetStory(storyId)?.Execute(ops);

        public static void ClearState(GameplayTag storyId, GameplayTag tag) =>
            GetStory(storyId)?.ClearNarrativeState(tag);

        // ── History — main story ──────────────────────────────────────────────

        public static void ClearHistory()          => _mainStory?.ClearHistory();
        public static void ClearHistory(int steps) => _mainStory?.ClearHistory(steps);

        // ── Persistence ───────────────────────────────────────────────────────

        public static OghamSaveState Snapshot(string name = "snapshot") =>
            _mainStory?.Snapshot(name);

        public static OghamSaveState Snapshot(GameplayTag storyId, string name = "snapshot") =>
            GetStory(storyId)?.Snapshot(name);

        // Routes to the story matching state.StoryId when registered; falls back to main story.
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
