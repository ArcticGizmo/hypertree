using Hypertree.Scopes;

namespace Hypertree.App;

/// <summary>
/// One entry in the command palette (F5): a display <paramref name="Name"/> and the action to
/// <paramref name="Run"/> when it's chosen. This iteration is just the bones — a flat in-app registry
/// (built in <see cref="App"/>) with a few real and stubbed commands. Later, commands become the
/// single home for actions currently scattered across the map footer and tray.
/// </summary>
/// <param name="DisabledReason">When non-null, the command is greyed out and inert; the text explains
/// why (shown alongside the row) so the command stays discoverable rather than vanishing.</param>
/// <param name="Preview">The board to show behind the command in the preview palette. Null falls back to
/// the current map (ambient context); a command with a distinct target supplies a map that highlights
/// what it will act on — e.g. the group it would remove.</param>
internal sealed record Command(string Name, Action Run, string? DisabledReason = null,
                               Func<NavMap>? Preview = null);
