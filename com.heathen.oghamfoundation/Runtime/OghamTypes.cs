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
        // Authoring-only string paths. Set by the graph editor; cleared in compiled assets.
        [SerializeField] private string _tagPath         = "";
        [SerializeField] private string _targetEntryPath = "";

        // Stored hash values. Compiler writes these directly.
        // Authoring setters keep them in sync with the string fields.
        public GameplayTag Tag;
        public GameplayTag TargetEntry;

        public string TagPath
        {
            get => _tagPath;
            set
            {
                _tagPath = value ?? "";
                Tag = string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);
            }
        }

        public string TargetEntryPath
        {
            get => _targetEntryPath;
            set
            {
                _targetEntryPath = value ?? "";
                TargetEntry = string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);
            }
        }

        // Runtime resolution: prefer stored hash, fall back to hashing the string path.
        // The fallback keeps old authoring assets working until they are re-saved.
        public GameplayTag ResolvedTag =>
            Tag.IsValid ? Tag : GameplayTag.FromName(_tagPath);

        public GameplayTag ResolvedTargetEntry =>
            TargetEntry.IsValid ? TargetEntry :
            string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);

        public bool HasTarget => ResolvedTargetEntry.Id != 0;

        public LexiconText TextKey = new();
        public List<GameplayTagCondition> Conditions = new();
        public List<GameplayTagOperation> Operations = new();

        // Set when this option was synthesized from a [text](tag) inline link in a ContentKey.
        public bool   SynthesizedFromInlineLink  = false;
        // Identifies the specific inline link span that owns this option.
        public string InlineLinkSourceKeyPath    = string.Empty;
    }

    public enum OghamNodeMode
    {
        Content,  // standard node: displays content, waits for a player option selection
        Fork,     // silent routing node: evaluates routes automatically, fires no OnEntered
    }

    [Serializable]
    public class DialogueEntry
    {
        // Authoring-only string path. Set by the graph editor; cleared in compiled assets.
        [SerializeField] private string _tagPath = "";

        // Stored hash value. Compiler writes this directly.
        // Authoring setter keeps it in sync with the string field.
        public GameplayTag Tag;

        public string TagPath
        {
            get => _tagPath;
            set
            {
                _tagPath = value ?? "";
                Tag = string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);
            }
        }

        // Runtime resolution: prefer stored hash, fall back to hashing the string path.
        public GameplayTag ResolvedTag =>
            Tag.IsValid ? Tag : GameplayTag.FromName(_tagPath);

        public OghamNodeMode Mode = OghamNodeMode.Content;

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
        public ulong StoryId;
        public ulong CurrentEntryId;
        public GameplayTagCollection State = new();
        public List<HistoryEntry> History = new();
    }
}
