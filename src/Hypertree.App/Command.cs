namespace Hypertree.App;

/// <summary>
/// One entry in the command palette (F5): a display <paramref name="Name"/> and the action to
/// <paramref name="Run"/> when it's chosen. This iteration is just the bones — a flat in-app registry
/// (built in <see cref="App"/>) with a few real and stubbed commands. Later, commands become the
/// single home for actions currently scattered across the map footer and tray.
/// </summary>
internal sealed record Command(string Name, Action Run);
