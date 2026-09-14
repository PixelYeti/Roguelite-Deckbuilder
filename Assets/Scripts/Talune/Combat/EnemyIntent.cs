namespace Talune.Combat
{
    /// <summary>Fix 9: the category an enemy's next action falls into, shown to the player
    /// BEFORE they commit cards, so Guard/counter/retaliation decisions are informed, not guesses.</summary>
    public enum IntentCategory
    {
        Attack,
        Block,
        Buff,
        Debuff,
        Special,
        Summon,  // Places a Ward onto the battlefield instead of acting directly - see WardCombatant.
        Disrupt, // "Counter-play" - destroys one of the player's Wards instead of acting directly.
    }

    /// <summary>A telegraphed enemy action: category + the number the player should see
    /// (e.g. the damage an Attack intent will deal), so it reads clearly on screen.</summary>
    public readonly struct EnemyIntent
    {
        public readonly IntentCategory Category;
        public readonly int Value; // Damage for Attack, block amount for Block, stacks for Buff/Debuff, etc.
        public readonly string Description; // e.g. "Special: Burns" for flavor-specific special moves.

        public EnemyIntent(IntentCategory category, int value, string description = null)
        {
            Category = category;
            Value = value;
            Description = description;
        }
    }
}
