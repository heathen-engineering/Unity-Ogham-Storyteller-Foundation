using System;
using System.Collections.Generic;

namespace Heathen.Ogham
{
    /// <summary>
    /// Serialiser-agnostic description of a complete story for runtime creation from code or disk.
    /// All enum values are stored as strings so JSON and XML serialisers round-trip cleanly.
    /// Pass this to <see cref="OghamStoryBuilder.Build(OghamStoryManifest, bool)"/> or
    /// <see cref="OghamStoryBuilder.BuildAsync(OghamStoryManifest, bool)"/> to produce a live <see cref="OghamStory"/>.
    /// </summary>
    [Serializable]
    public class OghamStoryManifest
    {
        /// <summary>The dot-path GameplayTag that uniquely identifies this story.</summary>
        public string StoryTagPath = string.Empty;
        /// <summary>String entries injected into <see cref="Heathen.Lexicon.LexiconRegistry"/> before the story is created.</summary>
        public List<OghamLocaleManifest>    Localisations = new();
        /// <summary>Asset entries loaded via <see cref="OghamStoryBuilder.AssetLoader"/> and injected into <see cref="Heathen.Lexicon.LexiconRegistry"/>.</summary>
        public List<OghamAssetManifest>     Assets        = new();
        /// <summary>All dialogue entries that form the story graph.</summary>
        public List<OghamEntryManifest>     Entries       = new();
    }

    /// <summary>
    /// A single localisation string entry for injection into <see cref="Heathen.Lexicon.LexiconRegistry"/>
    /// when a story is built from an <see cref="OghamStoryManifest"/>.
    /// </summary>
    [Serializable]
    public class OghamLocaleManifest
    {
        /// <summary>BCP 47 culture code, e.g. "en", "fr", "ja". Null or empty uses the active culture.</summary>
        public string Culture = string.Empty;
        /// <summary>The dot-path Lexicon key for this localised string.</summary>
        public string Key     = string.Empty;
        /// <summary>The localised string value.</summary>
        public string Value   = string.Empty;
    }

    /// <summary>
    /// Describes a single asset entry to be loaded asynchronously and injected into
    /// <see cref="Heathen.Lexicon.LexiconRegistry"/> when a story is built from an <see cref="OghamStoryManifest"/>.
    /// </summary>
    [Serializable]
    public class OghamAssetManifest
    {
        /// <summary>The Lexicon dot-path key under which the loaded asset is stored, e.g. "Story.Intro.Image".</summary>
        public string LexiconKey = string.Empty;
        /// <summary>The source path passed to <see cref="OghamStoryBuilder.AssetLoader"/>. Falls back to <see cref="LexiconKey"/> when empty.</summary>
        public string Source     = string.Empty;
        /// <summary>BCP 47 culture code for the asset entry. Null or empty uses the active culture.</summary>
        public string Culture    = string.Empty;
    }

    /// <summary>
    /// Describes a single dialogue entry within an <see cref="OghamStoryManifest"/>, including its content
    /// keys, entry operations, and player options.
    /// </summary>
    [Serializable]
    public class OghamEntryManifest
    {
        /// <summary>The dot-path GameplayTag that identifies this entry in the story graph.</summary>
        public string TagPath = string.Empty;
        /// <summary>The ordered list of content key descriptors for this entry (text, images, audio, etc.).</summary>
        public List<OghamContentManifest>   ContentKeys     = new();
        /// <summary>Operations applied to narrative state when this entry is entered.</summary>
        public List<OghamOperationManifest> EntryOperations = new();
        /// <summary>The player options available at this entry.</summary>
        public List<OghamOptionManifest>    Options         = new();
    }

    /// <summary>
    /// Describes a single content key slot within an <see cref="OghamEntryManifest"/>.
    /// Enum values are stored as strings for serialiser portability.
    /// </summary>
    [Serializable]
    public class OghamContentManifest
    {
        /// <summary><see cref="OghamContentType"/> name: "Text", "Image", "Audio", or "Prefab".</summary>
        public string Type       = "Text";
        /// <summary><see cref="Heathen.Lexicon.LexiconLocMode"/> name: "Literal", "Localised", or "Invariant".</summary>
        public string Mode       = "Literal";
        /// <summary>The literal value or Lexicon key, depending on <see cref="Mode"/>.</summary>
        public string KeyOrValue = string.Empty;
    }

    /// <summary>
    /// Describes a single player option within an <see cref="OghamEntryManifest"/>, including its
    /// display text, navigation target, conditions, and operations.
    /// </summary>
    [Serializable]
    public class OghamOptionManifest
    {
        /// <summary>The dot-path GameplayTag that identifies this option.</summary>
        public string TagPath         = string.Empty;
        /// <summary>The dot-path tag of the entry to navigate to. Empty closes the conversation when this option is selected.</summary>
        public string TargetEntryPath = string.Empty;
        /// <summary><see cref="Heathen.Lexicon.LexiconLocMode"/> name: "Literal" or "Localised".</summary>
        public string TextMode        = "Literal";
        /// <summary>The literal display string or Lexicon key for this option, depending on <see cref="TextMode"/>.</summary>
        public string TextKey         = string.Empty;
        /// <summary>Conditions that must all be satisfied for this option to be active.</summary>
        public List<OghamConditionManifest>  Conditions = new();
        /// <summary>Operations applied to narrative state when this option is chosen.</summary>
        public List<OghamOperationManifest>  Operations = new();
    }

    /// <summary>
    /// Describes a single narrative-state operation within an <see cref="OghamOptionManifest"/> or
    /// <see cref="OghamEntryManifest"/>. Enum values are stored as strings for serialiser portability.
    /// </summary>
    [Serializable]
    public class OghamOperationManifest
    {
        /// <summary>The dot-path tag whose state value is modified.</summary>
        public string TagPath    = string.Empty;
        /// <summary><see cref="GameplayTags.GameplayTagArithmetic"/> name: "Set", "Add", "Subtract", "Multiply", "Divide", "Min", or "Max".</summary>
        public string Arithmetic = "Set";
        /// <summary>The unsigned integer operand for the operation.</summary>
        public ulong  Value      = 1;
        /// <summary>Optional conditions that must be satisfied before this operation is applied.</summary>
        public List<OghamConditionManifest> Conditions = new();
    }

    /// <summary>
    /// Describes a single condition used to gate an option or operation within an <see cref="OghamStoryManifest"/>.
    /// Enum values are stored as strings for serialiser portability.
    /// </summary>
    [Serializable]
    public class OghamConditionManifest
    {
        /// <summary>The dot-path tag whose state value is tested.</summary>
        public string TagPath      = string.Empty;
        /// <summary><see cref="GameplayTags.GameplayTagComparisonOp"/> name, e.g. "Exists", "Equal", "Greater".</summary>
        public string Comparison   = "Exists";
        /// <summary>The right-hand side value for the comparison.</summary>
        public ulong  CompareValue = 1;
        /// <summary>
        /// When non-empty, the right-hand side is drawn from the named tag's state value instead of
        /// <see cref="CompareValue"/>, enabling tag-vs-tag comparisons such as "Total less-than-or-equal Money".
        /// </summary>
        public string CompareTagPath = string.Empty;
        /// <summary>When <c>true</c>, only an exact tag match is considered. Defaults to <c>true</c>.</summary>
        public bool   ExactMatch   = true;
        /// <summary><see cref="GameplayTags.GameplayTagLogicOp"/> name joining this condition to the preceding one: "And", "Or", or "Xor".</summary>
        public string LogicOp      = "And";
    }
}
