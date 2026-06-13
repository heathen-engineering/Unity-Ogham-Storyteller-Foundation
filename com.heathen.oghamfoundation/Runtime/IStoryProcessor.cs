using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Implemented by a component that drives the presentation of a single story (a "reader").
    /// The static <see cref="Storyteller"/> owns each <see cref="OghamStory"/> and its state; a processor
    /// registers itself as the active presenter for a given story id via
    /// <see cref="Storyteller.AcquireStory"/>.
    /// <para>
    /// Only one processor may present a story at a time. When a new processor acquires a story that another
    /// processor already holds, the previous processor is notified via <see cref="OnSuperseded"/> so it can
    /// detach from the story's events and tear down any presentation state it owns (spawned prefabs, buttons,
    /// displayed text, and so on). The story itself — graph and narrative state — is unaffected by the
    /// hand-over; it remains the managed domain of the <see cref="Storyteller"/>.
    /// </para>
    /// </summary>
    public interface IStoryProcessor
    {
        /// <summary>
        /// Called when another processor takes over the story this processor was presenting. The
        /// implementation should stop listening to the story and clear any presentation state it created.
        /// </summary>
        /// <param name="storyId">The story whose processing was handed over to another processor.</param>
        void OnSuperseded(GameplayTag storyId);
    }
}
