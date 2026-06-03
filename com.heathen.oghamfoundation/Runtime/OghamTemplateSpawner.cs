using System;
using System.Collections.Generic;
using UnityEngine;
using Heathen.GameplayTags;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// Associates a Lexicon text key with a prefab so <see cref="OghamTemplateSpawner"/> can map resolved
    /// content key values to specific prefab instances during a conversation.
    /// </summary>
    [Serializable]
    public class OghamTemplatePair
    {
        /// <summary>The Lexicon text key whose resolved value is matched against each node's content keys.</summary>
        public LexiconText TextKey = new();
        /// <summary>The prefab to spawn when <see cref="TextKey"/> matches a content key value in the active node.</summary>
        public GameObject Prefab;
    }

    /// <summary>
    /// Listens to <see cref="Storyteller"/> events and spawns or despawns prefab instances based on the
    /// Text-type content key values in each dialogue node. Pair each content key role (narrator, speaker,
    /// body text, etc.) with a prefab in <see cref="Templates"/>. Instantiation and cleanup behaviour is
    /// controlled by <see cref="Mode"/> and <see cref="CloseMode"/>.
    /// </summary>
    public class OghamTemplateSpawner : MonoBehaviour
    {
        [Tooltip("Track the current main story, or a specific named story.")]
        [SerializeField] private StoryTarget _target = StoryTarget.Main;

        [Tooltip("Dot-path tag identifying the story to track when Target is Specific.")]
        [SerializeField] private string _storyTagPath;

        /// <summary>Controls how existing prefab instances are managed when a new dialogue node is entered.</summary>
        public OghamInstantiationMode Mode      = OghamInstantiationMode.Diff;
        /// <summary>Controls whether spawned instances are destroyed or retained when the conversation closes.</summary>
        public OghamCloseMode         CloseMode = OghamCloseMode.Clear;
        /// <summary>Controls when prefab assets for upcoming nodes are pre-fetched.</summary>
        public OghamLoadMode          LoadMode  = OghamLoadMode.PreWarm;

        /// <summary>The list of Lexicon-key-to-prefab pairings this spawner manages.</summary>
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
