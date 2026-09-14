using Talune.Core;

namespace Talune.Cards
{
    /// <summary>Kinds of effect a card's effect list can be built from.
    /// Kept small on purpose (Fix from the master prompt: "avoid excessive complexity,
    /// every card should have a clear reason to exist").</summary>
    public enum CardEffectKind
    {
        Damage,
        Block,
        ApplyStatus,
        Heal,
        DrawCards,
        GainEnergy,
    }

    /// <summary>
    /// One effect within a card's effect list, e.g. Static Fang = [Damage(SingleEnemy, 6),
    /// Damage(SecondEnemy, 6)] to represent "deal damage, chain lightning once" (Fix 7:
    /// SecondEnemy targeting deals full effect with no bonus, and never fizzles solo).
    /// </summary>
    [System.Serializable]
    public struct CardEffect
    {
        public CardEffectKind Kind;
        public TargetType Target;
        public int Value; // Damage amount / Block amount / status stacks / heal amount / cards drawn / energy gained.
        public StatusEffectType Status; // Only used when Kind == ApplyStatus.

        public static CardEffect Damage(TargetType target, int amount) =>
            new() { Kind = CardEffectKind.Damage, Target = target, Value = amount };

        public static CardEffect Block(int amount) =>
            new() { Kind = CardEffectKind.Block, Target = TargetType.Self, Value = amount };

        public static CardEffect ApplyStatus(TargetType target, StatusEffectType status, int stacks) =>
            new() { Kind = CardEffectKind.ApplyStatus, Target = target, Status = status, Value = stacks };

        public static CardEffect Heal(int amount) =>
            new() { Kind = CardEffectKind.Heal, Target = TargetType.Self, Value = amount };

        public static CardEffect DrawCards(int count) =>
            new() { Kind = CardEffectKind.DrawCards, Target = TargetType.Self, Value = count };

        public static CardEffect GainEnergy(int amount) =>
            new() { Kind = CardEffectKind.GainEnergy, Target = TargetType.Self, Value = amount };
    }
}
