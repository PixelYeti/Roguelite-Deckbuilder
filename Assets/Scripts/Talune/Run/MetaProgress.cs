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
        private const string TotalRunsKey = "Talune_TotalRuns";
        private const string RunsWonKey = "Talune_RunsWon";
        private const string BestNodesKey = "Talune_BestNodesCompleted";

        public const int RelicUnlockThreshold = 40;
        public const int CardUnlockThreshold = 100;

        public static int Essence => PlayerPrefs.GetInt(EssenceKey, 0);
        public static bool RelicUnlocked => Essence >= RelicUnlockThreshold;
        public static bool CardUnlocked => Essence >= CardUnlockThreshold;

        public static int TotalRuns => PlayerPrefs.GetInt(TotalRunsKey, 0);
        public static int RunsWon => PlayerPrefs.GetInt(RunsWonKey, 0);
        public static int BestNodesCompleted => PlayerPrefs.GetInt(BestNodesKey, 0);

        /// <summary>Call once per run end (win or lose) - separate from AddEssence so a
        /// caller can't accidentally record a run twice just by awarding Essence twice.</summary>
        public static void RecordRunEnd(bool won, int nodesCompleted)
        {
            PlayerPrefs.SetInt(TotalRunsKey, TotalRuns + 1);
            if (won) PlayerPrefs.SetInt(RunsWonKey, RunsWon + 1);
            if (nodesCompleted > BestNodesCompleted) PlayerPrefs.SetInt(BestNodesKey, nodesCompleted);
            PlayerPrefs.Save();
        }

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
