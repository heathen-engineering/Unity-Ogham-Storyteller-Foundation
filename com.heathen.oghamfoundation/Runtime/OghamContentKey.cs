using System;
using UnityEngine;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// Enumerates the content roles a single <see cref="OghamContentKey"/> slot may fulfil in a dialogue entry.
    /// Used to route resolved content to the correct UI element (text label, image, audio source, etc.).
    /// </summary>
    public enum OghamContentType { Text, Image, Sprite, Audio, Prefab }

    /// <summary>
    /// Represents one content slot in a <see cref="DialogueEntry"/>, combining a type, a localisation mode,
    /// and either a literal value or a Lexicon key. Resolved at runtime via <see cref="ResolveText"/> or
    /// <see cref="ResolveAsset"/> depending on <see cref="Type"/>.
    /// </summary>
    [Serializable]
    public class OghamContentKey
    {
        /// <summary>The kind of content this slot carries (text, image, sprite, audio, or prefab).</summary>
        public OghamContentType  Type       = OghamContentType.Text;
        /// <summary>Whether <see cref="KeyOrValue"/> is a literal value, a Lexicon key, or invariant text.</summary>
        public LexiconLocMode    Mode       = LexiconLocMode.Literal;
        /// <summary>The literal text value or the Lexicon dot-path key, depending on <see cref="Mode"/>.</summary>
        public string            KeyOrValue = string.Empty;
        /// <summary>The direct asset reference used when <see cref="Mode"/> is <see cref="LexiconLocMode.Literal"/> and <see cref="Type"/> is not Text.</summary>
        public UnityEngine.Object AssetRef;

        /// <summary>Returns <c>true</c> when this slot carries text content.</summary>
        public bool IsText   => Type == OghamContentType.Text;
        /// <summary>Returns <c>true</c> when this slot carries an image (Texture2D) asset.</summary>
        public bool IsImage  => Type == OghamContentType.Image;
        /// <summary>Returns <c>true</c> when this slot carries a sprite asset.</summary>
        public bool IsSprite => Type == OghamContentType.Sprite;
        /// <summary>Returns <c>true</c> when this slot carries an audio clip asset.</summary>
        public bool IsAudio  => Type == OghamContentType.Audio;
        /// <summary>Returns <c>true</c> when this slot carries a prefab asset.</summary>
        public bool IsPrefab => Type == OghamContentType.Prefab;

        /// <summary>
        /// Resolves and returns the display string for Text-type keys, honouring the localisation mode.
        /// Returns an empty string for non-text content types.
        /// </summary>
        /// <returns>The resolved display string, or an empty string for non-text types.</returns>
        public string ResolveText()
        {
            if (Type != OghamContentType.Text) return string.Empty;
            if (Mode == LexiconLocMode.Localised)
                return LexiconRegistry.ResolveString(GetHash()) ?? KeyOrValue ?? string.Empty;
            return KeyOrValue ?? string.Empty;
        }

        /// <summary>
        /// Resolves and returns the asset reference for non-text content types, honouring the localisation mode.
        /// Returns <c>null</c> for Text-type slots.
        /// </summary>
        /// <returns>The resolved <see cref="UnityEngine.Object"/> asset, or <c>null</c> for text-type keys.</returns>
        public UnityEngine.Object ResolveAsset()
        {
            if (Type == OghamContentType.Text) return null;
            if (Mode == LexiconLocMode.Localised)
                return LexiconRegistry.ResolveAsset(GetHash()) ?? AssetRef;
            return AssetRef;
        }

        /// <summary>
        /// Computes and returns the Lexicon hash for <see cref="KeyOrValue"/>. Used to look up localised strings
        /// or assets from <see cref="LexiconRegistry"/> when <see cref="Mode"/> is localised.
        /// </summary>
        /// <returns>The Lexicon hash of <see cref="KeyOrValue"/>.</returns>
        public ulong GetHash() => LexiconRegistry.Hash(KeyOrValue ?? string.Empty);

        /// <summary>
        /// No-op. The hash is computed directly from <see cref="KeyOrValue"/> on each call, so no cached value
        /// needs invalidating. Present for API symmetry with <c>LexiconText</c>.
        /// </summary>
        public void InvalidateHash() { }
    }
}
