using PBLEngine;
using System.Collections.Generic;

namespace PBLApp.Core;

/// <summary>
/// Pure shortest-path search over the passive tree graph — the same BFS the
/// engine's <c>spec:AllocNode</c> walks. Main-tree ↔ ascendancy boundary is
/// never crossed: a link whose endpoints disagree on having an
/// <c>AscendancyName</c> is not traversed, so ascendancy islands are only
/// reachable from within (their allocated start node).
/// </summary>
public static class TreePathFinder
{
    /// <summary>Shortest path (node ids) from the allocated tree to
    /// <paramref name="targetId"/> over node links. Includes the target,
    /// excludes already-allocated nodes. Empty when unreachable, when the
    /// target is already allocated or absent from <paramref name="nodesById"/>.
    /// </summary>
    public static List<int> FindPath(
        IReadOnlyDictionary<int, TreeNodeDto> nodesById,
        IReadOnlyCollection<int> allocatedIds,
        int targetId)
    {
        if (!nodesById.TryGetValue(targetId, out var tNode)) return [];
        var alloc = allocatedIds;
        if (alloc.Count == 0 || alloc.Contains(targetId)) return [];

        var visited = new HashSet<int>(alloc);
        var parent  = new Dictionary<int, int>();
        var queue   = new Queue<int>(alloc);
        bool found  = false;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            if (cur == targetId) { found = true; break; }
            if (!nodesById.TryGetValue(cur, out var curNode)) continue;
            bool curAsc = !string.IsNullOrEmpty(curNode.AscendancyName);
            foreach (var lid in curNode.LinkedIds)
            {
                if (!visited.Add(lid)) continue;
                if (!nodesById.TryGetValue(lid, out var lnode)) continue;
                if (curAsc != !string.IsNullOrEmpty(lnode.AscendancyName)) continue;
                parent[lid] = cur;
                queue.Enqueue(lid);
            }
        }
        if (!found) return [];

        // Back-track target → frontier, then flip so the path reads tree-outward
        // (intermediates first, target last).
        var path = new List<int> { targetId };
        int n = targetId;
        while (parent.TryGetValue(n, out var p))
        {
            if (alloc.Contains(p)) break;
            path.Add(p);
            n = p;
        }
        path.Reverse();
        return path;
    }
}
