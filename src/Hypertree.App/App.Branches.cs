using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hypertree.App.Ipc;
using Hypertree.App.Status;
using Hypertree.App.Updates;
using Hypertree.App.Views;
using Hypertree.Changelog;
using Hypertree.Desktops;
using Hypertree.Ipc;
using Hypertree.Layout;
using Hypertree.Platform;
using Hypertree.Scopes;
using Hypertree.Settings;
using Hypertree.Spatial;
using Hypertree.Store;
using Hypertree.WindowLayout;

namespace Hypertree.App;

public sealed partial class App
{
    // ── Branch definition ─────────────────────────────────────────────────────────

    // "New branch…" — always the branch card. The card itself carries a "Load from template" button (only
    // when templates exist), so a template is an optional in-place fill, never a gate before the fields.
    private void PromptNewBranch()
    {
        if (_desktops is null) return;
        OpenNewBranchDialog(null);
    }

    // Open the branch prompt as a card, optionally pre-filled with a template's desktop labels. The card's
    // in-card "Load from template" button is wired only when there's at least one template to load.
    private void OpenNewBranchDialog(IReadOnlyList<string>? prefillLabels)
    {
        Action<Action<IReadOnlyList<string>>>? onLoadTemplate =
            _settings.BranchTemplates.Count > 0 ? PickTemplateInto : null;
        _stage?.Present(new BranchContent(CreateBranch, prefillLabels, onLoadTemplate));
    }

    // Open the template picker over the branch card. The chosen template's labels are handed to `fill`
    // (which writes them into the card's still-editable Desktops box), then we pop back to the card — so
    // you can tweak the loaded labels before creating, or Esc back with the card untouched.
    private void PickTemplateInto(Action<IReadOnlyList<string>> fill)
    {
        var items = _settings.BranchTemplates.Select(template =>
        {
            BranchTemplate t = template; // capture per iteration
            return new PaletteItem(t.Name, string.Join(" · ", t.Labels), "▸", () =>
            {
                fill(t.Labels);
                _stage?.Back(); // return to the branch card, now pre-filled
            });
        }).ToList();
        OpenPalette("Pick a template…", "↑↓ move · ↵ use · Esc back", items);
    }

    private void CreateBranch(BranchSpec spec)
    {
        if (_model is null || _desktops is null) return;

        var refs = new List<DesktopRef>(spec.Labels.Count);
        foreach (string label in spec.Labels)
        {
            DesktopId id = _desktops.Create($"{spec.Name} · {label}");
            _created.Add(id.Value);
            refs.Add(new DesktopRef(id, label));
        }

        var branch = new Branch(spec.Name, refs);
        // Created over the map (the branch prompt sits on top of it): attach the branch below the highlighted
        // room's group, not below main. Tray / command-palette creation has no map in the chain, so it falls
        // back to below main.
        if (_stage is { HasDurableBase: true } && _spatialOverlay?.SelectedRoom is { } room
            && _model.Locate(room) is { } at)
            _model.AddBranchBelow(at.OnMain, at.BranchIndex, branch);
        else _model.AddBranch(branch);
        RefreshOrFlash();
    }

    // ── Branch templates (reusable desktop recipes for new branches) ─────────────────

    // The single template manager: a palette listing every saved template (with a live preview of what it
    // would create) plus a "Create new template" row. Choosing a template deletes it (behind a confirm);
    // "Create new" (or typing a name that matches nothing) opens the definition card.
    private void ManageTemplatesPrompt() => ShowTemplateManager(refresh: false);

    // <param name="refresh">false: push the manager over the current surface (the command palette; Esc pops
    // back to it). true: rebuild it after a create/delete taken on a card pushed over it — the card and the
    // now-stale list are replaced in place, so the new list shows while the command palette beneath is kept
    // (Esc still returns there).</param>
    private void ShowTemplateManager(bool refresh)
    {
        if (_settingsStore is null) return;

        var items = new List<PaletteItem>
        {
            new("Create new template", "name it and list its desktops", "＋", () => OpenCreateTemplateCard()),
        };
        foreach (BranchTemplate template in _settings.BranchTemplates)
        {
            BranchTemplate t = template; // capture per iteration
            items.Add(new PaletteItem(t.Name, string.Join(" · ", t.Labels), "🗑",
                () => ConfirmDeleteTemplate(t),          // Enter deletes (pushes a confirm card over this palette)…
                SpatialPreview: () => TemplatePreview(t), // show the branch this template would stand up
                OnDelete: () => ConfirmDeleteTemplate(t))); // …and Del does the same on the highlighted row
        }

        // Typing a name that matches no existing template offers to create it with that name pre-filled.
        PaletteItem? CreateRow(string q) =>
            new($"Create “{q}”", "new template", "＋", () => OpenCreateTemplateCard(prefillName: q));

        var palette = new PaletteContent("Manage templates…",
            "↑↓ move · ↵ create/delete · ⌦ delete · Esc back · preview = what it creates", items, CreateRow);
        // Refresh drops the card/confirm (top) + the stale manager beneath it (popCount 2), keeping the
        // command palette under that; the initial open just pushes over the command palette.
        if (refresh) _stage?.ReplaceTop(2, palette); else _stage?.Present(palette);
    }

    // Open the template-definition card, optionally pre-filled. On confirm the template is saved and we
    // land back in the (refreshed) manager, so you can immediately create or delete another.
    private void OpenCreateTemplateCard(string? prefillName = null, IReadOnlyList<string>? prefillLabels = null)
    {
        _stage?.Present(new TemplateContent((name, labels) =>
        {
            SaveTemplate(name, labels);
            ShowTemplateManager(refresh: true); // return to the manager, now including the new template
        }, prefillName, prefillLabels));
    }

    private void SaveTemplate(string name, IReadOnlyList<string> labels)
    {
        if (_settingsStore is null) return;
        // Same name overwrites, so re-saving updates a template in place (mirrors snapshots).
        _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.BranchTemplates.Add(new BranchTemplate(name, labels));
        _settingsStore.Save(_settings);
    }

    private void ConfirmDeleteTemplate(BranchTemplate template)
    {
        _stage?.Present(new ConfirmContent($"Delete template “{template.Name}”?", () =>
        {
            _settings.BranchTemplates.RemoveAll(t => t.Name.Equals(template.Name, StringComparison.OrdinalIgnoreCase));
            _settingsStore?.Save(_settings);
            ShowTemplateManager(refresh: true); // return to the manager, now without the deleted template
        }, confirmLabel: "Delete"));
    }

    // Preview scene for a template: the single group it would add (its desktops as rooms), sitting below a
    // stub main row — so the manager shows, at a glance, exactly what picking this template stands up. Built
    // from a synthetic source with placeholder ids and an empty state, so the rooms fall back to the default
    // row layout (main on top, the template's group below).
    private static SpatialScene TemplatePreview(BranchTemplate t)
    {
        SpatialDesktop Room(string label) => new(new DesktopId(Guid.NewGuid()), label, Selected: false, Here: false, WindowCount: 0);

        var main = new SpatialGroupSource(Guid.Empty, "main", IsMain: true, new[] { Room("main") });
        var branch = new SpatialGroupSource(Guid.NewGuid(), t.Name, IsMain: false, t.Labels.Select(Room).ToList());
        return SpatialScene.From(new SpatialSource(new[] { main, branch }), new SpatialState());
    }

    private void RemoveBranch(int index)
    {
        if (_model is null) return;
        // RemoveBranch reassigns the branch's still-live desktops onto main; TearDownBranch then destroys them
        // in the OS. Resync re-derives the model from the live desktop list so the map reflects the destruction
        // now — without it the destroyed desktops ghost onto main until the next reconcile wipes them.
        TearDownBranch(_model.RemoveBranch(index));
        _model.Resync();
        RefreshOrFlash();
    }

    // ── Delete a single desktop (map × badge) with a confirm prompt ───────────────

    private void DeleteTopDesktop(int index)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekTopDesktop(index);
        if (peek is null || _model.TotalDesktops <= 1) return; // never delete the last desktop

        Confirm($"Delete desktop “{peek.Value.label}”?\nAny windows on it move to another desktop.", () =>
        {
            _desktops.Remove(peek.Value.id, Fallback(peek.Value.id));
            _created.Remove(peek.Value.id.Value);
            _model.Resync();
            RefreshOrFlash();
        });
    }

    private void DeleteBranchDesktop(int branchIndex, int desktopIndex)
    {
        if (_model is null || _desktops is null) return;
        var peek = _model.PeekBranchDesktop(branchIndex, desktopIndex);
        if (peek is null || _model.TotalDesktops <= 1) return;

        // Name the branch in the prompt: a label like "api" says nothing about which branch it sits in, and
        // the same label commonly repeats across branches (that's the point of templates).
        string branchName = _model.BranchNameAt(branchIndex) ?? "";
        // Taking a branch's only desktop takes the branch with it (see DetachBranchDesktop), which is a
        // bigger deal than the prompt would otherwise let on.
        string consequence = _model.BranchDesktopCount(branchIndex) == 1
            ? $"It’s the only desktop in “{branchName}”, so the branch goes too. Any windows on it move to another desktop."
            : "Any windows on it move to another desktop.";

        Confirm($"Delete desktop “{peek.Value.label}” from branch “{branchName}”?\n{consequence}", () =>
        {
            DesktopId fallback = Fallback(peek.Value.id);
            DesktopId? id = _model.DetachBranchDesktop(branchIndex, desktopIndex);
            if (id is not null)
            {
                _created.Remove(id.Value.Value);
                try { _desktops.Remove(id.Value, fallback); } catch { /* already gone */ }
            }
            _model.Resync();
            RefreshOrFlash();
        });
    }

    // Any live desktop other than the one being deleted (prefer the current view).
    private DesktopId Fallback(DesktopId avoid)
    {
        DesktopId cur = _desktops!.Current;
        if (cur != avoid) return cur;
        foreach (DesktopInfo d in _desktops.List()) if (d.Id != avoid) return d.Id;
        return avoid; // unreachable — guarded by TotalDesktops > 1
    }

    // A confirm card pushed over the current surface (the map, when a Del/Shift+Del came from it). Esc pops
    // back; confirming runs the action then unwinds to where the chain started.
    private void Confirm(string message, Action onConfirm)
        => _stage?.Present(new ConfirmContent(message, onConfirm));

    // Remove a branch's desktops — but ONLY ones Hypertree created, never the user's own desktops.
    private void TearDownBranch(Branch? branch)
    {
        if (branch is null || _model is null || _desktops is null) return;
        DesktopId fallback = _model.FallbackDesktopId;
        foreach (DesktopRef d in branch.Desktops)
        {
            if (_created.Remove(d.Id.Value))
            {
                try { _desktops.Remove(d.Id, fallback); } catch { /* already gone — best-effort */ }
            }
        }
    }
}
