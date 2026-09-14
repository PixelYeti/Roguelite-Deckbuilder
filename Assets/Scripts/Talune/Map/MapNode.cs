using System.Collections.Generic;

namespace Talune.Map
{
    public class MapNode
    {
        public int Id;
        public int RowIndex;
        public MapNodeType NodeType;
        public List<int> ConnectedNodeIds = new(); // Ids of nodes in RowIndex + 1 reachable from here.
        public bool Completed;

        /// <summary>Fix 6: every node's type is visible before the player commits to a
        /// path EXCEPT Mystery Event, which shows as "?" - its whole appeal is not
        /// knowing what's inside.</summary>
        public string DisplayLabel => NodeType == MapNodeType.MysteryEvent ? "?" : NodeType.ToString();
    }
}
