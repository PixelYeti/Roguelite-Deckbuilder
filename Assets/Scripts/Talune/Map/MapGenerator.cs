using System;
using System.Collections.Generic;
using System.Linq;

namespace Talune.Map
{
    /// <summary>
    /// Builds one Act's RunMap. Weights below are prototype placeholders (tune with
    /// real playtesting), but the structural rules are deliberate: row 0 is always
    /// Combat (never open on a Shop/Treasure freebie), Elite is excluded from row 1
    /// (no early ambush), the final row is always a single Boss node, and every
    /// non-final row converges so no node is ever unreachable.
    /// </summary>
    public static class MapGenerator
    {
        private static readonly (MapNodeType type, int weight)[] RegularWeights =
        {
            (MapNodeType.Combat, 40),
            (MapNodeType.Elite, 10),
            (MapNodeType.KinShrine, 8),
            (MapNodeType.KipShop, 10),
            (MapNodeType.MysteryEvent, 12),
            (MapNodeType.Treasure, 8),
            (MapNodeType.Healing, 7),
            (MapNodeType.FractureEvent, 3),
            (MapNodeType.BrambleEvent, 2),
        };

        public static RunMap Generate(int rowCount = 13, int nodesPerRow = 3, System.Random rng = null)
        {
            rng ??= new System.Random();
            var rows = new List<List<MapNode>>();
            int nextId = 0;

            for (int r = 0; r < rowCount; r++)
            {
                var row = new List<MapNode>();
                bool isFirstRow = r == 0;
                bool isBossRow = r == rowCount - 1;
                int count = isBossRow ? 1 : nodesPerRow;

                for (int i = 0; i < count; i++)
                {
                    MapNodeType type = isBossRow ? MapNodeType.Boss
                        : isFirstRow ? MapNodeType.Combat
                        : WeightedRandomType(rng, excludeElite: r == 1);

                    row.Add(new MapNode { Id = nextId++, RowIndex = r, NodeType = type });
                }
                rows.Add(row);
            }

            ConnectRows(rows, rng);
            return new RunMap(rows);
        }

        private static MapNodeType WeightedRandomType(System.Random rng, bool excludeElite)
        {
            var pool = excludeElite ? RegularWeights.Where(w => w.type != MapNodeType.Elite).ToArray() : RegularWeights;
            int totalWeight = pool.Sum(w => w.weight);
            int roll = rng.Next(totalWeight);
            int cumulative = 0;
            foreach (var (type, weight) in pool)
            {
                cumulative += weight;
                if (roll < cumulative) return type;
            }
            return pool[^1].type;
        }

        private static void ConnectRows(List<List<MapNode>> rows, System.Random rng)
        {
            for (int r = 0; r < rows.Count - 1; r++)
            {
                var current = rows[r];
                var next = rows[r + 1];

                // Every current-row node reaches 1-2 nodes in the next row (all of them, if next row is the single-node Boss row).
                foreach (var node in current)
                {
                    int connections = next.Count == 1 ? 1 : rng.Next(1, 3);
                    var targets = next.OrderBy(_ => rng.Next()).Take(connections);
                    node.ConnectedNodeIds.AddRange(targets.Select(t => t.Id));
                }

                // Guarantee every next-row node has at least one incoming edge - no node is ever unreachable.
                foreach (var orphan in next.Where(n => current.All(c => !c.ConnectedNodeIds.Contains(n.Id))))
                {
                    var connector = current[rng.Next(current.Count)];
                    connector.ConnectedNodeIds.Add(orphan.Id);
                }
            }
        }
    }
}
