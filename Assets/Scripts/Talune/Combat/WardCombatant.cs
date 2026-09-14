using Talune.AI;

namespace Talune.Combat
{
    /// <summary>Which side of the battlefield a Ward belongs to.</summary>
    public enum Side
    {
        Player,
        Enemy,
    }

    /// <summary>A small persistent unit placed on the shared battlefield - its own HP,
    /// and one recurring action (an EnemyMove, reusing the same move data/resolution the
    /// game already uses for enemy turns) that fires automatically once per turn on its
    /// owner's side until it dies. See CombatManager.ApplyMove and
    /// ResolvePlayerWardActions/ResolveEnemyWardActions.</summary>
    public class WardCombatant : CombatEntity
    {
        public Side Owner { get; }
        public EnemyMove Action { get; }
        public int SlotIndex { get; }

        public WardCombatant(string displayName, int maxHP, Side owner, EnemyMove action, int slotIndex) : base(displayName, maxHP)
        {
            Owner = owner;
            Action = action;
            SlotIndex = slotIndex;
        }
    }
}
