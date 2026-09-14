using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Talune.Cards;
using Talune.Combat;
using Talune.Content;

namespace Talune.Testing
{
    /// <summary>
    /// Drop this on any GameObject and press Play: it auto-plays one full scripted
    /// combat (no UI yet) and logs every step to the Console, so the whole engine -
    /// turn structure, targeting, status effects, enemy AI - can be smoke-tested
    /// before any visuals exist. Strategy: play every affordable card each turn,
    /// preferring Attack when the player is safe and Guard when an enemy intends
    /// to Attack next turn.
    /// </summary>
    public class CombatTestRunner : MonoBehaviour
    {
        [SerializeField] private int maxTurnsBeforeAbort = 30;

        private void Start()
        {
            var player = new PlayerCombatant(BaselineNumbers.RookMaxHP, BaselineNumbers.PlayerMaxEnergy);
            var enemies = new List<EnemyCombatant> { DefaultContent.CreateMeleeEnemy(), DefaultContent.CreateRangedEnemy() };
            var deck = DefaultContent.BuildStarterDeck();

            var combat = new CombatManager(player, enemies, deck);
            combat.OnLog += Debug.Log;
            combat.StartCombat();

            int safety = 0;
            while (combat.Outcome == CombatOutcome.Ongoing && safety < maxTurnsBeforeAbort)
            {
                safety++;
                PlayOneTurn(combat);
            }

            Debug.Log(combat.Outcome == CombatOutcome.Ongoing
                ? $"[CombatTestRunner] Aborted after {maxTurnsBeforeAbort} turns without a result - check for an infinite loop."
                : $"[CombatTestRunner] Combat resolved: {combat.Outcome} after {combat.TurnCount} player turns.");
        }

        private void PlayOneTurn(CombatManager combat)
        {
            bool enemyWillAttack = combat.Enemies.Any(e => !e.IsDead && e.NextIntent.Category == Talune.Combat.IntentCategory.Attack);

            bool playedSomething = true;
            while (playedSomething && combat.Outcome == CombatOutcome.Ongoing)
            {
                playedSomething = false;
                CardData choice = ChooseCard(combat, enemyWillAttack);
                if (choice == null) break;

                var target = combat.Enemies.FirstOrDefault(e => !e.IsDead);
                if (combat.TryPlayCard(choice, target)) playedSomething = true;
            }

            if (combat.Outcome == CombatOutcome.Ongoing) combat.EndPlayerTurn();
        }

        private CardData ChooseCard(CombatManager combat, bool preferGuard)
        {
            var hand = combat.Deck.Hand;
            CardData pick = preferGuard
                ? hand.FirstOrDefault(c => c.Type == Talune.Core.CardType.Guard && combat.Player.CanAfford(c.EnergyCost))
                  ?? hand.FirstOrDefault(c => c.Type == Talune.Core.CardType.Attack && combat.Player.CanAfford(c.EnergyCost))
                : hand.FirstOrDefault(c => c.Type == Talune.Core.CardType.Attack && combat.Player.CanAfford(c.EnergyCost))
                  ?? hand.FirstOrDefault(c => c.Type == Talune.Core.CardType.Guard && combat.Player.CanAfford(c.EnergyCost));

            return pick ?? hand.FirstOrDefault(c => combat.Player.CanAfford(c.EnergyCost));
        }
    }
}
