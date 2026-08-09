using UnityEngine;
using UnityEngine.Events;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>Determines which story a <see cref="StorytellerInspector"/> or <see cref="OghamTemplateSpawner"/> component tracks.</summary>
    public enum StoryTarget
    {
        /// <summary>Track whichever story is currently set as the main story in <see cref="Storyteller"/>.</summary>
        Main,
        /// <summary>Track the specific story identified by a dot-path tag.</summary>
        Specific
    }

    /// <summary>
    /// Inspector bridge for the Ogham Storyteller system. Subscribe to its UnityEvents and wire its public
    /// methods to UI buttons in the Inspector without writing code. <see cref="StoryTarget.Main"/> filters
    /// events to whichever story is currently set as main; <see cref="StoryTarget.Specific"/> filters to the
    /// story identified by its dot-path tag.
    /// </summary>
    public class StorytellerInspector : MonoBehaviour
    {
        [SerializeField] private StoryTarget _target = StoryTarget.Main;

        [Tooltip("Dot-path tag identifying the story to track when Target is Specific.")]
        [SerializeField] private string _storyTagPath;

        [SerializeField] private UnityEvent<StoryNode>   _onEntered = new();
        [SerializeField] private UnityEvent<StoryOption> _onChoice  = new();
        [SerializeField] private UnityEvent              _onClosed  = new();

        private void OnEnable()
        {
            Storyteller.OnEntered += HandleEntered;
            Storyteller.OnChoice  += HandleChoice;
            Storyteller.OnClosed  += HandleClosed;
        }

        private void OnDisable()
        {
            Storyteller.OnEntered -= HandleEntered;
            Storyteller.OnChoice  -= HandleChoice;
            Storyteller.OnClosed  -= HandleClosed;
        }

        /// <summary>
        /// Starts a conversation at the entry identified by <paramref name="nodeTagPath"/> in the tracked story.
        /// Wire this to a UI button's <c>OnClick</c> event in the Inspector.
        /// </summary>
        /// <param name="nodeTagPath">The dot-path GameplayTag of the entry to enter.</param>
        public void Enter(string nodeTagPath)
        {
            var nodeTag = GameplayTag.FromName(nodeTagPath);
            if (IsSpecific())
                Storyteller.Enter(GameplayTag.FromName(_storyTagPath), nodeTag);
            else
                Storyteller.Enter(nodeTag);
        }

        /// <summary>
        /// Selects the option identified by <paramref name="optionTagPath"/> in the tracked story.
        /// Wire this to a UI button's <c>OnClick</c> event in the Inspector.
        /// </summary>
        /// <param name="optionTagPath">The dot-path GameplayTag of the option to select.</param>
        public void Choose(string optionTagPath)
        {
            var optionTag = GameplayTag.FromName(optionTagPath);
            if (IsSpecific())
                Storyteller.Choose(GameplayTag.FromName(_storyTagPath), optionTag);
            else
                Storyteller.Choose(optionTag);
        }

        /// <summary>
        /// Closes the active conversation in the tracked story. Wire this to a UI button in the Inspector.
        /// </summary>
        public void Close()
        {
            if (IsSpecific())
                Storyteller.Close(GameplayTag.FromName(_storyTagPath));
            else
                Storyteller.Close();
        }

        // ── Private ───────────────────────────────────────────────────────────

        private bool IsSpecific() =>
            _target == StoryTarget.Specific && !string.IsNullOrEmpty(_storyTagPath);

        private bool IsTarget(GameplayTag storyId)
        {
            if (_target == StoryTarget.Main)
                return storyId.Id == Storyteller.MainStoryId.Id;
            if (string.IsNullOrEmpty(_storyTagPath)) return false;
            return storyId.Id == GameplayTag.FromName(_storyTagPath).Id;
        }

        private void HandleEntered(GameplayTag storyId, StoryNode node)
        {
            if (IsTarget(storyId)) _onEntered?.Invoke(node);
        }

        private void HandleChoice(GameplayTag storyId, StoryOption option)
        {
            if (IsTarget(storyId)) _onChoice?.Invoke(option);
        }

        private void HandleClosed(GameplayTag storyId)
        {
            if (IsTarget(storyId)) _onClosed?.Invoke();
        }
    }
}
