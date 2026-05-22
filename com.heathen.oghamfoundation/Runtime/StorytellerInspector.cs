using UnityEngine;
using UnityEngine.Events;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // Which story to track: the current main story, or a specific named story.
    public enum StoryTarget { Main, Specific }

    // Inspector bridge for the Storyteller system.
    // Subscribe to its UnityEvents and wire its public methods to UI buttons without writing code.
    // StoryTarget.Main filters to whichever story is currently set as main.
    // StoryTarget.Specific filters to the story identified by StoryTagPath.
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

        // ── Inspector-wirable methods ─────────────────────────────────────────

        public void Enter(string nodeTagPath)
        {
            var nodeTag = GameplayTag.FromName(nodeTagPath);
            if (IsSpecific())
                Storyteller.Enter(GameplayTag.FromName(_storyTagPath), nodeTag);
            else
                Storyteller.Enter(nodeTag);
        }

        public void Choose(string optionTagPath)
        {
            var optionTag = GameplayTag.FromName(optionTagPath);
            if (IsSpecific())
                Storyteller.Choose(GameplayTag.FromName(_storyTagPath), optionTag);
            else
                Storyteller.Choose(optionTag);
        }

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
