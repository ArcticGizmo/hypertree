namespace Hypertree.Platform;

/// <summary>
/// Controls whether Hypertree launches automatically when the user logs in. The OS is the source of
/// truth (on Windows, an <c>HKCU\…\Run</c> registry value), so this reads and writes it directly
/// rather than mirroring the flag in settings.json. Behind a Core interface so the App never touches
/// the registry and a non-Windows head can supply its own mechanism.
/// </summary>
public interface IStartupManager
{
    /// <summary>Whether launch-on-login is currently enabled for this user.</summary>
    bool IsEnabled { get; }

    /// <summary>Enable or disable launch-on-login. Best-effort; failures are swallowed.</summary>
    void SetEnabled(bool enabled);
}
