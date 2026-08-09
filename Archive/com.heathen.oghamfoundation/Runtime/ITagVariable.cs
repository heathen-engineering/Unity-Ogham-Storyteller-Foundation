using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// A pluggable resolver for an inline story variable token. Tokens are written in content text as
    /// <c>@Token(Tag.Path)</c> or <c>@Token(Tag.Path, arg1, arg2, …)</c>. At render time the matching
    /// processor reads the value stored for <c>Tag.Path</c> in the story's narrative state and returns
    /// the string to substitute in its place.
    /// <para>
    /// Register custom processors with <see cref="OghamVariables.Register(ITagVariable)"/> to extend the
    /// authoring vocabulary beyond the built-in types (<c>String</c>, <c>Float</c>, <c>Double</c>,
    /// <c>Long</c>, <c>Ulong</c>, <c>Int</c>, <c>UInt</c>).
    /// </para>
    /// </summary>
    public interface ITagVariable
    {
        /// <summary>
        /// The token name this processor handles, without the leading <c>@</c> or parentheses
        /// (for example <c>"Float"</c>). Matching is case-insensitive.
        /// </summary>
        string Token { get; }

        /// <summary>
        /// Resolves the token to its display string.
        /// </summary>
        /// <param name="state">The narrative state the value is read from.</param>
        /// <param name="tag">The tag named as the first argument (<c>Tag.Path</c>) inside the token.</param>
        /// <param name="args">
        /// Any further comma-separated arguments after the tag path, each trimmed; never <c>null</c>
        /// but may be empty. The numeric built-ins treat these (re-joined) as a .NET format string.
        /// </param>
        /// <returns>The string to substitute for the token in the rendered text.</returns>
        string Resolve(GameplayTagCollection state, GameplayTag tag, string[] args);
    }
}
