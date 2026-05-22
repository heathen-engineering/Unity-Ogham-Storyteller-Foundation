using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    [Serializable]
    public struct HistoryEntry
    {
        public ulong EntryId;
        // 0 = conversation closed externally with no option selection.
        public ulong SelectedOption;
    }

    [Serializable]
    public class DialogueOption
    {
        [SerializeField] private string _tagPath          = "";
        [SerializeField] private string _targetEntryPath  = "";

        public string TagPath         { get => _tagPath;         set => _tagPath         = value ?? ""; }
        public string TargetEntryPath { get => _targetEntryPath; set => _targetEntryPath = value ?? ""; }

        public GameplayTag Tag         => string.IsNullOrEmpty(_tagPath)         ? default : GameplayTag.FromName(_tagPath);
        // Empty = close conversation when this option is selected.
        public GameplayTag TargetEntry => string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);

        public LexiconText TextKey = new();
        public List<GameplayTagCondition> Conditions = new();
        public List<GameplayTagOperation> Operations = new();

        // Set when this option was synthesized from a [text](tag) inline link in a ContentKey.
        public bool   SynthesizedFromInlineLink  = false;
        // Identifies the specific inline link span that owns this option (format: "EntryTag.Keys[i].Links[j]").
        // Empty when the option was hand-authored or when the originating inline link was removed.
        public string InlineLinkSourceKeyPath    = string.Empty;
    }

    [Serializable]
    public class DialogueEntry
    {
        [SerializeField] private string _tagPath = "";

        public string TagPath { get => _tagPath; set => _tagPath = value ?? ""; }

        public GameplayTag Tag => string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);

        // Multi-role content: narrator, speaker, body, title, images, audio, prefabs, etc.
        public List<OghamContentKey> ContentKeys = new();
        public List<GameplayTagOperation> EntryOperations = new();
        public List<DialogueOption> Options = new();
    }

    [Serializable]
    public class OghamSaveState
    {
        public string Uuid;
        public string Name;
        // Identity of the OghamStory this snapshot belongs to.
        // Used by Storyteller.Restore to route the save to the correct story.
        public ulong StoryId;
        public ulong CurrentEntryId;
        public GameplayTagCollection State = new();
        public List<HistoryEntry> History = new();
    }
}
