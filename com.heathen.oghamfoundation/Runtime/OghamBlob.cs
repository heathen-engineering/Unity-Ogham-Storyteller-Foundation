#if UNITY_ENTITIES
using Unity.Collections;
using Unity.Entities;

namespace Heathen.Ogham
{
    // Burst-readable snapshot of a single DialogueEntry.
    // Conditions and operations live only in the managed layer; this blob
    // stores the structural data needed for Burst navigation.
    public struct OghamBlobEntry
    {
        public ulong TagId;
        // Lexicon key hashes for each ContentKey slot.
        public BlobArray<ulong> TextKeyHashes;
        // Option tag IDs in declaration order. Conditions/ops must be
        // resolved through the managed OghamData for Burst-accessible paths.
        public BlobArray<ulong> OptionTagIds;
        // Target entry IDs for each option (parallel to OptionTagIds).
        public BlobArray<ulong> OptionTargetIds;
    }

    // BlobAsset root. SortedKeys is a parallel sorted array of TagIds
    // for O(log n) binary search from Burst systems.
    public struct OghamBlob
    {
        public BlobArray<OghamBlobEntry> Entries;
        public BlobArray<ulong>          SortedKeys;
    }

    public struct OghamBlobComponent : IComponentData
    {
        public BlobAssetReference<OghamBlob> Value;
    }
}
#endif
