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
        public const int WardSlotsPerSide = 2;

        public PlayerCombatant Player { get; }
        public List<EnemyCombatant> Enemies { get; }
        public Deck Deck { get; }
        public CombatOutcome Outcome { get; private set; } = CombatOutcome.Ongoing;
        public int TurnCount { get; private set; } = 0;

        /// <summary>The shared battlefield (see WardCombatant) - additive on top of the
        /// existing HP-trading combat loop, not a replacement for it. Capped at
        /// WardSlotsPerSide per side, enforced at summon/steal time in ResolveCardEffect
        /// and in ResolveEnemyMove's Summon handling.</summary>
        public List<WardCombatant> PlayerWards { get; } = new();
        public List<WardCombatant> EnemyWards { get; } = new();

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
            ResolvePlayerWardActions();
            CleanupDeadWards();
            _hasPlayedCardThisTurn = false;
            _hasPlayedAttackThisTurn = false;
            Log($"[Turn {TurnCount}] Player turn. Energy {Player.Energy}/{Player.MaxEnergy}, hand: {Deck.Hand.Count} cards.");
            CheckCombatEnd();
        }

        /// <summary>Attempts to play a card. Returns false (and logs why) if it can't be played.
        /// `targetWard` is only consulted for effects targeting TargetType.EnemyWard
        /// (Counter/Steal) - unused otherwise.</summary>
        public bool TryPlayCard(CardData card, EnemyCombatant primaryTarget, WardCombatant targetWard = null)
        {
            if (Outcome != CombatOutcome.Ongoing) return false;
            if (!Deck.Hand.Contains(card)) { Log($"{card.CardName} is not in hand."); return false; }

            // Ward-specific effects are validated BEFORE spending energy, same tier as the
            // affordability check below - a blocked Summon/Destroy/Steal must not cost anything.
            foreach (var effect in card.Effects)
            {
                if (effect.Kind == CardEffectKind.SummonWard && PlayerWards.Count >= WardSlotsPerSide)
                { Log($"No open Ward slot for {card.CardName}."); return false; }
                if (effect.Kind is CardEffectKind.DestroyWard or CardEffectKind.StealWard && (targetWard == null || !EnemyWards.Contains(targetWard)))
                { Log($"{card.CardName} needs an enemy Ward to target."); return false; }
                if (effect.Kind == CardEffectKind.StealWard && PlayerWards.Count >= WardSlotsPerSide)
                { Log($"No open Ward slot to steal into for {card.CardName}."); return false; }
            }

            int cost = _relics.ModifyCardCost(card.EnergyCost, isFirstCardThisTurn: !_hasPlayedCardThisTurn);
            if (!Player.CanAfford(cost)) { Log($"Not enough energy for {card.CardName}."); return false; }

            Player.SpendEnergy(cost);
            foreach (var effect in card.Effects)
                ResolveCardEffect(effect, primaryTarget, targetWard);

            bool isAttack = card.Type == CardType.Attack;
            if (isAttack && _relics.ShouldDoubleAttackEffects(isFirstAttackThisTurn: !_hasPlayedAttackThisTurn))
            {
                Log($"  Relic: {card.CardName} triggers twice.");
                foreach (var effect in card.Effects)
                    ResolveCardEffect(effect, primaryTarget, targetWard);
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

        private void ResolveCardEffect(CardEffect effect, EnemyCombatant primaryTarget, WardCombatant targetWard = null)
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
                case CardEffectKind.SummonWard:
                    if (effect.WardToSummon != null && PlayerWards.Count < WardSlotsPerSide)
                    {
                        var ward = new WardCombatant(effect.WardToSummon.DisplayName, effect.WardToSummon.MaxHP, Side.Player, effect.WardToSummon.Action, PlayerWards.Count);
                        PlayerWards.Add(ward);
                        Log($"  Summoned {ward.DisplayName} ({ward.MaxHP} HP).");
                    }
                    break;
                case CardEffectKind.DestroyWard:
                    if (targetWard != null && EnemyWards.Remove(targetWard))
                        Log($"  Destroyed {targetWard.DisplayName}.");
                    break;
                case CardEffectKind.StealWard:
                    if (targetWard != null && PlayerWards.Count < WardSlotsPerSide && EnemyWards.Remove(targetWard))
                    {
                        var stolen = new WardCombatant(targetWard.DisplayName, targetWard.MaxHP, Side.Player, targetWard.Action, PlayerWards.Count);
                        PlayerWards.Add(stolen);
                        Log($"  Stole {stolen.DisplayName} to your side.");
                    }
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
            // Enemy Wards act first, as a simple deterministic "vanguard" ordering -
            // ahead of the enemies themselves, symmetric to player Wards acting at the
            // start of the player's own turn (see StartPlayerTurn).
            ResolveEnemyWardActions();
            CleanupDeadWards();
            if (Outcome != CombatOutcome.Ongoing) return;

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
            if (move.Category == IntentCategory.Summon)
            {
                if (move.WardToSummon != null && EnemyWards.Count < WardSlotsPerSide)
                {
                    var ward = new WardCombatant(move.WardToSummon.DisplayName, move.WardToSummon.MaxHP, Side.Enemy, move.WardToSummon.Action, EnemyWards.Count);
                    EnemyWards.Add(ward);
                    Log($"  {enemy.DisplayName} summons {ward.DisplayName} ({ward.MaxHP} HP)!");
                }
                else
                {
                    Log($"  {enemy.DisplayName} tries to summon, but the battlefield is full.");
                }
                CheckCombatEnd();
                return;
            }
            if (move.Category == IntentCategory.Disrupt)
            {
                // "Counter-play" from the enemy side, mirroring the player's own Destroy
                // (Sunder) card - always targets the player's first Ward slot rather than
                // a random one, so a telegraphed "will disrupt" intent tells the player
                // exactly which Ward is at risk, not just that something bad is coming.
                if (PlayerWards.Count > 0)
                {
                    var target = PlayerWards[0];
                    PlayerWards.RemoveAt(0);
                    Log($"  {enemy.DisplayName} disrupts your {target.DisplayName}!");
                }
                else
                {
                    Log($"  {enemy.DisplayName} tries to disrupt, but you have no Ward to target.");
                }
                CheckCombatEnd();
                return;
            }
            ApplyMove(enemy, enemy.DisplayName, move, Player, "Rook");
        }

        /// <summary>Resolves one EnemyMove for any actor that has one - an enemy on its
        /// own turn, or a Ward (see ResolvePlayerWardActions/ResolveEnemyWardActions) on
        /// its owner's turn. `self` receives Block/Buff; `opponent` receives Attack
        /// damage and Debuff/Special (when not self-targeting). Summon is handled
        /// separately in ResolveEnemyMove since only enemies summon in this slice -
        /// the case here is just a safety no-op if a Ward's own Action is ever Summon.</summary>
        private void ApplyMove(CombatEntity self, string selfName, EnemyMove move, CombatEntity opponent, string opponentName)
        {
            switch (move.Category)
            {
                case IntentCategory.Attack:
                    int dealt = opponent.TakeDamage(move.Value);
                    Log($"  {selfName} attacks for {dealt} ({opponentName} {opponent.CurrentHP}/{opponent.MaxHP} HP left).");
                    // Thorns (Fix 14): reflects on attacks taken. Simplification for the
                    // vertical slice - not yet gated to "melee only" since enemy roles (Melee/Ranged/
                    // Special) aren't a combat-mechanical flag yet, only a content-authoring one.
                    int thorns = opponent.ThornsReflectDamage();
                    if (thorns > 0)
                    {
                        self.TakeDamage(thorns);
                        Log($"  Thorns reflects {thorns} back at {selfName}.");
                    }
                    break;
                case IntentCategory.Block:
                    self.GainBlock(move.Value);
                    Log($"  {selfName} blocks for {move.Value}.");
                    break;
                case IntentCategory.Buff:
                    self.ApplyStatus(move.Status, move.Value);
                    Log($"  {selfName} buffs itself: +{move.Value} {move.Status}.");
                    break;
                case IntentCategory.Debuff:
                case IntentCategory.Special:
                    var recipient = move.StatusTargetsSelf ? self : opponent;
                    recipient.ApplyStatus(move.Status, move.Value);
                    Log($"  {selfName} uses {move.Description ?? move.Category.ToString()}.");
                    break;
                case IntentCategory.Summon:
                case IntentCategory.Disrupt:
                    break; // Wards don't summon or disrupt in this Phase 1 slice - both are handled at the enemy level (see ResolveEnemyMove), which needs CombatManager's own Ward lists.
            }
            CheckCombatEnd();
        }

        /// <summary>Player Wards act once per player turn, right after the draw step -
        /// see StartPlayerTurn. Each attacks the first living enemy (Wards don't yet
        /// have their own targeting choices - a natural Phase 2 extension).</summary>
        private void ResolvePlayerWardActions()
        {
            foreach (var ward in PlayerWards.Where(w => !w.IsDead).ToList())
            {
                var opponent = Enemies.FirstOrDefault(e => !e.IsDead);
                if (opponent == null) break; // Nothing left to act against.
                ApplyMove(ward, ward.DisplayName, ward.Action, opponent, opponent.DisplayName);
            }
        }

        /// <summary>Enemy Wards act once per enemy turn - see RunEnemyTurn.</summary>
        private void ResolveEnemyWardActions()
        {
            foreach (var ward in EnemyWards.Where(w => !w.IsDead).ToList())
                ApplyMove(ward, ward.DisplayName, ward.Action, Player, "Rook");
        }

        private void CleanupDeadWards()
        {
            PlayerWards.RemoveAll(w => w.IsDead);
            EnemyWards.RemoveAll(w => w.IsDead);
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
