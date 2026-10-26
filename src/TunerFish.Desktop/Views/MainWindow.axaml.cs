using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TunerFish.Services;
using TunerFish.ViewModels;

namespace TunerFish.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _timer;
    private readonly List<(TrackColumnSpec Spec, DataGridColumn Column, CheckBox Toggle)> _columns = new();
    private Flyout? _columnsFlyout;

    // Queue drag-to-reorder
    private const double DragThreshold = 4;
    private readonly DispatcherTimer _dragScrollTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private QueueItemVm? _dragItem;
    private Point _dragStart, _dragLast;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();

        _vm = new MainViewModel(new SettingsStore(), DecodeImage, appName: AppInfo.Name, log: AppLogFile.Write);
        Program.OnCrash = _vm.NoteCrash;
        DataContext = _vm;

        ApplyTheme();
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Theme)) ApplyTheme(); };

        RestoreWindowLayout();
        BuildTrackColumns();

        _vm.ArtistJumpRequested += ScrollArtistToTop;

        AlbumList.Tapped += OnAlbumTapped;
        AlbumList.DoubleTapped += OnAlbumDoubleTapped;
        TrackGrid.DoubleTapped += OnTrackDoubleTapped;
        TrackGrid.ColumnReordered += (_, _) => SaveColumns();
        TrackGrid.AddHandler(PointerPressedEvent, OnTrackGridPointerPressed, RoutingStrategies.Tunnel, true);

        QueueList.DoubleTapped += OnQueueDoubleTapped;
        QueueList.KeyDown += OnQueueKeyDown;
        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Bubble, true);
        QueueList.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Bubble, true);
        QueueList.AddHandler(PointerReleasedEvent, OnQueuePointerReleased, RoutingStrategies.Bubble, true);
        QueueList.PointerCaptureLost += (_, _) => EndQueueDrag();
        _dragScrollTimer.Tick += (_, _) => DragAutoScroll();

        // Don't let the position refresh fight the user while they drag the seek bar.
        SeekSlider.AddHandler(PointerPressedEvent, OnSeekPressed, RoutingStrategies.Tunnel, true);
        SeekSlider.AddHandler(PointerReleasedEvent, OnSeekReleased, RoutingStrategies.Tunnel, true);

        OptionsMenuItem.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(MenuButton);
        if (FlyoutBase.GetAttachedFlyout(MenuButton) is { } options)
            options.Opened += async (_, _) => await _vm.CheckServerAddressesAsync();
        ColumnsMenuItem.Click += (_, _) => _columnsFlyout?.ShowAt(MenuButton);
        AboutMenuItem.Header = $"About {AppInfo.Name}…";
        AboutMenuItem.Click += async (_, _) => await new AboutWindow(_vm.PlayerVersion).ShowDialog(this);
        LogFolderMenuItem.Click += async (_, _) =>
        {
            Directory.CreateDirectory(AppLogFile.Directory);
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(AppLogFile.Directory));
        };

        KeyDown += OnWindowKeyDown;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _vm.Tick();
        _timer.Start();

        Opened += async (_, _) => await _vm.StartAsync();
        Closing += (_, _) => SaveWindowLayout();
        Closed += (_, _) => { _timer.Stop(); _dragScrollTimer.Stop(); _vm.Dispose(); };
    }

    private void ApplyTheme()
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = _vm.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default, // follows the desktop
        };
    }

    private static object? DecodeImage(string file, int height)
    {
        using var stream = File.OpenRead(file);
        return Bitmap.DecodeToHeight(stream, height);
    }

    // ------------------------------------------------------------ track columns

    private void BuildTrackColumns()
    {
        var saved = new Dictionary<string, ColumnSetting>();
        foreach (var c in _vm.Settings.Columns) saved[c.Key] = c; // last one wins if duplicated

        var menu = new StackPanel { Spacing = 2, Margin = new Thickness(4) };
        menu.Children.Add(new TextBlock { Text = "Show columns", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });

        foreach (var spec in TrackColumns.All)
        {
            saved.TryGetValue(spec.Key, out var s);
            var column = new DataGridTextColumn
            {
                Header = spec.Header,
                Binding = new Binding(spec.BindingPath),
                IsReadOnly = true,
                Width = new DataGridLength(s is { Width: >= 20 } ? s.Width : spec.DefaultWidth),
                IsVisible = s?.Visible ?? spec.DefaultVisible,
            };
            TrackGrid.Columns.Add(column);

            var toggle = new CheckBox { Content = spec.MenuLabel, IsChecked = column.IsVisible };
            toggle.Click += (_, _) =>
            {
                column.IsVisible = toggle.IsChecked == true;
                SaveColumns();
            };
            menu.Children.Add(toggle);
            _columns.Add((spec, column, toggle));
        }

        var reset = new Button { Content = "Reset to defaults", Margin = new Thickness(0, 8, 0, 0) };
        reset.Click += (_, _) => ResetColumns();
        menu.Children.Add(reset);

        _columnsFlyout = new Flyout { Content = menu, Placement = PlacementMode.BottomEdgeAlignedRight };

        // Restore the saved left-to-right order.
        var order = _columns
            .Select((c, i) => (c.Column, Index: saved.TryGetValue(c.Spec.Key, out var s) && s.DisplayIndex >= 0 ? s.DisplayIndex : 1000 + i))
            .OrderBy(x => x.Index)
            .ToList();
        try
        {
            for (var i = 0; i < order.Count; i++) order[i].Column.DisplayIndex = i;
        }
        catch
        {
            // If the saved order is somehow invalid, keep the default order.
        }
    }

    private void ResetColumns()
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            var (spec, column, toggle) = _columns[i];
            column.IsVisible = spec.DefaultVisible;
            column.Width = new DataGridLength(spec.DefaultWidth);
            toggle.IsChecked = spec.DefaultVisible;
        }
        try
        {
            for (var i = 0; i < _columns.Count; i++) _columns[i].Column.DisplayIndex = i;
        }
        catch { /* ignore */ }
        SaveColumns();
    }

    private void SaveColumns()
    {
        _vm.Settings.Columns = _columns.Select(c => new ColumnSetting
        {
            Key = c.Spec.Key,
            Visible = c.Column.IsVisible,
            Width = c.Column.IsVisible && c.Column.ActualWidth >= 20 ? c.Column.ActualWidth
                  : c.Column.Width.IsAbsolute ? c.Column.Width.Value
                  : c.Spec.DefaultWidth,
            DisplayIndex = c.Column.DisplayIndex,
        }).ToList();
        _vm.SaveSettings();
    }

    // Right-click a column header to get the same column chooser as the toolbar button;
    // right-click a track to play or queue it.
    private void OnTrackGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_columnsFlyout is null) return;
        if (!e.GetCurrentPoint(TrackGrid).Properties.IsRightButtonPressed) return;
        if (e.Source is not Visual v) return;

        var onHeader = v.GetSelfAndVisualAncestors().Any(a => a.GetType().Name == "DataGridColumnHeader");
        if (onHeader)
        {
            _columnsFlyout.ShowAt(TrackGrid, true);
            e.Handled = true;
            return;
        }

        if (ItemUnder<TrackVm>(e.Source, x => x is DataGridRow) is not { } track) return;
        TrackGrid.SelectedItem = track;
        var play = new MenuItem { Header = "Play from here" };
        play.Click += (_, _) => _ = _vm.PlayFromTrackAsync(track);
        var queue = new MenuItem { Header = "Add to queue" };
        queue.Click += (_, _) => _vm.AddTrackToQueue(track);
        new ContextMenu { ItemsSource = new[] { play, queue } }.Open(TrackGrid);
        e.Handled = true;
    }

    // ------------------------------------------------------------ artists

    // ScrollIntoView only scrolls as far as it must, which leaves a forward jump's target at the
    // bottom. Going to the end first makes every jump a backward one, so the target lands on top.
    private void ScrollArtistToTop(ArtistVm artist)
    {
        var index = ArtistList.Items.IndexOf(artist);
        if (index < 0) return;
        ArtistList.ScrollIntoView(ArtistList.ItemCount - 1);
        ArtistList.ScrollIntoView(index);
    }

    // ------------------------------------------------------------ albums & tracks

    private static T? ItemUnder<T>(object? source, Func<Visual, bool> isContainer) where T : class
    {
        if (source is not Visual v) return null;
        var container = v.GetSelfAndVisualAncestors().FirstOrDefault(isContainer);
        return (container as StyledElement)?.DataContext as T;
    }

    private void OnAlbumTapped(object? sender, TappedEventArgs e)
    {
        if (!_vm.SingleClickPlaysAlbum) return;
        if (ItemUnder<AlbumVm>(e.Source, x => x is ListBoxItem) is { } album)
            _ = _vm.PlayAlbumAsync(album);
    }

    private void OnAlbumDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm.SingleClickPlaysAlbum) return; // the first click already started it
        if (ItemUnder<AlbumVm>(e.Source, x => x is ListBoxItem) is { } album)
            _ = _vm.PlayAlbumAsync(album);
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemUnder<TrackVm>(e.Source, x => x is DataGridRow) is { } track)
            _ = _vm.PlayFromTrackAsync(track);
    }

    // ------------------------------------------------------------ queue

    private void OnQueueDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemUnder<QueueItemVm>(e.Source, x => x is ListBoxItem) is { } entry)
            _vm.PlayQueueItem(entry);
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Delete or Key.Back) || QueueList.SelectedItem is not QueueItemVm entry) return;
        var index = QueueList.SelectedIndex;
        _vm.RemoveFromQueue(entry);
        // Keep a row selected so you can delete several in a row.
        if (_vm.Queue.Count > 0) QueueList.SelectedIndex = Math.Min(index, _vm.Queue.Count - 1);
        e.Handled = true;
    }

    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual v && v.GetSelfAndVisualAncestors().Any(a => a is Button)) return; // the ✕
        _dragItem = ItemUnder<QueueItemVm>(e.Source, x => x is ListBoxItem);
        _dragStart = e.GetPosition(QueueList);
    }

    private void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragItem is null) return;
        var p = e.GetPosition(QueueList);
        if (!_dragging)
        {
            if (!e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) { _dragItem = null; return; }
            if (Math.Abs(p.Y - _dragStart.Y) < DragThreshold && Math.Abs(p.X - _dragStart.X) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(QueueList);
            QueueList.Cursor = new Cursor(StandardCursorType.DragMove);
            QueueList.SelectedItem = _dragItem;
            _dragScrollTimer.Start();
        }
        _dragLast = p;
        DragMoveTo(p);
    }

    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        EndQueueDrag();
    }

    private void EndQueueDrag()
    {
        _dragItem = null;
        if (!_dragging) return;
        _dragging = false;
        _dragScrollTimer.Stop();
        QueueList.Cursor = null;
    }

    // The row moves live as you drag, so what you see is where it'll land.
    private void DragMoveTo(Point p)
    {
        if (_dragItem is null) return;
        var from = _vm.Queue.IndexOf(_dragItem);
        if (from < 0) { EndQueueDrag(); return; }

        // Outside the list, use the nearest visible row; auto-scroll takes it further.
        var size = QueueList.Bounds.Size;
        var probe = new Point(Math.Clamp(p.X, 1, Math.Max(1, size.Width - 1)), Math.Clamp(p.Y, 1, Math.Max(1, size.Height - 1)));
        int to;
        if (ItemUnder<QueueItemVm>(QueueList.InputHitTest(probe), x => x is ListBoxItem) is { } over)
            to = _vm.Queue.IndexOf(over);
        else if (LastRowBottom() is { } bottom && probe.Y > bottom)
            to = _vm.Queue.Count - 1; // in the empty space below the last row
        else
            return; // between rows or above the list: stay put
        if (to < 0 || to == from) return;

        _vm.MoveQueueItem(from, to);
        QueueList.SelectedItem = _dragItem;
    }

    private double? LastRowBottom()
    {
        var last = QueueList.ContainerFromIndex(_vm.Queue.Count - 1);
        return last?.TranslatePoint(new Point(0, last.Bounds.Height), QueueList)?.Y;
    }

    // Holding the pointer near the top or bottom edge scrolls the queue.
    private void DragAutoScroll()
    {
        if (!_dragging) return;
        var sv = QueueList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (sv is null) return;
        const double edge = 28, step = 14;
        var dy = _dragLast.Y < edge ? -step : _dragLast.Y > QueueList.Bounds.Height - edge ? step : 0;
        if (dy == 0) return;
        sv.Offset = new Vector(sv.Offset.X, Math.Clamp(sv.Offset.Y + dy, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height)));
        DragMoveTo(_dragLast);
    }

    // ------------------------------------------------------------ transport

    private void OnSeekPressed(object? sender, PointerPressedEventArgs e) => _vm.BeginSeek();
    private void OnSeekReleased(object? sender, PointerReleasedEventArgs e) => _vm.EndSeek();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Source is TextBox) return;
        switch (e.Key)
        {
            case Key.Space:
            case Key.MediaPlayPause:
                _vm.TogglePauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.MediaNextTrack:
                _vm.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.MediaPreviousTrack:
                _vm.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // ------------------------------------------------------------ window layout

    private void RestoreWindowLayout()
    {
        var s = _vm.Settings;
        if (s.WindowWidth >= MinWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight) Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        if (s.ArtistPaneWidth >= 120) MainGrid.ColumnDefinitions[0].Width = new GridLength(s.ArtistPaneWidth);
        if (s.TrackPaneHeight >= 100) RightGrid.RowDefinitions[2].Height = new GridLength(s.TrackPaneHeight);
        if (s.QueuePaneWidth >= 180) MainGrid.ColumnDefinitions[4].Width = new GridLength(s.QueuePaneWidth);
    }

    private void SaveWindowLayout()
    {
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ClientSize.Width;
            s.WindowHeight = ClientSize.Height;
        }
        s.ArtistPaneWidth = MainGrid.ColumnDefinitions[0].ActualWidth;
        s.TrackPaneHeight = RightGrid.RowDefinitions[2].ActualHeight;
        s.QueuePaneWidth = MainGrid.ColumnDefinitions[4].ActualWidth;
        SaveColumns(); // also saves everything else
    }
}
