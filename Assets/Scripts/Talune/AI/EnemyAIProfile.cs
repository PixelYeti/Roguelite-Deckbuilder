using System.Collections.Generic;
using UnityEngine;

namespace Talune.AI
{
    /// <summary>
    /// An enemy's move pool. One asset per enemy so the 3 prototype enemies
    /// (Melee/Ranged/Special per biome, per the master prompt's ENEMY STRUCTURE
    /// section) can be authored without touching code.
    /// </summary>
    [CreateAssetMenu(fileName = "NewEnemyAIProfile", menuName = "Talune/Enemy AI Profile")]
    public class EnemyAIProfile : ScriptableObject
    {
        public List<EnemyMoveRule> Rules = new();
    }
}
