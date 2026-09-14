using UnityEngine;

namespace Talune.AI
{
    /// <summary>
    /// One selectable move plus the conditions under which the AI may pick it and
    /// how strongly it prefers to. This is what makes a pattern more than "cycle a
    /// list" or "flat random": moves can be phase-gated by HP, gated to not appear
    /// before a given turn (openers), weighted against each other, and capped so an
    /// enemy can't spam its scariest move every single turn.
    /// </summary>
    [System.Serializable]
    public struct EnemyMoveRule
    {
        public EnemyMove Move;

        [Tooltip("Relative likelihood versus other eligible rules this turn. 1 = baseline.")]
        public float Weight;

        [Range(0f, 1f)] public float MinHPPercent; // Eligible only when self HP% >= this (e.g. 0 = always).
        [Range(0f, 1f)] public float MaxHPPercent; // Eligible only when self HP% <= this (e.g. 1 = always).
        public int MinTurn;                        // Eligible only from this (0-based) enemy turn number onward.
        public int MaxConsecutiveUses;              // 0 = unlimited. Caps back-to-back repeats of this exact rule.
        public bool ForcedOpener;                   // If true, always used on the enemy's turn 0, ignoring weight.

        public static EnemyMoveRule Simple(EnemyMove move, float weight = 1f) => new()
        {
            Move = move,
            Weight = weight,
            MinHPPercent = 0f,
            MaxHPPercent = 1f,
            MinTurn = 0,
            MaxConsecutiveUses = 0,
        };
    }
}
