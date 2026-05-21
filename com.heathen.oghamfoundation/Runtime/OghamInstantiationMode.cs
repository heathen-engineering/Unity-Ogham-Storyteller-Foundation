namespace Heathen.Ogham
{
    public enum OghamInstantiationMode
    {
        // Keep alive instances whose prefab key appears in the new entry; spawn new, destroy absent.
        Diff,
        // Destroy all active instances on each entry, then spawn fresh.
        Replace,
        // Always spawn; never proactively destroy (self-destructing prefabs pattern).
        Append,
    }
}
