using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// Records one step in a conversation's history: the entry that was displayed and the option the player chose.
    /// A <see cref="SelectedOption"/> value of zero means the conversation was closed externally without a selection.
    /// </summary>
    [Serializable]
    public struct HistoryEntry
    {
        /// <summary>The tag ID of the dialogue entry that was displayed at this step.</summary>
        public ulong EntryId;
        /// <summary>The tag ID of the option the player selected. Zero when the conversation was closed without a selection.</summary>
        public ulong SelectedOption;
    }

    /// <summary>
    /// Represents one player-selectable option within a <see cref="DialogueEntry"/>. At runtime, options are
    /// evaluated against the narrative state and exposed as <see cref="StoryOption"/> wrappers via <see cref="StoryNode"/>.
    /// </summary>
    [Serializable]
    public class DialogueOption
    {
        /// <summary>
        /// The dot-path string identifying this option's GameplayTag. Setting this property also updates
        /// <see cref="Tag"/> to keep the stored hash in sync. Cleared in compiled assets.
        /// </summary>
        public string TagPath
        {
            get => _tagPath;
            set
            {
                _tagPath = value ?? "";
                Tag = string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);
            }
        }

        /// <summary>
        /// The dot-path string of the entry to navigate to when this option is chosen. Setting this property
        /// also updates <see cref="TargetEntry"/>. Empty means close the conversation.
        /// </summary>
        public string TargetEntryPath
        {
            get => _targetEntryPath;
            set
            {
                _targetEntryPath = value ?? "";
                TargetEntry = string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);
            }
        }

        /// <summary>The cached tag hash for this option. Prefer <see cref="ResolvedTag"/> for runtime use.</summary>
        public GameplayTag Tag;
        /// <summary>The cached tag hash for the navigation target. Prefer <see cref="ResolvedTargetEntry"/> for runtime use.</summary>
        public GameplayTag TargetEntry;

        /// <summary>
        /// Returns the option's resolved <see cref="GameplayTag"/>, preferring the stored hash and falling back
        /// to hashing <see cref="TagPath"/> for backward compatibility with unsaved authoring assets.
        /// </summary>
        public GameplayTag ResolvedTag =>
            Tag.IsValid ? Tag : GameplayTag.FromName(_tagPath);

        /// <summary>
        /// Returns the resolved navigation target tag, preferring the stored hash and falling back to hashing
        /// <see cref="TargetEntryPath"/>. Returns a default tag when no target is set.
        /// </summary>
        public GameplayTag ResolvedTargetEntry =>
            TargetEntry.IsValid ? TargetEntry :
            string.IsNullOrEmpty(_targetEntryPath) ? default : GameplayTag.FromName(_targetEntryPath);

        /// <summary>Returns <c>true</c> when this option has a non-empty navigation target.</summary>
        public bool HasTarget => ResolvedTargetEntry.Id != 0;

        /// <summary>The display text or Lexicon key for this option's label.</summary>
        public LexiconText TextKey = new();
        /// <summary>Conditions evaluated against the narrative state to determine whether this option is active.</summary>
        public List<GameplayTagCondition> Conditions = new();
        /// <summary>Operations applied to the narrative state when this option is chosen.</summary>
        public List<GameplayTagOperation> Operations = new();

        /// <summary>
        /// When <c>true</c>, this option was synthesised automatically from a <c>[text](Ogham://Tag)</c>
        /// inline link inside a Text ContentKey rather than being authored explicitly in the graph editor.
        /// </summary>
        public bool   SynthesizedFromInlineLink  = false;
        /// <summary>Identifies the specific inline link span within its ContentKey that owns this synthesised option.</summary>
        public string InlineLinkSourceKeyPath    = string.Empty;

        [SerializeField] private string _tagPath         = "";
        [SerializeField] private string _targetEntryPath = "";
    }

    /// <summary>
    /// Determines the behavioural mode of a <see cref="DialogueEntry"/> node in the story graph.
    /// </summary>
    public enum OghamNodeMode
    {
        /// <summary>Standard content node: displays content and waits for a player option selection.</summary>
        Content,
        /// <summary>Silent routing node: automatically evaluates routes and navigates without raising <see cref="OghamSession.OnEntered"/>.</summary>
        Fork,
    }

    /// <summary>
    /// Represents one node in the dialogue graph. Contains content keys for display, entry operations that
    /// fire when the node is entered, and options that the player can select to navigate the graph.
    /// </summary>
    [Serializable]
    public class DialogueEntry
    {
        /// <summary>
        /// The dot-path string identifying this entry's GameplayTag. Setting this property also updates
        /// <see cref="Tag"/> to keep the stored hash in sync. Cleared in compiled assets.
        /// </summary>
        public string TagPath
        {
            get => _tagPath;
            set
            {
                _tagPath = value ?? "";
                Tag = string.IsNullOrEmpty(_tagPath) ? default : GameplayTag.FromName(_tagPath);
            }
        }

        /// <summary>The cached tag hash for this entry. Prefer <see cref="ResolvedTag"/> for runtime use.</summary>
        public GameplayTag Tag;

        /// <summary>
        /// Returns the entry's resolved <see cref="GameplayTag"/>, preferring the stored hash and falling back
        /// to hashing <see cref="TagPath"/> for backward compatibility with unsaved authoring assets.
        /// </summary>
        public GameplayTag ResolvedTag =>
            Tag.IsValid ? Tag : GameplayTag.FromName(_tagPath);

        /// <summary>The behavioural mode of this node (content or fork).</summary>
        public OghamNodeMode Mode = OghamNodeMode.Content;

        /// <summary>
        /// Multi-role content keys in authoring order: narrator text, speaker name, body, title, images, audio,
        /// prefabs, and so on. Accessed by index via <see cref="StoryNode.GetText"/>, <see cref="StoryNode.GetAudio"/>, etc.
        /// </summary>
        public List<OghamContentKey> ContentKeys = new();
        /// <summary>Operations applied to the narrative state immediately when this entry is entered.</summary>
        public List<GameplayTagOperation> EntryOperations = new();
        /// <summary>The player-selectable options available at this node.</summary>
        public List<DialogueOption> Options = new();

        [SerializeField] private string _tagPath = "";
    }

    /// <summary>
    /// A serialisable snapshot of a single <see cref="OghamSession"/> used for save and restore.
    /// Captures the current entry, narrative state, and conversation history at a point in time.
    /// </summary>
    [Serializable]
    public class OghamSaveState
    {
        /// <summary>A unique identifier for this save state, generated by <see cref="OghamSession.Snapshot"/>.</summary>
        public string Uuid;
        /// <summary>A human-readable label for this save state.</summary>
        public string Name;
        /// <summary>The tag ID of the story (<see cref="OghamSession.Id"/>) this snapshot belongs to.</summary>
        public ulong StoryId;
        /// <summary>The tag ID of the dialogue entry that was active when the snapshot was taken.</summary>
        public ulong CurrentEntryId;
        /// <summary>A copy of the narrative state at snapshot time.</summary>
        public GameplayTagCollection State = new();
        /// <summary>A copy of the conversation history at snapshot time.</summary>
        public List<HistoryEntry> History = new();
    }
}
