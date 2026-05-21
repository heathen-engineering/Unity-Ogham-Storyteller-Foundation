namespace Heathen.Ogham
{
    public enum OghamLoadMode
    {
        // On OnDialogueEntered, pre-load prefab assets for all option target entries (one node lookahead).
        PreWarm,
        // Load prefab assets only when the entry that needs them is actually entered.
        OnDemand,
    }
}
