namespace Heathen.Ogham
{
    /// <summary>
    /// Controls what happens to spawned template instances when a conversation closes.
    /// Used by <see cref="OghamTemplateSpawner"/> to determine whether to destroy or retain active prefab instances.
    /// </summary>
    public enum OghamCloseMode
    {
        /// <summary>Destroy all spawned instances when the conversation closes.</summary>
        Clear,
        /// <summary>Leave spawned instances alive when the conversation closes.</summary>
        None,
    }
}
