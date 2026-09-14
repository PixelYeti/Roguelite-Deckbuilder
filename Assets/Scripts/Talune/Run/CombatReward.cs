using System.Collections.Generic;
using System.Linq;
using Talune.Cards;
using Talune.Relics;

namespace Talune.Run
{
    /// <summary>The node types that grant a reward on completion (Fix 11's mapping table).</summary>
    public enum RewardNodeType { Combat, Elite, Treasure, Boss, KinShrine }

    /// <summary>One reward screen's contents. CardChoices deliberately always includes
    /// the option to take nothing (Fix 12) - callers should present a Skip button
    /// alongside CardChoices, not force a pick.</summary>
    public class CombatReward
    {
        public int FragmentsAwarded;
        public List<CardData> CardChoices = new();
        public RelicData RelicAwarded; // Null if none.

        /// <summary>Fix 11's reward mapping by node type, with Fix 12's Skip option implicit
        /// (the caller just doesn't call RunState.AddCardToDeck if the player skips).
        /// Kin Shrine (Fix 13) is handled separately by GenerateKinShrineReward below,
        /// since it needs a player-chosen Kin filter rather than the general pool.</summary>
        public static CombatReward Generate(RewardNodeType nodeType, List<CardData> cardPool, List<RelicData> relicPool, System.Random rng)
        {
            var reward = new CombatReward();
            switch (nodeType)
            {
                case RewardNodeType.Combat:
                    reward.FragmentsAwarded = RollRange(10, 20, rng);
                    reward.CardChoices = PickRandom(cardPool, 3, rng);
                    break;
                case RewardNodeType.Elite:
                    reward.FragmentsAwarded = RollRange(30, 50, rng);
                    reward.CardChoices = PickRandom(cardPool, 3, rng);
                    reward.RelicAwarded = PickRandom(relicPool, 1, rng).FirstOrDefault();
                    break;
                case RewardNodeType.Treasure:
                    // Player's choice of Relic or a larger Fragments haul (Fix 11) - caller
                    // decides which by only taking one of these two fields, not both.
                    reward.FragmentsAwarded = RollRange(60, 100, rng);
                    reward.RelicAwarded = PickRandom(relicPool, 1, rng).FirstOrDefault();
                    break;
                case RewardNodeType.Boss:
                    reward.FragmentsAwarded = RollRange(100, 150, rng); // "Large haul" - above Treasure's ceiling.
                    reward.RelicAwarded = PickRandom(relicPool, 1, rng).FirstOrDefault();
                    break;
            }
            return reward;
        }

        /// <summary>Fix 13: Kin Shrine offers 3 cards of ONE player-chosen Kin (or Skip),
        /// distinct from the random-Kin pool used everywhere else.</summary>
        public static CombatReward GenerateKinShrineReward(Core.KinType chosenKin, List<CardData> cardPool, System.Random rng)
        {
            var ofKin = cardPool.Where(c => c.KinTags.Contains(chosenKin)).ToList();
            return new CombatReward { CardChoices = PickRandom(ofKin, 3, rng) };
        }

        private static int RollRange(int min, int max, System.Random rng) => rng.Next(min, max + 1);

        private static List<CardData> PickRandom(List<CardData> pool, int count, System.Random rng) =>
            pool.OrderBy(_ => rng.Next()).Take(count).ToList();

        private static List<RelicData> PickRandom(List<RelicData> pool, int count, System.Random rng) =>
            pool.OrderBy(_ => rng.Next()).Take(count).ToList();
    }
}
