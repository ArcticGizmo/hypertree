namespace Hypertree.Spatial;

/// <summary>
/// The default group colours for the spatial map, as <c>#RRGGBB</c> hex strings (Core has no Avalonia, so
/// colours are strings here and parsed at the drawing edge). These mirror the metro theme's branch palette
/// so a group keeps a familiar hue whichever model is showing.
///
/// A group's colour is only a <em>default</em>: it is derived from the group's index and then overridden by
/// any explicit choice the user has made (stored in <see cref="SpatialState.GroupColors"/>). The palette is
/// cycled by index, so more groups than slots reuse hues — see the plan's open question on palette size.
/// <see cref="Main"/> is the neutral near-white for the <c>main</c> "ungrouped" bucket, which is not a
/// deliberate grouping and so gets no palette hue.
/// </summary>
public static class SpatialPalette
{
    /// <summary>The neutral colour for the <c>main</c> / ungrouped bucket (the metro "light spine").</summary>
    public const string Main = "#C5D0E0";

    /// <summary>The cycled group hues, in order — coral, sky, green, lilac, amber, pink, teal, periwinkle.</summary>
    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#F4795B", "#5BC8F4", "#7BD88F", "#C99BF4", "#F4C95B", "#F45B9C", "#63D6C4", "#9CB2F4",
    };

    /// <summary>The default colour for the group at <paramref name="index"/> (its slot in the stack),
    /// cycled through <see cref="Colors"/>. A negative index (used for the main bucket) yields
    /// <see cref="Main"/>.</summary>
    public static string For(int index)
        => index < 0 ? Main : Colors[index % Colors.Count];

    /// <summary>
    /// A <b>stable</b> default colour for a group derived from its id, so the default hue survives adding,
    /// removing and reordering groups (unlike an index-based default, which would shift). Deterministic
    /// across runs — it hashes the id's bytes rather than using <see cref="object.GetHashCode"/>, which is
    /// only stable within a single process. An explicit user choice still overrides this.
    /// </summary>
    public static string For(Guid id)
    {
        int sum = 0;
        foreach (byte b in id.ToByteArray()) sum = unchecked(sum * 31 + b);
        return Colors[(sum & int.MaxValue) % Colors.Count];
    }
}
