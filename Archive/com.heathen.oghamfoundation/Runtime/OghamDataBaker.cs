#if UNITY_ENTITIES
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// MonoBehaviour authoring component that references an <see cref="OghamData"/> asset for ECS baking.
    /// Add this component to a SubScene object and assign <see cref="Data"/> to produce an entity with an
    /// <see cref="OghamBlobComponent"/> at build time.
    /// </summary>
    public class OghamDataAuthoring : MonoBehaviour
    {
        /// <summary>The authoring data asset to bake into an ECS blob asset.</summary>
        public OghamData Data;
    }

    /// <summary>
    /// Baker that converts an <see cref="OghamDataAuthoring"/> component into an ECS entity carrying an
    /// <see cref="OghamBlobComponent"/>. The blob asset contains all dialogue entries sorted by tag ID
    /// for efficient O(log n) lookup from Burst jobs.
    /// </summary>
    public class OghamDataBaker : Baker<OghamDataAuthoring>
    {
        /// <summary>
        /// Bakes the referenced <see cref="OghamData"/> into a persistent <see cref="OghamBlob"/> asset
        /// and attaches it to the baked entity as an <see cref="OghamBlobComponent"/>.
        /// </summary>
        /// <param name="authoring">The authoring component whose <see cref="OghamDataAuthoring.Data"/> is baked.</param>
        public override void Bake(OghamDataAuthoring authoring)
        {
            if (authoring.Data == null) return;

            authoring.Data.BuildIndex();
            var entries = authoring.Data.Entries;

            // Build a sorted key list for binary search.
            var sorted = new List<(ulong tagId, int idx)>(entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var id = entries[i].Tag.Id;
                if (id != 0) sorted.Add((id, i));
            }
            sorted.Sort((a, b) => a.tagId.CompareTo(b.tagId));

            var builder = new BlobBuilder(Allocator.Temp);
            ref var blob = ref builder.ConstructRoot<OghamBlob>();

            var blobEntries = builder.Allocate(ref blob.Entries, sorted.Count);
            var blobKeys    = builder.Allocate(ref blob.SortedKeys, sorted.Count);

            for (int s = 0; s < sorted.Count; s++)
            {
                var (tagId, srcIdx) = sorted[s];
                var entry = entries[srcIdx];
                blobKeys[s] = tagId;

                ref var be = ref blobEntries[s];
                be.TagId = tagId;

                // Content key hashes
                var textHashes = builder.Allocate(ref be.TextKeyHashes, entry.ContentKeys.Count);
                for (int t = 0; t < entry.ContentKeys.Count; t++)
                    textHashes[t] = entry.ContentKeys[t].GetHash();

                // Option arrays
                var optIds     = builder.Allocate(ref be.OptionTagIds,    entry.Options.Count);
                var optTargets = builder.Allocate(ref be.OptionTargetIds, entry.Options.Count);
                for (int o = 0; o < entry.Options.Count; o++)
                {
                    optIds[o]     = entry.Options[o].Tag.Id;
                    optTargets[o] = entry.Options[o].TargetEntry.Id;
                }
            }

            var blobRef = builder.CreateBlobAssetReference<OghamBlob>(Allocator.Persistent);
            builder.Dispose();

            var entity = GetEntity(TransformUsageFlags.None);
            AddBlobAsset(ref blobRef, out _);
            AddComponent(entity, new OghamBlobComponent { Value = blobRef });
        }
    }
}
#endif
