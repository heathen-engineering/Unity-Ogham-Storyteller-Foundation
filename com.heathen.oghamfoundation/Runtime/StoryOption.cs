using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // Developer-facing wrapper around a single dialogue option.
    // Call Choose() to advance the conversation; wire it directly to a UI button's onClick.
    public sealed class StoryOption
    {
        private readonly DialogueOption _option;
        private readonly OghamStory     _story;

        internal StoryOption(DialogueOption option, OghamStory story)
        {
            _option = option;
            _story  = story;
        }

        public GameplayTag Tag       => _option.ResolvedTag;
        public GameplayTag TargetTag => _option.ResolvedTargetEntry;
        public bool        HasTarget => _option.ResolvedTargetEntry.Id != 0;

        // True when this option's conditions are satisfied at the moment this node was entered.
        // False means the option exists but is currently gated (visible but not actionable).
        // Use this to style inline links differently depending on whether the player can take them.
        public bool IsActive { get; internal set; } = true;

        public string GetText() => _option.TextKey.Resolve();

        // Advance the conversation by selecting this option.
        // Safe to wire directly to a UI button — no Storyteller reference required.
        // Returns false silently when IsActive is false.
        public void Choose() => _story.Choose(_option.ResolvedTag);

        // Internal access for OghamStory to read operations and navigation target.
        internal DialogueOption RawOption => _option;
    }
}
