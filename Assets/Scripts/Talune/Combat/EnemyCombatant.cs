using Talune.AI;

namespace Talune.Combat
{
    /// <summary>An enemy in combat. Telegraphs its NextIntent (Fix 9) before the
    /// player commits cards each turn.</summary>
    public class EnemyCombatant : CombatEntity
    {
        public EnemyAIProfile AIProfile { get; }
        public EnemyMove PendingMove { get; private set; }
        public EnemyIntent NextIntent => PendingMove.ToIntent();
        public int TurnNumber { get; private set; } = 0;

        /// <summary>Index into AIProfile.Rules of the rule most recently chosen, and how
        /// many turns in a row it's been chosen - lets EnemyAI enforce MaxConsecutiveUses.</summary>
        public int LastChosenRuleIndex { get; private set; } = -1;
        public int ConsecutiveUsesOfLastRule { get; private set; } = 0;

        public EnemyCombatant(string displayName, int maxHP, EnemyAIProfile aiProfile) : base(displayName, maxHP)
        {
            AIProfile = aiProfile;
        }

        /// <summary>Decide and publish the move for the enemy's NEXT turn, so the
        /// player can see it (as NextIntent) before they act (Fix 9's core requirement).</summary>
        public void TelegraphNextMove(CombatContext context)
        {
            PendingMove = EnemyAI.ChooseNextMove(this, context, out int chosenRuleIndex);
            ConsecutiveUsesOfLastRule = (chosenRuleIndex == LastChosenRuleIndex) ? ConsecutiveUsesOfLastRule + 1 : 1;
            LastChosenRuleIndex = chosenRuleIndex;
        }

        public void AdvanceTurnCounter() => TurnNumber++;

        public float HPPercent => MaxHP > 0 ? CurrentHP / (float)MaxHP : 0f;
    }
}
