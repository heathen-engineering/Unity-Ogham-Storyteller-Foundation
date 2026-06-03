using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;

namespace Heathen.Ogham
{
    /// <summary>
    /// Runtime-ready compiled story asset. Merges one or more <see cref="OghamData"/>
    /// source files into a single indexed asset for use at runtime.
    /// <para>
    /// Source file references are stored as asset GUIDs (strings) rather than direct
    /// <see cref="OghamData"/> object references. This prevents Unity's dependency tracker
    /// from pulling the authoring assets into player builds.
    /// </para>
    /// <para>
    /// Generated automatically on build by <c>OghamBuildProcessor</c>, or manually via
    /// the "Compile…" button in the Ogham Storyteller tool window
    /// (Tools → Heathen → Ogham Storyteller).
    /// </para>
    /// </summary>
    /// <summary>
    /// A single localised string entry embedded inside a compiled story asset. Used to inject
    /// inline localisations from a .ogham source file into <see cref="Heathen.Lexicon.LexiconRegistry"/> at runtime.
    /// </summary>
    [Serializable]
    public struct OghamCompiledLocale
    {
        /// <summary>BCP 47 culture code, for example "en" or "fr". Empty means the invariant culture.</summary>
        public string Culture;
        /// <summary>The dot-path Lexicon key used to look up this string at runtime.</summary>
        public string Key;
        /// <summary>The localised string value for the given culture and key.</summary>
        public string Value;
    }

    [CreateAssetMenu(menuName = "Heathen/Ogham/Compiled Story", fileName = "OghamStory")]
    public class OghamCompiledData : ScriptableObject
    {
        /// <summary>All compiled dialogue entries in this story, ready for runtime use.</summary>
        [SerializeField] public List<DialogueEntry> Entries = new();

        /// <summary>
        /// The dot-path GameplayTag that identifies this story. Set by the importer when compiled from a
        /// .ogham source file. <see cref="StorytellerRegistry"/> uses this as the story identity when its
        /// own story tag path field is left blank.
        /// </summary>
        public string StoryTagPath = string.Empty;

        /// <summary>
        /// Inline localisations extracted from the .ogham source file. These are injected into
        /// <see cref="Heathen.Lexicon.LexiconRegistry"/> when the story is registered at runtime.
        /// </summary>
        public OghamCompiledLocale[] Localisations = System.Array.Empty<OghamCompiledLocale>();

        /// <summary>
        /// Asset GUIDs of the source <see cref="OghamData"/> authoring assets. Stored as strings rather than
        /// direct object references to prevent Unity from including authoring assets in player builds.
        /// Editor-only; not used at runtime.
        /// </summary>
        [SerializeField] private string[] _sourceGuids = System.Array.Empty<string>();

        private Dictionary<ulong, DialogueEntry>  _index;
        private Dictionary<ulong, HashSet<ulong>> _childIndex;

        private void OnEnable() => BuildIndex();

        /// <summary>
        /// Finds and returns the compiled dialogue entry matching the given tag, or <c>null</c> if not found.
        /// </summary>
        /// <param name="tag">The GameplayTag whose ID is used to look up the entry.</param>
        /// <returns>The matching <see cref="DialogueEntry"/>, or <c>null</c>.</returns>
        public DialogueEntry FindEntry(GameplayTag tag) => FindEntry(tag.Id);

        internal DialogueEntry FindEntry(ulong tagId)
        {
            if (_index == null) BuildIndex();
            return _index.TryGetValue(tagId, out var e) ? e : null;
        }

        /// <summary>
        /// Returns the tag IDs of all entries that are direct navigation targets of options on the given parent entry.
        /// </summary>
        /// <param name="parentId">The tag ID of the parent entry whose children are requested.</param>
        /// <returns>An enumerable of child entry tag IDs, or an empty sequence when none exist.</returns>
        public IEnumerable<ulong> GetChildren(ulong parentId)
        {
            if (_childIndex == null) BuildIndex();
            return _childIndex.TryGetValue(parentId, out var set) ? set : System.Array.Empty<ulong>();
        }

        /// <summary>
        /// Performs a breadth-first traversal of the graph from <paramref name="entryId"/> and adds all reachable
        /// descendant entry IDs to <paramref name="results"/>. Already-visited IDs are not added twice.
        /// </summary>
        /// <param name="entryId">The tag ID of the entry to start traversal from.</param>
        /// <param name="results">The set to populate with discovered descendant IDs.</param>
        public void CollectDescendants(ulong entryId, HashSet<ulong> results)
        {
            if (_childIndex == null) BuildIndex();
            var queue = new Queue<ulong>();
            queue.Enqueue(entryId);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!_childIndex.TryGetValue(current, out var children)) continue;
                foreach (var child in children)
                    if (results.Add(child)) queue.Enqueue(child);
            }
        }

        /// <summary>
        /// Rebuilds the internal tag-ID-to-entry and parent-to-child lookup dictionaries from <see cref="Entries"/>.
        /// Called automatically on <c>OnEnable</c> and <c>OnValidate</c>; call manually after modifying
        /// <see cref="Entries"/> at runtime.
        /// </summary>
        public void BuildIndex()
        {
            _index      = new Dictionary<ulong, DialogueEntry>(Entries.Count);
            _childIndex = new Dictionary<ulong, HashSet<ulong>>();
            foreach (var entry in Entries)
            {
                var id = entry.ResolvedTag.Id;
                if (id == 0) continue;
                _index.TryAdd(id, entry);
            }

            foreach (var entry in Entries)
            {
                var parentId = entry.ResolvedTag.Id;
                if (parentId == 0) continue;
                foreach (var opt in entry.Options)
                {
                    var childId = opt.ResolvedTargetEntry.Id;
                    if (childId == 0) continue;
                    if (!_childIndex.TryGetValue(parentId, out var set))
                    {
                        set = new HashSet<ulong>();
                        _childIndex[parentId] = set;
                    }
                    set.Add(childId);
                }
            }
        }

#if UNITY_EDITOR
        private void OnValidate() => BuildIndex();

        /// <summary>
        /// Returns all source <see cref="OghamData"/> authoring assets registered with this compiled asset,
        /// resolving them from their stored GUIDs via the AssetDatabase. Editor-only.
        /// </summary>
        /// <returns>A list of resolved <see cref="OghamData"/> assets; entries whose GUIDs no longer resolve are omitted.</returns>
        public List<OghamData> GetSourceFiles()
        {
            var result = new List<OghamData>(_sourceGuids.Length);
            foreach (var guid in _sourceGuids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                var data = UnityEditor.AssetDatabase.LoadAssetAtPath<OghamData>(path);
                if (data != null) result.Add(data);
            }
            return result;
        }

        /// <summary>
        /// Stores the given <see cref="OghamData"/> assets as source files by recording their AssetDatabase GUIDs.
        /// Marks this asset dirty so Unity serialises the change. Editor-only.
        /// </summary>
        /// <param name="files">The authoring assets whose GUIDs will be stored. Null entries are skipped.</param>
        public void SetSourceFiles(IEnumerable<OghamData> files)
        {
            var guids = new List<string>();
            foreach (var file in files)
            {
                if (file == null) continue;
                var path = UnityEditor.AssetDatabase.GetAssetPath(file);
                var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) guids.Add(guid);
            }
            _sourceGuids = guids.ToArray();
            UnityEditor.EditorUtility.SetDirty(this);
        }

        /// <summary>
        /// Merges all registered source files into <see cref="Entries"/> and rebuilds the
        /// runtime index. Text ContentKeys are converted to TMPro markup at this point;
        /// the runtime always receives clean TMPro markup, never the authoring [text](tag) syntax.
        /// </summary>
        public void Compile()
        {
            var sources = GetSourceFiles();
            if (sources.Count == 0)
            {
                Debug.LogWarning($"[Ogham] {name}: no source files registered — nothing to compile.");
                return;
            }

            Entries.Clear();
            foreach (var source in sources)
            {
                if (source == null) continue;
                // Ensure inline-link options are in sync before compiling.
                source.BuildIndex();
                foreach (var entry in source.Entries)
                    Entries.Add(CompileEntry(entry));
            }
            BuildIndex();
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[Ogham] Compiled {Entries.Count} entries from {sources.Count} source file(s) into '{name}'.");
        }

        /// <summary>
        /// Deep-copies a source <see cref="DialogueEntry"/> into a compiled form suitable for runtime use.
        /// Tag and option tags are stored as hashed <see cref="GameplayTags.GameplayTag"/> values with no string paths,
        /// Text ContentKeys are converted to TMPro markup, pure-link keys are dropped, and options are deep-copied
        /// so the compiled and authoring data share no instances.
        /// </summary>
        /// <param name="src">The authoring entry to compile.</param>
        /// <returns>A new <see cref="DialogueEntry"/> containing compiled runtime data.</returns>
        public static DialogueEntry CompileEntry(DialogueEntry src)
        {
            var dst = new DialogueEntry();
            // Write hash directly — compiled asset carries no string tag paths.
            dst.Tag  = GameplayTag.FromName(src.TagPath);
            dst.Mode = src.Mode;

            foreach (var key in src.ContentKeys)
            {
                if (key.Type == OghamContentType.Text)
                {
                    var raw = key.KeyOrValue ?? "";
                    if (OghamInlineLinkParser.IsPureLink(raw, out _, out _))
                        continue;

                    dst.ContentKeys.Add(new OghamContentKey {
                        Type       = key.Type,
                        Mode       = key.Mode,
                        KeyOrValue = OghamInlineLinkParser.ToTMProMarkup(raw),
                        AssetRef   = key.AssetRef,
                    });
                }
                else
                {
                    dst.ContentKeys.Add(new OghamContentKey {
                        Type       = key.Type,
                        Mode       = key.Mode,
                        KeyOrValue = key.KeyOrValue,
                        AssetRef   = key.AssetRef,
                    });
                }
            }

            dst.EntryOperations.AddRange(src.EntryOperations);

            foreach (var srcOpt in src.Options)
            {
                var dstOpt = new DialogueOption();
                // Write hashes directly — no string paths in compiled options.
                dstOpt.Tag         = GameplayTag.FromName(srcOpt.TagPath);
                dstOpt.TargetEntry = string.IsNullOrEmpty(srcOpt.TargetEntryPath)
                    ? default : GameplayTag.FromName(srcOpt.TargetEntryPath);
                dstOpt.TextKey                 = srcOpt.TextKey;
                dstOpt.SynthesizedFromInlineLink = srcOpt.SynthesizedFromInlineLink;
                dstOpt.Conditions.AddRange(srcOpt.Conditions);
                dstOpt.Operations.AddRange(srcOpt.Operations);
                dst.Options.Add(dstOpt);
            }

            return dst;
        }
#endif
    }
}
