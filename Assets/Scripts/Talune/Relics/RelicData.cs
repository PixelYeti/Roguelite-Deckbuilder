using UnityEngine;

namespace Talune.Relics
{
    [CreateAssetMenu(fileName = "NewRelic", menuName = "Talune/Relic")]
    public class RelicData : ScriptableObject
    {
        public string RelicName;
        [TextArea] public string Description;
        public RelicEffectKind Kind;
        public int Value; // Meaning depends on Kind - see RelicEffectKind's comments.
    }
}
