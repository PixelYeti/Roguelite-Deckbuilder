namespace Talune.Core
{
    /// <summary>Who a CardEffect resolves against.</summary>
    public enum TargetType
    {
        Self,
        SingleEnemy,   // The enemy the player targeted when playing the card.
        SecondEnemy,   // Fix 7: "chains"/"spreads" effects - the next enemy after SingleEnemy.
                       // Deals base effect with no bonus / never fizzles if only 1 enemy exists.
        AllEnemies,
    }
}
