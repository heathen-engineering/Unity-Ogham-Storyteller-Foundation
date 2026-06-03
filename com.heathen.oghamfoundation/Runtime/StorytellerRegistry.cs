using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// MonoBehaviour that creates and registers an <see cref="OghamStory"/> with <see cref="Storyteller"/> on Awake.
    /// The story persists beyond this component's lifetime; destroying the GameObject does NOT unregister the story.
    /// Call <see cref="Storyteller.UnregisterStory"/> explicitly if the story should be removed, for example
    /// when a mod is unloaded.
    /// </summary>
    public class StorytellerRegistry : MonoBehaviour
    {
        [Tooltip("Dot-path tag that uniquely identifies this story. E.g. Story.MainQuest")]
        [SerializeField] private string _storyTagPath;

        [Tooltip("Make this the main story on registration. First registered story becomes main automatically.")]
        [SerializeField] private bool _setAsMain;

        [Tooltip("Compiled story asset produced by the Ogham build pipeline. Takes priority over Additional Data.")]
        [SerializeField] private OghamCompiledData _compiledStory;

        [Tooltip("Individual OghamData assets. Used when Compiled Story is not assigned (editor iteration).")]
        [SerializeField] private List<OghamData> _additionalData = new();

        private void Awake()
        {
            // Use the compiled asset's story tag when the Inspector field is left blank.
            var tagPath = !string.IsNullOrWhiteSpace(_storyTagPath)
                ? _storyTagPath
                : _compiledStory?.StoryTagPath ?? string.Empty;
            var storyId = GameplayTag.FromName(tagPath);
            Storyteller.RegisterStory(storyId, _compiledStory, _additionalData, _setAsMain);
        }
    }
}
