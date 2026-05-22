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

        public GameplayTag Tag       => _option.Tag;
        public GameplayTag TargetTag => _option.TargetEntry;
        public bool        HasTarget => _option.TargetEntry.Id != 0;

        public string GetText() => _option.TextKey.Resolve();

        // Advance the conversation by selecting this option.
        // Safe to wire directly to a UI button — no Storyteller reference required.
        public void Choose() => _story.Choose(_option.Tag);

        // Internal access for OghamStory to read operations and navigation target.
        internal DialogueOption RawOption => _option;
    }
}
