using System.Collections.Generic;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    // Converts raw Ogham authoring text to styled TMPro markup.
    // All visual formatting is supplied by the caller via format-string templates
    // where {%} is replaced with the plain display text of the link.
    //
    // Format templates use {%} as the placeholder for the display text.
    // {%} is chosen over bare % to avoid collision with percent signs in real content.
    // Reference the constant OghamLinkFormatter.Token rather than hard-coding the string.
    //
    // Example templates:
    //   activeFormat   = "<color=#4A9EFF><u>{%}</u></color>"
    //   inactiveFormat = "<color=#808080><s>{%}</s></color>"
    //   customExample  = "<b><color=white>1): </color></b><color=blue><u>{%}</u></color>"
    //
    // Usage — full node text (e.g. in Storyteller.OnEntered):
    //   tmpText.text = OghamLinkFormatter.Format(node.GetText(0), node,
    //       activeFormat:   "<color=#4A9EFF><u>{%}</u></color>",
    //       inactiveFormat: "<color=#808080><s>{%}</s></color>");
    //
    // Usage — individual option label (e.g. populating a button):
    //   btn.label.text = option.GetFormattedLabel(
    //       validFormat:   "<color=#4A9EFF>{%}</color>",
    //       invalidFormat: "<color=#808080>{%}</color>");
    //
    // Usage — TMPro OnPointerClick handler:
    //   var option = OghamLinkFormatter.FindOption(linkInfo.GetLinkID(), node);
    //   if (option != null && option.IsActive) option.Choose();
    public static class OghamLinkFormatter
    {
        // Placeholder token inside format strings. Replaced with the clean display text.
        // Use this constant instead of the literal string to avoid typos.
        public const string Token = "{%}";

        // Convert raw authoring text to TMPro markup.
        // Each Ogham:// link is wrapped in <link="Ogham://...">FORMATTED_DISPLAY</link>.
        // The display text is extracted from the authoring syntax, stripped of any existing
        // markup, then substituted for {%} in the appropriate format template.
        //
        // Outcomes per link:
        //   IsActive  true  → <link="...">activeFormat  .Replace("{%}", display)</link>
        //   IsActive  false → <link="...">inactiveFormat.Replace("{%}", display)</link>
        //   not found       → inactiveFormat applied, no <link> tag (authoring error)
        //
        // Non-Ogham links (no Ogham:// prefix) pass through as plain <link="tag">display</link>.
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

            return text;
        }

        // Find the StoryOption that owns a TMPro link ID (e.g. "Ogham://Hub.GoNorth").
        // Returns null when the link is not an Ogham:// link or the option is not on this node.
        // Searches AllOptions so inactive options are also found.
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

        // True when the Ogham:// link maps to an active option on this node.
        public static bool IsLinkActive(string linkId, StoryNode node) =>
            FindOption(linkId, node)?.IsActive == true;

        // ── StoryOption extension ─────────────────────────────────────────────

        // Returns the option's display text formatted via the supplied template.
        // Strips any existing markup from the raw label before substituting {%}.
        //
        //   option.GetFormattedLabel(
        //       "<color=#4A9EFF><u>{%}</u></color>",
        //       "<color=#808080><s>{%}</s></color>");
        public static string GetFormattedLabel(
            this StoryOption option,
            string           validFormat,
            string           invalidFormat)
        {
            var clean = OghamInlineLinkParser.StripMarkup(option.GetText());
            var fmt   = option.IsActive ? validFormat : invalidFormat;
            return (fmt ?? clean).Replace(Token, clean);
        }

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
