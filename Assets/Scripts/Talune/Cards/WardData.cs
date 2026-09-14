using Talune.AI;
using UnityEngine;

namespace Talune.Cards
{
    /// <summary>Blueprint for a Ward unit a card (or an enemy's Summon move) can place
    /// onto the battlefield - mirrors an enemy's shape (name, HP, one AI-style move)
    /// but for a small persistent unit rather than a full combatant. Built at runtime
    /// via ScriptableObject.CreateInstance, same pattern as CardData/RelicData.</summary>
    public class WardData : ScriptableObject
    {
        public string DisplayName;
        public int MaxHP;
        public EnemyMove Action;
    }
}
