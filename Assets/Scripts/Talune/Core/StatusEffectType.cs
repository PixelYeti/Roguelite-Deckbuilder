namespace Talune.Core
{
    /// <summary>
    /// The 5 persistent sub-mechanics defined in Fix 3 + Fix 14 of the design
    /// addendum. Each has a distinct stacking/decay rule - see StatusEffectInstance.
    /// </summary>
    public enum StatusEffectType
    {
        Burn,      // Numeric, additive stack. Ticks at start of owner's turn, then -1.
        Stun,      // Binary, single-use. Skips owner's next action, does not stack.
        Growth,    // Numeric, additive, does not decay. +1 dmg per stack on caster's Attacks.
        Thorns,    // Numeric, additive, does not decay. Reflects dmg on melee hits taken.
        Illusion,  // Binary token. Absorbs next attack like Block, then consumed. Replaces, doesn't stack.
    }

    /// <summary>How a status effect resolves when reapplied or ticked.</summary>
    public enum StatusEffectBehavior
    {
        AdditiveStack,   // New applications add to the existing stack (Burn, Growth, Thorns).
        RefreshOnly,     // Reapplying has no additional effect (Stun).
        ReplaceToken,    // A new application replaces rather than stacks (Illusion).
    }
}
