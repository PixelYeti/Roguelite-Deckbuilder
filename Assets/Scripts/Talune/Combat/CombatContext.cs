using System.Collections.Generic;

namespace Talune.Combat
{
    /// <summary>Read-only view of the fight, handed to AI decisions and card-effect
    /// resolution so they can see the player, all living enemies, and shared RNG.</summary>
    public class CombatContext
    {
        public PlayerCombatant Player { get; }
        public IReadOnlyList<EnemyCombatant> Enemies { get; }
        public System.Random Rng { get; }

        public CombatContext(PlayerCombatant player, IReadOnlyList<EnemyCombatant> enemies, System.Random rng)
        {
            Player = player;
            Enemies = enemies;
            Rng = rng;
        }

        public IEnumerable<EnemyCombatant> LivingEnemies()
        {
            foreach (var e in Enemies) if (!e.IsDead) yield return e;
        }
    }
}
