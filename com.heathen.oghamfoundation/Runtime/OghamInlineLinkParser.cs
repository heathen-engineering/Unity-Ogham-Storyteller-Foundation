using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Heathen.Ogham
{
    // Parses and converts the Ogham inline-link dialect used in Text ContentKeys.
    //
    // Authoring syntax:
    //   [display text](Tag.Path)  — link with explicit target
    //   [display text]            — link with no target (author wires later)
    //   **text**                  — bold
    //   *text*                    — italic
    //
    // At compile time, ToTMProMarkup() converts authoring syntax to TMPro <link> / <b> / <i> tags.
    // At edit time, StripMarkup() produces a clean plain-text preview.
    public static class OghamInlineLinkParser
    {
        // Protocol prefix used to identify links that map to a StoryOption tag.
        // [display](Ogham://My.Option.Tag) → resolves via node.AllOptions, styled per IsActive.
        public const string OghamScheme = "Ogham://";

        // [display](tag)  —  group 1 = display, group 2 = tag (may be null/empty)
        public static readonly Regex LinkRx = new Regex(
            @"\[([^\]]*)\](?:\(([^)]*)\))?",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Entire trimmed string is a single link (pure link)
        private static readonly Regex PureLinkRx = new Regex(
            @"^\[([^\]]*)\](?:\(([^)]*)\))?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Bold before italic to avoid double-asterisk collisions
        internal static readonly Regex BoldRx   = new Regex(@"\*\*([^*]+)\*\*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        internal static readonly Regex ItalicRx = new Regex(@"\*([^*]+)\*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex RichTagRx = new Regex(@"<[^>]+>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // True when a TMPro link ID (the value of <link="..."/>) uses the Ogham option protocol.
        public static bool IsOghamLink(string linkId) =>
            linkId != null && linkId.StartsWith(OghamScheme, StringComparison.Ordinal);

        // Strips the Ogham:// prefix to yield the raw GameplayTag dot-path.
        // Call after IsOghamLink returns true.
        public static string GetTagPath(string oghamLinkId) =>
            oghamLinkId is null ? string.Empty : oghamLinkId.Substring(OghamScheme.Length);

        // Returns true if the entire trimmed text is one [display](tag) span.
        // Outputs the display text and the tag path (tag may be empty string).
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

        // Returns all [display](tag) pairs found anywhere in the text.
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

        // Converts authoring syntax to TMPro markup.
        // [display](tag) → <link="tag">display</link>
        // [display]      → <link="">display</link>
        // **text**       → <b>text</b>
        // *text*         → <i>text</i>
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

        // Strips all authoring markup and basic TMPro rich-text tags, returning plain preview text.
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

        // Converts a display string to a valid tag-path segment (PascalCase, alphanumeric only).
        // "go north"         → "GoNorth"
        // "talk to the guard" → "TalkToTheGuard"
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
