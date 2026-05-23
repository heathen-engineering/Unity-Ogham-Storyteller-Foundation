using System;
using System.Collections.Generic;

namespace Heathen.Ogham
{
    // Serializer-agnostic description of a complete story for runtime creation from code or disk.
    // All enum values are stored as strings so JSON/XML serialisers round-trip cleanly.
    // Feed this to OghamStoryBuilder.Build / BuildAsync to get a live OghamStory.
    [Serializable]
    public class OghamStoryManifest
    {
        public string StoryTagPath = string.Empty;
        // String entries injected into LexiconRegistry before the story is created.
        public List<OghamLocaleManifest>    Localisations = new();
        // Asset entries loaded via OghamStoryBuilder.AssetLoader and injected into LexiconRegistry.
        public List<OghamAssetManifest>     Assets        = new();
        public List<OghamEntryManifest>     Entries       = new();
    }

    [Serializable]
    public class OghamLocaleManifest
    {
        // BCP 47 culture code, e.g. "en", "fr", "ja". Null/empty uses the active culture.
        public string Culture = string.Empty;
        public string Key     = string.Empty;
        public string Value   = string.Empty;
    }

    [Serializable]
    public class OghamAssetManifest
    {
        // Lexicon dot-path key used for resolve calls, e.g. "Story.Intro.Image".
        public string LexiconKey = string.Empty;
        // Source path passed to OghamStoryBuilder.AssetLoader. Falls back to LexiconKey when empty.
        public string Source     = string.Empty;
        public string Culture    = string.Empty;
    }

    [Serializable]
    public class OghamEntryManifest
    {
        public string TagPath = string.Empty;
        public List<OghamContentManifest>   ContentKeys     = new();
        public List<OghamOperationManifest> EntryOperations = new();
        public List<OghamOptionManifest>    Options         = new();
    }

    [Serializable]
    public class OghamContentManifest
    {
        // OghamContentType name: "Text", "Image", "Audio", "Prefab"
        public string Type       = "Text";
        // LexiconLocMode name: "Literal", "Localised", "Invariant"
        public string Mode       = "Literal";
        public string KeyOrValue = string.Empty;
    }

    [Serializable]
    public class OghamOptionManifest
    {
        public string TagPath         = string.Empty;
        // Empty = close the conversation when this option is selected.
        public string TargetEntryPath = string.Empty;
        // LexiconLocMode name: "Literal" or "Localised"
        public string TextMode        = "Literal";
        public string TextKey         = string.Empty;
        public List<OghamConditionManifest>  Conditions = new();
        public List<OghamOperationManifest>  Operations = new();
    }

    [Serializable]
    public class OghamOperationManifest
    {
        public string TagPath    = string.Empty;
        // GameplayTagArithmetic name: "Set", "Add", "Subtract", "Multiply", "Divide", "Min", "Max"
        public string Arithmetic = "Set";
        public ulong  Value      = 1;
        public List<OghamConditionManifest> Conditions = new();
    }

    [Serializable]
    public class OghamConditionManifest
    {
        public string TagPath      = string.Empty;
        // GameplayTagComparisonOp name: "Exists", "NotExists", "Equal", "NotEqual",
        //   "Less", "LessEqual", "Greater", "GreaterEqual"
        public string Comparison   = "Exists";
        public ulong  CompareValue = 1;
        public bool   ExactMatch   = true;
        // GameplayTagLogicOp name: "And", "Or", "Xor"
        public string LogicOp      = "And";
    }
}
