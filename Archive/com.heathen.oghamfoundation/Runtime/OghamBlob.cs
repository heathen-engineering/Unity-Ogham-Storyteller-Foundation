#if UNITY_ENTITIES
using Unity.Collections;
using Unity.Entities;

namespace Heathen.Ogham
{
    /// <summary>
    /// Burst-readable snapshot of a single <see cref="DialogueEntry"/>, stored inside an <see cref="OghamBlob"/>.
    /// Conditions and operations live only in the managed layer; this struct stores the structural data
    /// needed for Burst navigation such as tag IDs and option target IDs.
    /// </summary>
    public struct OghamBlobEntry
    {
        /// <summary>The unique tag ID that identifies this dialogue entry.</summary>
        public ulong TagId;
        /// <summary>Lexicon key hashes for each ContentKey slot, in authoring order.</summary>
        public BlobArray<ulong> TextKeyHashes;
        /// <summary>
        /// Option tag IDs in declaration order. Conditions and operations must be resolved through
        /// the managed <see cref="OghamData"/> for Burst-accessible paths.
        /// </summary>
        public BlobArray<ulong> OptionTagIds;
        /// <summary>Target entry IDs for each option, parallel to <see cref="OptionTagIds"/>.</summary>
        public BlobArray<ulong> OptionTargetIds;
    }

    /// <summary>
    /// BlobAsset root for Burst-accessible Ogham story data. Contains all dialogue entries and a parallel
    /// sorted key array that enables O(log n) binary search from Burst systems.
    /// </summary>
    public struct OghamBlob
    {
        /// <summary>All baked dialogue entries in the story.</summary>
        public BlobArray<OghamBlobEntry> Entries;
        /// <summary>Sorted array of <see cref="OghamBlobEntry.TagId"/> values for binary-search lookups.</summary>
        public BlobArray<ulong>          SortedKeys;
    }

    /// <summary>
    /// ECS component that holds a reference to the baked <see cref="OghamBlob"/> for a story entity.
    /// Attach this to an entity produced by <see cref="OghamDataBaker"/> to make story data accessible to Burst jobs.
    /// </summary>
    public struct OghamBlobComponent : IComponentData
    {
        /// <summary>Persistent reference to the baked blob asset for this story.</summary>
        public BlobAssetReference<OghamBlob> Value;
    }
}
#endif
