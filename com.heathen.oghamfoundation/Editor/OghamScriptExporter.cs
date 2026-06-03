using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Heathen.Lexicon;

namespace Heathen.Ogham.Editor
{
    /// <summary>Enumerates the output formats supported by <see cref="OghamScriptExporter"/>.</summary>
    public enum OghamExportFormat { CSV, Markdown, HTML, PlainText }

    // VO script exporter. Supports CSV, Markdown, HTML and plain-text output.
    // All formats share the same text-cleaning pipeline; only the surrounding
    // structure differs per format.
    internal static class OghamScriptExporter
    {
        // ── Options bag ───────────────────────────────────────────────────────

        internal class ExportOptions
        {
            public OghamExportFormat   Format             = OghamExportFormat.CSV;
            public bool                StripTrailingLinks = true;
            public bool                ListOptions        = true;
            public bool                RemoveFormatting   = true;
            public IReadOnlyList<string> ContentLabels    = Array.Empty<string>();
            // key → resolved text from helex; null = use raw key string
            public Func<string, string> ResolveKey        = null;
            // Wrappers for @@TagPath() substitutions. Markdown uses {} since <<>> is HTML.
            public string StateVarOpen  = "<<";
            public string StateVarClose = ">>";
        }

        // ── Strip tables ──────────────────────────────────────────────────────

        private static readonly string[] s_FormatSequences =
            { "**", "__", "*", "_", "`", "~~" };

        private static readonly char[] s_StripChars =
        {
            '→', '←', '↑', '↓', '·', '—', '…',
            '■', '▶', '▼', '►', '◄', '▸', '▾',
            '●', '○', '◆', '◇', '★', '☆',
            '|', '~', '^',
        };

        private static readonly Regex s_StateVarRegex = new Regex(
            @"@@([A-Za-z_][A-Za-z0-9_.]*)\([^)]*\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex s_InlineLinkRegex = new Regex(
            @"\[([^\]]*)\]\(Ogham://[^)]*\)",
            RegexOptions.Compiled);

        private static readonly Regex s_MultiSpace   = new Regex(@"[ \t]+",  RegexOptions.Compiled);
        private static readonly Regex s_NewlineSpace = new Regex(@"\n[ \t]+",RegexOptions.Compiled);
        private static readonly Regex s_MultiNewline = new Regex(@"\n{3,}",  RegexOptions.Compiled);

        // Chars treated as non-speakable during trailing-link scan (same set as s_StripChars).
        // Populated once in the static constructor so we get O(1) lookup.
        private static readonly HashSet<char> s_NonSpeakableSet;

        static OghamScriptExporter()
        {
            s_NonSpeakableSet = new HashSet<char>(s_StripChars);
        }

        private static bool IsNonSpeakable(char c)
            => char.IsWhiteSpace(c) || s_NonSpeakableSet.Contains(c);

        /// <summary>
        /// Exports the given dialogue entries to a VO script string in the format specified by
        /// <see cref="ExportOptions.Format"/>. Text is cleaned via the shared pipeline before output.
        /// </summary>
        /// <param name="entries">The dialogue entries to include in the export.</param>
        /// <param name="metaLookup">A dictionary mapping tag paths to <see cref="OghamNodeMeta"/> for director notes.</param>
        /// <param name="opts">Export options controlling format, filtering, and text cleaning behaviour.</param>
        /// <returns>The exported script as a string in the requested format.</returns>
        public static string Export(
            IEnumerable<DialogueEntry>                   entries,
            IReadOnlyDictionary<string, OghamNodeMeta>  metaLookup,
            ExportOptions                                opts)
        {
            return opts.Format switch {
                OghamExportFormat.Markdown  => ExportMarkdown(entries, metaLookup, opts),
                OghamExportFormat.HTML      => ExportHtml(entries, metaLookup, opts),
                OghamExportFormat.PlainText => ExportPlainText(entries, metaLookup, opts),
                _                           => ExportCsv(entries, metaLookup, opts),
            };
        }

        // ── CSV ───────────────────────────────────────────────────────────────

        private static string ExportCsv(
            IEnumerable<DialogueEntry>                  entries,
            IReadOnlyDictionary<string, OghamNodeMeta> metaLookup,
            ExportOptions opts)
        {
            var sb = new StringBuilder(4096);

            foreach (var entry in entries)
            {
                if (entry.Mode == OghamNodeMode.Fork) continue;

                var tagPath = ResolveTagPath(entry);
                OghamNodeMeta meta = null;
                metaLookup?.TryGetValue(tagPath, out meta);

                sb.AppendLine();
                sb.AppendLine(CsvCell(tagPath));
                sb.AppendLine($"{CsvCell("Notes")},{CsvCell(meta?.DirectorNotes ?? "")}");

                for (int i = 0; i < entry.ContentKeys.Count; i++)
                {
                    var text  = ResolveKeyText(entry.ContentKeys[i], opts);
                    var label = opts.ContentLabels != null && i < opts.ContentLabels.Count
                        ? opts.ContentLabels[i] ?? "" : "";
                    sb.AppendLine($"{CsvCell(label)},{CsvCell(text)}");
                }

                if (opts.ListOptions)
                {
                    var optTexts = GatherOptions(entry, opts);
                    if (optTexts.Count > 0)
                    {
                        sb.AppendLine($"{CsvCell("Options")},{CsvCell(optTexts[0])}");
                        for (int oi = 1; oi < optTexts.Count; oi++)
                            sb.AppendLine($",{CsvCell(optTexts[oi])}");
                    }
                }
            }

            sb.AppendLine();
            return sb.ToString();
        }

        // ── Markdown ──────────────────────────────────────────────────────────

        private static string ExportMarkdown(
            IEnumerable<DialogueEntry>                  entries,
            IReadOnlyDictionary<string, OghamNodeMeta> metaLookup,
            ExportOptions opts)
        {
            var sb = new StringBuilder(4096);

            foreach (var entry in entries)
            {
                if (entry.Mode == OghamNodeMode.Fork) continue;

                var tagPath = ResolveTagPath(entry);
                OghamNodeMeta meta = null;
                metaLookup?.TryGetValue(tagPath, out meta);

                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine($"## {tagPath}");
                sb.AppendLine();

                var notes = meta?.DirectorNotes ?? "";
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    sb.AppendLine($"> {notes.Replace("\n", "\n> ")}");
                    sb.AppendLine();
                }

                for (int i = 0; i < entry.ContentKeys.Count; i++)
                {
                    var text  = ResolveKeyText(entry.ContentKeys[i], opts);
                    var label = opts.ContentLabels != null && i < opts.ContentLabels.Count
                        ? opts.ContentLabels[i] ?? "" : "";

                    if (!string.IsNullOrEmpty(label))
                        sb.AppendLine($"**{MdEscape(label)}:** {text}  ");
                    else
                        sb.AppendLine($"{text}  ");
                }

                if (opts.ListOptions)
                {
                    var optTexts = GatherOptions(entry, opts);
                    if (optTexts.Count > 0)
                    {
                        sb.AppendLine();
                        sb.AppendLine("**Options:**");
                        foreach (var o in optTexts)
                            sb.AppendLine($"- {o}");
                    }
                }

                sb.AppendLine();
            }

            sb.AppendLine("---");
            return sb.ToString();
        }

        // ── HTML ──────────────────────────────────────────────────────────────

        private static string ExportHtml(
            IEnumerable<DialogueEntry>                  entries,
            IReadOnlyDictionary<string, OghamNodeMeta> metaLookup,
            ExportOptions opts)
        {
            var sb = new StringBuilder(8192);

            sb.AppendLine("<!DOCTYPE html>");
            sb.AppendLine("<html lang=\"en\"><head><meta charset=\"UTF-8\">");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:sans-serif;max-width:900px;margin:40px auto;padding:0 20px;color:#222}");
            sb.AppendLine("h2{color:#1a4080;border-bottom:2px solid #1a4080;padding-bottom:4px;margin-top:2em}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;margin:8px 0}");
            sb.AppendLine("td{padding:6px 12px;vertical-align:top;border:1px solid #ddd}");
            sb.AppendLine("td:first-child{font-weight:bold;width:120px;white-space:nowrap;background:#f5f5f5}");
            sb.AppendLine(".notes{background:#fffbe6;border-left:4px solid #f0c040;padding:8px 12px;margin:8px 0;font-style:italic}");
            sb.AppendLine(".options li{margin:2px 0}");
            sb.AppendLine("hr{border:none;border-top:1px solid #ccc;margin:2em 0}");
            sb.AppendLine("</style></head><body>");

            foreach (var entry in entries)
            {
                if (entry.Mode == OghamNodeMode.Fork) continue;

                var tagPath = ResolveTagPath(entry);
                OghamNodeMeta meta = null;
                metaLookup?.TryGetValue(tagPath, out meta);

                sb.AppendLine($"<hr><h2>{HtmlEncode(tagPath)}</h2>");

                var notes = meta?.DirectorNotes ?? "";
                if (!string.IsNullOrWhiteSpace(notes))
                    sb.AppendLine($"<div class=\"notes\"><strong>Director Notes:</strong> {HtmlEncode(notes)}</div>");

                if (entry.ContentKeys.Count > 0)
                {
                    sb.AppendLine("<table>");
                    for (int i = 0; i < entry.ContentKeys.Count; i++)
                    {
                        var text  = ResolveKeyText(entry.ContentKeys[i], opts);
                        var label = opts.ContentLabels != null && i < opts.ContentLabels.Count
                            ? opts.ContentLabels[i] ?? "" : "";
                        sb.AppendLine($"<tr><td>{HtmlEncode(label)}</td><td>{HtmlEncode(text).Replace("\n", "<br>")}</td></tr>");
                    }
                    sb.AppendLine("</table>");
                }

                if (opts.ListOptions)
                {
                    var optTexts = GatherOptions(entry, opts);
                    if (optTexts.Count > 0)
                    {
                        sb.AppendLine("<p><strong>Options:</strong></p><ul class=\"options\">");
                        foreach (var o in optTexts)
                            sb.AppendLine($"<li>{HtmlEncode(o)}</li>");
                        sb.AppendLine("</ul>");
                    }
                }
            }

            sb.AppendLine("<hr></body></html>");
            return sb.ToString();
        }

        // ── Plain text ────────────────────────────────────────────────────────

        private static string ExportPlainText(
            IEnumerable<DialogueEntry>                  entries,
            IReadOnlyDictionary<string, OghamNodeMeta> metaLookup,
            ExportOptions opts)
        {
            const string divider = "================================================================================";
            var sb = new StringBuilder(4096);

            foreach (var entry in entries)
            {
                if (entry.Mode == OghamNodeMode.Fork) continue;

                var tagPath = ResolveTagPath(entry);
                OghamNodeMeta meta = null;
                metaLookup?.TryGetValue(tagPath, out meta);

                sb.AppendLine();
                sb.AppendLine(divider);
                sb.AppendLine(tagPath);
                sb.AppendLine(divider);
                sb.AppendLine();

                var notes = meta?.DirectorNotes ?? "";
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    sb.AppendLine($"NOTES: {notes}");
                    sb.AppendLine();
                }

                for (int i = 0; i < entry.ContentKeys.Count; i++)
                {
                    var text  = ResolveKeyText(entry.ContentKeys[i], opts);
                    var label = opts.ContentLabels != null && i < opts.ContentLabels.Count
                        ? opts.ContentLabels[i] ?? "" : "";
                    var prefix = string.IsNullOrEmpty(label) ? "" : $"{label}: ";
                    sb.AppendLine($"{prefix}{text}");
                    sb.AppendLine();
                }

                if (opts.ListOptions)
                {
                    var optTexts = GatherOptions(entry, opts);
                    if (optTexts.Count > 0)
                    {
                        sb.AppendLine("Options:");
                        foreach (var o in optTexts)
                            sb.AppendLine($"  - {o}");
                        sb.AppendLine();
                    }
                }
            }

            return sb.ToString();
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static string ResolveTagPath(DialogueEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.TagPath)) return entry.TagPath;
            return entry.Tag.Id != 0 ? entry.Tag.Id.ToString("X16") : "Unknown";
        }

        private static string ResolveKeyText(OghamContentKey key, ExportOptions opts)
        {
            string raw = key.Mode == LexiconLocMode.Localised
                ? opts.ResolveKey?.Invoke(key.KeyOrValue) ?? key.KeyOrValue ?? ""
                : key.KeyOrValue ?? "";
            return CleanText(raw, opts);
        }

        private static List<string> GatherOptions(DialogueEntry entry, ExportOptions opts)
        {
            var result = new List<string>();
            foreach (var opt in entry.Options)
            {
                var raw = opt.TextKey.KeyOrValue ?? "";
                if (opt.TextKey.Mode == LexiconLocMode.Localised)
                    raw = opts.ResolveKey?.Invoke(raw) ?? raw;
                // Options never have trailing-link stripping — their text IS the link label
                var cleaned = CleanText(raw, new ExportOptions
                {
                    StripTrailingLinks = false,
                    RemoveFormatting   = opts.RemoveFormatting,
                    ResolveKey         = opts.ResolveKey,
                });
                if (!string.IsNullOrWhiteSpace(cleaned))
                    result.Add(cleaned);
            }
            return result;
        }

        // ── Text cleaning ─────────────────────────────────────────────────────

        private static string CleanText(string raw, ExportOptions opts)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            // @@TagPath() → {TagName} or <<TagName>> depending on format
            raw = s_StateVarRegex.Replace(raw, m =>
            {
                var path = m.Groups[1].Value;
                var dot  = path.LastIndexOf('.');
                var name = dot >= 0 ? path.Substring(dot + 1) : path;
                return opts.StateVarOpen + name + opts.StateVarClose;
            });

            if (opts.StripTrailingLinks)
                raw = StripTrailingLinks(raw);

            // Inline links → display text only
            raw = s_InlineLinkRegex.Replace(raw, m => m.Groups[1].Value);

            if (opts.RemoveFormatting)
            {
                foreach (var seq in s_FormatSequences)
                    raw = raw.Replace(seq, "");

                foreach (var ch in s_StripChars)
                    raw = raw.Replace(ch.ToString(), " ");
            }

            // Normalise whitespace
            raw = s_MultiSpace.Replace(raw, " ");
            raw = s_NewlineSpace.Replace(raw, "\n");
            raw = s_MultiNewline.Replace(raw, "\n\n");
            raw = raw.Trim();

            return raw;
        }

        // ── Trailing-link stripping ───────────────────────────────────────────

        // Removes inline Ogham:// links that appear only at the END of the text —
        // after the last human-readable (non-link, non-whitespace) character.
        // Links embedded in spoken text (text follows them) are left in place.
        private static string StripTrailingLinks(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var matches = s_InlineLinkRegex.Matches(text);
            if (matches.Count == 0) return text;

            // Mark every character position occupied by a link tag
            var inLink = new bool[text.Length];
            foreach (Match m in matches)
                for (int i = m.Index; i < m.Index + m.Length; i++)
                    inLink[i] = true;

            // Scan backwards for the last character that is genuinely speakable —
            // not part of a link, not whitespace, and not in the non-speakable strip set
            // (so separators like · — … don't anchor the cut point).
            int cutAt = -1;
            for (int i = text.Length - 1; i >= 0; i--)
            {
                if (!inLink[i] && !IsNonSpeakable(text[i]))
                {
                    cutAt = i + 1;
                    break;
                }
            }

            // Text is all links + whitespace — strip all links
            if (cutAt < 0)
                return s_InlineLinkRegex.Replace(text, "").Trim();

            // Keep up to the end of spoken text; drop the trailing link block
            return text.Substring(0, cutAt).TrimEnd();
        }

        // ── Format-specific encoding ──────────────────────────────────────────

        private static string CsvCell(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool needsQuote = value.IndexOf(',')  >= 0
                           || value.IndexOf('"')  >= 0
                           || value.IndexOf('\n') >= 0
                           || value.IndexOf('\r') >= 0;
            return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static string HtmlEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        private static string MdEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");
        }
    }
}
