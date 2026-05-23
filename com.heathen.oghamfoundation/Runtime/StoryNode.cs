using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // Developer-facing snapshot of a dialogue node delivered via Storyteller.OnEntered.
    // Content keys are accessed by absolute index matching the order authored in the editor.
    public sealed class StoryNode
    {
        private readonly DialogueEntry _entry;
        private readonly IReadOnlyList<StoryOption> _options;
        private readonly IReadOnlyList<StoryOption> _allOptions;

        internal StoryNode(DialogueEntry entry, IReadOnlyList<StoryOption> options, IReadOnlyList<StoryOption> allOptions)
        {
            _entry      = entry;
            _options    = options;
            _allOptions = allOptions;
        }

        public GameplayTag Tag              => _entry.Tag;
        public int ContentCount             => _entry.ContentKeys.Count;

        // Active options only — condition-passing. Use this to populate button lists.
        public IReadOnlyList<StoryOption> Options => _options;

        // All options including gated ones (IsActive = false).
        // Use this to resolve inline Ogham:// links so gated links can be styled differently.
        public IReadOnlyList<StoryOption> AllOptions => _allOptions;

        public string     GetText(int index)    => Valid(index) ? _entry.ContentKeys[index].ResolveText()           : string.Empty;
        public Sprite     GetSprite(int index)  => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as Sprite      : null;
        public AudioClip  GetAudio(int index)   => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as AudioClip   : null;
        public GameObject GetPrefab(int index)  => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as GameObject  : null;
        // Returns the raw Lexicon key path or literal value without resolving through Lexicon.
        // Useful in editor tools when Lexicon may not be fully initialised.
        public string     GetRawKey(int index)  => Valid(index) ? _entry.ContentKeys[index].KeyOrValue                    : string.Empty;

        private bool Valid(int index) => index >= 0 && index < _entry.ContentKeys.Count;
    }
}
