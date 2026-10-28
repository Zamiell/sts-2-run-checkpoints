using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Saves;

namespace RunCheckpoints;

internal static class RewindUi
{
    private static Window? _window;
    private static bool _settingsLoaded;
    private static bool _shortcutEnabled;
    public static bool IsOpen => GodotObject.IsInstanceValid(_window);

    public static bool ShortcutEnabled
    {
        get
        {
            if (!_settingsLoaded)
            {
                var path = SettingsPath;
                _shortcutEnabled = File.Exists(path) && JsonSerializer.Deserialize<bool>(File.ReadAllText(path));
                _settingsLoaded = true;
            }
            return _shortcutEnabled;
        }
    }

    private static string SettingsPath => ProjectSettings.GlobalizePath("user://run-checkpoints/settings.json");

    public static void AddButtons(Control screen)
    {
        if (!RewindService.Available || screen.HasNode("RunCheckpointButtons"))
        {
            return;
        }
        var bar = new HBoxContainer { Name = "RunCheckpointButtons" };
        screen.AddChild(bar);
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        bar.OffsetLeft = 48;
        bar.OffsetTop = -160;
        bar.OffsetRight = 800;
        bar.OffsetBottom = -108;
        bar.AddThemeConstantOverride("separation", 16);
        var picker = Button("Rewind...", () => Open(false));
        var previous = Button("Rewind to previous checkpoint", () => Open(true));
        bar.AddChild(picker);
        bar.AddChild(previous);
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(0, 46) };
        button.AddThemeFontSizeOverride("font_size", 22);
        button.Pressed += action;
        return button;
    }

    public static void Open(bool quick)
    {
        if (IsOpen || RewindService.Busy || !RewindService.Available
            || NModalContainer.Instance?.OpenModal != null)
        {
            return;
        }
        try
        {
            var context = RewindService.Context();
            var errors = new List<string>();
            var checkpoints = context.Store.List(context.Identity, (path, error) =>
            {
                Log.Error($"[RunCheckpoints] Cannot read {path}: {error}");
                errors.Add(Path.GetFileName(path));
            });
            Build(context, checkpoints, quick, errors);
        }
        catch (Exception exception)
        {
            Log.Error($"[RunCheckpoints] Cannot open checkpoint picker: {exception}");
            ShowError(exception.Message);
        }
    }

    private static void Build(RewindContext context, IReadOnlyList<CheckpointInfo> checkpoints,
        bool quick, List<string> errors)
    {
        var window = new Window
        {
            Title = "Run Checkpoints",
            Transient = true,
            Exclusive = true,
            MinSize = new Vector2I(640, 480)
        };
        _window = window;
        NGame.Instance!.AddChild(window);
        var panel = new PanelContainer();
        window.AddChild(panel);
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var margin = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" })
        {
            margin.AddThemeConstantOverride("margin_" + side, 20);
        }
        panel.AddChild(margin);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 12);
        column.AddThemeFontSizeOverride("font_size", 21);
        margin.AddChild(column);
        var header = new Label
        {
            Text = CheckpointPresentation.Header(CheckpointPresentation.Parse(context.LiveState)),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(header);
        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(split);
        var tree = new Tree
        {
            Columns = 3,
            HideRoot = true,
            ColumnTitlesVisible = true,
            SelectMode = Tree.SelectModeEnum.Row,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(360, 170)
        };
        tree.SetColumnTitle(0, "Checkpoint (newest first)");
        tree.SetColumnTitle(1, "HP / Gold");
        tree.SetColumnTitle(2, "Saved");
        tree.SetColumnExpand(1, false);
        tree.SetColumnCustomMinimumWidth(1, 120);
        tree.SetColumnExpand(2, false);
        tree.SetColumnCustomMinimumWidth(2, 100);
        split.AddChild(tree);
        var details = new RichTextLabel
        {
            BbcodeEnabled = false,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(250, 170),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        split.AddChild(details);
        var notice = new Label
        {
            Text = "Room-boundary saves, not card-by-card undo. Before rewind preserves the last resumable save.\n"
                + "Progress, unlocks, and run history are NOT rolled back. All future checkpoints are retained.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        column.AddChild(notice);
        var status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(status);
        var shortcut = new CheckButton { Text = "Enable Ctrl+Z quick-rewind confirmation", ButtonPressed = ShortcutEnabled };
        shortcut.Toggled += enabled =>
        {
            try
            {
                CheckpointStore.AtomicWrite(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(enabled));
                _shortcutEnabled = enabled;
            }
            catch (Exception exception) when (CheckpointStore.IsFileError(exception))
            {
                Log.Error($"[RunCheckpoints] Cannot save shortcut setting: {exception}");
                shortcut.SetPressedNoSignal(_shortcutEnabled);
                status.Text = "Could not save shortcut preference: " + exception.Message;
            }
        };
        column.AddChild(shortcut);
        var footer = new HBoxContainer();
        column.AddChild(footer);
        var rewind = Button("Rewind here", () => { });
        var cancel = Button("Close", () => { });
        footer.AddChild(rewind);
        footer.AddChild(cancel);
        var root = tree.CreateItem();
        var rows = new Dictionary<TreeItem, (CheckpointInfo Checkpoint, SerializableRun Save)>();
        var lastLoaded = context.Store.ReadJournal(context.Identity).LastLoaded;
        foreach (var checkpoint in checkpoints)
        {
            try
            {
                var save = CheckpointPresentation.Parse(context.Store.ReadVerified(checkpoint));
                var loaded = Path.GetFileName(checkpoint.Path) == lastLoaded;
                var location = (loaded ? "[Last loaded] " : "") + (checkpoint.Label != null ? "[Before rewind] " : "")
                    + CheckpointPresentation.Location(save);
                var row = tree.CreateItem(root);
                row.SetText(0, location);
                row.SetText(1, $"{save.Players[0].CurrentHp}/{save.Players[0].MaxHp}  /  {save.Players[0].Gold}");
                row.SetText(2, Age(checkpoint.SavedUtc));
                row.SetTooltipText(0, checkpoint.Path);
                row.SetTooltipText(2, checkpoint.SavedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                if (loaded)
                {
                    row.SetCustomColor(0, new Color(1f, 0.85f, 0.3f));
                }
                rows.Add(row, (checkpoint, save));
            }
            catch (Exception exception)
            {
                Log.Error($"[RunCheckpoints] Cannot display {checkpoint.Path}: {exception}");
                errors.Add(Path.GetFileName(checkpoint.Path));
            }
        }

        var confirming = false;
        void ResetConfirmation()
        {
            confirming = false;
            rewind.Text = "Rewind here";
            cancel.Text = "Close";
        }
        void Select()
        {
            if (RewindService.Busy)
            {
                return;
            }
            ResetConfirmation();
            if (tree.GetSelected() is { } item && rows.TryGetValue(item, out var selected))
            {
                try
                {
                    details.Text = CheckpointPresentation.Details(selected.Save, selected.Checkpoint);
                    rewind.Disabled = selected.Save.Players[0].CurrentHp <= 0;
                    status.Text = rewind.Disabled ? "This checkpoint contains a dead player and cannot be resumed." : "";
                }
                catch (Exception exception)
                {
                    Log.Error($"[RunCheckpoints] Cannot display checkpoint details: {exception}");
                    rewind.Disabled = true;
                    status.Text = "Cannot display this checkpoint: " + exception.Message;
                }
            }
        }
        void Confirm()
        {
            if (RewindService.Busy)
            {
                return;
            }
            if (tree.GetSelected() is not { } item || !rows.TryGetValue(item, out var selected))
            {
                return;
            }
            confirming = true;
            status.Text = $"Return to {CheckpointPresentation.Location(selected.Save)}?\n"
                + "Unsaved combat actions will be lost. A Before rewind checkpoint will preserve your last resumable save.";
            rewind.Text = "Confirm rewind";
            cancel.Text = "Cancel";
            cancel.GrabFocus();
        }
        void Close()
        {
            if (RewindService.Busy)
            {
                return;
            }
            if (confirming)
            {
                ResetConfirmation();
                status.Text = "";
                return;
            }
            window.Hide();
            window.QueueFree();
            _window = null;
        }
        async Task RestoreSelected()
        {
            if (tree.GetSelected() is not { } item || !rows.TryGetValue(item, out var selected))
            {
                return;
            }
            rewind.Disabled = true;
            cancel.Disabled = true;
            shortcut.Disabled = true;
            tree.MouseFilter = Control.MouseFilterEnum.Ignore;
            status.Text = "Preserving your save and restoring checkpoint...";
            try
            {
                await RewindService.Restore(context, selected.Checkpoint);
                confirming = false;
                Close();
            }
            catch (Exception exception)
            {
                Log.Error($"[RunCheckpoints] Restore request failed: {exception}");
                ResetConfirmation();
                status.Text = "Rewind failed: " + exception.Message;
                rewind.Disabled = false;
                cancel.Disabled = false;
                shortcut.Disabled = false;
                tree.MouseFilter = Control.MouseFilterEnum.Stop;
            }
        }
        rewind.Pressed += () =>
        {
            if (RewindService.Busy)
            {
                return;
            }
            if (confirming)
            {
                TaskHelper.RunSafely(RestoreSelected());
            }
            else
            {
                Confirm();
            }
        };
        cancel.Pressed += Close;
        window.CloseRequested += Close;
        window.WindowInput += input =>
        {
            if (input is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
            {
                Close();
                window.SetInputAsHandled();
            }
        };
        tree.ItemSelected += Select;
        rewind.Disabled = true;
        if (quick)
        {
            var previous = context.Store.Previous(checkpoints, context.Identity, CheckpointStore.Fingerprint(context.LiveState));
            var row = rows.FirstOrDefault(pair => pair.Value.Checkpoint == previous).Key;
            if (row != null)
            {
                row.Select(0);
                Select();
                if (!rewind.Disabled)
                {
                    Confirm();
                }
            }
            else
            {
                status.Text = "No earlier distinct checkpoint is available on this branch. Select any checkpoint below.";
            }
        }
        else if (rows.Count > 0)
        {
            rows.Keys.First().Select(0);
            Select();
        }
        if (rows.Count == 0)
        {
            status.Text = "No checkpoints for this run yet. Checkpoints appear after the game saves.";
        }
        if (errors.Count > 0)
        {
            notice.Text += $"\nWARNING: {errors.Count} unreadable checkpoints were skipped. See the game log.";
        }
        var viewport = NGame.Instance.GetViewportRect().Size;
        window.PopupCentered(new Vector2I((int)Math.Min(1200, viewport.X * 0.9f),
            (int)Math.Min(780, viewport.Y * 0.9f)));
        if (confirming)
        {
            cancel.GrabFocus();
        }
        else
        {
            tree.GrabFocus();
        }
    }

    private static string Age(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "Just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} hr ago" : utc.ToLocalTime().ToString("MMM d");
    }

    private static void ShowError(string message)
    {
        if (GodotObject.IsInstanceValid(_window))
        {
            _window!.Hide();
            _window.QueueFree();
            _window = null;
        }
        var dialog = new AcceptDialog { Title = "Run Checkpoints", DialogText = message };
        NGame.Instance!.AddChild(dialog);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        dialog.PopupCentered();
    }
}
