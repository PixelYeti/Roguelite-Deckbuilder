using UnityEngine;

namespace Talune.Run
{
    /// <summary>
    /// Lightweight cross-run progression, persisted via PlayerPrefs (this prototype has
    /// no save-file system yet). Essence is a meta-currency separate from a run's own
    /// Fragments - it survives death and carries between runs, and crossing a threshold
    /// permanently unlocks one extra relic/card into every future run's pools.
    /// </summary>
    public static class MetaProgress
    {
        private const string EssenceKey = "Talune_Essence";

        public const int RelicUnlockThreshold = 40;
        public const int CardUnlockThreshold = 100;

        public static int Essence => PlayerPrefs.GetInt(EssenceKey, 0);
        public static bool RelicUnlocked => Essence >= RelicUnlockThreshold;
        public static bool CardUnlocked => Essence >= CardUnlockThreshold;

        /// <returns>Any unlock names newly crossed by this award (for a one-time "UNLOCKED!" callout) - empty if none.</returns>
        public static System.Collections.Generic.List<string> AddEssence(int amount)
        {
            var newlyUnlocked = new System.Collections.Generic.List<string>();
            if (amount <= 0) return newlyUnlocked;

            int before = Essence;
            int after = before + amount;
            PlayerPrefs.SetInt(EssenceKey, after);
            PlayerPrefs.Save();

            if (before < RelicUnlockThreshold && after >= RelicUnlockThreshold) newlyUnlocked.Add("Veteran's Charm (relic)");
            if (before < CardUnlockThreshold && after >= CardUnlockThreshold) newlyUnlocked.Add("Essence Burst (card)");
            return newlyUnlocked;
        }
    }
}
