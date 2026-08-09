namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Implement this interface to register a custom import source with the Ogham graph editor.
    /// Implementations are discovered automatically via <c>TypeCache</c> with no manual registration required.
    /// The editor's Import menu lists every <see cref="IOghamImporter"/> found across all editor assemblies.
    /// </summary>
    public interface IOghamImporter
    {
        /// <summary>The human-readable name shown in the Import menu of the Ogham graph editor.</summary>
        string DisplayName { get; }
        /// <summary>Opens the importer window or begins the import process when the user selects this importer from the menu.</summary>
        void Open();
    }
}
