using System;
using UnityEngine;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    public enum OghamContentType { Text, Image, Audio, Prefab }

    [Serializable]
    public class OghamContentKey
    {
        public OghamContentType  Type       = OghamContentType.Text;
        public LexiconLocMode    Mode       = LexiconLocMode.Literal;
        public string            KeyOrValue = string.Empty;
        public UnityEngine.Object AssetRef;

        public bool IsText   => Type == OghamContentType.Text;
        public bool IsImage  => Type == OghamContentType.Image;
        public bool IsAudio  => Type == OghamContentType.Audio;
        public bool IsPrefab => Type == OghamContentType.Prefab;

        // Resolves the display string for Text-type keys. Returns empty for non-text types.
        public string ResolveText()
        {
            if (Type != OghamContentType.Text) return string.Empty;
            if (Mode == LexiconLocMode.Localised)
                return LexiconRegistry.ResolveString(GetHash()) ?? KeyOrValue ?? string.Empty;
            return KeyOrValue ?? string.Empty;
        }

        // Resolves the asset reference for non-text types. Returns null for Text type.
        public UnityEngine.Object ResolveAsset()
        {
            if (Type == OghamContentType.Text) return null;
            if (Mode == LexiconLocMode.Localised)
                return LexiconRegistry.ResolveAsset(GetHash()) ?? AssetRef;
            return AssetRef;
        }

        public ulong GetHash() => LexiconRegistry.Hash(KeyOrValue ?? string.Empty);

        // No-op — hash is computed directly from KeyOrValue each call. Present for API symmetry with LexiconText.
        public void InvalidateHash() { }
    }
}
