namespace Talune.Core
{
    /// <summary>
    /// The 5 card types (Fix 2: "Kin" was removed from this list - it's an
    /// alignment tag every one of these types can carry, not a 6th type).
    /// </summary>
    public enum CardType
    {
        Attack,
        Guard,
        Skill,
        Power,
        Hybrid, // Always carries exactly 2 Kin tags - see CardData.KinTags.
    }
}
