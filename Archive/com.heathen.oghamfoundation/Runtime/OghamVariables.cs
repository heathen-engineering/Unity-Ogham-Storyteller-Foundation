using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// The inline-variable engine for story content text. Substitutes <c>@Token(Tag.Path)</c> and
    /// <c>@Token(Tag.Path, args…)</c> tokens with values read from a story's narrative state.
    /// <para>
    /// Every token reads a <see cref="GameplayTag"/> value from the state; how that value is interpreted
    /// is the responsibility of the registered <see cref="ITagVariable"/> for the token. Built-in tokens:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>@String(Tag.Path)</c> — treats the stored value as a Lexicon key and resolves localised text.</description></item>
    /// <item><description><c>@Float(Tag.Path, format)</c> — reads the value as a 32-bit float; <c>format</c> is an optional .NET format string.</description></item>
    /// <item><description><c>@Double(Tag.Path, format)</c> — reads the value as a 64-bit double.</description></item>
    /// <item><description><c>@Long(Tag.Path, format)</c> — reads the value as a signed 64-bit integer.</description></item>
    /// <item><description><c>@Ulong(Tag.Path, format)</c> — reads the value as an unsigned 64-bit integer.</description></item>
    /// <item><description><c>@Int(Tag.Path, format)</c> — reads the low 32 bits as a signed integer.</description></item>
    /// <item><description><c>@UInt(Tag.Path, format)</c> — reads the low 32 bits as an unsigned integer.</description></item>
    /// </list>
    /// <para>
    /// Register additional tokens with <see cref="Register(ITagVariable)"/>. Unknown tokens are left in the
    /// text unchanged so authoring mistakes are visible rather than silently dropped. There is no built-in
    /// <c>@Image</c> token: inlining an arbitrary runtime <c>Sprite</c> into TextMeshPro requires a project
    /// sprite asset, so image substitution is left to a project-specific <see cref="ITagVariable"/>.
    /// </para>
    /// </summary>
    public static class OghamVariables
    {
        private static readonly Dictionary<string, ITagVariable> _processors =
            new(StringComparer.OrdinalIgnoreCase);

        // @Token(TagPath) or @Token(TagPath, rest...). The tag path is a dot-separated identifier.
        // Group 3 captures everything after the first comma up to the closing paren, so a numeric
        // format string may itself contain commas (e.g. "#,##0.00").
        private static readonly Regex TokenRx = new Regex(
            @"@([A-Za-z_][A-Za-z0-9_]*)\(\s*([A-Za-z_][A-Za-z0-9_.]*)\s*(?:,(.*?))?\)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly char[] ArgSeparator = { ',' };

        static OghamVariables() => ResetToDefaults();

        /// <summary>
        /// Clears all registered processors and re-installs the built-in set. Called automatically on each
        /// play session so custom registrations never leak across "Enter Play Mode without Domain Reload".
        /// </summary>
        public static void ResetToDefaults()
        {
            _processors.Clear();
            Register(new StringVariable());
            Register(new NumericVariable("Float",  (s, t) => s.GetFloat(t)));
            Register(new NumericVariable("Double", (s, t) => s.GetDouble(t)));
            Register(new NumericVariable("Long",   (s, t) => s.GetLong(t)));
            Register(new NumericVariable("Ulong",  (s, t) => s.GetValue(t)));
            Register(new NumericVariable("Int",    (s, t) => s.GetInt(t)));
            Register(new NumericVariable("UInt",   (s, t) => (uint)(s.GetValue(t) & 0xFFFFFFFFUL)));
        }

        /// <summary>
        /// Registers (or replaces, by token name) a custom variable processor.
        /// </summary>
        /// <param name="variable">The processor to register. <c>null</c> or a blank token is ignored.</param>
        public static void Register(ITagVariable variable)
        {
            if (variable == null || string.IsNullOrWhiteSpace(variable.Token)) return;
            _processors[variable.Token] = variable;
        }

        /// <summary>Removes the processor registered for <paramref name="token"/>, if any.</summary>
        /// <param name="token">The token name to unregister (case-insensitive).</param>
        public static void Unregister(string token)
        {
            if (!string.IsNullOrWhiteSpace(token)) _processors.Remove(token);
        }

        /// <summary>Returns <c>true</c> when a processor is registered for <paramref name="token"/>.</summary>
        /// <param name="token">The token name to test (case-insensitive).</param>
        public static bool IsRegistered(string token) =>
            !string.IsNullOrWhiteSpace(token) && _processors.ContainsKey(token);

        /// <summary>
        /// Substitutes every recognised <c>@Token(Tag.Path, …)</c> in <paramref name="text"/> with the value
        /// resolved from <paramref name="state"/>. Unknown tokens are left unchanged. Call this before any
        /// link/markup formatting so variable values are expanded first.
        /// </summary>
        /// <param name="text">The raw content text that may contain variable tokens.</param>
        /// <param name="state">The narrative state to read values from.</param>
        /// <returns>The text with all recognised tokens replaced by their resolved values.</returns>
        public static string Interpolate(string text, GameplayTagCollection state)
        {
            if (string.IsNullOrEmpty(text) || state == null) return text;

            return TokenRx.Replace(text, m =>
            {
                var token = m.Groups[1].Value;
                if (!_processors.TryGetValue(token, out var processor))
                    return m.Value; // leave unknown tokens visible

                var tag  = GameplayTag.FromName(m.Groups[2].Value);
                var args = SplitArgs(m.Groups[3]);

                try { return processor.Resolve(state, tag, args) ?? string.Empty; }
                catch (Exception) { return m.Value; }
            });
        }

        private static string[] SplitArgs(Group group)
        {
            if (!group.Success || group.Value.Length == 0) return Array.Empty<string>();
            var parts = group.Value.Split(ArgSeparator);
            for (int i = 0; i < parts.Length; i++)
                parts[i] = parts[i].Trim();
            return parts;
        }

        // Rejoins split args into a single .NET format string, preserving commas the author wrote
        // inside a numeric format (the regex split them, this puts them back).
        private static string AsFormat(string[] args) =>
            args.Length == 0 ? null : string.Join(",", args);

        // ── Built-in processors ───────────────────────────────────────────────

        private sealed class StringVariable : ITagVariable
        {
            public string Token => "String";

            public string Resolve(GameplayTagCollection state, GameplayTag tag, string[] args)
            {
                var key = state.GetValue(tag);
                if (key == 0) return string.Empty;
                // The stored value is a Lexicon key hash (== a GameplayTag id, same hash function),
                // so it resolves directly. Fall back to the tag name, then empty.
                return LexiconRegistry.ResolveString(key)
                       ?? GameplayTagRegistry.GetName(key)
                       ?? string.Empty;
            }
        }

        private sealed class NumericVariable : ITagVariable
        {
            private readonly Func<GameplayTagCollection, GameplayTag, IFormattable> _read;

            public NumericVariable(string token, Func<GameplayTagCollection, GameplayTag, IFormattable> read)
            {
                Token = token;
                _read = read;
            }

            public string Token { get; }

            public string Resolve(GameplayTagCollection state, GameplayTag tag, string[] args)
            {
                var value  = _read(state, tag);
                var format = AsFormat(args);
                if (string.IsNullOrEmpty(format)) return value.ToString();
                try { return value.ToString(format, CultureInfo.CurrentCulture); }
                catch (FormatException) { return value.ToString(); }
            }
        }
    }
}
