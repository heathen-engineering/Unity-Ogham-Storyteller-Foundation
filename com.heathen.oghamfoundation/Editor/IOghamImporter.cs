namespace Heathen.Ogham.Editor
{
    // Implement this interface to register an import source with the Ogham graph editor.
    // Implementations are discovered automatically via TypeCache — no manual registration.
    // The editor's Import menu will list every IOghamImporter found across all editor assemblies.
    public interface IOghamImporter
    {
        string DisplayName { get; }
        void Open();
    }
}
