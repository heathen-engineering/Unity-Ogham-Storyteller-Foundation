namespace Heathen.Ogham
{
    /// <summary>
    /// A single inline localisation entry parsed from a <c>.ogham</c> document's <c>localisations</c> block:
    /// a culture code, a Lexicon key, and its value. Injected into the LexiconRegistry at edit/import time so
    /// inline strings resolve without a runtime play session. (Previously nested in the removed
    /// <c>OghamCompiledData</c>; now a standalone DTO.)
    /// </summary>
    public struct OghamCompiledLocale
    {
        /// <summary>BCP 47 culture code; null or empty uses the active culture.</summary>
        public string Culture;
        /// <summary>The Lexicon dot-path key.</summary>
        public string Key;
        /// <summary>The localised string value.</summary>
        public string Value;
    }
}
