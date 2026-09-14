using System.Collections.Generic;
using Talune.Core;

namespace Talune.Combat
{
    /// <summary>Rook, in combat. Baseline HP from Fix 10 (70) lives in BaselineNumbers.</summary>
    public class PlayerCombatant : CombatEntity
    {
        public int Energy { get; private set; }
        public int MaxEnergy { get; }

        public PlayerCombatant(int maxHP, int maxEnergy) : base("Rook", maxHP)
        {
            MaxEnergy = maxEnergy;
        }

        /// <summary>Fix: 3 Energy refills at the start of each of the player's turns.</summary>
        public void RefillEnergy() => Energy = MaxEnergy;

        public bool CanAfford(int cost) => Energy >= cost;

        public void SpendEnergy(int cost) => Energy = System.Math.Max(0, Energy - cost);

        public void GainEnergy(int amount) => Energy += System.Math.Max(0, amount);

        /// <summary>Kin Rank per Fix 4: count of cards carrying `kin` currently in the whole deck
        /// (draw + discard + hand, i.e. everything except exhausted/removed cards).</summary>
        public static int KinRank(int cardCountOfKin) => cardCountOfKin switch
        {
            <= 0 => 0,
            <= 2 => 1,
            <= 4 => 2,
            <= 6 => 3,
            <= 8 => 4,
            _ => 5,
        };
    }
}
