using Talune.Core;

namespace Talune.Combat
{
    /// <summary>
    /// One status effect living on a CombatEntity. Stacking/decay behavior
    /// follows Fix 3 / Fix 14 in Docs/design-addendum-v1.md.
    /// </summary>
    public class StatusEffectInstance
    {
        public StatusEffectType Type { get; }
        public StatusEffectBehavior Behavior { get; }
        public int Stacks { get; private set; }

        public StatusEffectInstance(StatusEffectType type, int initialStacks)
        {
            Type = type;
            Stacks = initialStacks;
            Behavior = BehaviorFor(type);
        }

        public static StatusEffectBehavior BehaviorFor(StatusEffectType type) => type switch
        {
            StatusEffectType.Burn => StatusEffectBehavior.AdditiveStack,
            StatusEffectType.Growth => StatusEffectBehavior.AdditiveStack,
            StatusEffectType.Thorns => StatusEffectBehavior.AdditiveStack,
            StatusEffectType.Stun => StatusEffectBehavior.RefreshOnly,
            StatusEffectType.Illusion => StatusEffectBehavior.ReplaceToken,
            _ => StatusEffectBehavior.AdditiveStack,
        };

        /// <summary>Apply a reapplication of this effect per its stacking rule.</summary>
        public void Reapply(int amount)
        {
            switch (Behavior)
            {
                case StatusEffectBehavior.AdditiveStack:
                    Stacks += amount;
                    break;
                case StatusEffectBehavior.RefreshOnly:
                    // Stun: already active, reapplying does nothing extra.
                    break;
                case StatusEffectBehavior.ReplaceToken:
                    // Illusion: a new token replaces the old one outright.
                    Stacks = amount;
                    break;
            }
        }

        /// <summary>Burn's start-of-turn tick: deal Stacks damage, then decrement by 1.</summary>
        public void TickDecrement()
        {
            if (Stacks > 0) Stacks -= 1;
        }

        /// <summary>Growth and Thorns do not decay - only removed by combat end or a card effect.</summary>
        public bool Decays => Type == StatusEffectType.Burn;

        public bool IsExpired => Stacks <= 0;
    }
}
