using System.Collections.Generic;
using System.Linq;
using Talune.Cards;
using Talune.Relics;

namespace Talune.Run
{
    /// <summary>
    /// Kip's actual buy/sell/remove logic (Fix 11's price ranges). Every Try* method
    /// spends Fragments atomically - if the player can't afford it, nothing happens
    /// and it returns false, rather than partially applying an effect.
    /// </summary>
    public static class KipShop
    {
        public const int CardPriceMin = 50, CardPriceMax = 75;
        public const int UpgradePrice = 50;
        public const int RemovalBasePrice = 75;
        public const int RemovalPriceIncrement = 25; // Escalates each purchase this run - can't be spammed.
        public const int RelicPriceMin = 150, RelicPriceMax = 200;

        public class Offer
        {
            public List<(CardData Card, int Price)> CardsForSale = new();
            public RelicData RelicForSale;
            public int RelicPrice;
        }

        public static Offer GenerateOffer(List<CardData> cardPool, List<RelicData> relicPool, System.Random rng)
        {
            var offer = new Offer();
            foreach (var card in cardPool.OrderBy(_ => rng.Next()).Take(3))
                offer.CardsForSale.Add((card, rng.Next(CardPriceMin, CardPriceMax + 1)));

            var relic = relicPool.OrderBy(_ => rng.Next()).FirstOrDefault();
            if (relic != null)
            {
                offer.RelicForSale = relic;
                offer.RelicPrice = rng.Next(RelicPriceMin, RelicPriceMax + 1);
            }
            return offer;
        }

        public static bool TryBuyCard(RunState state, CardData card, int price)
        {
            if (!state.TrySpendFragments(price)) return false;
            state.AddCardToDeck(card);
            return true;
        }

        public static bool TryBuyRelic(RunState state, RelicData relic, int price)
        {
            if (!state.TrySpendFragments(price)) return false;
            state.AddRelic(relic);
            return true;
        }

        /// <returns>false if the card isn't in the run deck, has no authored upgrade
        /// (CardData.CanUpgrade), or the player can't afford it.</returns>
        public static bool TryBuyUpgrade(RunState state, CardData cardInDeck)
        {
            if (!state.Deck.Contains(cardInDeck) || !cardInDeck.CanUpgrade) return false;
            if (!state.TrySpendFragments(UpgradePrice)) return false;
            cardInDeck.TryUpgrade();
            return true;
        }

        public static int CurrentRemovalPrice(RunState state) =>
            RemovalBasePrice + RemovalPriceIncrement * state.RemovalsPurchasedThisRun;

        public static bool TryBuyRemoval(RunState state, CardData cardInDeck)
        {
            if (!state.Deck.Contains(cardInDeck)) return false;
            int price = CurrentRemovalPrice(state);
            if (!state.TrySpendFragments(price)) return false;
            state.RemoveCardFromDeck(cardInDeck);
            state.RemovalsPurchasedThisRun++;
            return true;
        }
    }
}
