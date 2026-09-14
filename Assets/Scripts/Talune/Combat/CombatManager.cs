using System;
using System.Collections.Generic;
using System.Linq;
using Talune.AI;
using Talune.Cards;
using Talune.Content;
using Talune.Core;
using Talune.Relics;

namespace Talune.Combat
{
    public enum CombatOutcome { Ongoing, Victory, Defeat }

    /// <summary>
    /// Orchestrates one combat: turn structure, card resolution, enemy actions.
    /// Implements the numbered fixes from Docs/design-addendum-v1.md:
    /// Fix 7 (multi-enemy targeting), Fix 8 (block decay), Fix 9 (intent telegraph),
    /// Fix 10 (baseline numbers live in BaselineNumbers.cs).
    /// </summary>
    public class CombatManager
    {
        public PlayerCombatant Player { get; }
        public List<EnemyCombatant> Enemies { get; }
        public Deck Deck { get; }
        public CombatOutcome Outcome { get; private set; } = CombatOutcome.Ongoing;
        public int TurnCount { get; private set; } = 0;

        public event Action<string> OnLog; // Simple text log for the test harness / debug UI.

        private readonly CombatContext _context;
        private readonly System.Random _rng;
        private readonly RelicManager _relics;
        private bool _hasPlayedCardThisTurn;
        private bool _hasPlayedAttackThisTurn;

        public CombatManager(PlayerCombatant player, List<EnemyCombatant> enemies, IEnumerable<CardData> startingDeck,
            System.Random rng = null, IEnumerable<RelicData> relics = null)
        {
            Player = player;
            Enemies = enemies;
            _rng = rng ?? new System.Random();
            Deck = new Deck(startingDeck, _rng);
            _context = new CombatContext(Player, Enemies, _rng);
            _relics = new RelicManager(relics);

            foreach (var enemy in Enemies)
                enemy.OnDied += _ => Player.GainEnergy(_relics.EnergyFromEnemyDeath());
        }

        private void Log(string message) => OnLog?.Invoke(message);

        public void StartCombat()
        {
            Log($"--- Combat start: Rook ({Player.CurrentHP} HP) vs {string.Join(", ", Enemies.Select(e => $"{e.DisplayName} ({e.CurrentHP} HP)"))} ---");
            foreach (var e in Enemies) e.TelegraphNextMove(_context);
            StartPlayerTurn();

            // Granted AFTER turn 1's own setup (which includes Fix 8's block reset) so the
            // bonus survives into turn 1 instead of being immediately zeroed out by it.
            int startBlock = _relics.StartCombatBlockBonus();
            if (startBlock > 0) { Player.GainBlock(startBlock); Log($"  Relic: start with {startBlock} Block."); }
        }

        public void StartPlayerTurn()
        {
            TurnCount++;
            // Fix 8: block resets at the start of the player's own turn, UNLESS a relic
            // overrides that rule (e.g. "Still Water" - see RelicEffectKind.BlockPersistsAcrossTurns).
            if (!_relics.BlockPersistsAcrossTurns()) Player.ResetBlockForNewTurn();
            Player.TickStartOfTurnStatuses(); // Burn ticks, if the player is burning.
            Player.RefillEnergy();
            Deck.DrawCards(BaselineNumbers.PlayerDrawPerTurn);
            _hasPlayedCardThisTurn = false;
            _hasPlayedAttackThisTurn = false;
            Log($"[Turn {TurnCount}] Player turn. Energy {Player.Energy}/{Player.MaxEnergy}, hand: {Deck.Hand.Count} cards.");
            CheckCombatEnd();
        }

        /// <summary>Attempts to play a card. Returns false (and logs why) if it can't be played.</summary>
        public bool TryPlayCard(CardData card, EnemyCombatant primaryTarget)
        {
            if (Outcome != CombatOutcome.Ongoing) return false;
            if (!Deck.Hand.Contains(card)) { Log($"{card.CardName} is not in hand."); return false; }

            int cost = _relics.ModifyCardCost(card.EnergyCost, isFirstCardThisTurn: !_hasPlayedCardThisTurn);
            if (!Player.CanAfford(cost)) { Log($"Not enough energy for {card.CardName}."); return false; }

            Player.SpendEnergy(cost);
            foreach (var effect in card.Effects)
                ResolveCardEffect(effect, primaryTarget);

            bool isAttack = card.Type == CardType.Attack;
            if (isAttack && _relics.ShouldDoubleAttackEffects(isFirstAttackThisTurn: !_hasPlayedAttackThisTurn))
            {
                Log($"  Relic: {card.CardName} triggers twice.");
                foreach (var effect in card.Effects)
                    ResolveCardEffect(effect, primaryTarget);
            }

            if (card.KinTags.Contains(KinType.Mossmaw))
            {
                int growth = _relics.GrowthFromMossmawCard();
                if (growth > 0) Player.ApplyStatus(StatusEffectType.Growth, growth);
            }

            Deck.DiscardFromHand(card);
            Log($"Played {card.CardName} (-{cost} energy, {Player.Energy} left).");
            _hasPlayedCardThisTurn = true;
            if (isAttack) _hasPlayedAttackThisTurn = true;
            CheckCombatEnd();
            return true;
        }

        private void ResolveCardEffect(CardEffect effect, EnemyCombatant primaryTarget)
        {
            switch (effect.Kind)
            {
                case CardEffectKind.Damage:
                    foreach (var target in ResolveEnemyTargets(effect.Target, primaryTarget))
                        DealDamageToEnemy(target, effect.Value + Player.GrowthDamageBonus());
                    break;
                case CardEffectKind.Block:
                    Player.GainBlock(effect.Value);
                    int bonusThorns = _relics.ThornsFromBlockGain();
                    if (bonusThorns > 0) Player.ApplyStatus(StatusEffectType.Thorns, bonusThorns);
                    break;
                case CardEffectKind.ApplyStatus:
                    int stacks = effect.Value + (effect.Status == StatusEffectType.Burn ? _relics.ExtraBurnStacks() : 0);
                    if (effect.Target == TargetType.Self)
                        Player.ApplyStatus(effect.Status, stacks);
                    else
                        foreach (var target in ResolveEnemyTargets(effect.Target, primaryTarget))
                            target.ApplyStatus(effect.Status, stacks);
                    break;
                case CardEffectKind.Heal:
                    Player.Heal(effect.Value);
                    break;
                case CardEffectKind.DrawCards:
                    Deck.DrawCards(effect.Value);
                    break;
                case CardEffectKind.GainEnergy:
                    Player.GainEnergy(effect.Value);
                    break;
            }
        }

        /// <summary>Fix 7: resolves who an enemy-targeting effect actually hits.
        /// SecondEnemy falls back to the primary target itself when no other enemy
        /// exists - it never fizzles, it just doesn't get a "bonus" second body.</summary>
        private IEnumerable<EnemyCombatant> ResolveEnemyTargets(TargetType target, EnemyCombatant primary)
        {
            switch (target)
            {
                case TargetType.SingleEnemy:
                    if (primary != null && !primary.IsDead) yield return primary;
                    break;
                case TargetType.SecondEnemy:
                    var second = Enemies.FirstOrDefault(e => e != primary && !e.IsDead);
                    yield return second ?? primary;
                    break;
                case TargetType.AllEnemies:
                    foreach (var e in _context.LivingEnemies()) yield return e;
                    break;
            }
        }

        private void DealDamageToEnemy(EnemyCombatant enemy, int amount)
        {
            if (enemy.IsDead) return; // Already down - don't log a phantom hit (e.g. Fix 7's SecondEnemy fallback landing on a target that died to the SingleEnemy hit moments earlier).
            int dealt = enemy.TakeDamage(amount);
            Log($"  -> {enemy.DisplayName} takes {dealt} ({enemy.CurrentHP}/{enemy.MaxHP} HP left).");
        }

        public void EndPlayerTurn()
        {
            if (Outcome != CombatOutcome.Ongoing) return;
            Deck.DiscardHand();
            Log($"[Turn {TurnCount}] End player turn.");
            RunEnemyTurn();
        }

        private void RunEnemyTurn()
        {
            foreach (var enemy in Enemies.Where(e => !e.IsDead))
            {
                enemy.ResetBlockForNewTurn(); // Fix 8 applies symmetrically to enemies.
                enemy.TickStartOfTurnStatuses();
                if (enemy.IsDead) continue; // Burn could finish them off.

                ResolveEnemyMove(enemy);
                enemy.AdvanceTurnCounter();
                if (Outcome != CombatOutcome.Ongoing) return;
            }

            // Telegraph what each surviving enemy will do next, ready for the player's upcoming turn.
            foreach (var enemy in Enemies.Where(e => !e.IsDead))
                enemy.TelegraphNextMove(_context);

            if (Outcome == CombatOutcome.Ongoing) StartPlayerTurn();
        }

        private void ResolveEnemyMove(EnemyCombatant enemy)
        {
            var move = enemy.PendingMove;
            switch (move.Category)
            {
                case IntentCategory.Attack:
                    int dealt = Player.TakeDamage(move.Value);
                    Log($"  {enemy.DisplayName} attacks for {dealt} ({Player.CurrentHP}/{Player.MaxHP} HP left).");
                    // Thorns (Fix 14): reflects on attacks the player takes. Simplification for the
                    // vertical slice - not yet gated to "melee only" since enemy roles (Melee/Ranged/
                    // Special) aren't a combat-mechanical flag yet, only a content-authoring one.
                    int thorns = Player.ThornsReflectDamage();
                    if (thorns > 0)
                    {
                        enemy.TakeDamage(thorns);
                        Log($"  Thorns reflects {thorns} back at {enemy.DisplayName}.");
                    }
                    break;
                case IntentCategory.Block:
                    enemy.GainBlock(move.Value);
                    Log($"  {enemy.DisplayName} blocks for {move.Value}.");
                    break;
                case IntentCategory.Buff:
                    enemy.ApplyStatus(move.Status, move.Value);
                    Log($"  {enemy.DisplayName} buffs itself: +{move.Value} {move.Status}.");
                    break;
                case IntentCategory.Debuff:
                case IntentCategory.Special:
                    var recipient = move.StatusTargetsSelf ? (CombatEntity)enemy : Player;
                    recipient.ApplyStatus(move.Status, move.Value);
                    Log($"  {enemy.DisplayName} uses {move.Description ?? move.Category.ToString()}.");
                    break;
            }
            CheckCombatEnd();
        }

        private void CheckCombatEnd()
        {
            if (Player.IsDead)
            {
                Outcome = CombatOutcome.Defeat;
                Log("Rook has fallen. Run ends here (see DEATH/RUN END in the master prompt).");
            }
            else if (Enemies.All(e => e.IsDead))
            {
                Outcome = CombatOutcome.Victory;
                Log("Victory!");
            }
        }
    }
}
