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
    private static readonly Brush MemoText = new SolidColorBrush(Color.FromRgb(0xFF, 0xD1, 0x66));
    private static readonly Brush GroupText = new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0xAE));
    private static readonly Brush ChipBg = new SolidColorBrush(Color.FromRgb(0x24, 0x36, 0x2F));
    private static readonly Brush ChipBorder = new SolidColorBrush(Color.FromRgb(0x3F, 0x6B, 0x57));
    private static readonly Brush ChipBorderHover = new SolidColorBrush(Color.FromRgb(0x7F, 0xD1, 0xAE));
    private static readonly Brush CloseHover = new SolidColorBrush(Color.FromRgb(0xD9, 0x58, 0x5C));

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

    // 位置の記憶・グループ（一覧の再取得ごとに照合し直す）
    private HashSet<IntPtr> _rememberedHandles = new();
    private readonly Dictionary<IntPtr, List<WindowGroup>> _groupsByHandle = new();
    private readonly Dictionary<WindowGroup, List<WindowItem>> _groupPresent = new();

    /// <summary>確認ダイアログ等を表示中は、フォーカスが外れても自動で閉じない。</summary>
    private int _modalDepth;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();

        SearchBox.TextChanged += (_, _) => BuildCards();
        SelectAllButton.Click += (_, _) => SelectAll();
        RefreshButton.Click += (_, _) => ReloadAll();
        CloseButton.Click += (_, _) => Close();
        SnapshotButton.Click += (_, _) => SnapshotLayout();
        RememberButton.Click += (_, _) => RememberSelected();
        RestoreButton.Click += (_, _) => RestoreSelected();
        CloseSelectedButton.Click += (_, _) => CloseWindows(SelectedItems());
        Scroller.ScrollChanged += (_, _) => UpdateThumbnails();
        SizeChanged += (_, _) => UpdateThumbnails();
        Deactivated += (_, _) =>
        {
            if (_modalDepth == 0) Close();
        };
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
        RefreshMemoryInfo();
        BuildMonitorMap();
        BuildGroupBar();
        BuildCards();
    }

    /// <summary>記憶・グループと、いま開いているウィンドウとの対応を取り直す。</summary>
    private void RefreshMemoryInfo()
    {
        var state = StateStore.Current;
        _rememberedHandles = WindowMatcher.Match(state.Windows, _windows).Values.Select(w => w.Handle).ToHashSet();

        _groupsByHandle.Clear();
        _groupPresent.Clear();
        foreach (var g in state.Groups)
        {
            var present = WindowMatcher.Match(g.Members, _windows).Values.ToList();
            _groupPresent[g] = present;
            foreach (var w in present)
            {
                if (!_groupsByHandle.TryGetValue(w.Handle, out var list))
                    _groupsByHandle[w.Handle] = list = new List<WindowGroup>();
                list.Add(g);
            }
        }
    }

    /// <summary>記憶やグループを変更した後の再表示（選択状態は保つ）。</summary>
    private void RefreshAfterMemoryChange()
    {
        RefreshMemoryInfo();
        BuildGroupBar();
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
        // 再構築しても、表示が続くウィンドウの選択は保つ
        var keep = _cards.Where(c => c.Selected).Select(c => c.Item.Handle).ToHashSet();

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
            card.Selected = keep.Contains(item.Handle);
            _cards.Add(card);
            CardPanel.Children.Add(card.Root);
        }

        CountText.Text = $"{shown.Count} / {_windows.Count} ウィンドウ";
        RegisterThumbnails();
        RefreshSelectionVisual();
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
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = item.Title
        };

        // ウィンドウを閉じるボタン（サムネイルの上は DWM が上書きするため、タイトル行の右端に置く）
        var closeGlyph = new TextBlock
        {
            Text = "✕",
            Foreground = TextSub,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var closeBtn = new Border
        {
            Width = 22,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = Brushes.Transparent,
            Margin = new Thickness(4, 0, 0, 0),
            Child = closeGlyph,
            ToolTip = "このウィンドウを閉じる"
        };
        closeBtn.MouseEnter += (_, _) => { closeBtn.Background = CloseHover; closeGlyph.Foreground = Brushes.White; };
        closeBtn.MouseLeave += (_, _) => { closeBtn.Background = Brushes.Transparent; closeGlyph.Foreground = TextSub; };
        closeBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // カードの選択処理に流さない
            CloseWindows(new List<WindowItem> { item });
        };

        var titleRow = new DockPanel { Width = ThumbWidth, Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(closeBtn, Dock.Right);
        titleRow.Children.Add(mark);
        titleRow.Children.Add(closeBtn);
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

        // 位置の記憶・所属グループの表示
        bool remembered = _rememberedHandles.Contains(item.Handle);
        _groupsByHandle.TryGetValue(item.Handle, out var groups);
        if (remembered || groups is { Count: > 0 })
        {
            var info = new TextBlock
            {
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = ThumbWidth,
                Margin = new Thickness(18, 2, 0, 0)
            };
            if (remembered)
                info.Inlines.Add(new System.Windows.Documents.Run("📌 位置記憶　") { Foreground = MemoText });
            if (groups is { Count: > 0 })
                info.Inlines.Add(new System.Windows.Documents.Run("▣ " + string.Join("、", groups.Select(g => g.Name))) { Foreground = GroupText });
            stack.Children.Add(info);
        }

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
        root.MouseRightButtonUp += (_, e) => OnCardRightClicked(card, e);

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
            : $"{n} 件を選択中。配置図の移動先モニタをクリックするか数字キーで移動。右クリックで位置の記憶・閉じるの操作ができます。";
    }

    private List<WindowItem> SelectedItems() => _cards.Where(c => c.Selected).Select(c => c.Item).ToList();

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

    // ------------------------------------------------------------------ 右クリックメニュー

    private void OnCardRightClicked(Card card, MouseButtonEventArgs e)
    {
        // 未選択のカードを右クリックしたら、そのカードだけを選択してから操作する（エクスプローラーと同じ）
        if (!card.Selected)
        {
            foreach (var c in _cards) c.Selected = false;
            card.Selected = true;
            _anchorIndex = _cards.IndexOf(card);
            RefreshSelectionVisual();
        }

        var sel = SelectedItems();
        bool anyRemembered = sel.Any(w => _rememberedHandles.Contains(w.Handle));
        bool anyInGroup = sel.Any(w => _groupsByHandle.ContainsKey(w.Handle));

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{sel.Count} 件を選択中", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("位置を記憶", "Ctrl+S", RememberSelected));
        menu.Items.Add(MakeItem("記憶した位置に戻す", "Ctrl+R", RestoreSelected, anyRemembered || anyInGroup));
        menu.Items.Add(MakeItem("位置の記憶を解除", null, ForgetSelected, anyRemembered));

        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("閉じる…", "Del", () => CloseWindows(SelectedItems())));

        menu.PlacementTarget = card.Root;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem MakeItem(string header, string? gesture, Action action, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled };
        mi.Click += (_, _) => action();
        return mi;
    }

    // ------------------------------------------------------------------ グループバー

    private void BuildGroupBar()
    {
        GroupPanel.Children.Clear();
        var groups = StateStore.Current.Groups;

        if (groups.Count == 0)
        {
            GroupPanel.Children.Add(GroupHint);
            return;
        }

        GroupPanel.Children.Add(new TextBlock
        {
            Text = "グループ",
            Foreground = TextSub,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0)
        });

        foreach (var g in groups)
        {
            var group = g;
            int present = _groupPresent.TryGetValue(g, out var p) ? p.Count : 0;

            var matched = WindowMatcher.Match(g.Members, _windows);
            var tip = string.Join("\n", g.Members.Select(m =>
                matched.ContainsKey(m) ? $"● {m.Signature.Title}" : $"○ {m.Signature.Title}（閉じています）"));

            var chip = new Border
            {
                Background = ChipBg,
                BorderBrush = ChipBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 2, 8, 2),
                Cursor = Cursors.Hand,
                ToolTip = $"{g.Name}（{present} / {g.Members.Count} 件が開いています）\n{tip}\n\nクリックで選択、右クリックでメニュー",
                Child = new TextBlock
                {
                    Text = $"{g.Name}  {present}/{g.Members.Count}",
                    Foreground = present > 0 ? TextMain : TextSub,
                    FontSize = 12.5
                }
            };
            chip.MouseEnter += (_, _) => chip.BorderBrush = ChipBorderHover;
            chip.MouseLeave += (_, _) => chip.BorderBrush = ChipBorder;
            chip.MouseLeftButtonDown += (_, e) =>
            {
                SelectGroup(group, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                e.Handled = true;
            };
            chip.MouseRightButtonUp += (_, e) =>
            {
                ShowGroupMenu(group, chip);
                e.Handled = true;
            };
            GroupPanel.Children.Add(chip);
        }
    }

    /// <summary>グループのうち、いま開いているウィンドウを選択する（その後は数字キー等でまとめて移動できる）。</summary>
    private void SelectGroup(WindowGroup g, bool additive)
    {
        var handles = (_groupPresent.TryGetValue(g, out var p) ? p : new List<WindowItem>())
            .Select(w => w.Handle).ToHashSet();

        if (handles.Count == 0)
        {
            StatusText.Text = $"「{g.Name}」のウィンドウは、いまひとつも開いていません。";
            return;
        }

        // 検索で隠れているメンバーがいたら絞り込みを解除する
        if (handles.Any(h => _cards.All(c => c.Item.Handle != h)))
            SearchBox.Text = "";

        foreach (var c in _cards)
        {
            if (handles.Contains(c.Item.Handle)) c.Selected = true;
            else if (!additive) c.Selected = false;
        }
        _anchorIndex = -1;
        RefreshSelectionVisual();
    }

    private void ShowGroupMenu(WindowGroup g, FrameworkElement anchor)
    {
        int present = _groupPresent.TryGetValue(g, out var p) ? p.Count : 0;

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"{g.Name}（{present} / {g.Members.Count} 件が開いています）", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("このグループを選択", null, () => SelectGroup(g, false), present > 0));
        menu.Items.Add(MakeItem("記憶した配置に戻す", null, () => RestoreGroup(g), present > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("グループを削除…", null, () => DeleteGroup(g)));

        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ グループ操作

    /// <summary>現在の配置を記憶する（押すたびに、モニタごとのグループを作り直す）。</summary>
    private void SnapshotLayout()
    {
        var (groups, windows) = MemoryService.SnapshotByMonitor(_windows, _monitors);
        RefreshAfterMemoryChange();
        StatusText.Text = windows == 0
            ? "記憶できるウィンドウがありませんでした。"
            : $"現在の配置を記憶しました（{groups} 台のモニタ、{windows} 件のウィンドウ）。以前のグループは置き換えました。";
    }

    private void DeleteGroup(WindowGroup g)
    {
        var r = WithModal(() => MessageBox.Show(this,
            $"グループ「{g.Name}」を削除します。よろしいですか？\n（ウィンドウ自体は閉じません。記憶していた配置は消えます）",
            "グループの削除", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No));
        if (r != MessageBoxResult.Yes) return;

        StateStore.Current.Groups.Remove(g);
        StateStore.Save();
        RefreshAfterMemoryChange();
        StatusText.Text = $"グループ「{g.Name}」を削除しました。";
    }

    // ------------------------------------------------------------------ 位置の記憶

    private void RememberSelected()
    {
        var sel = SelectedItems();
        if (sel.Count == 0)
        {
            StatusText.Text = "位置を記憶するウィンドウを選択してください。";
            return;
        }
        int n = MemoryService.RememberWindows(sel, _monitors);
        RefreshAfterMemoryChange();
        StatusText.Text = $"{n} 件のウィンドウの今の位置を記憶しました。モニタを抜き差ししても、このモニタが戻れば自動で元の位置に戻ります。";
    }

    private void ForgetSelected()
    {
        int n = MemoryService.ForgetWindows(SelectedItems());
        RefreshAfterMemoryChange();
        StatusText.Text = $"{n} 件のウィンドウの位置の記憶を解除しました（グループの記憶はそのままです）。";
    }

    private void RestoreSelected()
    {
        var handles = SelectedItems().Select(w => w.Handle).ToHashSet();
        if (handles.Count == 0)
        {
            StatusText.Text = "記憶した位置に戻すウィンドウを選択してください。";
            return;
        }
        var targets = MemoryService.Resolve(_windows).Values.Where(t => handles.Contains(t.Window.Handle)).ToList();
        if (targets.Count == 0)
        {
            StatusText.Text = "選択中のウィンドウには、記憶した位置がありません。";
            return;
        }
        RunRestore(targets);
    }

    private void RestoreGroup(WindowGroup g)
    {
        var targets = WindowMatcher.Match(g.Members, _windows).Select(kv => (kv.Key, kv.Value)).ToList();
        if (targets.Count == 0) return;
        RunRestore(targets);
    }

    private void RunRestore(List<(SavedWindow Entry, WindowItem Window)> targets)
    {
        // 移動と同様、サムネイルを解除し自分を隠してから動かす
        _timer?.Stop();
        ClearThumbnails();
        Hide();

        var (ok, skipped, failed) = MemoryService.RestoreTargets(targets, _monitors);
        Close();

        if (skipped > 0 || failed.Count > 0)
        {
            string msg = $"{ok} 件を記憶した位置に戻しました。";
            if (skipped > 0) msg += $"\n\n記憶したときのモニタが今は接続されていないため、{skipped} 件は戻していません。";
            if (failed.Count > 0)
                msg += $"\n\n次の {failed.Count} 件は戻せませんでした。\n" + string.Join("\n", failed.Take(10))
                       + "\n\n管理者権限で動作しているアプリのウィンドウは、WindowMover 自身も管理者として実行しないと動かせません。";
            MessageBox.Show(msg, "WindowMover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ ウィンドウを閉じる

    private void CloseWindows(List<WindowItem> targets)
    {
        if (targets.Count == 0)
        {
            StatusText.Text = "閉じるウィンドウを選択してください。";
            return;
        }

        string list = string.Join("\n", targets.Take(10).Select(w => $"・{w.Title}（{w.ProcessName}）"))
                      + (targets.Count > 10 ? $"\n…ほか {targets.Count - 10} 件" : "");
        var answer = WithModal(() => MessageBox.Show(this,
            $"次の {targets.Count} 件のウィンドウを閉じます。よろしいですか？\n\n{list}\n\n"
            + "保存していない内容があるアプリは、アプリ側で保存の確認が表示されます。",
            "ウィンドウを閉じる", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
        if (answer != MessageBoxResult.Yes) return;

        // 強制終了ではなく、タイトルバーの × を押したのと同じ WM_CLOSE を送る
        var failed = new List<WindowItem>();
        foreach (var w in targets)
            if (Native.IsWindow(w.Handle) && !Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero))
                failed.Add(w);

        StatusText.Text = "ウィンドウを閉じています…";

        // 少し待ってから結果を確認する。残っているウィンドウは保存確認などを出している可能性が高いので、
        // 最前面のこのパネルがそれを隠さないよう、パネルを閉じる
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_isClosed) return;

            var remaining = targets.Where(w => !failed.Contains(w) && Native.IsWindow(w.Handle) && Native.IsWindowVisible(w.Handle)).ToList();
            int closed = targets.Count - failed.Count - remaining.Count;

            if (failed.Count > 0)
                WithModal(() => MessageBox.Show(this,
                    $"次の {failed.Count} 件は閉じられませんでした。\n"
                    + string.Join("\n", failed.Take(10).Select(w => $"・{w.Title}（{w.ProcessName}）"))
                    + "\n\n管理者権限で動作しているアプリのウィンドウは、WindowMover 自身も管理者として実行しないと閉じられません。",
                    "WindowMover", MessageBoxButton.OK, MessageBoxImage.Warning));

            if (remaining.Count > 0)
            {
                Close();
                return;
            }

            ReloadAll();
            StatusText.Text = $"{closed} 件のウィンドウを閉じました。";
        };
        check.Start();
    }

    private T WithModal<T>(Func<T> show)
    {
        _modalDepth++;
        try { return show(); }
        finally { _modalDepth--; }
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

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            Action? action = e.Key switch
            {
                Key.G => SnapshotLayout,
                Key.S => RememberSelected,
                Key.R => RestoreSelected,
                _ => null
            };
            if (action != null)
            {
                action();
                e.Handled = true;
                return;
            }
        }

        // Delete は検索ボックス入力中は文字の削除に使う
        if (e.Key == Key.Delete && !inSearch)
        {
            CloseWindows(SelectedItems());
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
        _isClosed = true;
        _timer?.Stop();
        _timer = null;
        ClearThumbnails();
    }
}
