using System.Collections.Generic;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Converts raw Ogham authoring text to styled TMPro markup, applying active and inactive format templates
    /// to each <c>Ogham://</c> inline link based on whether the matching <see cref="StoryOption"/> is currently active.
    /// All visual formatting is supplied by the caller via format-string templates where <see cref="Token"/>
    /// is replaced with the clean display text.
    /// </summary>
    public static class OghamLinkFormatter
    {
        /// <summary>
        /// The placeholder token used inside format-string templates. Replaced with the clean display text
        /// of each inline link. Use this constant instead of a literal string to avoid typos.
        /// </summary>
        public const string Token = "{%}";

        // Matches already-compiled <link="Ogham://tag">display</link> tags produced by
        // OghamCompiledData.CompileEntry(). A second pass in Format() re-applies
        // active/inactive styling so compiled and raw paths behave identically.
        private static readonly System.Text.RegularExpressions.Regex CompiledLinkRx =
            new System.Text.RegularExpressions.Regex(
                @"<link=""(Ogham://[^""]+)"">(.+?)</link>",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant |
                System.Text.RegularExpressions.RegexOptions.Singleline);

        /// <summary>
        /// Converts raw authoring text to styled TMPro markup, applying <paramref name="activeFormat"/> or
        /// <paramref name="inactiveFormat"/> to each <c>Ogham://</c> inline link based on the matching
        /// <see cref="StoryOption.IsActive"/> value. Non-Ogham links pass through unchanged.
        /// Call <see cref="InterpolateState"/> before this method to expand any <c>@Token(Tag.Path)</c> variables first.
        /// </summary>
        /// <param name="rawText">The raw authoring text containing Ogham inline-link syntax.</param>
        /// <param name="node">The current <see cref="StoryNode"/> used to resolve option states. May be <c>null</c>.</param>
        /// <param name="activeFormat">TMPro template applied to active links; use <see cref="Token"/> as placeholder.</param>
        /// <param name="inactiveFormat">TMPro template applied to inactive or unresolved links; use <see cref="Token"/> as placeholder.</param>
        /// <returns>The text with all authoring markers replaced by styled TMPro markup.</returns>
        public static string Format(
            string    rawText,
            StoryNode node,
            string    activeFormat   = "<color=#4A9EFF><u>{%}</u></color>",
            string    inactiveFormat = "<color=#808080><s>{%}</s></color>")
        {
            if (string.IsNullOrEmpty(rawText)) return rawText;
            if (node == null) return OghamInlineLinkParser.ToTMProMarkup(rawText);

            var lookup = BuildLookup(node.AllOptions);

            var text = OghamInlineLinkParser.BoldRx.Replace(rawText,  "<b>$1</b>");
            text     = OghamInlineLinkParser.ItalicRx.Replace(text,   "<i>$1</i>");

            text = OghamInlineLinkParser.LinkRx.Replace(text, m =>
            {
                var display = m.Groups[1].Value;
                var target  = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;

                if (!OghamInlineLinkParser.IsOghamLink(target))
                    return $"<link=\"{target}\">{display}</link>";

                var tagPath = OghamInlineLinkParser.GetTagPath(target);
                var tagId   = GameplayTag.FromName(tagPath).Id;
                var clean   = OghamInlineLinkParser.StripMarkup(display);

                var fmt = lookup.TryGetValue(tagId, out var option)
                    ? (option.IsActive ? activeFormat : inactiveFormat)
                    : inactiveFormat;

                // No <link> tag on an unmatched Ogham reference — keeps it unclickable.
                if (option == null)
                    return (fmt ?? clean).Replace(Token, clean);

                return $"<link=\"{target}\">{(fmt ?? clean).Replace(Token, clean)}</link>";
            });

            // Second pass: re-style already-compiled <link="Ogham://...">display</link> tags.
            // OghamCompiledData.CompileEntry() converts authoring text to plain TMPro links at
            // bake time; this pass applies the active/inactive format at runtime so both the
            // compiled and raw-source paths produce identical styled output.
            text = CompiledLinkRx.Replace(text, m =>
            {
                var target  = m.Groups[1].Value;
                var inner   = m.Groups[2].Value;
                var tagPath = OghamInlineLinkParser.GetTagPath(target);
                var tagId   = GameplayTag.FromName(tagPath).Id;
                var clean   = OghamInlineLinkParser.StripMarkup(inner);

                var fmt = lookup.TryGetValue(tagId, out var option)
                    ? (option.IsActive ? activeFormat : inactiveFormat)
                    : inactiveFormat;

                if (option == null)
                    return (fmt ?? clean).Replace(Token, clean);

                return $"<link=\"{target}\">{(fmt ?? clean).Replace(Token, clean)}</link>";
            });

            return text;
        }

        /// <summary>
        /// Finds the <see cref="StoryOption"/> on <paramref name="node"/> that owns the given TMPro link ID.
        /// Searches <see cref="StoryNode.AllOptions"/> so inactive options are also found.
        /// Returns <c>null</c> when the link ID is not an Ogham scheme link or no matching option exists.
        /// </summary>
        /// <param name="linkId">The TMPro link ID string, for example <c>"Ogham://Hub.GoNorth"</c>.</param>
        /// <param name="node">The current story node whose options are searched.</param>
        /// <returns>The matching <see cref="StoryOption"/>, or <c>null</c>.</returns>
        public static StoryOption FindOption(string linkId, StoryNode node)
        {
            if (node == null || !OghamInlineLinkParser.IsOghamLink(linkId))
                return null;

            var tagPath = OghamInlineLinkParser.GetTagPath(linkId);
            var tagId   = GameplayTag.FromName(tagPath).Id;

            foreach (var opt in node.AllOptions)
                if (opt.Tag.Id == tagId) return opt;

            return null;
        }

        /// <summary>
        /// Returns <c>true</c> when the <c>Ogham://</c> link ID maps to an active option on the given node.
        /// </summary>
        /// <param name="linkId">The TMPro link ID to test.</param>
        /// <param name="node">The current story node whose options are searched.</param>
        /// <returns><c>true</c> if the matching option exists and <see cref="StoryOption.IsActive"/> is <c>true</c>.</returns>
        public static bool IsLinkActive(string linkId, StoryNode node) =>
            FindOption(linkId, node)?.IsActive == true;

        /// <summary>
        /// Extension method. Returns the option's display text formatted via the supplied template, stripping any
        /// existing markup from the raw label before substituting <see cref="Token"/>.
        /// </summary>
        /// <param name="option">The option whose display text is formatted.</param>
        /// <param name="validFormat">Template applied when <see cref="StoryOption.IsActive"/> is <c>true</c>.</param>
        /// <param name="invalidFormat">Template applied when <see cref="StoryOption.IsActive"/> is <c>false</c>.</param>
        /// <returns>The formatted display string.</returns>
        public static string GetFormattedLabel(
            this StoryOption option,
            string           validFormat,
            string           invalidFormat)
        {
            var clean = OghamInlineLinkParser.StripMarkup(option.GetText());
            var fmt   = option.IsActive ? validFormat : invalidFormat;
            return (fmt ?? clean).Replace(Token, clean);
        }

        /// <summary>
        /// Substitutes inline <c>@Token(Tag.Path, …)</c> variables in the text with values resolved from
        /// <paramref name="state"/> via <see cref="OghamVariables"/>. Built-in tokens cover localised text
        /// (<c>@String</c>) and the numeric types (<c>@Float</c>, <c>@Double</c>, <c>@Long</c>, <c>@Ulong</c>,
        /// <c>@Int</c>, <c>@UInt</c>); projects can register their own. Call this before <see cref="Format"/>
        /// so variable values are expanded before link markup runs.
        /// </summary>
        /// <param name="text">The raw text that may contain <c>@Token(Tag.Path, …)</c> variables.</param>
        /// <param name="state">The <see cref="GameplayTagCollection"/> holding the current narrative state values.</param>
        /// <returns>The text with all recognised variable tokens replaced by their resolved values.</returns>
        public static string InterpolateState(string text, GameplayTagCollection state) =>
            OghamVariables.Interpolate(text, state);

        // ── Private ───────────────────────────────────────────────────────────

        private static Dictionary<ulong, StoryOption> BuildLookup(IReadOnlyList<StoryOption> options)
        {
            var d = new Dictionary<ulong, StoryOption>(options.Count);
            foreach (var opt in options)
                d[opt.Tag.Id] = opt;
            return d;
        }
    }
}
