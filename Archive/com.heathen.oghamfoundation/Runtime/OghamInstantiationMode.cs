namespace Heathen.Ogham
{
    /// <summary>
    /// Controls how <see cref="OghamTemplateSpawner"/> manages prefab instances when a new dialogue node is entered.
    /// </summary>
    public enum OghamInstantiationMode
    {
        /// <summary>
        /// Retains instances whose prefab key is present in the new entry, spawns instances for new keys,
        /// and destroys instances whose key is absent. Minimises instantiation overhead when keys overlap between nodes.
        /// </summary>
        Diff,
        /// <summary>Destroys all active instances when each entry is entered, then spawns fresh instances.</summary>
        Replace,
        /// <summary>
        /// Always spawns new instances without proactively destroying existing ones.
        /// Use this when spawned prefabs manage their own lifetime (self-destructing prefab pattern).
        /// </summary>
        Append,
    }
}
