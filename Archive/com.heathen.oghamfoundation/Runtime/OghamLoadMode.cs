namespace Heathen.Ogham
{
    /// <summary>
    /// Controls when <see cref="OghamTemplateSpawner"/> pre-fetches prefab assets for upcoming dialogue nodes.
    /// </summary>
    public enum OghamLoadMode
    {
        /// <summary>
        /// When a dialogue node is entered, pre-fetch prefab assets for all target entries reachable via the
        /// current node's options (one-node lookahead), so assets are resident before the player makes a choice.
        /// </summary>
        PreWarm,
        /// <summary>Load prefab assets only when the dialogue entry that requires them is actually entered.</summary>
        OnDemand,
    }
}
