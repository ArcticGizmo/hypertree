namespace Hypertree.Desktops;

/// <summary>
/// Stable identity of a Windows virtual desktop — the OS's per-desktop GUID. Hypertree keys
/// all its state on this rather than the desktop's ordinal, because the ordinal shifts when
/// desktops are created/removed/reordered, while the GUID survives for the desktop's lifetime.
/// </summary>
public readonly record struct DesktopId(Guid Value)
{
    public override string ToString() => Value.ToString("B");
}

/// <summary>A virtual desktop as the OS currently reports it: identity, name, and ordinal.</summary>
public sealed record DesktopInfo(DesktopId Id, string Name, int Index);
