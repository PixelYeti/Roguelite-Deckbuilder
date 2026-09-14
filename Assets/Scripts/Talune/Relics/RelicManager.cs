using System.Collections.Generic;
using System.Linq;

namespace Talune.Relics
{
    /// <summary>
    /// Holds the player's active relics for a run and answers the small set of
    /// "does a rule-bending relic apply here?" questions CombatManager asks at each
    /// relevant hook point. Kept as plain queries rather than C# events so each call
    /// site in CombatManager stays easy to read top-to-bottom.
    /// </summary>
    public class RelicManager
    {
        private readonly List<RelicData> _relics;

        public RelicManager(IEnumerable<RelicData> relics) => _relics = relics?.ToList() ?? new List<RelicData>();

        public IReadOnlyList<RelicData> Relics => _relics;

        private bool Has(RelicEffectKind kind) => _relics.Any(r => r.Kind == kind);
        private int ValueOf(RelicEffectKind kind) => _relics.FirstOrDefault(r => r.Kind == kind)?.Value ?? 0;

        public int StartCombatBlockBonus() => Has(RelicEffectKind.StartCombatBlock) ? ValueOf(RelicEffectKind.StartCombatBlock) : 0;

        public int ModifyCardCost(int printedCost, bool isFirstCardThisTurn) =>
            (isFirstCardThisTurn && Has(RelicEffectKind.FreeFirstCardEachTurn)) ? 0 : printedCost;

        public bool ShouldDoubleAttackEffects(bool isFirstAttackThisTurn) =>
            isFirstAttackThisTurn && Has(RelicEffectKind.DoubleFirstAttackEachTurn);

        public bool BlockPersistsAcrossTurns() => Has(RelicEffectKind.BlockPersistsAcrossTurns);

        public int ThornsFromBlockGain() => Has(RelicEffectKind.BlockGrantsThorns) ? ValueOf(RelicEffectKind.BlockGrantsThorns) : 0;

        public int ExtraBurnStacks() => Has(RelicEffectKind.ExtraBurnStack) ? ValueOf(RelicEffectKind.ExtraBurnStack) : 0;

        public int GrowthFromMossmawCard() => Has(RelicEffectKind.MossmawCardGrantsGrowth) ? ValueOf(RelicEffectKind.MossmawCardGrantsGrowth) : 0;

        public int EnergyFromEnemyDeath() => Has(RelicEffectKind.EnemyDeathGrantsEnergy) ? ValueOf(RelicEffectKind.EnemyDeathGrantsEnergy) : 0;
    }
}
