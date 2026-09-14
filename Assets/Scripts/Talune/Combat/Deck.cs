using System.Collections.Generic;
using Talune.Cards;
using Talune.Core;

namespace Talune.Combat
{
    /// <summary>
    /// Draw/hand/discard piles for one combat. When the draw pile empties, the
    /// discard pile is shuffled into a fresh draw pile (standard genre rule, no
    /// addendum fix needed - it was already unambiguous in the master prompt).
    /// </summary>
    public class Deck
    {
        private readonly List<CardData> _drawPile = new();
        private readonly List<CardData> _discardPile = new();
        private readonly List<CardData> _hand = new();
        private readonly System.Random _rng;

        public IReadOnlyList<CardData> Hand => _hand;
        public int DrawPileCount => _drawPile.Count;
        public int DiscardPileCount => _discardPile.Count;

        public Deck(IEnumerable<CardData> startingDeck, System.Random rng = null)
        {
            _rng = rng ?? new System.Random();
            _drawPile.AddRange(startingDeck);
            Shuffle(_drawPile);
        }

        private void Shuffle(List<CardData> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public void DrawCards(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_drawPile.Count == 0)
                {
                    if (_discardPile.Count == 0) return; // Nothing left anywhere.
                    _drawPile.AddRange(_discardPile);
                    _discardPile.Clear();
                    Shuffle(_drawPile);
                }

                var card = _drawPile[^1];
                _drawPile.RemoveAt(_drawPile.Count - 1);
                _hand.Add(card);
            }
        }

        /// <summary>Discards a specific card from hand (e.g. after it's played).</summary>
        public void DiscardFromHand(CardData card)
        {
            if (_hand.Remove(card)) _discardPile.Add(card);
        }

        /// <summary>End of turn: unplayed hand cards go to discard unless a card says otherwise.</summary>
        public void DiscardHand()
        {
            _discardPile.AddRange(_hand);
            _hand.Clear();
        }

        /// <summary>Kin Rank (Fix 4) counts cards in draw + discard + hand - i.e. everything here.</summary>
        public int CountOfKin(KinType kin)
        {
            int count = 0;
            foreach (var c in _drawPile) if (c.KinTags.Contains(kin)) count++;
            foreach (var c in _discardPile) if (c.KinTags.Contains(kin)) count++;
            foreach (var c in _hand) if (c.KinTags.Contains(kin)) count++;
            return count;
        }
    }
}
