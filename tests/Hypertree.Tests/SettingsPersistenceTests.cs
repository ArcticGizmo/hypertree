using System;
using System.IO;
using Hypertree.Platform;
using Hypertree.Settings;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// The settings file round-trips through <see cref="FileSettingsStore"/>. Guards the regression where the
/// writer used the string-enum converter but the reader didn't, so a persisted <c>MapStyle</c> (the first
/// always-present top-level enum) failed to parse and the whole file silently reverted to defaults.
/// </summary>
public class SettingsPersistenceTests
{
    private static FileSettingsStore StoreInTempDir()
        => new(Path.Combine(Path.GetTempPath(), "hypertree-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void MapStyle_survives_a_save_and_reload()
    {
        var store = StoreInTempDir();
        store.Save(new AppSettings { MapStyle = MapStyle.Metro });

        Assert.Equal(MapStyle.Metro, store.Load().MapStyle);
    }

    [Fact]
    public void Non_default_settings_and_string_serialised_enums_all_round_trip()
    {
        var store = StoreInTempDir();
        var saved = new AppSettings
        {
            MapStyle = MapStyle.Metro,
            ShowTaskbarLabel = false,
            DisplayBeforeMoving = false,
            AnimateNavigation = false,
            ShowChangelogOnUpdate = false,
            HotkeyBindings =
            {
                new HotkeyBinding(HotkeyCommand.Dive, HotkeyModifiers.Control | HotkeyModifiers.Alt, HotkeyKey.ArrowDown),
            },
        };
        store.Save(saved);

        AppSettings loaded = store.Load();
        Assert.Equal(MapStyle.Metro, loaded.MapStyle);
        Assert.False(loaded.ShowTaskbarLabel);
        Assert.False(loaded.DisplayBeforeMoving);
        Assert.False(loaded.AnimateNavigation);
        Assert.False(loaded.ShowChangelogOnUpdate);
        HotkeyBinding binding = Assert.Single(loaded.HotkeyBindings);
        Assert.Equal(HotkeyCommand.Dive, binding.Command);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Modifiers);
        Assert.Equal(HotkeyKey.ArrowDown, binding.Key);
    }

    [Fact]
    public void Switcher_state_round_trips()
    {
        var store = StoreInTempDir();
        store.Save(new AppSettings
        {
            ShowSwitcher = true,
            SwitcherCollapsed = true,
            SwitcherX = 1820,
            SwitcherY = 24,
        });

        AppSettings loaded = store.Load();
        Assert.True(loaded.ShowSwitcher);
        Assert.True(loaded.SwitcherCollapsed);
        Assert.Equal(1820, loaded.SwitcherX);
        Assert.Equal(24, loaded.SwitcherY);
    }

    [Fact]
    public void Switcher_defaults_off_and_undocked()
    {
        var settings = new AppSettings();
        Assert.False(settings.ShowSwitcher);
        Assert.False(settings.SwitcherCollapsed);
        Assert.Null(settings.SwitcherX);
        Assert.Null(settings.SwitcherY);
    }

    [Fact]
    public void Missing_file_yields_defaults()
    {
        // A fresh store with no file loads defaults rather than throwing (MapStyle defaults to ASCII).
        Assert.Equal(MapStyle.Ascii, StoreInTempDir().Load().MapStyle);
    }

    [Fact]
    public void Custom_commands_round_trip_including_optional_fields()
    {
        var store = StoreInTempDir();
        var saved = new AppSettings
        {
            CustomCommands =
            {
                new CustomCommand("Open work email", "https://mail.example.com"),                 // optionals null
                new CustomCommand("Build", @"C:\tools\build.exe", "--release", @"C:\projects\app"), // all fields set
            },
        };
        store.Save(saved);

        AppSettings loaded = store.Load();
        Assert.Equal(2, loaded.CustomCommands.Count);

        CustomCommand email = loaded.CustomCommands[0];
        Assert.Equal("Open work email", email.Name);
        Assert.Equal("https://mail.example.com", email.Target);
        Assert.Null(email.Arguments);
        Assert.Null(email.WorkingDirectory);

        CustomCommand build = loaded.CustomCommands[1];
        Assert.Equal("--release", build.Arguments);
        Assert.Equal(@"C:\projects\app", build.WorkingDirectory);
    }
}
