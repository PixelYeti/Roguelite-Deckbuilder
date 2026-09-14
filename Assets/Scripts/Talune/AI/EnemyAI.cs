using System.Collections.Generic;
using Talune.Combat;

namespace Talune.AI
{
    /// <summary>
    /// Decides what an enemy does on its next turn, from its EnemyAIProfile's rule
    /// set. Called once per enemy per turn by EnemyCombatant.TelegraphNextMove, and
    /// the result is shown to the player as an intent icon BEFORE they act (Fix 9).
    ///
    /// Selection order:
    ///  1. A ForcedOpener rule always fires on the enemy's very first turn (TurnNumber == 0).
    ///  2. Otherwise, filter rules to ones eligible right now: self HP% within
    ///     [MinHPPercent, MaxHPPercent], self.TurnNumber >= MinTurn, and not already
    ///     at its MaxConsecutiveUses streak.
    ///  3. Weighted-random pick among the eligible rules.
    ///  4. If nothing is eligible (e.g. every rule is streak-capped this turn), retry
    ///     ignoring the consecutive-use cap so the enemy never stalls with no move.
    /// </summary>
    public static class EnemyAI
    {
        public static EnemyMove ChooseNextMove(EnemyCombatant self, CombatContext context) =>
            ChooseNextMove(self, context, out _);

        public static EnemyMove ChooseNextMove(EnemyCombatant self, CombatContext context, out int chosenRuleIndex)
        {
            var rules = self.AIProfile != null ? self.AIProfile.Rules : null;
            chosenRuleIndex = -1;
            if (rules == null || rules.Count == 0) return default;

            if (self.TurnNumber == 0)
            {
                int openerIndex = rules.FindIndex(r => r.ForcedOpener);
                if (openerIndex >= 0)
                {
                    chosenRuleIndex = openerIndex;
                    return rules[openerIndex].Move;
                }
            }

            var eligible = EligibleIndices(self, rules, respectConsecutiveCap: true);
            if (eligible.Count == 0) eligible = EligibleIndices(self, rules, respectConsecutiveCap: false);
            if (eligible.Count == 0) eligible.Add(0); // Last-resort fallback: never return nothing.

            chosenRuleIndex = WeightedPick(eligible, rules, context.Rng);
            return rules[chosenRuleIndex].Move;
        }

        private static List<int> EligibleIndices(EnemyCombatant self, List<EnemyMoveRule> rules, bool respectConsecutiveCap)
        {
            var result = new List<int>();
            float hpPercent = self.HPPercent;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (hpPercent < rule.MinHPPercent || hpPercent > rule.MaxHPPercent) continue;
                if (self.TurnNumber < rule.MinTurn) continue;
                if (respectConsecutiveCap && rule.MaxConsecutiveUses > 0
                    && i == self.LastChosenRuleIndex && self.ConsecutiveUsesOfLastRule >= rule.MaxConsecutiveUses)
                    continue;

                result.Add(i);
            }
            return result;
        }

        private static int WeightedPick(List<int> eligible, List<EnemyMoveRule> rules, System.Random rng)
        {
            float totalWeight = 0f;
            foreach (var i in eligible) totalWeight += System.Math.Max(0.0001f, rules[i].Weight);

            double roll = rng.NextDouble() * totalWeight;
            float cumulative = 0f;
            foreach (var i in eligible)
            {
                cumulative += System.Math.Max(0.0001f, rules[i].Weight);
                if (roll <= cumulative) return i;
            }
            return eligible[^1]; // Floating-point rounding safety net.
        }
    }
}
