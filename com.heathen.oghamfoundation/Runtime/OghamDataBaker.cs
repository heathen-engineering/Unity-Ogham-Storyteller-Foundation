#if UNITY_ENTITIES
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    public class OghamDataAuthoring : MonoBehaviour
    {
        public OghamData Data;
    }

    public class OghamDataBaker : Baker<OghamDataAuthoring>
    {
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
