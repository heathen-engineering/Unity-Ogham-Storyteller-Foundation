using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    [Serializable]
    public class OghamTemplatePair
    {
        public LexiconText TextKey = new();
        public GameObject Prefab;
    }

    // Listens to OghamProcessor events and spawns / despawns Prefabs based on
    // Text-type OghamContentKey slots found in each DialogueEntry's ContentKeys list.
    //
    // Pair up each content key role (narrator, speaker, body, etc.) with a Prefab
    // in the Templates list. The spawner resolves text keys and manages instances
    // according to the chosen Mode and CloseMode.
    public class OghamTemplateSpawner : MonoBehaviour
    {
        [Tooltip("If null, searches for OghamProcessor on the same GameObject.")]
        public OghamProcessor Processor;

        public OghamInstantiationMode Mode      = OghamInstantiationMode.Diff;
        public OghamCloseMode         CloseMode = OghamCloseMode.Clear;
        public OghamLoadMode          LoadMode  = OghamLoadMode.PreWarm;

        // Maps TextKey resolved values to Prefabs.
        public List<OghamTemplatePair> Templates = new();

        // key string -> active instances spawned for that key this entry.
        private readonly Dictionary<string, List<GameObject>> _active = new();

        private void Awake()
        {
            Processor ??= GetComponent<OghamProcessor>();
        }

        private void OnEnable()
        {
            if (Processor != null)
            {
                Processor.OnDialogueEntered += HandleEntered;
                Processor.OnDialogueClosed  += HandleClosed;
            }
        }

        private void OnDisable()
        {
            if (Processor != null)
            {
                Processor.OnDialogueEntered -= HandleEntered;
                Processor.OnDialogueClosed  -= HandleClosed;
            }
        }

        private void HandleEntered(DialogueEntry entry, List<DialogueOption> options)
        {
            var newKeys = ResolveKeys(entry);

            switch (Mode)
            {
                case OghamInstantiationMode.Diff:
                    ApplyDiff(newKeys);
                    break;
                case OghamInstantiationMode.Replace:
                    DestroyAll();
                    SpawnAll(newKeys);
                    break;
                case OghamInstantiationMode.Append:
                    SpawnAll(newKeys);
                    break;
            }

            if (LoadMode == OghamLoadMode.PreWarm)
                PreWarm(options);
        }

        private void HandleClosed(bool interrupted)
        {
            if (CloseMode == OghamCloseMode.Clear)
                DestroyAll();
        }

        private List<string> ResolveKeys(DialogueEntry entry)
        {
            var keys = new List<string>(entry.ContentKeys.Count);
            foreach (var key in entry.ContentKeys)
            {
                var resolved = key.ResolveText();
                if (!string.IsNullOrEmpty(resolved))
                    keys.Add(resolved);
            }
            return keys;
        }

        private void ApplyDiff(List<string> newKeys)
        {
            // Remove instances whose key is no longer in the new entry.
            var toRemove = new List<string>();
            foreach (var kvp in _active)
                if (!newKeys.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);

            foreach (var key in toRemove)
            {
                foreach (var go in _active[key])
                    if (go != null) Destroy(go);
                _active.Remove(key);
            }

            // Spawn new entries not already active.
            foreach (var key in newKeys)
                if (!_active.ContainsKey(key))
                    SpawnForKey(key);
        }

        private void SpawnAll(List<string> keys)
        {
            foreach (var key in keys)
                SpawnForKey(key);
        }

        private void SpawnForKey(string key)
        {
            var prefab = FindPrefab(key);
            if (prefab == null) return;

            var instance = Instantiate(prefab, transform);
            if (!_active.TryGetValue(key, out var list))
            {
                list = new List<GameObject>();
                _active[key] = list;
            }
            list.Add(instance);
        }

        private void DestroyAll()
        {
            foreach (var list in _active.Values)
                foreach (var go in list)
                    if (go != null) Destroy(go);
            _active.Clear();
        }

        // One-node lookahead: pre-load prefabs referenced by option target entries
        // so they are resident before the player makes a selection.
        private void PreWarm(List<DialogueOption> options)
        {
            if (Processor == null) return;
            foreach (var opt in options)
            {
                if (opt.TargetEntry.Id == 0) continue;
                var target = Processor.FindEntry(opt.TargetEntry);
                if (target == null) continue;
                foreach (var contentKey in target.ContentKeys)
                {
                    var key = contentKey.ResolveText();
                    if (string.IsNullOrEmpty(key)) continue;
                    _ = FindPrefab(key);
                }
            }
        }

        private GameObject FindPrefab(string resolvedKey)
        {
            foreach (var pair in Templates)
                if (pair.Prefab != null && pair.TextKey.Resolve() == resolvedKey)
                    return pair.Prefab;
            return null;
        }
    }
}
