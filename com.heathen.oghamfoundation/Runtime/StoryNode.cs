using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Immutable developer-facing snapshot of a dialogue node, delivered via <see cref="Storyteller.OnEntered"/>.
    /// Content keys are accessed by absolute index matching the order authored in the editor.
    /// </summary>
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

        /// <summary>The GameplayTag that uniquely identifies this dialogue entry.</summary>
        public GameplayTag Tag              => _entry.ResolvedTag;
        /// <summary>The number of content key slots on this node.</summary>
        public int ContentCount             => _entry.ContentKeys.Count;

        /// <summary>Active options only (those whose conditions are satisfied). Use this to populate button lists.</summary>
        public IReadOnlyList<StoryOption> Options => _options;

        /// <summary>
        /// All options including gated ones where <see cref="StoryOption.IsActive"/> is <c>false</c>.
        /// Use this to resolve inline <c>Ogham://</c> links so gated links can be styled differently.
        /// </summary>
        public IReadOnlyList<StoryOption> AllOptions => _allOptions;

        /// <summary>Returns the resolved display string for the content key at <paramref name="index"/>, or an empty string if out of range.</summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The resolved text, or an empty string.</returns>
        public string     GetText(int index)    => Valid(index) ? _entry.ContentKeys[index].ResolveText()                  : string.Empty;
        /// <summary>Returns the resolved <see cref="Texture2D"/> asset for the content key at <paramref name="index"/>, or <c>null</c>.</summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The resolved texture, or <c>null</c>.</returns>
        public Texture2D  GetTexture(int index) => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as Texture2D   : null;
        /// <summary>Returns the resolved <see cref="Sprite"/> asset for the content key at <paramref name="index"/>, or <c>null</c>.</summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The resolved sprite, or <c>null</c>.</returns>
        public Sprite     GetSprite(int index)  => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as Sprite      : null;
        /// <summary>Returns the resolved <see cref="AudioClip"/> asset for the content key at <paramref name="index"/>, or <c>null</c>.</summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The resolved audio clip, or <c>null</c>.</returns>
        public AudioClip  GetAudio(int index)   => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as AudioClip   : null;
        /// <summary>Returns the resolved <see cref="GameObject"/> prefab asset for the content key at <paramref name="index"/>, or <c>null</c>.</summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The resolved prefab, or <c>null</c>.</returns>
        public GameObject GetPrefab(int index)  => Valid(index) ? _entry.ContentKeys[index].ResolveAsset() as GameObject  : null;
        /// <summary>
        /// Returns the raw Lexicon key path or literal value for the content key at <paramref name="index"/>,
        /// without resolving through Lexicon. Useful in editor tools when Lexicon may not be fully initialised.
        /// </summary>
        /// <param name="index">The zero-based content key index.</param>
        /// <returns>The raw key or value string, or an empty string if out of range.</returns>
        public string     GetRawKey(int index)  => Valid(index) ? _entry.ContentKeys[index].KeyOrValue                    : string.Empty;

        private bool Valid(int index) => index >= 0 && index < _entry.ContentKeys.Count;
    }
}
