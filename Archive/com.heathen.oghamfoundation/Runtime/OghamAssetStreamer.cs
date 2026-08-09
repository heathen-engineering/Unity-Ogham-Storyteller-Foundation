using System.Collections.Generic;
using Heathen.Lexicon;

namespace Heathen.Ogham
{
    /// <summary>
    /// Streams a story's GUID-addressed assets (images, audio, VFX, prefabs, etc.) so memory tracks the size of
    /// the active window rather than the whole story. A typical story is 100-1000 nodes with several heavy assets
    /// each, so bulk-loading is not viable. Instead, as the conversation moves the streamer keeps a window of the
    /// current node plus its look-ahead reachable nodes acquired, and releases nodes that fall out of the window.
    /// <para>
    /// Acquisition is reference-counted in <see cref="LexiconAssetLoader"/>, so an asset shared by several
    /// in-window nodes stays resident until the last of them leaves. A presenter (e.g. the Story Reader) calls
    /// <see cref="SetCurrent"/> on each node entry, gates display on <see cref="AreAssetsResident"/>, and calls
    /// <see cref="ReleaseAll"/> when the story closes or the presenter is destroyed.
    /// </para>
    /// </summary>
    public sealed class OghamAssetStreamer
    {
        private readonly OghamStory _story;
        private readonly int        _lookAhead;
        private readonly HashSet<ulong> _resident = new();   // node ids whose assets are currently acquired
        private readonly Dictionary<ulong, List<(string guid, string subAssetName)>> _nodeAssets = new();

        /// <summary>
        /// Creates a streamer for a story definition.
        /// </summary>
        /// <param name="story">The definition whose graph drives the streaming window.</param>
        /// <param name="lookAhead">How many option hops ahead of the current node to keep resident. 1 keeps the
        /// next choices instant; 0 streams only the current node. Negative values are treated as 0.</param>
        public OghamAssetStreamer(OghamStory story, int lookAhead = 1)
        {
            _story     = story;
            _lookAhead = lookAhead < 0 ? 0 : lookAhead;
        }

        /// <summary>
        /// Re-centres the streaming window on <paramref name="nodeId"/>: acquires the assets of nodes newly inside
        /// the window (the node plus its look-ahead reachable nodes) and releases the assets of nodes that left it.
        /// Call on each node entry, before displaying the node.
        /// </summary>
        /// <param name="nodeId">The entry tag id now being presented.</param>
        public void SetCurrent(ulong nodeId)
        {
            if (_story == null) return;

            var window = new HashSet<ulong>();
            _story.CollectWithinDepth(nodeId, _lookAhead, window);

            // Acquire nodes new to the window BEFORE releasing those that left, so an asset shared between an
            // outgoing and an incoming node never drops to a zero ref-count (which would unload then reload it).
            foreach (var n in window)
                if (_resident.Add(n)) AcquireNode(n);

            // Release nodes that left the window (collect first to avoid mutating the set during iteration).
            List<ulong> dropped = null;
            foreach (var n in _resident)
                if (!window.Contains(n)) (dropped ??= new List<ulong>()).Add(n);
            if (dropped != null)
                foreach (var n in dropped) { ReleaseNode(n); _resident.Remove(n); }
        }

        /// <summary>
        /// Returns <c>true</c> when every asset referenced by <paramref name="nodeId"/> is resolvable now, so the
        /// node can be displayed without a blank slot. In the editor this is immediate; in a player it becomes
        /// true once the node's acquired assets finish loading. Nodes with no assets are always resident.
        /// </summary>
        /// <param name="nodeId">The entry tag id to test.</param>
        public bool AreAssetsResident(ulong nodeId)
        {
            foreach (var (guid, sub) in AssetsOf(nodeId))
                if (LexiconRegistry.ResolveAssetByGuid(guid, sub) == null) return false;
            return true;
        }

        /// <summary>Releases every asset this streamer is holding. Call when the story closes or the presenter is destroyed.</summary>
        public void ReleaseAll()
        {
            foreach (var n in _resident) ReleaseNode(n);
            _resident.Clear();
        }

        private void AcquireNode(ulong nodeId)
        {
            foreach (var (guid, sub) in AssetsOf(nodeId))
                _ = LexiconRegistry.AcquireAssetByGuidAsync(guid, sub);
        }

        private void ReleaseNode(ulong nodeId)
        {
            foreach (var (guid, sub) in AssetsOf(nodeId))
                LexiconRegistry.ReleaseAssetByGuid(guid, sub);
        }

        private List<(string guid, string subAssetName)> AssetsOf(ulong nodeId)
        {
            if (!_nodeAssets.TryGetValue(nodeId, out var list))
            {
                list = new List<(string, string)>();
                _story.CollectNodeAssets(nodeId, list);
                _nodeAssets[nodeId] = list;
            }
            return list;
        }
    }
}
