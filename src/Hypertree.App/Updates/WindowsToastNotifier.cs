using System;
using Hypertree.Platform;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Hypertree.App.Updates;

/// <summary>
/// Real Windows Action Center toasts via the UWP notifications compat shim (the same approach as perch's
/// <c>WindowsToastNotifier</c>). Works for an unpackaged Win32 app because
/// <see cref="ToastNotificationManagerCompat"/> auto-registers an AppUserModelId <i>and</i> a COM
/// activator on first use — no Start Menu shortcut to hand-write, no registry work, and no clash with
/// the shortcut Velopack installs.
/// </summary>
/// <remarks>
/// This replaced a tray balloon (<c>Shell_NotifyIcon</c> with <c>NIF_INFO</c>), which reads like the
/// cheaper option and isn't: Windows 11 25H2 accepts balloons — every call returns success and the shell
/// even auto-creates a <c>NotifyIconGeneratedAumid_*</c> sender for you — and then renders nothing at
/// all. Measured on build 26200 across four variants (icon version 4 and legacy, custom and system
/// glyph): no banner, no history entry. See docs/design/update-notifications.md.
///
/// Lives in the app head rather than Hypertree.Platform.Windows because the toolkit package needs the
/// Windows 10 SDK target (see the app's <c>TargetFramework</c>), which the platform project doesn't carry.
/// </remarks>
internal sealed class WindowsToastNotifier : INotifier
{
    public event Action<string>? Activated;

    // Group for every notification we raise, so `replaces` keys only ever collide with our own.
    private const string Group = "hypertree";

    public WindowsToastNotifier()
    {
        // Subscribing is what stands the COM activator up, so a click reaches this already-running tray
        // instance. The callback arrives on a background thread — see INotifier.Activated.
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        NameTheSender();
    }

    public void Show(string title, string body, string? action = null, bool silent = false,
                     string? replaces = null)
    {
        try
        {
            var toast = new ToastContentBuilder();
            if (!string.IsNullOrEmpty(action)) toast.AddArgument("action", action);
            toast.AddText(title).AddText(body);
            if (silent) toast.AddAudio(new ToastAudio { Silent = true });

            if (string.IsNullOrEmpty(replaces)) toast.Show();
            // Tag + group is what makes the shell treat this as the same notification as the last one
            // under that key: it updates the existing entry instead of stacking a new one beside it.
            else toast.Show(t => { t.Tag = replaces; t.Group = Group; });
        }
        catch { /* best-effort by contract — a toast must never fail the update check that raised it */ }
    }

    // The compat shim derives the name shown on every toast from the .exe, which gives us a lowercase
    // "hypertree". Correct it on the AppUserModelId key the shim just wrote (our own app's identity,
    // under HKCU) so notifications are branded the way the rest of the app is.
    //
    // The shim has no API for this, and its AUMID for an unpackaged app is the executable path with
    // forward slashes — mirrored here. Should that ever change, OpenSubKey simply finds nothing and the
    // name stays lowercase; nothing else depends on it.
    private static void NameTheSender()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            string aumid = exe.Replace('\\', '/');
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser
                .OpenSubKey($@"SOFTWARE\Classes\AppUserModelId\{aumid}", writable: true);
            key?.SetValue("DisplayName", "Hypertree");
        }
        catch { /* cosmetic — a lowercase sender name is not worth failing startup over */ }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            ToastArguments args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("action", out string? action) && !string.IsNullOrEmpty(action))
                Activated?.Invoke(action);
        }
        catch { /* a malformed argument string is not worth taking the app down for */ }
    }
}
