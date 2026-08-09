using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Immutable developer-facing wrapper around a single dialogue option, produced by <see cref="OghamSession"/>
    /// when a node is entered. Call <see cref="Choose"/> to advance the conversation; this method can be wired
    /// directly to a UI button's <c>onClick</c> event without requiring a reference to <see cref="Storyteller"/>.
    /// </summary>
    public sealed class StoryOption
    {
        private readonly DialogueOption _option;
        private readonly OghamSession   _session;

        internal StoryOption(DialogueOption option, OghamSession session)
        {
            _option  = option;
            _session = session;
        }

        /// <summary>The GameplayTag that uniquely identifies this option.</summary>
        public GameplayTag Tag       => _option.ResolvedTag;
        /// <summary>The GameplayTag of the dialogue entry this option navigates to, or a default tag when it closes the conversation.</summary>
        public GameplayTag TargetTag => _option.ResolvedTargetEntry;
        /// <summary>Returns <c>true</c> when this option has a non-empty navigation target.</summary>
        public bool        HasTarget => _option.ResolvedTargetEntry.Id != 0;

        /// <summary>
        /// <c>true</c> when this option's conditions were satisfied at the moment the current node was entered.
        /// <c>false</c> means the option exists but is currently gated. Use this to style inline links
        /// differently depending on whether the player can take them.
        /// </summary>
        public bool IsActive { get; internal set; } = true;

        /// <summary>Returns the resolved display text for this option's label.</summary>
        /// <returns>The resolved display string.</returns>
        public string GetText() => _option.TextKey.Resolve();

        /// <summary>
        /// Advances the conversation by selecting this option, applying its operations, and navigating to its target.
        /// Safe to wire directly to a UI button. Silently does nothing when <see cref="IsActive"/> is <c>false</c>.
        /// </summary>
        public void Choose() => _session.Choose(_option.ResolvedTag);

        internal DialogueOption RawOption => _option;
    }
}
