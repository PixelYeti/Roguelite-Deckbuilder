using Talune.Combat;
using Talune.Core;

namespace Talune.AI
{
    /// <summary>One possible action in an enemy's move pool/pattern.</summary>
    [System.Serializable]
    public struct EnemyMove
    {
        public IntentCategory Category;
        public int Value;           // Damage for Attack, block for Block, stacks for Buff/Debuff/Special.
        public string Description;  // Optional flavor, e.g. "Ember Spit" for a Special move.

        // Only used when Category is Buff, Debuff, or Special.
        public StatusEffectType Status;
        public bool StatusTargetsSelf; // true = Buff (applies to the enemy), false = Debuff/Special on the player.

        public EnemyIntent ToIntent() => new(Category, Value, Description);
    }
}
