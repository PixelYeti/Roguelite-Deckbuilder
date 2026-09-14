using System.Collections.Generic;
using UnityEngine;
using Talune.AI;
using Talune.Cards;
using Talune.Combat;
using Talune.Core;
using Talune.Relics;

namespace Talune.Content
{
    /// <summary>
    /// Hand-authored example content for the vertical slice (Bubblo / Voltrix / Mossmaw,
    /// per PROTOTYPE PRIORITY in the master prompt). Built at runtime with
    /// ScriptableObject.CreateInstance rather than as .asset files, so the slice is
    /// playable immediately without hand-authoring assets in the Editor first.
    /// Replace with real .asset-based content once the game has an editor workflow for it.
    /// </summary>
    public static class DefaultContent
    {
        // --- Cards ---

        public static CardData BasicAttack() => MakeCard("Basic Attack",
            "Deal 6 damage.", CardType.Attack, KinType.None, BaselineNumbers.BasicAttackCost,
            CardEffect.Damage(TargetType.SingleEnemy, BaselineNumbers.BasicAttackDamage));

        public static CardData BasicGuard() => MakeCard("Basic Guard",
            "Gain 5 Block.", CardType.Guard, KinType.None, BaselineNumbers.BasicGuardCost,
            CardEffect.Block(BaselineNumbers.BasicGuardBlock));

        public static CardData Quickstep() => MakeCard("Quickstep",
            "Draw 1 card. (Movement/utility starter card - Voltrix flavor, no literal repositioning; see the addendum's 'movement is not a spatial system' note.)",
            CardType.Skill, KinType.None, 0,
            CardEffect.DrawCards(1));

        public static CardData StaticFang() => MakeCard("Static Fang",
            "Deal 6 damage. Chain lightning once.", CardType.Attack, KinType.Voltrix, 1,
            CardEffect.Damage(TargetType.SingleEnemy, 6),
            CardEffect.Damage(TargetType.SecondEnemy, 6));

        public static CardData BubbleGuard() => MakeCard("Bubble Guard",
            "Gain 8 Block.", CardType.Guard, KinType.Bubblo, 1,
            CardEffect.Block(8));

        public static CardData RootStrike() => MakeCard("Root Strike",
            "Deal 5 damage. Gain 1 Growth.", CardType.Attack, KinType.Mossmaw, 1,
            CardEffect.Damage(TargetType.SingleEnemy, 5),
            CardEffect.ApplyStatus(TargetType.Self, StatusEffectType.Growth, 1));

        /// <summary>Fix 1: Voltrix+Mossmaw hybrid, prototype-required. Simplified to a
        /// one-shot resolution (gain Thorns, immediately discharge it as bonus lightning
        /// damage) rather than the full passive "Thorns always discharge on trigger" rule,
        /// which needs a reactive-trigger system this vertical slice doesn't have yet.</summary>
        public static CardData Stormroot() => MakeCard("Stormroot",
            "Gain 2 Thorns. Deal 2 lightning damage to a second enemy.",
            CardType.Hybrid, new List<KinType> { KinType.Voltrix, KinType.Mossmaw }, 2,
            CardEffect.ApplyStatus(TargetType.Self, StatusEffectType.Thorns, 2),
            CardEffect.Damage(TargetType.SecondEnemy, 2));

        public static List<CardData> BuildStarterDeck()
        {
            var deck = new List<CardData>();
            for (int i = 0; i < 4; i++) deck.Add(BasicAttack());
            for (int i = 0; i < 4; i++) deck.Add(BasicGuard());
            deck.Add(Quickstep());
            deck.Add(StaticFang()); // Starter Kin card - Voltrix, for this default build.
            return deck;
        }

        /// <summary>Fix 1: Bubblo+Voltrix hybrid ("water conducts lightning, chains between
        /// bubbled enemies"). Simplified the same way as Stormroot: a one-shot resolution
        /// (Block + a lightning hit that reaches a second enemy) rather than a passive
        /// always-on conduction rule.</summary>
        public static CardData Stormwater() => MakeCard("Stormwater",
            "Gain 6 Block. Deal 5 lightning damage to a second enemy.",
            CardType.Hybrid, new List<KinType> { KinType.Bubblo, KinType.Voltrix }, 2,
            CardEffect.Block(6),
            CardEffect.Damage(TargetType.SecondEnemy, 5));

        /// <summary>Fix 1: Bubblo+Mossmaw hybrid ("healing causes plants/roots to grow").</summary>
        public static CardData Bloomtide() => MakeCard("Bloomtide",
            "Heal 5. Gain 2 Growth.", CardType.Hybrid, new List<KinType> { KinType.Bubblo, KinType.Mossmaw }, 2,
            CardEffect.Heal(5),
            CardEffect.ApplyStatus(TargetType.Self, StatusEffectType.Growth, 2));

        public static List<CardData> BuildRewardPool() => new()
        {
            StaticFang(), BubbleGuard(), RootStrike(), Stormroot(), Stormwater(), Bloomtide(),
        };

        // --- Relics (Fix: "modify rules, not flat bonuses" - see RelicEffectKind) ---

        public static List<RelicData> BuildStarterRelicPool() => new()
        {
            MakeRelic("Ember Compass", "The first card you play each turn costs 0.", RelicEffectKind.FreeFirstCardEachTurn, 0),
            MakeRelic("Bramble Charm", "Whenever you gain Block, also gain 1 Thorns.", RelicEffectKind.BlockGrantsThorns, 1),
            MakeRelic("Twin Echo", "The first Attack card you play each turn triggers twice.", RelicEffectKind.DoubleFirstAttackEachTurn, 0),
            MakeRelic("Still Water", "Block no longer resets at the start of your turn.", RelicEffectKind.BlockPersistsAcrossTurns, 0),
            MakeRelic("Kindling Core", "Whenever you apply Burn, apply 1 extra stack.", RelicEffectKind.ExtraBurnStack, 1),
            MakeRelic("Molt Skin", "At the start of combat, gain 5 Block.", RelicEffectKind.StartCombatBlock, 5),
            MakeRelic("Growing Pride", "Whenever you play a Mossmaw card, gain 1 Growth.", RelicEffectKind.MossmawCardGrantsGrowth, 1),
            MakeRelic("Fracture Shard", "Whenever an enemy dies, gain 1 Energy.", RelicEffectKind.EnemyDeathGrantsEnergy, 1),
        };

        private static RelicData MakeRelic(string relicName, string description, RelicEffectKind kind, int value)
        {
            var relic = ScriptableObject.CreateInstance<RelicData>();
            relic.RelicName = relicName;
            relic.Description = description;
            relic.Kind = kind;
            relic.Value = value;
            return relic;
        }

        private static CardData MakeCard(string cardName, string description, CardType type, KinType kin, int cost, params CardEffect[] effects)
            => MakeCard(cardName, description, type, kin == KinType.None ? new List<KinType>() : new List<KinType> { kin }, cost, effects);

        private static CardData MakeCard(string cardName, string description, CardType type, List<KinType> kinTags, int cost, params CardEffect[] effects)
        {
            var card = ScriptableObject.CreateInstance<CardData>();
            card.CardName = cardName;
            card.Description = description;
            card.Type = type;
            card.KinTags = kinTags;
            card.EnergyCost = cost;
            card.Effects = new List<CardEffect>(effects);
            return card;
        }

        // --- Enemies (Fix 10 baseline HP; Melee/Ranged/Special per ENEMY STRUCTURE) ---
        // AI patterns use EnemyMoveRule (weight + HP-phase gate + turn gate + anti-repeat
        // cap + forced opener) - see EnemyAI.ChooseNextMove for the selection algorithm.

        public static EnemyCombatant CreateMeleeEnemy() => new(
            "Sparkmite", BaselineNumbers.BasicEnemyStandardHP,
            MakeAIProfile(
                Rule(Move(IntentCategory.Attack, 7), weight: 2f),
                Rule(Move(IntentCategory.Attack, 9), weight: 1f, maxConsecutive: 1), // Can't throw the big hit twice in a row.
                Rule(Move(IntentCategory.Block, 6), weight: 1f)));

        public static EnemyCombatant CreateRangedEnemy() => new(
            "Glowmoth", BaselineNumbers.BasicEnemyWeakHP,
            MakeAIProfile(
                Rule(Move(IntentCategory.Attack, 4), weight: 2f),
                Rule(Move(IntentCategory.Debuff, 0, StatusEffectType.Burn, 2, "Spark Dust"), weight: 1f)));

        /// <summary>A genuinely phased pattern: opens with a buff, plays defensively
        /// above half HP, then commits to an aggressive (capped) attack pattern with
        /// an occasional Burn special once below half HP.</summary>
        public static EnemyCombatant CreateSpecialEnemy() => new(
            "Bog Behemoth", BaselineNumbers.BasicEnemySpecialHP,
            MakeAIProfile(
                Rule(Move(IntentCategory.Buff, 0, StatusEffectType.Growth, 2, "Overgrowth", targetsSelf: true), forcedOpener: true),
                Rule(Move(IntentCategory.Block, 8), weight: 2f, maxHP: 1f, minHP: 0.5f),
                Rule(Move(IntentCategory.Attack, 6), weight: 1f, maxHP: 1f, minHP: 0.5f),
                Rule(Move(IntentCategory.Attack, 11), weight: 3f, maxHP: 0.5f, minHP: 0f, maxConsecutive: 2),
                Rule(Move(IntentCategory.Special, 0, StatusEffectType.Burn, 3, "Bog Fury"), weight: 1f, maxHP: 0.5f, minHP: 0f)));

        /// <summary>Elite Combat, per ENEMY STRUCTURE: tougher single foe with a real
        /// pattern (charges up, then unleashes a capped-frequency big hit).</summary>
        public static EnemyCombatant CreateElite() => new(
            "Shard Lancer", BaselineNumbers.EliteHP,
            MakeAIProfile(
                Rule(Move(IntentCategory.Buff, 0, StatusEffectType.Growth, 3, "Charge", targetsSelf: true), forcedOpener: true),
                Rule(Move(IntentCategory.Attack, 14), weight: 2f, maxConsecutive: 1), // Can't unleash the charged hit twice in a row.
                Rule(Move(IntentCategory.Block, 10), weight: 1f),
                Rule(Move(IntentCategory.Attack, 8), weight: 2f)));

        /// <summary>Boss, per BOSSES: tests slow/passive builds by escalating Burn over
        /// time, with a defensive phase that punishes players who can't break through Block.</summary>
        public static EnemyCombatant CreateBoss() => new(
            "Geode Worm", BaselineNumbers.BossHP,
            MakeAIProfile(
                Rule(Move(IntentCategory.Special, 0, StatusEffectType.Burn, 2, "Molten Core"), weight: 1f, maxHP: 1f, minHP: 0.5f),
                Rule(Move(IntentCategory.Block, 15), weight: 2f, maxHP: 1f, minHP: 0.5f),
                Rule(Move(IntentCategory.Attack, 12), weight: 2f, maxHP: 0.5f, minHP: 0f),
                Rule(Move(IntentCategory.Special, 0, StatusEffectType.Burn, 4, "Eruption"), weight: 1f, maxHP: 0.5f, minHP: 0f, maxConsecutive: 1)));

        private static EnemyMove Move(IntentCategory category, int value, StatusEffectType status = default, int statusStacks = 0, string description = null, bool targetsSelf = false)
        {
            return new EnemyMove
            {
                Category = category,
                Value = category is IntentCategory.Buff or IntentCategory.Debuff or IntentCategory.Special ? statusStacks : value,
                Description = description,
                Status = status,
                StatusTargetsSelf = targetsSelf,
            };
        }

        private static EnemyMoveRule Rule(EnemyMove move, float weight = 1f, float minHP = 0f, float maxHP = 1f, int minTurn = 0, int maxConsecutive = 0, bool forcedOpener = false)
        {
            return new EnemyMoveRule
            {
                Move = move,
                Weight = weight,
                MinHPPercent = minHP,
                MaxHPPercent = maxHP,
                MinTurn = minTurn,
                MaxConsecutiveUses = maxConsecutive,
                ForcedOpener = forcedOpener,
            };
        }

        private static EnemyAIProfile MakeAIProfile(params EnemyMoveRule[] rules)
        {
            var profile = ScriptableObject.CreateInstance<EnemyAIProfile>();
            profile.Rules = new List<EnemyMoveRule>(rules);
            return profile;
        }
    }
}
