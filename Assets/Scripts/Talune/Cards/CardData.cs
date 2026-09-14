using System.Collections.Generic;
using UnityEngine;
using Talune.Core;

namespace Talune.Cards
{
    /// <summary>
    /// Data-driven card definition. One asset per card so designers can add the
    /// ~30-card prototype roster without touching code. Type/Kin split follows Fix 2:
    /// exactly one CardType, 0-2 Kin tags (Hybrid cards always carry exactly 2).
    /// </summary>
    [CreateAssetMenu(fileName = "NewCard", menuName = "Talune/Card")]
    public class CardData : ScriptableObject
    {
        public string CardName;
        [TextArea] public string Description;
        public CardType Type;
        public List<KinType> KinTags = new(); // 0 = Neutral, 1 = single-Kin, 2 = Hybrid (Type must be Hybrid).
        [Range(0, 3)] public int EnergyCost;
        public List<CardEffect> Effects = new();

        public bool IsNeutral => KinTags.Count == 0;

        private void OnValidate()
        {
            if (Type == CardType.Hybrid && KinTags.Count != 2)
            {
                Debug.LogWarning($"{name}: Hybrid cards must carry exactly 2 Kin tags (Fix 2). Currently has {KinTags.Count}.");
            }
            if (Type != CardType.Hybrid && KinTags.Count > 1)
            {
                Debug.LogWarning($"{name}: non-Hybrid cards should carry at most 1 Kin tag.");
            }
        }
    }
}
