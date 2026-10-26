using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WindowMover;

public partial class MainWindow : Window
{
    private const double ThumbWidth = 260;
    private const double ThumbHeight = 150;
    private const double DragThreshold = 5;

    private static readonly Brush CardBg = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x2B));
    private static readonly Brush CardBgSelected = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F));
    private static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3C));
    private static readonly Brush CardBorderSelected = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
    private static readonly Brush ThumbBg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18));
    private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF));
    private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x96, 0x96, 0xA4));
    private static readonly Brush MonBg = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x3E));
    private static readonly Brush MonBgPrimary = new SolidColorBrush(Color.FromRgb(0x1F, 0x4B, 0x82));
    private static readonly Brush MonBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x56));
    private static readonly Brush MonBorderHover = new SolidColorBrush(Color.FromRgb(0x5C, 0x9B, 0xEF));

    private sealed class Card
    {
        public WindowItem Item = null!;
        public Border Root = null!;
        public Border Host = null!;
        public TextBlock Mark = null!;
        public IntPtr Thumb = IntPtr.Zero;
        public bool Selected;
    }

    private readonly List<Card> _cards = new();
    private List<MonitorItem> _monitors = new();
    private List<WindowItem> _windows = new();
    private MonitorItem? _defaultTarget;
    private IntPtr _hwnd = IntPtr.Zero;
    private int _anchorIndex = -1;
    private DispatcherTimer? _timer;

    // 矩形選択
    private bool _dragArmed;
    private bool _dragging;
    private Point _dragOriginPanel;
    private Point _dragOriginScroller;
    private HashSet<Card> _dragBaseSelection = new();

    // モニタ配置図
    private readonly List<(MonitorItem Monitor, Border Box, TextBlock Badge)> _monitorBoxes = new();

    public MainWindow()
    {
        InitializeComponent();

        SearchBox.TextChanged += (_, _) => BuildCards();
        SelectAllButton.Click += (_, _) => SelectAll();
        RefreshButton.Click += (_, _) => ReloadAll();
        CloseButton.Click += (_, _) => Close();
        Scroller.ScrollChanged += (_, _) => UpdateThumbnails();
        SizeChanged += (_, _) => UpdateThumbnails();
        Deactivated += (_, _) => Close();
        Closed += OnClosed;

        Scroller.MouseLeftButtonDown += OnEmptyAreaMouseDown;
        Scroller.MouseMove += OnScrollerMouseMove;
        Scroller.MouseLeftButtonUp += OnScrollerMouseUp;
        Scroller.LostMouseCapture += (_, _) => EndDrag();

        MonitorMap.SizeChanged += (_, _) => BuildMonitorMap();

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            BuildMonitorMap();
            UpdateThumbnails();
        };
    }

    // ------------------------------------------------------------------ 初期化

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        _monitors = MonitorService.Enumerate();
        _defaultTarget = MonitorService.UnderCursor(_monitors);

        // カーソルのあるモニタの中央に、物理ピクセル指定で配置する（DPI 混在環境でも正確）
        var work = _defaultTarget.Work;
        int w = Math.Min(1500, (int)(work.Width * 0.92));
        int h = Math.Min(980, (int)(work.Height * 0.90));
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (work.Height - h) / 2;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        ReloadAll();

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _timer.Tick += (_, _) => UpdateThumbnails();
        _timer.Start();
    }

    private void ReloadAll()
    {
        _monitors = MonitorService.Enumerate();
        if (_defaultTarget != null)
            _defaultTarget = _monitors.FirstOrDefault(m => m.Handle == _defaultTarget.Handle) ?? _monitors[0];

        _windows = WindowEnumerator.Enumerate(_monitors);
        BuildMonitorMap();
        BuildCards();
    }

    // ------------------------------------------------------------------ モニタ配置図

    private void BuildMonitorMap()
    {
        MonitorMap.Children.Clear();
        _monitorBoxes.Clear();

        if (_monitors.Count == 0) return;
        double availW = MonitorMap.ActualWidth;
        double availH = MonitorMap.ActualHeight;
        if (availW <= 1 || availH <= 1) return;

        const double pad = 10;
        int minL = _monitors.Min(m => m.Bounds.Left);
        int minT = _monitors.Min(m => m.Bounds.Top);
        int maxR = _monitors.Max(m => m.Bounds.Right);
        int maxB = _monitors.Max(m => m.Bounds.Bottom);
        double vw = Math.Max(1, maxR - minL);
        double vh = Math.Max(1, maxB - minT);

        double scale = Math.Min((availW - pad * 2) / vw, (availH - pad * 2) / vh);
        double mapW = vw * scale;
        double mapH = vh * scale;
        double offX = (availW - mapW) / 2;
        double offY = (availH - mapH) / 2;

        foreach (var m in _monitors)
        {
            double bx = offX + (m.Bounds.Left - minL) * scale;
            double by = offY + (m.Bounds.Top - minT) * scale;
            double bw = Math.Max(24, m.Bounds.Width * scale);
            double bh = Math.Max(18, m.Bounds.Height * scale);

            var number = new TextBlock
            {
                Text = m.DisplayNumber.ToString(),
                Foreground = Brushes.White,
                FontSize = Math.Max(14, Math.Min(40, bh * 0.42)),
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var detail = new TextBlock
            {
                Text = m.IsPrimary ? $"{m.Detail}　メイン" : m.Detail,
                Foreground = TextSub,
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var badge = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66)),
                FontSize = 10.5,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(number);
            if (bh > 52) stack.Children.Add(detail);
            if (bh > 68) stack.Children.Add(badge);

            var box = new Border
            {
                Width = bw,
                Height = bh,
                Background = m.IsPrimary ? MonBgPrimary : MonBg,
                BorderBrush = MonBorder,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Child = stack,
                ToolTip = $"{m.Label}　{m.Detail}　位置 ({m.Bounds.Left}, {m.Bounds.Top})",
                Tag = m
            };

            box.MouseEnter += (s, _) => ((Border)s).BorderBrush = MonBorderHover;
            box.MouseLeave += (s, _) => ((Border)s).BorderBrush = MonBorder;
            box.MouseLeftButtonDown += (s, e) =>
            {
                MoveSelected((MonitorItem)((Border)s).Tag!);
                e.Handled = true;
            };

            Canvas.SetLeft(box, bx);
            Canvas.SetTop(box, by);
            MonitorMap.Children.Add(box);
            _monitorBoxes.Add((m, box, badge));
        }

        UpdateMonitorBadges();
    }

    /// <summary>選択中のウィンドウが今どのモニタにあるかを配置図に表示する。</summary>
    private void UpdateMonitorBadges()
    {
        foreach (var (m, _, badge) in _monitorBoxes)
        {
            int n = _cards.Count(c => c.Selected && c.Item.Monitor == m.Handle);
            badge.Text = n > 0 ? $"選択中 {n} 件" : "";
        }
    }

    // ------------------------------------------------------------------ カード構築

    private void BuildCards()
    {
        ClearThumbnails();
        CardPanel.Children.Clear();
        _cards.Clear();
        _anchorIndex = -1;

        string filter = SearchBox.Text.Trim();
        var shown = string.IsNullOrEmpty(filter)
            ? _windows
            : _windows.Where(w =>
                w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var item in shown)
        {
            var card = CreateCard(item);
            _cards.Add(card);
            CardPanel.Children.Add(card.Root);
        }

        CountText.Text = $"{shown.Count} / {_windows.Count} ウィンドウ";
        RegisterThumbnails();
        UpdateStatus();
        UpdateMonitorBadges();
    }

    private Card CreateCard(WindowItem item)
    {
        var card = new Card { Item = item };

        // サムネイルの描画先（DWM が WPF の描画の上に直接合成するため、中身は空にしておく）
        var host = new Border
        {
            Width = ThumbWidth,
            Height = ThumbHeight,
            Background = ThumbBg,
            CornerRadius = new CornerRadius(3)
        };

        if (item.IsMinimized)
        {
            var box = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = TryCreateIcon(item.IconHandle);
            if (icon != null)
                box.Children.Add(new Image { Source = icon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 0, 8) });

            box.Children.Add(new TextBlock
            {
                Text = "最小化中",
                Foreground = TextSub,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            host.Child = box;
        }

        var mark = new TextBlock
        {
            Text = "",
            Foreground = CardBorderSelected,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center
        };

        var title = new TextBlock
        {
            Text = item.Title,
            Foreground = TextMain,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ThumbWidth - 18,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.Title
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        titleRow.Children.Add(mark);
        titleRow.Children.Add(title);

        var sub = new TextBlock
        {
            Text = $"{item.ProcessName}　{item.MonitorLabel}{(item.IsMaximized ? "　最大化" : "")}",
            Foreground = TextSub,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = ThumbWidth,
            Margin = new Thickness(18, 2, 0, 0)
        };

        var stack = new StackPanel();
        stack.Children.Add(host);
        stack.Children.Add(titleRow);
        stack.Children.Add(sub);

        var root = new Border
        {
            BorderThickness = new Thickness(3),
            BorderBrush = CardBorder,
            Background = CardBg,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9),
            Margin = new Thickness(7),
            Cursor = Cursors.Hand,
            Child = stack
        };
        root.MouseLeftButtonDown += (_, e) => OnCardClicked(card, e);

        card.Root = root;
        card.Host = host;
        card.Mark = mark;
        return card;
    }

    // ------------------------------------------------------------------ 選択

    /// <summary>HICON を WPF の画像に変換する（失敗したら null）。アイコンは所有者のものなので破棄しない。</summary>
    private static ImageSource? TryCreateIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    private void OnCardClicked(Card card, MouseButtonEventArgs e)
    {
        int index = _cards.IndexOf(card);
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (shift && _anchorIndex >= 0)
        {
            int from = Math.Min(_anchorIndex, index);
            int to = Math.Max(_anchorIndex, index);
            if (!ctrl)
                foreach (var c in _cards) c.Selected = false;
            for (int i = from; i <= to; i++) _cards[i].Selected = true;
        }
        else if (ctrl)
        {
            card.Selected = !card.Selected;
            _anchorIndex = index;
        }
        else
        {
            bool wasOnlyThis = card.Selected && _cards.Count(c => c.Selected) == 1;
            foreach (var c in _cards) c.Selected = false;
            card.Selected = !wasOnlyThis;
            _anchorIndex = index;
        }

        RefreshSelectionVisual();
        e.Handled = true;
    }

    private void SelectAll()
    {
        bool allSelected = _cards.Count > 0 && _cards.All(c => c.Selected);
        foreach (var c in _cards) c.Selected = !allSelected;
        RefreshSelectionVisual();
    }

    private void RefreshSelectionVisual()
    {
        foreach (var c in _cards)
        {
            c.Root.BorderBrush = c.Selected ? CardBorderSelected : CardBorder;
            c.Root.Background = c.Selected ? CardBgSelected : CardBg;
            c.Mark.Text = c.Selected ? "✓" : "";
        }
        UpdateStatus();
        UpdateMonitorBadges();
    }

    private void UpdateStatus()
    {
        int n = _cards.Count(c => c.Selected);
        StatusText.Text = n == 0
            ? "クリックで選択、Ctrl+クリックで追加、Shift+クリックで範囲選択。何もない場所からドラッグすると矩形で囲んで一括選択できます。"
            : $"{n} 件を選択中。上の配置図で移動先モニタをクリックするか、数字キーを押してください。";
    }

    // ------------------------------------------------------------------ 矩形選択

    private void OnEmptyAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        // カード上のクリックは Handled 済みなのでここには来ない
        _dragArmed = true;
        _dragging = false;
        _dragOriginPanel = e.GetPosition(CardPanel);
        _dragOriginScroller = e.GetPosition(Scroller);

        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        _dragBaseSelection = additive
            ? _cards.Where(c => c.Selected).ToHashSet()
            : new HashSet<Card>();

        if (!additive)
        {
            foreach (var c in _cards) c.Selected = false;
            RefreshSelectionVisual();
        }

        Scroller.CaptureMouse();
    }

    private void OnScrollerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        Point nowScroller = e.GetPosition(Scroller);
        if (!_dragging)
        {
            if (Math.Abs(nowScroller.X - _dragOriginScroller.X) < DragThreshold &&
                Math.Abs(nowScroller.Y - _dragOriginScroller.Y) < DragThreshold)
                return;

            _dragging = true;
            Band.Visibility = Visibility.Visible;
            UpdateThumbnails(); // ドラッグ中はサムネイルを隠して選択枠を見えるようにする
        }

        // 端に寄せたら自動スクロール
        if (nowScroller.Y < 24) Scroller.LineUp();
        else if (nowScroller.Y > Scroller.ActualHeight - 24) Scroller.LineDown();

        Point nowPanel = e.GetPosition(CardPanel);
        var sel = new Rect(_dragOriginPanel, nowPanel);

        foreach (var c in _cards)
        {
            Rect bounds = CardBoundsInPanel(c);
            bool hit = bounds.IntersectsWith(sel);
            c.Selected = hit || _dragBaseSelection.Contains(c);
        }
        RefreshSelectionVisual();
        DrawBand(sel);
    }

    private void OnScrollerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Scroller.IsMouseCaptured) Scroller.ReleaseMouseCapture();
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragArmed && !_dragging) return;
        _dragArmed = false;
        _dragging = false;
        _anchorIndex = -1;
        Band.Visibility = Visibility.Collapsed;
        UpdateThumbnails();
    }

    private Rect CardBoundsInPanel(Card c)
    {
        try
        {
            Point p = c.Root.TransformToVisual(CardPanel).Transform(new Point(0, 0));
            return new Rect(p.X, p.Y, c.Root.ActualWidth, c.Root.ActualHeight);
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private void DrawBand(Rect selInPanel)
    {
        try
        {
            var t = CardPanel.TransformToVisual(BandLayer);
            Point tl = t.Transform(selInPanel.TopLeft);
            Point br = t.Transform(selInPanel.BottomRight);
            var r = new Rect(tl, br);
            Canvas.SetLeft(Band, r.X);
            Canvas.SetTop(Band, r.Y);
            Band.Width = r.Width;
            Band.Height = r.Height;
        }
        catch
        {
            // 変換できない場合は描画をあきらめる（選択処理自体は継続）
        }
    }

    // ------------------------------------------------------------------ サムネイル

    private void RegisterThumbnails()
    {
        if (_hwnd == IntPtr.Zero) return;

        foreach (var c in _cards)
        {
            if (c.Item.IsMinimized) continue; // 最小化中は DWM サムネイルが描画されない
            if (Native.DwmRegisterThumbnail(_hwnd, c.Item.Handle, out IntPtr thumb) == 0)
                c.Thumb = thumb;
        }
    }

    private void ClearThumbnails()
    {
        foreach (var c in _cards)
        {
            if (c.Thumb != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(c.Thumb);
                c.Thumb = IntPtr.Zero;
            }
        }
    }

    private void UpdateThumbnails()
    {
        if (_hwnd == IntPtr.Zero || !IsLoaded || _cards.Count == 0) return;

        var origin = new POINT();
        if (!Native.ClientToScreen(_hwnd, ref origin)) return;

        if (!TryGetScreenRect(Scroller, out Rect view)) return;

        foreach (var c in _cards)
        {
            if (c.Thumb == IntPtr.Zero) continue;

            // DWM のサムネイルは WPF の描画より手前に合成されるため、
            // 矩形選択中は一時的に隠して選択枠が見えるようにする
            bool visible = !_dragging
                           && TryGetScreenRect(c.Host, out Rect r)
                           && r.Top >= view.Top - 0.5
                           && r.Bottom <= view.Bottom + 0.5;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION
                          | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                opacity = 255,
                fVisible = visible,
                fSourceClientAreaOnly = false
            };

            if (visible && TryGetScreenRect(c.Host, out Rect rect))
            {
                double tw = rect.Width, th = rect.Height;
                if (Native.DwmQueryThumbnailSourceSize(c.Thumb, out SIZE s) == 0 && s.cx > 0 && s.cy > 0)
                {
                    // 元ウィンドウの縦横比を保ったまま枠内に収める
                    double scale = Math.Min(rect.Width / s.cx, rect.Height / s.cy);
                    tw = Math.Max(1, s.cx * scale);
                    th = Math.Max(1, s.cy * scale);
                }

                double left = rect.Left + (rect.Width - tw) / 2 - origin.X;
                double top = rect.Top + (rect.Height - th) / 2 - origin.Y;

                props.rcDestination = new RECT
                {
                    Left = (int)Math.Round(left),
                    Top = (int)Math.Round(top),
                    Right = (int)Math.Round(left + tw),
                    Bottom = (int)Math.Round(top + th)
                };
            }

            Native.DwmUpdateThumbnailProperties(c.Thumb, ref props);
        }
    }

    /// <summary>要素の位置を物理ピクセルのスクリーン座標で返す。</summary>
    private static bool TryGetScreenRect(FrameworkElement e, out Rect rect)
    {
        rect = default;
        if (!e.IsLoaded || !e.IsVisible || e.ActualWidth <= 0 || e.ActualHeight <= 0) return false;
        try
        {
            Point tl = e.PointToScreen(new Point(0, 0));
            Point br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
            rect = new Rect(tl, br);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ 移動

    private void MoveSelected(MonitorItem target)
    {
        var selected = _cards.Where(c => c.Selected).Select(c => c.Item).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "移動するウィンドウを選択してください。";
            return;
        }

        // サムネイルを解除し、自分自身を隠してから移動する
        _timer?.Stop();
        ClearThumbnails();
        Hide();

        int ok = 0;
        var failed = new List<string>();

        foreach (var w in selected)
        {
            if (WindowMoverService.MoveTo(w, target)) ok++;
            else failed.Add($"{w.Title}（{w.ProcessName}）");
        }

        Close();

        if (failed.Count > 0)
        {
            MessageBox.Show(
                $"{ok} 件を {target.Label} へ移動しました。\n\n"
                + $"次の {failed.Count} 件は移動できませんでした。\n"
                + string.Join("\n", failed.Take(10))
                + (failed.Count > 10 ? "\n…" : "")
                + "\n\n管理者権限で動作しているアプリのウィンドウは、WindowMover 自身も管理者として実行しないと移動できません。",
                "WindowMover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ キー操作

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool inSearch = Equals(e.OriginalSource, SearchBox);

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            ReloadAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_defaultTarget != null) MoveSelected(_defaultTarget);
            e.Handled = true;
            return;
        }

        // 数字キーでの移動先指定は、検索ボックス入力中は無効にする
        if (!inSearch || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            int digit = KeyToDigit(e.Key);
            if (digit > 0)
            {
                var target = _monitors.FirstOrDefault(m => m.DisplayNumber == digit);
                if (target != null)
                {
                    MoveSelected(target);
                    e.Handled = true;
                }
            }
        }
    }

    private static int KeyToDigit(Key key)
    {
        if (key >= Key.D1 && key <= Key.D9) return key - Key.D1 + 1;
        if (key >= Key.NumPad1 && key <= Key.NumPad9) return key - Key.NumPad1 + 1;
        return -1;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _timer = null;
        ClearThumbnails();
    }
}
