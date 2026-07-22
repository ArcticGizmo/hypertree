namespace Hypertree.Scopes;

/// <summary>
/// The four Model-P navigation intents, decoupled from any key chord. The hotkey layer maps a
/// registered chord (default Ctrl+Alt+Arrow) to one of these and hands it to the
/// <see cref="NavigationModel"/>; the chord itself is config-driven and rebindable (M3), so the
/// intent must not carry key identity.
/// </summary>
public enum NavAction
{
    /// <summary>Move one desktop left within the current level (← / within-level).</summary>
    MoveLeft,

    /// <summary>Move one desktop right within the current level (→ / within-level).</summary>
    MoveRight,

    /// <summary>Dive into the scope anchored at the current desktop (↓ / the new depth axis).</summary>
    Dive,

    /// <summary>Surface back to the current scope's anchor (↑ / the new depth axis).</summary>
    Surface,
}
