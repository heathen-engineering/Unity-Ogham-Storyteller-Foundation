using System;
using System.Collections.Generic;
using Heathen;
using Heathen.GameplayTags;
using UnityEngine;

namespace Heathen.Ogham
{
    /// <summary>
    /// Static convenience facade for the single-world case: routes every call to the main world's
    /// <see cref="StorytellerSubsystem"/> (<c>GameFramework.MainWorld</c>). Existing code that used the old
    /// static <c>Storyteller</c> keeps working unchanged. Multi-world games (a pause world, per-player
    /// worlds) should resolve the per-world instance directly via <c>world.Get&lt;StorytellerSubsystem&gt;()</c>.
    ///
    /// <para>Events and calls target whatever is the main world at the time; subscribe after framework boot
    /// (e.g. in <c>Awake</c>/<c>OnEnable</c>), by which point the main world and its subsystem exist.</para>
    /// </summary>
    public static class Storyteller
    {
        private static StorytellerSubsystem Main => GameFramework.MainWorld?.Get<StorytellerSubsystem>();

        private static readonly IReadOnlyList<StoryOption>  _emptyOptions = Array.Empty<StoryOption>();
        private static readonly IReadOnlyList<HistoryEntry> _emptyHistory = Array.Empty<HistoryEntry>();

        // Narrative variables are global for now (a candidate for per-world in a later slice); reset them
        // once per play session. Per-story/per-world registry state now lives on the subsystem instances,
        // which are recreated fresh each session, so they need no manual reset here.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetGlobals() => OghamVariables.ResetToDefaults();

        /// <inheritdoc cref="StorytellerSubsystem.OnEntered"/>
        public static event Action<GameplayTag, StoryNode> OnEntered
        {
            add    { var s = Main; if (s != null) s.OnEntered += value; }
            remove { var s = Main; if (s != null) s.OnEntered -= value; }
        }

        /// <inheritdoc cref="StorytellerSubsystem.OnChoice"/>
        public static event Action<GameplayTag, StoryOption> OnChoice
        {
            add    { var s = Main; if (s != null) s.OnChoice += value; }
            remove { var s = Main; if (s != null) s.OnChoice -= value; }
        }

        /// <inheritdoc cref="StorytellerSubsystem.OnClosed"/>
        public static event Action<GameplayTag> OnClosed
        {
            add    { var s = Main; if (s != null) s.OnClosed += value; }
            remove { var s = Main; if (s != null) s.OnClosed -= value; }
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <inheritdoc cref="StorytellerSubsystem.OpenSession(OghamStory, bool)"/>
        public static OghamSession OpenSession(OghamStory definition, bool setAsMain = false) =>
            Main?.OpenSession(definition, setAsMain);

        /// <inheritdoc cref="StorytellerSubsystem.OpenSession(GameplayTag, bool)"/>
        public static OghamSession OpenSession(GameplayTag storyTag, bool setAsMain = false) =>
            Main?.OpenSession(storyTag, setAsMain);

        /// <inheritdoc cref="StorytellerSubsystem.RegisterSession"/>
        public static void RegisterSession(OghamSession session, bool setAsMain = false) =>
            Main?.RegisterSession(session, setAsMain);

        /// <inheritdoc cref="StorytellerSubsystem.RegisterStory(OghamStory, bool)"/>
        public static void RegisterStory(OghamStory definition, bool setAsMain = false) =>
            Main?.RegisterStory(definition, setAsMain);

        /// <inheritdoc cref="StorytellerSubsystem.UnregisterStory"/>
        public static void UnregisterStory(GameplayTag storyId) => Main?.UnregisterStory(storyId);

        /// <inheritdoc cref="StorytellerSubsystem.GetStory"/>
        public static OghamSession GetStory(GameplayTag storyId) => Main?.GetStory(storyId);

        /// <inheritdoc cref="StorytellerSubsystem.HasStory"/>
        public static bool HasStory(GameplayTag storyId) => Main?.HasStory(storyId) ?? false;

        /// <inheritdoc cref="StorytellerSubsystem.SetMain"/>
        public static void SetMain(GameplayTag storyId) => Main?.SetMain(storyId);

        /// <inheritdoc cref="StorytellerSubsystem.MainStoryId"/>
        public static GameplayTag MainStoryId => Main?.MainStoryId ?? default;

        // ── Processor ownership ───────────────────────────────────────────────

        /// <inheritdoc cref="StorytellerSubsystem.AcquireStory"/>
        public static void AcquireStory(GameplayTag storyId, IStoryProcessor processor) =>
            Main?.AcquireStory(storyId, processor);

        /// <inheritdoc cref="StorytellerSubsystem.ReleaseStory"/>
        public static void ReleaseStory(GameplayTag storyId, IStoryProcessor processor) =>
            Main?.ReleaseStory(storyId, processor);

        /// <inheritdoc cref="StorytellerSubsystem.IsProcessor"/>
        public static bool IsProcessor(GameplayTag storyId, IStoryProcessor processor) =>
            Main?.IsProcessor(storyId, processor) ?? false;

        // ── Main-story conversation ───────────────────────────────────────────

        /// <inheritdoc cref="StorytellerSubsystem.Enter(GameplayTag)"/>
        public static bool Enter(GameplayTag nodeTag)    => Main?.Enter(nodeTag)    ?? false;
        /// <inheritdoc cref="StorytellerSubsystem.Choose(GameplayTag)"/>
        public static bool Choose(GameplayTag optionTag) => Main?.Choose(optionTag) ?? false;
        /// <inheritdoc cref="StorytellerSubsystem.Close()"/>
        public static void Close()                       => Main?.Close();
        /// <inheritdoc cref="StorytellerSubsystem.Resume()"/>
        public static bool Resume()                      => Main?.Resume()          ?? false;

        /// <inheritdoc cref="StorytellerSubsystem.Enter(GameplayTag, GameplayTag)"/>
        public static bool Enter(GameplayTag storyId, GameplayTag nodeTag)    => Main?.Enter(storyId, nodeTag)    ?? false;
        /// <inheritdoc cref="StorytellerSubsystem.Choose(GameplayTag, GameplayTag)"/>
        public static bool Choose(GameplayTag storyId, GameplayTag optionTag) => Main?.Choose(storyId, optionTag) ?? false;
        /// <inheritdoc cref="StorytellerSubsystem.Close(GameplayTag)"/>
        public static void Close(GameplayTag storyId)                         => Main?.Close(storyId);
        /// <inheritdoc cref="StorytellerSubsystem.Resume(GameplayTag)"/>
        public static bool Resume(GameplayTag storyId)                        => Main?.Resume(storyId) ?? false;

        /// <inheritdoc cref="StorytellerSubsystem.IsActive"/>
        public static bool                        IsActive   => Main?.IsActive   ?? false;
        /// <inheritdoc cref="StorytellerSubsystem.Data"/>
        public static StoryNode                   Data       => Main?.Data;
        /// <inheritdoc cref="StorytellerSubsystem.Options"/>
        public static IReadOnlyList<StoryOption>  Options    => Main?.Options    ?? _emptyOptions;
        /// <inheritdoc cref="StorytellerSubsystem.AllOptions"/>
        public static IReadOnlyList<StoryOption>  AllOptions => Main?.AllOptions ?? _emptyOptions;
        /// <inheritdoc cref="StorytellerSubsystem.History"/>
        public static IReadOnlyList<HistoryEntry> History    => Main?.History    ?? _emptyHistory;

        // ── Narrative state ───────────────────────────────────────────────────

        /// <inheritdoc cref="StorytellerSubsystem.ReadState(GameplayTag)"/>
        public static GameplayTagCollection ReadState(GameplayTag tag) =>
            Main?.ReadState(tag) ?? new GameplayTagCollection();

        /// <inheritdoc cref="StorytellerSubsystem.Execute(GameplayTagOperation[])"/>
        public static void Execute(params GameplayTagOperation[] ops) => Main?.Execute(ops);

        /// <inheritdoc cref="StorytellerSubsystem.ClearState()"/>
        public static void ClearState() => Main?.ClearState();

        /// <inheritdoc cref="StorytellerSubsystem.ClearState(GameplayTag)"/>
        public static void ClearState(GameplayTag tag) => Main?.ClearState(tag);

        /// <inheritdoc cref="StorytellerSubsystem.ReadState(GameplayTag, GameplayTag)"/>
        public static GameplayTagCollection ReadState(GameplayTag storyId, GameplayTag tag) =>
            Main?.ReadState(storyId, tag) ?? new GameplayTagCollection();

        /// <inheritdoc cref="StorytellerSubsystem.Execute(GameplayTag, GameplayTagOperation[])"/>
        public static void Execute(GameplayTag storyId, params GameplayTagOperation[] ops) =>
            Main?.Execute(storyId, ops);

        /// <inheritdoc cref="StorytellerSubsystem.ClearState(GameplayTag, GameplayTag)"/>
        public static void ClearState(GameplayTag storyId, GameplayTag tag) =>
            Main?.ClearState(storyId, tag);

        // ── History & save/load ───────────────────────────────────────────────

        /// <inheritdoc cref="StorytellerSubsystem.ClearHistory()"/>
        public static void ClearHistory()          => Main?.ClearHistory();
        /// <inheritdoc cref="StorytellerSubsystem.ClearHistory(int)"/>
        public static void ClearHistory(int steps) => Main?.ClearHistory(steps);

        /// <inheritdoc cref="StorytellerSubsystem.Snapshot(string)"/>
        public static OghamSaveState Snapshot(string name = "snapshot") => Main?.Snapshot(name);
        /// <inheritdoc cref="StorytellerSubsystem.Snapshot(GameplayTag, string)"/>
        public static OghamSaveState Snapshot(GameplayTag storyId, string name = "snapshot") =>
            Main?.Snapshot(storyId, name);

        /// <inheritdoc cref="StorytellerSubsystem.Restore"/>
        public static void Restore(OghamSaveState state) => Main?.Restore(state);
    }
}
