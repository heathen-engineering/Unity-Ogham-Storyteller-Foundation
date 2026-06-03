using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Heathen.Ogham
{
    /// <summary>
    /// Parses and converts the Ogham inline-link authoring dialect used in Text ContentKeys.
    /// At compile time, <see cref="ToTMProMarkup"/> converts authoring syntax to TMPro markup.
    /// At edit time, <see cref="StripMarkup"/> produces a clean plain-text preview.
    /// Authoring syntax: <c>[display](Ogham://Tag.Path)</c> for story links, <c>**text**</c> for bold,
    /// and <c>*text*</c> for italic.
    /// </summary>
    public static class OghamInlineLinkParser
    {
        /// <summary>
        /// Protocol prefix used to identify links that map to a <see cref="StoryOption"/> tag.
        /// For example, <c>[Go North](Ogham://Hub.GoNorth)</c> resolves via <see cref="StoryNode.AllOptions"/> at runtime.
        /// </summary>
        public const string OghamScheme = "Ogham://";

        /// <summary>
        /// Compiled regex that matches the authoring link syntax <c>[display](tag)</c>.
        /// Group 1 captures the display text; group 2 captures the tag URL (may be absent).
        /// </summary>
        public static readonly Regex LinkRx = new Regex(
            @"\[([^\]]*)\](?:\(([^)]*)\))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex PureLinkRx = new Regex(
            @"^\[([^\]]*)\](?:\(([^)]*)\))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        
        private static readonly Regex RichTagRx = new Regex(@"<[^>]+>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        
        /// <summary>
        /// Compiled regex that matches <c>**text**</c> bold spans. Applied before
        /// <see cref="ItalicRx"/> to avoid double-asterisk collisions.
        /// </summary>
        public static readonly Regex BoldRx   = new Regex(@"\*\*([^*]+)\*\*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        /// <summary>
        /// Compiled regex that matches <c>*text*</c> italic spans. Applied after
        /// <see cref="BoldRx"/> to avoid double-asterisk collisions.
        /// </summary>
        public static readonly Regex ItalicRx = new Regex(@"\*([^*]+)\*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Returns <c>true</c> when a TMPro link ID uses the Ogham option protocol (i.e. starts with <see cref="OghamScheme"/>).
        /// </summary>
        /// <param name="linkId">The link ID string from a TMPro <c>&lt;link="..."&gt;</c> tag.</param>
        /// <returns><c>true</c> if the ID begins with the Ogham scheme prefix; otherwise <c>false</c>.</returns>
        public static bool IsOghamLink(string linkId) =>
            linkId != null && linkId.StartsWith(OghamScheme, StringComparison.Ordinal);

        /// <summary>
        /// Strips the <see cref="OghamScheme"/> prefix and returns the raw GameplayTag dot-path.
        /// Call only after <see cref="IsOghamLink"/> returns <c>true</c>.
        /// </summary>
        /// <param name="oghamLinkId">A link ID string that begins with the Ogham scheme.</param>
        /// <returns>The dot-path tag string without the Ogham prefix, or an empty string if the input is <c>null</c>.</returns>
        public static string GetTagPath(string oghamLinkId) =>
            oghamLinkId is null ? string.Empty : oghamLinkId.Substring(OghamScheme.Length);

        /// <summary>
        /// Returns <c>true</c> if the entire trimmed text is a single <c>[display](tag)</c> span.
        /// The display text and tag path are written to the output parameters; tag may be an empty string.
        /// </summary>
        /// <param name="text">The raw authoring text to test.</param>
        /// <param name="display">Receives the display text of the link, or an empty string.</param>
        /// <param name="tag">Receives the tag URL of the link, or an empty string.</param>
        /// <returns><c>true</c> when the entire string is a single link span; <c>false</c> otherwise.</returns>
        public static bool IsPureLink(string text, out string display, out string tag)
        {
            display = tag = string.Empty;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = PureLinkRx.Match(text.Trim());
            if (!m.Success) return false;
            display = m.Groups[1].Value;
            tag     = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
            return true;
        }

        /// <summary>
        /// Returns all <c>[display](tag)</c> pairs found anywhere in the text, including those with no tag.
        /// </summary>
        /// <param name="text">The raw authoring text to scan.</param>
        /// <returns>A list of (display, tag) tuples; tag may be an empty string when absent.</returns>
        public static List<(string display, string tag)> ExtractLinks(string text)
        {
            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (Match m in LinkRx.Matches(text))
            {
                var display = m.Groups[1].Value;
                var tag     = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
                result.Add((display, tag));
            }
            return result;
        }

        /// <summary>
        /// Converts authoring syntax to TMPro-compatible markup. Bold and italic markers are converted first;
        /// link spans become <c>&lt;link="tag"&gt;display&lt;/link&gt;</c> tags.
        /// </summary>
        /// <param name="text">The raw authoring text using Ogham inline-link syntax.</param>
        /// <returns>The text with all authoring markers replaced by TMPro markup tags.</returns>
        public static string ToTMProMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Bold first, then italic (avoids **foo** matching as *(*foo*)*  )
            text = BoldRx.Replace(text,   "<b>$1</b>");
            text = ItalicRx.Replace(text, "<i>$1</i>");

            // Links
            text = LinkRx.Replace(text, m =>
            {
                var display = m.Groups[1].Value;
                var tag     = m.Groups[2].Success ? m.Groups[2].Value.Trim() : string.Empty;
                return $"<link=\"{tag}\">{display}</link>";
            });

            return text;
        }

        /// <summary>
        /// Strips all authoring markup (bold/italic markers, link wrappers) and basic TMPro rich-text tags,
        /// returning clean plain-text suitable for preview display or search.
        /// </summary>
        /// <param name="text">The raw or partially-converted text to strip.</param>
        /// <returns>Plain text with all markup removed.</returns>
        public static string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            // Remove bold/italic markers
            text = BoldRx.Replace(text,   "$1");
            text = ItalicRx.Replace(text, "$1");

            // Replace [display](tag) and [display] with display text only
            text = LinkRx.Replace(text, m => m.Groups[1].Value);

            // Strip TMPro rich-text tags (<color=#...>, </color>, <link=...>, </link>, <b>, </b>, etc.)
            text = RichTagRx.Replace(text, "");

            return text;
        }

        /// <summary>
        /// Converts a display string to a valid GameplayTag path segment using PascalCase with alphanumeric
        /// characters only. For example, "go north" becomes "GoNorth" and "talk to the guard" becomes "TalkToTheGuard".
        /// Returns "Link" when the result would be empty.
        /// </summary>
        /// <param name="display">The human-readable display text to normalise.</param>
        /// <returns>A PascalCase, alphanumeric tag segment string of at most 32 characters.</returns>
        public static string NormaliseForTag(string display)
        {
            if (string.IsNullOrWhiteSpace(display)) return "Link";

            var sb    = new StringBuilder();
            bool next = true;
            foreach (char c in display)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(next ? char.ToUpperInvariant(c) : c);
                    next = false;
                }
                else if (c == ' ' || c == '_' || c == '-')
                {
                    next = true;
                }
            }

            if (sb.Length == 0) return "Link";
            if (sb.Length > 32) sb.Length = 32;
            return sb.ToString();
        }
    }
}
