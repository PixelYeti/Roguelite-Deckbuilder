namespace Talune.Core
{
    /// <summary>
    /// The six Kin factions. A card carries 0-2 of these as alignment tags
    /// (see Fix 2 in Docs/design-addendum-v1.md) - Kin is NOT a card Type.
    /// </summary>
    public enum KinType
    {
        None = 0, // Neutral cards (basic starter Attack/Guard) carry no Kin tag.
        Bubblo,
        Hush,
        Voltrix,
        Mossmaw,
        Razorwing,
        Ironjaw,
    }
}
