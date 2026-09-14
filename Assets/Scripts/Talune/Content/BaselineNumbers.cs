namespace Talune.Content
{
    /// <summary>
    /// Fix 10 (Docs/design-addendum-v1.md): explicit placeholder numbers so the
    /// vertical slice is playable before real balance passes happen. Change these,
    /// don't leave them blank.
    /// </summary>
    public static class BaselineNumbers
    {
        public const int RookMaxHP = 70;
        public const int PlayerMaxEnergy = 3;
        public const int PlayerDrawPerTurn = 5;

        public const int BasicEnemyWeakHP = 12;     // 8-15 range midpoint.
        public const int BasicEnemyStandardHP = 32; // 25-40 range midpoint.
        public const int BasicEnemySpecialHP = 52;  // 45-60 range midpoint.
        public const int EliteHP = 105;              // 90-120 range midpoint.
        public const int BossHP = 250;                // 200-300 range midpoint.

        public const int BasicAttackDamage = 6;
        public const int BasicAttackCost = 1;
        public const int BasicGuardBlock = 5;
        public const int BasicGuardCost = 1;
    }
}
