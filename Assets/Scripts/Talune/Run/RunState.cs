using System.Collections.Generic;
using Talune.Cards;
using Talune.Combat;
using Talune.Core;
using Talune.Relics;

namespace Talune.Run
{
    /// <summary>
    /// Persists across combats within one run: the actual deck (not the in-combat
    /// Deck's draw/discard/hand split - that's rebuilt fresh each CombatManager from
    /// this list), owned relics, and Fragments (Fix 11's currency). Lost entirely on
    /// death per DEATH/RUN END - a new RunState is created for the next run.
    /// </summary>
    public class RunState
    {
        public List<CardData> Deck { get; } = new();
        public List<RelicData> Relics { get; } = new();
        public int Fragments { get; private set; }

        public void AddFragments(int amount) => Fragments += System.Math.Max(0, amount);

        /// <returns>false if the player can't afford it - nothing is spent.</returns>
        public bool TrySpendFragments(int amount)
        {
            if (amount > Fragments) return false;
            Fragments -= amount;
            return true;
        }

        public void AddCardToDeck(CardData card) => Deck.Add(card);
        public bool RemoveCardFromDeck(CardData card) => Deck.Remove(card); // Kip's removal service.
        public void AddRelic(RelicData relic) => Relics.Add(relic);

        /// <summary>Kin Rank (Fix 4) computed over the persistent run deck, not just one combat's Deck.</summary>
        public int CountOfKin(KinType kin)
        {
            int count = 0;
            foreach (var c in Deck) if (c.KinTags.Contains(kin)) count++;
            return count;
        }

        public int KinRank(KinType kin) => PlayerCombatant.KinRank(CountOfKin(kin));
    }
}
