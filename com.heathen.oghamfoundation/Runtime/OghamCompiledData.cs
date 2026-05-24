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
    [Serializable]
    public struct OghamCompiledLocale
    {
        public string Culture;
        public string Key;
        public string Value;
    }

    [CreateAssetMenu(menuName = "Heathen/Ogham/Compiled Story", fileName = "OghamStory")]
    public class OghamCompiledData : ScriptableObject
    {
        [SerializeField] public List<DialogueEntry> Entries = new();

        // Set by OghamImporter when compiled from a .ogham source file.
        // StorytellerRegistry uses this as the story identity when _storyTagPath is not set.
        public string StoryTagPath = string.Empty;

        // Inline localisations from the .ogham source — injected into LexiconRegistry on registration.
        public OghamCompiledLocale[] Localisations = System.Array.Empty<OghamCompiledLocale>();

        // GUIDs of source OghamData authoring assets — editor side only.
        // Stored as strings to avoid Unity pulling OghamData assets into player builds
        // through serialized UnityEngine.Object references.
        [SerializeField] private string[] _sourceGuids = System.Array.Empty<string>();

        private Dictionary<ulong, DialogueEntry>  _index;
        private Dictionary<ulong, HashSet<ulong>> _childIndex;

        private void OnEnable() => BuildIndex();

        public DialogueEntry FindEntry(GameplayTag tag) => FindEntry(tag.Id);

        internal DialogueEntry FindEntry(ulong tagId)
        {
            if (_index == null) BuildIndex();
            return _index.TryGetValue(tagId, out var e) ? e : null;
        }

        public IEnumerable<ulong> GetChildren(ulong parentId)
        {
            if (_childIndex == null) BuildIndex();
            return _childIndex.TryGetValue(parentId, out var set) ? set : System.Array.Empty<ulong>();
        }

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

        // Deep-copies an entry into a compiled form:
        //   - Tag and option tags stored as GameplayTag (ulong hash), no string paths.
        //   - Text ContentKeys converted to TMPro markup; pure-link keys dropped.
        //   - Options deep-copied so compiled and authoring data don't share instances.
        public static DialogueEntry CompileEntry(DialogueEntry src)
        {
            var dst = new DialogueEntry();
            // Write hash directly — compiled asset carries no string tag paths.
            dst.Tag = GameplayTag.FromName(src.TagPath);

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
