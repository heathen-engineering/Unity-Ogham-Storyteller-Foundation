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

    // Listens to Storyteller events and spawns / despawns Prefabs based on
    // Text-type content key slots in each node's ContentKeys list.
    //
    // Pair up each content key role (narrator, speaker, body, etc.) with a Prefab
    // in the Templates list. The spawner resolves text keys and manages instances
    // according to the chosen Mode and CloseMode.
    public class OghamTemplateSpawner : MonoBehaviour
    {
        [Tooltip("Track the current main story, or a specific named story.")]
        [SerializeField] private StoryTarget _target = StoryTarget.Main;

        [Tooltip("Dot-path tag identifying the story to track when Target is Specific.")]
        [SerializeField] private string _storyTagPath;

        public OghamInstantiationMode Mode      = OghamInstantiationMode.Diff;
        public OghamCloseMode         CloseMode = OghamCloseMode.Clear;
        public OghamLoadMode          LoadMode  = OghamLoadMode.PreWarm;

        public List<OghamTemplatePair> Templates = new();

        private readonly Dictionary<string, List<GameObject>> _active = new();

        private void OnEnable()
        {
            Storyteller.OnEntered += HandleEntered;
            Storyteller.OnClosed  += HandleClosed;
        }

        private void OnDisable()
        {
            Storyteller.OnEntered -= HandleEntered;
            Storyteller.OnClosed  -= HandleClosed;
        }

        private bool IsTarget(GameplayTag storyId)
        {
            if (_target == StoryTarget.Main)
                return storyId.Id == Storyteller.MainStoryId.Id;
            if (string.IsNullOrEmpty(_storyTagPath)) return false;
            return storyId.Id == GameplayTag.FromName(_storyTagPath).Id;
        }

        private void HandleEntered(GameplayTag storyId, StoryNode node)
        {
            if (!IsTarget(storyId)) return;

            var newKeys = ResolveKeys(node);

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
                PreWarm(storyId, node.Options);
        }

        private void HandleClosed(GameplayTag storyId)
        {
            if (!IsTarget(storyId)) return;
            if (CloseMode == OghamCloseMode.Clear)
                DestroyAll();
        }

        private List<string> ResolveKeys(StoryNode node)
        {
            var keys = new List<string>(node.ContentCount);
            for (int i = 0; i < node.ContentCount; i++)
            {
                var resolved = node.GetText(i);
                if (!string.IsNullOrEmpty(resolved))
                    keys.Add(resolved);
            }
            return keys;
        }

        private void ApplyDiff(List<string> newKeys)
        {
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
        private void PreWarm(GameplayTag storyId, IReadOnlyList<StoryOption> options)
        {
            var story = Storyteller.GetStory(storyId);
            if (story == null) return;

            foreach (var opt in options)
            {
                if (!opt.HasTarget) continue;
                var target = story.FindEntry(opt.TargetTag);
                if (target == null) continue;
                for (int i = 0; i < target.ContentKeys.Count; i++)
                {
                    var key = target.ContentKeys[i].ResolveText();
                    if (!string.IsNullOrEmpty(key))
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
