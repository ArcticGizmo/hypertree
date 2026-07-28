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
    public void Missing_file_yields_defaults()
    {
        // A fresh store with no file loads defaults rather than throwing (MapStyle defaults to Board).
        Assert.Equal(MapStyle.Board, StoreInTempDir().Load().MapStyle);
    }
}
