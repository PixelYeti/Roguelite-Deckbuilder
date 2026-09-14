namespace Talune.Relics
{
    /// <summary>
    /// Each kind bends a rule rather than adding a flat bonus, per the master
    /// prompt's RELIC SYSTEM philosophy ("First card played each turn costs 0" is a
    /// good relic; "+5% damage" is not). Value's meaning depends on Kind - see each
    /// case in RelicManager for what it's used for.
    /// </summary>
    public enum RelicEffectKind
    {
        FreeFirstCardEachTurn,     // The first card played each turn costs 0, regardless of its printed cost.
        BlockGrantsThorns,         // Whenever the player gains Block, also gain Value Thorns.
        DoubleFirstAttackEachTurn, // The first Attack card played each turn resolves its effects twice.
        BlockPersistsAcrossTurns,  // Overrides Fix 8's decay for the player only: Block no longer resets each turn.
        ExtraBurnStack,            // Whenever the player applies Burn, apply Value extra stacks.
        StartCombatBlock,          // At the start of combat, the player gains Value Block.
        MossmawCardGrantsGrowth,   // Whenever the player plays a card tagged Kin: Mossmaw, gain Value Growth.
        EnemyDeathGrantsEnergy,    // Whenever an enemy dies, the player immediately gains Value Energy.
    }
}
