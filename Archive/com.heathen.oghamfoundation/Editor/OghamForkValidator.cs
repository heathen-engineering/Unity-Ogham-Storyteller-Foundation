using System.Collections.Generic;
using Heathen.GameplayTags;
using Heathen.Ogham;

namespace Heathen.Ogham.Editor
{
    /// <summary>
    /// Validates that Fork routing terminates. Every path out of a Fork must resolve to a Content node
    /// or to no target (ending the conversation); a chain of Forks that loops back on itself never
    /// resolves and would spin forever at runtime. This validator finds the Forks that lie on such a
    /// cycle so the editor can flag them and the compiler can report them.
    /// </summary>
    public static class OghamForkValidator
    {
        /// <summary>
        /// Returns the tag IDs of every Fork entry that lies on a fork-to-fork cycle. An empty set means
        /// all Fork routing is well-formed.
        /// </summary>
        /// <param name="entries">The full set of dialogue entries forming the story graph.</param>
        /// <returns>The set of offending Fork tag IDs; empty when the graph is valid.</returns>
        public static HashSet<ulong> FindCyclicForks(IEnumerable<DialogueEntry> entries)
        {
            var result = new HashSet<ulong>();
            if (entries == null) return result;

            // Index Fork entries by tag id.
            var forks = new Dictionary<ulong, DialogueEntry>();
            foreach (var e in entries)
            {
                if (e == null || e.Mode != OghamNodeMode.Fork) continue;
                var id = e.ResolvedTag.Id;
                if (id != 0) forks[id] = e;
            }
            if (forks.Count == 0) return result;

            // Build the fork-only adjacency: a Fork's route targets that are themselves Forks.
            // Routes to a Content node or to no target are terminal and ignored — they are always valid.
            var adjacency = new Dictionary<ulong, List<ulong>>(forks.Count);
            foreach (var pair in forks)
            {
                var next = new List<ulong>();
                foreach (var opt in pair.Value.Options)
                {
                    var target = opt.ResolvedTargetEntry.Id;
                    if (target != 0 && forks.ContainsKey(target))
                        next.Add(target);
                }
                adjacency[pair.Key] = next;
            }

            // A Fork is unsafe when a path of Forks leaving it can return to it (it sits on a cycle).
            foreach (var start in forks.Keys)
                if (ReachesSelf(start, start, adjacency, new HashSet<ulong>()))
                    result.Add(start);

            return result;
        }

        private static bool ReachesSelf(ulong from, ulong target,
            Dictionary<ulong, List<ulong>> adjacency, HashSet<ulong> visited)
        {
            if (!adjacency.TryGetValue(from, out var next)) return false;
            foreach (var n in next)
            {
                if (n == target) return true;
                if (visited.Add(n) && ReachesSelf(n, target, adjacency, visited))
                    return true;
            }
            return false;
        }
    }
}
