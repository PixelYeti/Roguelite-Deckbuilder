using System;
using System.Collections.Generic;
using Talune.Core;

namespace Talune.Combat
{
    /// <summary>
    /// Shared state/behavior for anything that fights: the player and enemies.
    /// Block decay rule = Fix 8 (resets to 0 at the start of the OWNER's own turn).
    /// </summary>
    public abstract class CombatEntity
    {
        public string DisplayName { get; protected set; }
        public int MaxHP { get; protected set; }
        public int CurrentHP { get; protected set; }
        public int Block { get; private set; }
        public bool IsDead => CurrentHP <= 0;

        private readonly Dictionary<StatusEffectType, StatusEffectInstance> _statusEffects = new();

        public event Action<CombatEntity, int> OnDamaged;
        public event Action<CombatEntity> OnDied;

        protected CombatEntity(string displayName, int maxHP)
        {
            DisplayName = displayName;
            MaxHP = maxHP;
            CurrentHP = maxHP;
        }

        // --- Block ---

        public void GainBlock(int amount)
        {
            if (amount <= 0) return;
            Block += amount;
        }

        /// <summary>Fix 8: call at the start of THIS entity's own turn, before it acts.</summary>
        public void ResetBlockForNewTurn() => Block = 0;

        // --- Damage / healing ---

        /// <summary>
        /// Applies incoming damage: an Illusion token absorbs one hit first (if present),
        /// then Block reduces the remainder, then HP. Thorns reflection for melee hits
        /// taken is handled by the caller (CombatManager), since it needs to know the source.
        /// </summary>
        /// <returns>The HP actually lost (0 if already dead, fully blocked, or absorbed by Illusion) -
        /// callers that log damage should report this, not the nominal incoming amount.</returns>
        public int TakeDamage(int amount)
        {
            if (amount <= 0 || IsDead) return 0;

            if (HasStatus(StatusEffectType.Illusion))
            {
                RemoveStatus(StatusEffectType.Illusion);
                return 0; // Fully absorbed - Illusion consumes one whole attack, not a partial soak.
            }

            int remaining = amount;
            if (Block > 0)
            {
                int absorbed = Math.Min(Block, remaining);
                Block -= absorbed;
                remaining -= absorbed;
            }

            if (remaining <= 0) return 0;

            CurrentHP = Math.Max(0, CurrentHP - remaining);
            OnDamaged?.Invoke(this, remaining);
            if (IsDead) OnDied?.Invoke(this);
            return remaining;
        }

        public void Heal(int amount)
        {
            if (amount <= 0 || IsDead) return;
            CurrentHP = Math.Min(MaxHP, CurrentHP + amount);
        }

        // --- Status effects ---

        public void ApplyStatus(StatusEffectType type, int stacks)
        {
            if (stacks <= 0) return;
            if (_statusEffects.TryGetValue(type, out var existing))
            {
                existing.Reapply(stacks);
            }
            else
            {
                _statusEffects[type] = new StatusEffectInstance(type, stacks);
            }
        }

        public bool HasStatus(StatusEffectType type) => _statusEffects.ContainsKey(type);

        public int GetStacks(StatusEffectType type) =>
            _statusEffects.TryGetValue(type, out var s) ? s.Stacks : 0;

        public void RemoveStatus(StatusEffectType type) => _statusEffects.Remove(type);

        /// <summary>Fix 3: Burn ticks (deals damage, then -1) at the start of the owner's own turn.</summary>
        public void TickStartOfTurnStatuses()
        {
            if (_statusEffects.TryGetValue(StatusEffectType.Burn, out var burn) && burn.Stacks > 0)
            {
                TakeDamage(burn.Stacks);
                burn.TickDecrement();
                if (burn.IsExpired) _statusEffects.Remove(StatusEffectType.Burn);
            }
        }

        /// <summary>Growth stacks: +1 damage per stack on this entity's Attack cards (Fix 14).</summary>
        public int GrowthDamageBonus() => GetStacks(StatusEffectType.Growth);

        /// <summary>Thorns: damage reflected back at a melee attacker (Fix 14).</summary>
        public int ThornsReflectDamage() => GetStacks(StatusEffectType.Thorns);

        public IReadOnlyCollection<StatusEffectInstance> AllStatuses => _statusEffects.Values;
    }
}
