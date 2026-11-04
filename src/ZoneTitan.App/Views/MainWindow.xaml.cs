using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using ZoneTitan.App.Hotkeys;
using ZoneTitan.App.Input;
using ZoneTitan.App.Interop;
using ZoneTitan.App.Services;
using ZoneTitan.Core.Diagnostics;
using ZoneTitan.Core.Display;
using ZoneTitan.Core.Zones;

namespace ZoneTitan.App.Views;

/// <summary>
/// Setup surface. Daily driving is hotkeys, tray and wake routing — this window exists to
/// define zones, not to run them (spec §10).
/// </summary>
public partial class MainWindow : Window
{
    private readonly ZoneController _controller;
    private readonly TaskbarGuard _taskbarGuard;
    private readonly WakeRouter? _wakeRouter;

    private ZoneDefinition? _selected;
    private IReadOnlyList<MonitorInfo> _monitors = [];

    private Button? _capturingHotkeyButton;
    private Action<string>? _hotkeyCaptured;

    private DispatcherTimer? _testTimer;
    private int _testSecondsLeft;
    private string? _testRevertTo;

    // Monitor-map drag state. The map renders device pixels scaled down, so a drop has to be
    // converted back through the same scale to become a real desktop position.
    private double _mapScale = 1;
    private readonly Dictionary<Border, MonitorInfo> _mapTiles = [];
    private Border? _dragTile;
    private Point _dragStartPoint;
    private double _dragOriginLeft;
    private double _dragOriginTop;

    internal MainWindow(ZoneController controller, TaskbarGuard taskbarGuard, WakeRouter? wakeRouter = null)
    {
        _controller = controller;
        _taskbarGuard = taskbarGuard;
        _wakeRouter = wakeRouter;

        InitializeComponent();

        _selected = _controller.Config.Zones.FirstOrDefault();

        Loaded += (_, _) =>
        {
            ApplyDarkTitleBar();
            RenderAll();
        };

        _controller.Changed += OnControllerChanged;
        Closed += (_, _) => _controller.Changed -= OnControllerChanged;

        PreviewKeyDown += OnPreviewKeyDownForCapture;
    }

    private void OnControllerChanged() => Dispatcher.Invoke(RenderAll);

    /// <summary>Dark title bar — WPF will not do this for us (spec §9).</summary>
    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var enabled = 1;
            NativeMethods.DwmSetWindowAttribute(
                handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Cosmetic only — an older build without the attribute is not worth failing over.
        }
    }

    // ==================================================================
    // Rendering
    // ==================================================================

    private void RenderAll()
    {
        _monitors = SafeEnumerate();

        // The selected zone may have vanished under a config reload.
        if (_selected is not null)
            _selected = _controller.Config.FindZone(_selected.Name) ?? _controller.Config.Zones.FirstOrDefault();
        else
            _selected = _controller.Config.Zones.FirstOrDefault();

        SubtitleText.Text = _controller.ActiveZone is { } active
            ? $"Active zone: {active}"
            : "No zone active — the current layout matches no zone";

        StartWithWindowsCheck.IsChecked = AutoStart.IsEnabled();
        TaskbarGuardCheck.IsChecked = _taskbarGuard.IsEnabled;
        FailsafeText.Text = $"failsafe  {_controller.Config.Options.FailsafeHotkey}";

        RenderMonitorMap();
        RenderZoneRail();
        RenderEditor();
        RenderRouting();
    }

    private static IReadOnlyList<MonitorInfo> SafeEnumerate()
    {
        try { return MonitorEnumerator.Enumerate(); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// Draws the monitors in their physical arrangement, like Settings → Display. Monitors in
    /// the selected zone glow in its accent; detached ones sit at 30% opacity.
    /// </summary>
    private void RenderMonitorMap()
    {
        MonitorMap.Children.Clear();
        _mapTiles.Clear();

        if (_monitors.Count == 0)
            return;

        var canvasWidth = Math.Max(MonitorMap.ActualWidth, 600);
        var canvasHeight = MonitorMap.Height;

        // Detached monitors have no position, so park them in a row to the right of the
        // live arrangement rather than dropping them from the map entirely.
        var placed = new List<(MonitorInfo Monitor, double X, double Y, double W, double H)>();

        double detachedCursor = 0;
        foreach (var monitor in _monitors)
        {
            var mode = monitor.CurrentMode ?? monitor.PreferredMode;
            if (mode is null)
                continue;

            if (monitor.IsActive && monitor.Position is { } position)
                placed.Add((monitor, position.X, position.Y, mode.Width, mode.Height));
            else
                placed.Add((monitor, detachedCursor, 0, mode.Width, mode.Height));

            if (!monitor.IsActive)
                detachedCursor += mode.Width + 200;
        }

        if (placed.Count == 0)
            return;

        // Detached block sits clear of the live block.
        var liveRight = placed.Where(p => p.Monitor.IsActive).Select(p => p.X + p.W).DefaultIfEmpty(0).Max();
        placed = placed.Select(p => p.Monitor.IsActive ? p : (p.Monitor, p.X + liveRight + 400, p.Y, p.W, p.H)).ToList();

        var minX = placed.Min(p => p.X);
        var minY = placed.Min(p => p.Y);
        var maxX = placed.Max(p => p.X + p.W);
        var maxY = placed.Max(p => p.Y + p.H);

        var scale = Math.Min((canvasWidth - 20) / Math.Max(maxX - minX, 1), (canvasHeight - 40) / Math.Max(maxY - minY, 1));
        var offsetX = (canvasWidth - (maxX - minX) * scale) / 2;
        var offsetY = 10;
        _mapScale = scale;

        var accent = ZoneController.ColorFor(_selected?.Accent);

        foreach (var (monitor, x, y, w, h) in placed)
        {
            var inZone = _selected?.Monitors.Any(a =>
                string.Equals(_controller.Config.DevicePathForAlias(a), monitor.DevicePath,
                    StringComparison.OrdinalIgnoreCase)) ?? false;

            var alias = _controller.Config.AliasForDevicePath(monitor.DevicePath);
            var isPrimaryInZone = string.Equals(_selected?.Primary, alias, StringComparison.OrdinalIgnoreCase);

            var border = new Border
            {
                Width = Math.Max((w * scale) - 6, 40),
                Height = Math.Max((h * scale) - 6, 30),
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource(inZone ? "Surface2Brush" : "SurfaceBrush"),
                BorderBrush = inZone ? new SolidColorBrush(accent) : (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(inZone ? 2 : 1),
                Opacity = monitor.IsActive ? 1.0 : 0.3,
            };

            // Glow budget: only the zone's primary gets an effect.
            if (inZone && isPrimaryInZone)
            {
                border.Effect = new DropShadowEffect
                {
                    Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.35,
                };
            }

            var stack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            stack.Children.Add(new TextBlock
            {
                Text = isPrimaryInZone ? $"{alias}  ★" : alias,
                Style = (Style)FindResource("BodyText"),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = inZone ? new SolidColorBrush(accent) : (Brush)FindResource("TextBrush"),
            });

            stack.Children.Add(new TextBlock
            {
                Text = monitor.FriendlyName,
                Style = (Style)FindResource("DimText"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            stack.Children.Add(new TextBlock
            {
                Text = monitor.IsActive
                    ? monitor.CurrentMode?.ToString() ?? ""
                    : "detached",
                Style = (Style)FindResource("MonoText"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            border.Child = stack;

            // Monitors belonging to the selected zone can be dragged to rearrange the desk.
            if (inZone)
            {
                border.Cursor = Cursors.SizeAll;
                border.ToolTip = "Drag to rearrange";
                _mapTiles[border] = monitor;

                border.MouseLeftButtonDown += OnTileDragStart;
                border.MouseMove += OnTileDragMove;
                border.MouseLeftButtonUp += OnTileDragEnd;
            }

            Canvas.SetLeft(border, offsetX + (x - minX) * scale);
            Canvas.SetTop(border, offsetY + (y - minY) * scale);
            MonitorMap.Children.Add(border);
        }
    }

    // ==================================================================
    // Monitor map: drag to rearrange
    // ==================================================================

    private void OnTileDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border tile || !_mapTiles.ContainsKey(tile))
            return;

        _dragTile = tile;
        _dragStartPoint = e.GetPosition(MonitorMap);
        _dragOriginLeft = Canvas.GetLeft(tile);
        _dragOriginTop = Canvas.GetTop(tile);

        tile.CaptureMouse();
        Panel.SetZIndex(tile, 10);
        e.Handled = true;
    }

    private void OnTileDragMove(object sender, MouseEventArgs e)
    {
        if (_dragTile is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(MonitorMap);

        Canvas.SetLeft(_dragTile, _dragOriginLeft + (current.X - _dragStartPoint.X));
        Canvas.SetTop(_dragTile, _dragOriginTop + (current.Y - _dragStartPoint.Y));
    }

    private void OnTileDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (_dragTile is null)
            return;

        _dragTile.ReleaseMouseCapture();
        Panel.SetZIndex(_dragTile, 0);

        var dropped = _dragTile;
        _dragTile = null;

        // A click without movement is a selection, not a rearrange.
        var moved = Math.Abs(Canvas.GetLeft(dropped) - _dragOriginLeft) > 4 ||
                    Math.Abs(Canvas.GetTop(dropped) - _dragOriginTop) > 4;

        if (moved)
            CommitLayoutFromMap();
        else
            RenderMonitorMap();
    }

    /// <summary>
    /// Turns the dragged tile positions into a valid desktop arrangement and stores it on the
    /// zone.
    /// <para>
    /// The map is a sketch, not the truth: Windows rejects a topology whose rectangles leave
    /// gaps or overlap, so the drop order is taken as intent and the monitors are then packed
    /// edge-to-edge left to right. Vertical offset is kept but clamped so neighbours always
    /// share some edge, which keeps the desktop connected.
    /// </para>
    /// </summary>
    private void CommitLayoutFromMap()
    {
        if (_selected is null || _mapTiles.Count == 0)
            return;

        var ordered = _mapTiles
            .Select(kv => new
            {
                Monitor = kv.Value,
                CentreX = Canvas.GetLeft(kv.Key) + kv.Key.Width / 2,
                Top = Canvas.GetTop(kv.Key),
            })
            .OrderBy(t => t.CentreX)
            .ToList();

        var layout = new Dictionary<string, MonitorPlacement>(StringComparer.OrdinalIgnoreCase);

        var cursorX = 0;
        int? previousTop = null;
        var previousHeight = 0;

        foreach (var tile in ordered)
        {
            var mode = tile.Monitor.CurrentMode ?? tile.Monitor.PreferredMode;
            if (mode is null)
                continue;

            var width = (int)mode.Width;
            var height = (int)mode.Height;

            // Convert the tile's on-screen top back into device pixels.
            var y = (int)Math.Round(tile.Top / Math.Max(_mapScale, 0.0001));

            if (previousTop is { } prevTop)
            {
                // Overlap the neighbour vertically by at least a quarter of the shorter panel,
                // so the desktop stays contiguous no matter how sloppy the drag was.
                var minOverlap = Math.Min(height, previousHeight) / 4;
                var lowest = prevTop + previousHeight - minOverlap;
                var highest = prevTop - height + minOverlap;
                y = Math.Clamp(y, highest, lowest);
            }
            else
            {
                y = 0;
            }

            var alias = _controller.Config.AliasForDevicePath(tile.Monitor.DevicePath);
            layout[alias] = new MonitorPlacement { X = cursorX, Y = y };

            cursorX += width;
            previousTop = y;
            previousHeight = height;
        }

        if (layout.Count == 0)
        {
            RenderMonitorMap();
            return;
        }

        // Normalise so the primary is the origin — that is what primary *means* in CCD.
        if (_selected.Primary is { } primaryAlias && layout.TryGetValue(primaryAlias, out var anchor))
        {
            foreach (var placement in layout.Values)
            {
                placement.X -= anchor.X;
                placement.Y -= anchor.Y;
            }
        }

        _selected.Layout = layout;
        _controller.SaveConfig();

        // Apply straight away when this zone is live, so the drag is visibly the truth.
        if (string.Equals(_controller.ActiveZone, _selected.Name, StringComparison.OrdinalIgnoreCase))
            _controller.ApplyZone(_selected.Name, showToast: false);
        else
            RenderAll();
    }

    private void RenderZoneRail()
    {
        ZoneRail.Children.Clear();

        foreach (var zone in _controller.Config.Zones)
        {
            var isSelected = ReferenceEquals(zone, _selected) ||
                             string.Equals(zone.Name, _selected?.Name, StringComparison.OrdinalIgnoreCase);
            var isActive = string.Equals(zone.Name, _controller.ActiveZone, StringComparison.OrdinalIgnoreCase);
            var accent = ZoneController.ColorFor(zone.Accent);

            var card = new Border
            {
                Style = (Style)FindResource("Card"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
                Cursor = Cursors.Hand,
                BorderBrush = isSelected || isActive
                    ? new SolidColorBrush(accent)
                    : (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(isSelected || isActive ? 2 : 1),
            };

            if (isActive)
            {
                card.Effect = new DropShadowEffect
                {
                    Color = accent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.35,
                };
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(dot, 0);
            grid.Children.Add(dot);

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = zone.Name,
                Style = (Style)FindResource("BodyText"),
                FontWeight = FontWeights.SemiBold,
            });
            text.Children.Add(new TextBlock
            {
                Text = $"{zone.Monitors.Count} monitor{(zone.Monitors.Count == 1 ? "" : "s")}"
                       + (isActive ? "  ·  active" : ""),
                Style = (Style)FindResource("DimText"),
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (HotkeyBinding.TryParse(zone.Hotkey) is { } binding)
            {
                var chip = new Border
                {
                    Style = (Style)FindResource("HotkeyChip"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = binding.ToChipGlyphs(),
                        Style = (Style)FindResource("HotkeyChipText"),
                    },
                };
                Grid.SetColumn(chip, 2);
                grid.Children.Add(chip);
            }

            card.Child = grid;

            var captured = zone;
            card.MouseLeftButtonUp += (_, _) =>
            {
                _selected = captured;
                RenderAll();
            };

            ZoneRail.Children.Add(card);
        }
    }

    private void RenderEditor()
    {
        EditorPanel.Children.Clear();

        if (_selected is null)
        {
            EditorPanel.Children.Add(new TextBlock
            {
                Text = "No zones defined yet. Create one to get started.",
                Style = (Style)FindResource("DimText"),
            });
            return;
        }

        var zone = _selected;

        // ---- Name ----
        EditorPanel.Children.Add(Label("ZONE NAME"));
        var nameBox = new TextBox
        {
            Text = zone.Name,
            Style = (Style)FindResource("ZoneTextBox"),
            Margin = new Thickness(0, 0, 0, 16),
            ToolTip = "Saves when you press Enter or click away",
        };

        // Enter commits, so the name does not depend on noticing that focus loss saves it.
        nameBox.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
                Keyboard.ClearFocus();
        };
        nameBox.LostFocus += (_, _) =>
        {
            var trimmed = nameBox.Text.Trim();
            if (trimmed.Length == 0 || trimmed == zone.Name)
                return;

            var oldName = zone.Name;
            zone.Name = trimmed;

            // Wake-routing rules reference zones by name. Without this, renaming a zone
            // silently orphans every rule pointing at it and routing fails at wake time —
            // the worst moment to discover a dangling reference.
            var rules = _controller.Config.InputRouting?.Rules;
            if (rules is not null)
            {
                foreach (var rule in rules.Where(r =>
                             string.Equals(r.Zone, oldName, StringComparison.OrdinalIgnoreCase)))
                {
                    rule.Zone = trimmed;
                }
            }

            Save();
        };
        EditorPanel.Children.Add(nameBox);

        // ---- Monitors and primary ----
        EditorPanel.Children.Add(Label("MONITORS   ·   the dot marks the primary"));

        foreach (var (devicePath, alias) in _controller.Config.Monitors)
        {
            var included = zone.Monitors.Contains(alias.Alias, StringComparer.OrdinalIgnoreCase);
            var attached = _monitors.Any(m =>
                string.Equals(m.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));

            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var check = new CheckBox
            {
                Content = attached ? alias.Alias : $"{alias.Alias}   (not detected)",
                IsChecked = included,
                Style = (Style)FindResource("ZoneCheckBox"),
            };

            var aliasName = alias.Alias;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true)
                {
                    if (!zone.Monitors.Contains(aliasName, StringComparer.OrdinalIgnoreCase))
                        zone.Monitors.Add(aliasName);
                }
                else
                {
                    // Engine rejects empty zones independently, but the UI should never
                    // let the user build one in the first place (spec §13.7).
                    if (zone.Monitors.Count <= 1)
                    {
                        check.IsChecked = true;
                        MessageBox.Show(this,
                            "A zone must contain at least one monitor.",
                            "ZoneTitan", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }

                    zone.Monitors.RemoveAll(m => string.Equals(m, aliasName, StringComparison.OrdinalIgnoreCase));

                    if (string.Equals(zone.Primary, aliasName, StringComparison.OrdinalIgnoreCase))
                        zone.Primary = zone.Monitors.FirstOrDefault();
                }

                Save();
            };

            Grid.SetColumn(check, 0);
            row.Children.Add(check);

            var primary = new RadioButton
            {
                GroupName = "primary_" + zone.Name,
                IsChecked = string.Equals(zone.Primary, aliasName, StringComparison.OrdinalIgnoreCase),
                IsEnabled = included,
                Style = (Style)FindResource("ZoneRadioButton"),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Make this the primary display (placed at 0,0)",
            };
            primary.Checked += (_, _) =>
            {
                zone.Primary = aliasName;
                Save();
            };
            Grid.SetColumn(primary, 1);
            row.Children.Add(primary);

            EditorPanel.Children.Add(row);
        }

        // ---- HDR ----
        var hdrCapable = _controller.Config.Monitors
            .Where(kv => zone.Monitors.Contains(kv.Value.Alias, StringComparer.OrdinalIgnoreCase))
            .Where(kv => _monitors.Any(m =>
                string.Equals(m.DevicePath, kv.Key, StringComparison.OrdinalIgnoreCase) && m.Hdr.Supported))
            .ToList();

        if (hdrCapable.Count > 0)
        {
            EditorPanel.Children.Add(Label("HDR", 16));

            foreach (var (_, alias) in hdrCapable)
            {
                var aliasName = alias.Alias;
                var current = zone.Hdr is not null &&
                              zone.Hdr.TryGetValue(aliasName, out var want) && want;

                var toggle = new CheckBox
                {
                    Content = $"{aliasName} — HDR on in this zone",
                    IsChecked = current,
                    Style = (Style)FindResource("ZoneCheckBox"),
                };

                toggle.Click += (_, _) =>
                {
                    zone.Hdr ??= [];
                    zone.Hdr[aliasName] = toggle.IsChecked == true;
                    Save();
                };

                EditorPanel.Children.Add(toggle);
            }
        }

        // ---- Modes ----
        var pinned = zone.Modes?.Where(kv =>
            zone.Monitors.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        if (pinned is { Count: > 0 })
        {
            EditorPanel.Children.Add(Label("PINNED MODES", 16));

            foreach (var (alias, mode) in pinned)
            {
                EditorPanel.Children.Add(new TextBlock
                {
                    Text = $"{alias}   {mode}",
                    Style = (Style)FindResource("MonoText"),
                    Margin = new Thickness(0, 0, 0, 2),
                });
            }

            EditorPanel.Children.Add(new TextBlock
            {
                Text = "Windows brings a re-attached display up at 60 Hz unless a mode is pinned.",
                Style = (Style)FindResource("DimText"),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        // ---- Hotkey and accent ----
        EditorPanel.Children.Add(Label("HOTKEY", 16));

        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal };

        var hotkeyButton = new Button
        {
            Content = string.IsNullOrWhiteSpace(zone.Hotkey) ? "Click, then press keys…" : zone.Hotkey,
            Style = (Style)FindResource("QuietButton"),
            MinWidth = 200,
        };
        hotkeyButton.Click += (_, _) => BeginHotkeyCapture(hotkeyButton, combo =>
        {
            zone.Hotkey = combo;
            Save();
        });
        hotkeyRow.Children.Add(hotkeyButton);

        var clearHotkey = new Button
        {
            Content = "Clear",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        clearHotkey.Click += (_, _) =>
        {
            zone.Hotkey = null;
            Save();
        };
        hotkeyRow.Children.Add(clearHotkey);

        EditorPanel.Children.Add(hotkeyRow);

        EditorPanel.Children.Add(Label("ACCENT", 16));

        var accentRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var swatch in new[] { "#FF2E97", "#38D4FF", "#DCE6FF", "#3DDC97", "#FFB454", "#FF5470" })
        {
            var colour = ZoneController.ColorFor(swatch);
            var isCurrent = string.Equals(zone.Accent, swatch, StringComparison.OrdinalIgnoreCase);

            var button = new Border
            {
                Width = 30, Height = 30,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(colour),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                BorderBrush = (Brush)FindResource("TextBrush"),
                BorderThickness = new Thickness(isCurrent ? 2 : 0),
            };

            var value = swatch;
            button.MouseLeftButtonUp += (_, _) =>
            {
                zone.Accent = value;
                Save();
            };

            accentRow.Children.Add(button);
        }

        EditorPanel.Children.Add(accentRow);

        // ---- Wake routing, scoped to this zone ----
        // Lives here rather than in a separate tab because the zone is already chosen: you
        // are editing it. That removes the naming box, the zone dropdown and the dialog.
        EditorPanel.Children.Add(Label("WAKE INTO THIS ZONE WITH", 16));

        var routing = _controller.Config.InputRouting ??= new InputRoutingOptions();
        var mine = routing.Rules
            .Where(r => string.Equals(r.Zone, zone.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (mine.Count == 0)
        {
            EditorPanel.Children.Add(new TextBlock
            {
                Text = "No devices yet. Waking with a device mapped here activates this zone.",
                Style = (Style)FindResource("DimText"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
        }

        foreach (var rule in mine)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = rule.Label ?? rule.Device,
                Style = (Style)FindResource("BodyText"),
            });
            info.Children.Add(new TextBlock
            {
                Text = rule.Device,
                Style = (Style)FindResource("MonoText"),
            });
            Grid.SetColumn(info, 0);
            row.Children.Add(info);

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("DangerButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = rule;
            remove.Click += (_, _) =>
            {
                routing.Rules.Remove(captured);
                Save();
            };
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            EditorPanel.Children.Add(row);
        }

        var learnStatus = new TextBlock
        {
            Style = (Style)FindResource("MonoText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var learnButton = new Button
        {
            Content = "+  Add a device…",
            Style = (Style)FindResource("QuietButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 8, 0, 0),
            IsEnabled = _wakeRouter is not null,
            ToolTip = "Click, then press a key or move the mouse you want to wake into this zone",
        };

        var targetZoneName = zone.Name;

        learnButton.Click += (_, _) =>
        {
            if (_wakeRouter is null)
                return;

            learnButton.Content = "Now press a key or move that mouse…";
            learnButton.IsEnabled = false;
            learnStatus.Text = "listening…";

            _wakeRouter.CaptureNextDevice((deviceName, kind) =>
                Dispatcher.Invoke(() =>
                {
                    var fragment = ExtractVidPid(deviceName);
                    if (fragment is null)
                    {
                        learnButton.Content = "+  Add a device…";
                        learnButton.IsEnabled = true;
                        learnStatus.Text = $"could not read an ID from {deviceName}";
                        return;
                    }

                    // Replace any existing rule for this device — mapping it again is a
                    // correction, not a second rule.
                    routing.Rules.RemoveAll(r =>
                        string.Equals(r.Device, fragment, StringComparison.OrdinalIgnoreCase));

                    routing.Rules.Add(new InputRule
                    {
                        Device = fragment,
                        Match = "vidPid",
                        Zone = targetZoneName,
                        Label = $"{kind} · {fragment}",
                    });

                    Save();

                    // Re-render replaced the controls, so report through the tray instead.
                    Log.Info($"Wake rule added: {kind} {fragment} → {targetZoneName}");
                }));
        };

        EditorPanel.Children.Add(learnButton);
        EditorPanel.Children.Add(learnStatus);

        // ---- Actions ----
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 24, 0, 0) };

        var apply = new Button
        {
            Content = "Activate",
            Style = (Style)FindResource("AccentButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        apply.Click += (_, _) => _controller.ApplyZone(zone.Name);
        actions.Children.Add(apply);

        var test = new Button
        {
            Content = "Test (auto-revert)",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        test.Click += (_, _) => BeginTest(zone.Name);
        actions.Children.Add(test);

        var duplicate = new Button
        {
            Content = "Duplicate",
            Style = (Style)FindResource("QuietButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        duplicate.Click += (_, _) => DuplicateZone(zone);
        actions.Children.Add(duplicate);

        var delete = new Button { Content = "Delete", Style = (Style)FindResource("DangerButton") };
        delete.Click += (_, _) => DeleteZone(zone);
        actions.Children.Add(delete);

        EditorPanel.Children.Add(actions);
    }

    private void RenderRouting()
    {
        RoutingContent.Children.Clear();

        var options = _controller.Config.InputRouting ??= new InputRoutingOptions();

        RoutingContent.Children.Add(new TextBlock
        {
            Text = "When the machine wakes, the first mouse or keyboard you touch decides the zone.",
            Style = (Style)FindResource("BodyText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        RoutingContent.Children.Add(new TextBlock
        {
            Text = "Windows does not record which device woke the machine on this hardware, so "
                   + "ZoneTitan routes on first input instead. Touch nothing and the zone is left alone.",
            Style = (Style)FindResource("DimText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        var enabled = new CheckBox
        {
            Content = "Wake routing enabled",
            IsChecked = options.Enabled,
            Style = (Style)FindResource("ZoneCheckBox"),
            Margin = new Thickness(0, 0, 0, 16),
        };
        enabled.Click += (_, _) =>
        {
            options.Enabled = enabled.IsChecked == true;
            Save();
        };
        RoutingContent.Children.Add(enabled);

        RoutingContent.Children.Add(Label("RULES"));

        if (options.Rules.Count == 0)
        {
            RoutingContent.Children.Add(new TextBlock
            {
                Text = "No rules yet. Add one from a connected device below.",
                Style = (Style)FindResource("DimText"),
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        foreach (var rule in options.Rules.ToList())
        {
            var row = new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 6),
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = rule.Label ?? rule.Device,
                Style = (Style)FindResource("BodyText"),
            });
            info.Children.Add(new TextBlock
            {
                Text = rule.Device,
                Style = (Style)FindResource("MonoText"),
            });

            // A rule pointing at a zone that no longer exists would fail at wake time, which
            // is the worst place to find out. Say so here instead.
            var zoneExists = _controller.Config.Zones.Any(z =>
                string.Equals(z.Name, rule.Zone, StringComparison.OrdinalIgnoreCase));

            if (!zoneExists)
            {
                info.Children.Add(new TextBlock
                {
                    Text = $"⚠  no zone named '{rule.Zone}' — pick one below",
                    Style = (Style)FindResource("DimText"),
                    Foreground = (Brush)FindResource("DangerBrush"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }

            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var zonePicker = new ComboBox
            {
                Width = 130,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                ItemsSource = _controller.Config.Zones.Select(z => z.Name).ToList(),
                SelectedItem = rule.Zone,
            };
            zonePicker.SelectionChanged += (_, _) =>
            {
                if (zonePicker.SelectedItem is string zoneName)
                {
                    rule.Zone = zoneName;
                    Save();
                }
            };
            Grid.SetColumn(zonePicker, 1);
            grid.Children.Add(zonePicker);

            var remove = new Button
            {
                Content = "Remove",
                Style = (Style)FindResource("DangerButton"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var captured = rule;
            remove.Click += (_, _) =>
            {
                options.Rules.Remove(captured);
                Save();
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            row.Child = grid;
            RoutingContent.Children.Add(row);
        }

        RoutingContent.Children.Add(new TextBlock
        {
            Text = "Add devices from the Zones tab — pick a zone, then \"Add a device…\" under "
                   + "WAKE INTO THIS ZONE WITH. The zone is already chosen there, so mapping a "
                   + "device is one click and one keypress.",
            Style = (Style)FindResource("DimText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private static string? ExtractVidPid(string deviceName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            deviceName, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(&MI_[0-9A-Fa-f]{2})?");

        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Offers to map a just-identified device to a zone, replacing any existing rule for it so
    /// a mis-labelled guess gets corrected rather than duplicated.
    /// </summary>
    private void PromptToAddRule(string fragment)
    {
        var options = _controller.Config.InputRouting ??= new InputRoutingOptions();

        if (_controller.Config.Zones.Count == 0)
            return;

        var dialog = new Window
        {
            Title = "ZoneTitan",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)FindResource("BgBrush"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "Which zone should this device wake into?",
            Style = (Style)FindResource("BodyText"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new TextBlock
        {
            Text = fragment,
            Style = (Style)FindResource("MonoText"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var labelBox = new TextBox
        {
            Text = "",
            Style = (Style)FindResource("ZoneTextBox"),
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "A name you will recognise, e.g. 'Wired gaming keyboard'",
        };
        panel.Children.Add(new TextBlock
        {
            Text = "NAME IT",
            Style = (Style)FindResource("SectionLabel"),
        });
        panel.Children.Add(labelBox);

        panel.Children.Add(new TextBlock
        {
            Text = "ZONE",
            Style = (Style)FindResource("SectionLabel"),
        });

        var picker = new ComboBox
        {
            ItemsSource = _controller.Config.Zones.Select(z => z.Name).ToList(),
            SelectedIndex = 0,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(picker);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };

        var save = new Button { Content = "Save rule", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            if (picker.SelectedItem is not string zoneName)
                return;

            // Replace rather than duplicate — this flow exists to correct bad guesses.
            options.Rules.RemoveAll(r =>
                string.Equals(r.Device, fragment, StringComparison.OrdinalIgnoreCase));

            options.Rules.Add(new InputRule
            {
                Device = fragment,
                Match = "vidPid",
                Zone = zoneName,
                Label = string.IsNullOrWhiteSpace(labelBox.Text) ? fragment : labelBox.Text.Trim(),
            });

            Save();
            dialog.Close();
        };
        buttons.Children.Add(save);

        var cancel = new Button { Content = "Cancel", Style = (Style)FindResource("QuietButton") };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);

        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
    }

    private TextBlock Label(string text, double topMargin = 0) => new()
    {
        Text = text,
        Style = (Style)FindResource("SectionLabel"),
        Margin = new Thickness(0, topMargin, 0, 8),
    };

    // ==================================================================
    // Zone actions
    // ==================================================================

    /// <summary>
    /// Commits the config immediately. There is no Save button by design — every edit is
    /// written straight to disk — so this flashes a confirmation, because silent persistence
    /// is indistinguishable from a control that did nothing.
    /// </summary>
    private void Save()
    {
        _controller.SaveConfig();
        RenderAll();
        FlashSaved();
    }

    private void FlashSaved()
    {
        SavedIndicator.Opacity = 1;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(900),
        };

        SavedIndicator.BeginAnimation(OpacityProperty, fade);
    }

    private void DuplicateZone(ZoneDefinition zone)
    {
        var copy = new ZoneDefinition
        {
            Name = MakeUniqueName(zone.Name + " copy"),
            Monitors = [.. zone.Monitors],
            Primary = zone.Primary,
            Accent = zone.Accent,
            Hdr = zone.Hdr is null ? null : new Dictionary<string, bool>(zone.Hdr),
            Modes = zone.Modes is null ? null : new Dictionary<string, DesiredMode>(zone.Modes),
        };

        _controller.Config.Zones.Add(copy);
        _selected = copy;
        Save();
    }

    private void DeleteZone(ZoneDefinition zone)
    {
        var confirm = MessageBox.Show(this,
            $"Delete the zone '{zone.Name}'?",
            "ZoneTitan", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _controller.Config.Zones.Remove(zone);
        _selected = _controller.Config.Zones.FirstOrDefault();
        Save();
    }

    private string MakeUniqueName(string candidate)
    {
        var name = candidate;
        var suffix = 2;

        while (_controller.Config.Zones.Any(z => string.Equals(z.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{candidate} {suffix++}";

        return name;
    }

    private void OnAddZone(object sender, RoutedEventArgs e)
    {
        var firstAlias = _controller.Config.Monitors.Values.FirstOrDefault()?.Alias;

        var zone = new ZoneDefinition
        {
            Name = MakeUniqueName("new zone"),
            Monitors = firstAlias is null ? [] : [firstAlias],
            Primary = firstAlias,
            Accent = "#DCE6FF",
        };

        _controller.Config.Zones.Add(zone);
        _selected = zone;
        Save();
    }

    // ==================================================================
    // Test with auto-revert — the Windows resolution-change pattern
    // ==================================================================

    private void BeginTest(string zoneName)
    {
        _testRevertTo = _controller.ActiveZone;
        _controller.ApplyZone(zoneName);

        _testSecondsLeft = 10;

        var dialog = new Window
        {
            Title = "ZoneTitan",
            Width = 380,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Background = (Brush)FindResource("BgBrush"),
        };

        var panel = new StackPanel { Margin = new Thickness(20) };

        var message = new TextBlock
        {
            Text = $"Keep '{zoneName}'? Reverting in {_testSecondsLeft}s.",
            Style = (Style)FindResource("BodyText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(message);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };

        var keep = new Button { Content = "Keep", Style = (Style)FindResource("AccentButton"), Margin = new Thickness(0, 0, 8, 0) };
        keep.Click += (_, _) =>
        {
            StopTest();
            dialog.Close();
        };
        buttons.Children.Add(keep);

        var revert = new Button { Content = "Revert now", Style = (Style)FindResource("QuietButton") };
        revert.Click += (_, _) =>
        {
            StopTest();
            RevertTest();
            dialog.Close();
        };
        buttons.Children.Add(revert);

        panel.Children.Add(buttons);
        dialog.Content = panel;

        _testTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _testTimer.Tick += (_, _) =>
        {
            _testSecondsLeft--;
            message.Text = $"Keep '{zoneName}'? Reverting in {_testSecondsLeft}s.";

            if (_testSecondsLeft > 0)
                return;

            StopTest();
            RevertTest();

            try { dialog.Close(); }
            catch (InvalidOperationException) { /* already closed */ }
        };
        _testTimer.Start();

        dialog.Closed += (_, _) => StopTest();
        dialog.ShowDialog();
    }

    private void StopTest()
    {
        _testTimer?.Stop();
        _testTimer = null;
    }

    private void RevertTest()
    {
        if (_testRevertTo is { } previous)
            _controller.ApplyZone(previous);
        else
            _controller.RunFailsafe();
    }

    // ==================================================================
    // Hotkey capture
    // ==================================================================

    private void BeginHotkeyCapture(Button button, Action<string> onCaptured)
    {
        _capturingHotkeyButton = button;
        _hotkeyCaptured = onCaptured;
        button.Content = "Press a combination…";
        Keyboard.Focus(button);
    }

    private void OnPreviewKeyDownForCapture(object sender, KeyEventArgs e)
    {
        if (_capturingHotkeyButton is null || _hotkeyCaptured is null)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Ignore the modifiers themselves — wait for the actual key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        if (key == Key.Escape)
        {
            CancelCapture();
            return;
        }

        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            // A bare key would swallow that key system-wide. Refuse it.
            _capturingHotkeyButton.Content = "Needs a modifier — try Ctrl+Alt+…";
            return;
        }

        parts.Add(key.ToString());
        var combination = string.Join("+", parts);

        var callback = _hotkeyCaptured;
        CancelCapture();
        callback(combination);
    }

    private void CancelCapture()
    {
        _capturingHotkeyButton = null;
        _hotkeyCaptured = null;
        RenderEditor();
    }

    // ==================================================================
    // Chrome handlers
    // ==================================================================

    private void OnShowZonesTab(object sender, RoutedEventArgs e)
    {
        ZonesPanel.Visibility = Visibility.Visible;
        RoutingPanel.Visibility = Visibility.Collapsed;
    }

    private void OnShowRoutingTab(object sender, RoutedEventArgs e)
    {
        ZonesPanel.Visibility = Visibility.Collapsed;
        RoutingPanel.Visibility = Visibility.Visible;
        RenderRouting();
    }

    private void OnIdentify(object sender, RoutedEventArgs e) =>
        IdentifyOverlay.ShowForAllMonitors(_controller.Config);

    private void OnFailsafe(object sender, RoutedEventArgs e) => _controller.RunFailsafe();

    private void OnToggleStartWithWindows(object sender, RoutedEventArgs e)
    {
        var wanted = StartWithWindowsCheck.IsChecked == true;

        if (!AutoStart.SetEnabled(wanted))
        {
            StartWithWindowsCheck.IsChecked = !wanted;
            MessageBox.Show(this, "Could not change the start-with-Windows setting.",
                "ZoneTitan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _controller.Config.Options.StartWithWindows = wanted;
        _controller.SaveConfig();
    }

    private void OnToggleTaskbarGuard(object sender, RoutedEventArgs e)
    {
        var wanted = TaskbarGuardCheck.IsChecked == true;
        _taskbarGuard.IsEnabled = wanted;
        _controller.Config.Options.EnforceTaskbarAutoHide = wanted;
        _controller.SaveConfig();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenFolder(ConfigStore.LogDirectory);

    private void OnOpenConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ConfigStore.ConfigPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the config file:\n\n{ex.Message}",
                "ZoneTitan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open the folder:\n\n{ex.Message}",
                "ZoneTitan", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
