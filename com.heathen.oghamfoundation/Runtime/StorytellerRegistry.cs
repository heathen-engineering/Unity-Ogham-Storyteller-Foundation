using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Builds and registers a baked story on <see cref="Awake"/>, addressed by its story
    /// <see cref="GameplayTag"/> (a story's "name" is a tag). The story's nodes, tags and inline
    /// localisations are baked from its <c>.ogham</c> source into code and registered in
    /// <see cref="OghamStoryCatalog"/>; this component just names which story to bring up. It holds no
    /// ScriptableObject references — destroying the GameObject does not unregister the story (call
    /// <see cref="Storyteller.UnregisterStory"/> for that).
    /// </summary>
    public class StorytellerRegistry : MonoBehaviour
    {
        [Tooltip("Dot-path GameplayTag identifying the story to build, e.g. Story.MainQuest.")]
        [SerializeField] private string _storyTag;

        [Tooltip("Make this the main story on registration. The first registered story becomes main automatically.")]
        [SerializeField] private bool _setAsMain;

        private void Awake()
        {
            if (string.IsNullOrWhiteSpace(_storyTag)) return;

            var tag = GameplayTag.FromName(_storyTag.Trim());
            if (OghamStoryCatalog.Build(tag, _setAsMain) == null)
                Debug.LogWarning(
                    $"[Ogham] No baked story is registered for tag '{_storyTag}'. Generate story code via " +
                    "Tools ▸ Heathen ▸ Ogham ▸ Generate Story Code.", this);
        }
    }
}
