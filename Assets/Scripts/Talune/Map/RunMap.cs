using System.Collections.Generic;
using System.Linq;

namespace Talune.Map
{
    /// <summary>
    /// One Act's node graph (Fix's "Open Items Resolved": a run = 3 Acts, each Act =
    /// one biome, ~12-15 nodes, ending in a Boss). Tracks the player's current
    /// position and exposes only the legitimately reachable next nodes, so a route
    /// choice is always between real, connected options - never an arbitrary jump.
    /// </summary>
    public class RunMap
    {
        public List<List<MapNode>> Rows { get; }
        public int CurrentNodeId { get; private set; } = -1; // -1 = not yet entered the map.

        public RunMap(List<List<MapNode>> rows) => Rows = rows;

        public MapNode GetNode(int id) => Rows.SelectMany(r => r).First(n => n.Id == id);

        /// <summary>Nodes the player may move to right now: row 0 if the run hasn't
        /// started, otherwise whatever the current node connects to.</summary>
        public IEnumerable<MapNode> AvailableNextNodes()
        {
            if (CurrentNodeId == -1) return Rows[0];
            var current = GetNode(CurrentNodeId);
            return current.ConnectedNodeIds.Select(GetNode);
        }

        public bool TryMoveTo(int nodeId)
        {
            if (!AvailableNextNodes().Any(n => n.Id == nodeId)) return false;
            CurrentNodeId = nodeId;
            return true;
        }

        /// <summary>Call once the current node's content (combat, shop, event...) has
        /// actually been resolved - NOT automatically on moving away, since the final
        /// Boss node has nowhere left to move to and would never complete otherwise.</summary>
        public void MarkCurrentNodeCompleted()
        {
            if (CurrentNodeId != -1) GetNode(CurrentNodeId).Completed = true;
        }

        public bool IsAtBoss => CurrentNodeId != -1 && GetNode(CurrentNodeId).NodeType == MapNodeType.Boss;
        public bool ActComplete => IsAtBoss && GetNode(CurrentNodeId).Completed;
    }
}
