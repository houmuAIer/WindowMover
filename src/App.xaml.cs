using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace WindowMover;

public partial class App : Application
{
    private const int HotkeyId = 0xB001;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private Mutex? _singleInstance;
    private HwndSource? _msgWindow;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _picker;
    private Forms.ToolStripMenuItem? _autoRestoreItem;

    // モニタ抜き差しの検知（接続直後は設定変更通知が連続するので、落ち着いてから処理する）
    private DispatcherTimer? _displayDebounce;
    private List<RECT> _stableMonitors = new();
    private readonly List<RECT> _wentMissing = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\WindowMover.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("WindowMover はすでに起動しています。タスクトレイのアイコンをご確認ください。",
                "WindowMover", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // メッセージ専用ウィンドウを作り、そこで WM_HOTKEY を受ける
        var parameters = new HwndSourceParameters("WindowMoverHotkeyWindow")
        {
            ParentWindow = HWND_MESSAGE,
            WindowStyle = 0
        };
        _msgWindow = new HwndSource(parameters);
        _msgWindow.AddHook(WndProc);

        if (!Native.RegisterHotKey(_msgWindow.Handle, HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_W))
        {
            MessageBox.Show(
                "ショートカット Ctrl+Alt+W を登録できませんでした。ほかのアプリが同じ組み合わせを使用している可能性があります。\n"
                + "タスクトレイのメニューからは引き続き起動できます。",
                "WindowMover", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        SetupTray();

        _stableMonitors = MonitorService.Enumerate().Select(m => m.Bounds).ToList();
        _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _displayDebounce.Tick += (_, _) => OnDisplaySettled();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // ------------------------------------------------------------------ モニタ再接続時の自動復元

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // SystemEvents は別スレッドから呼ばれることがあるので UI スレッドへ移す
        Dispatcher.BeginInvoke(() =>
        {
            // 落ち着く前に一度でも消えたモニタを覚えておく（すぐ再接続された場合も復元対象にするため）
            var now = MonitorService.Enumerate().Select(m => m.Bounds).ToList();
            foreach (var r in _stableMonitors)
                if (!now.Any(n => PlacementService.SameRect(n, r, 0)) && !_wentMissing.Any(x => PlacementService.SameRect(x, r, 0)))
                    _wentMissing.Add(r);

            _displayDebounce?.Stop();
            _displayDebounce?.Start();
        });
    }

    private void OnDisplaySettled()
    {
        _displayDebounce?.Stop();

        var monitors = MonitorService.Enumerate();
        var now = monitors.Select(m => m.Bounds).ToList();

        // 新たに現れたモニタ＝前回の安定状態に無かったもの＋途中で一度消えて戻ってきたもの
        var appeared = now
            .Where(n => !_stableMonitors.Any(r => PlacementService.SameRect(r, n, 0))
                        || _wentMissing.Any(r => PlacementService.SameRect(r, n, 0)))
            .ToList();

        _stableMonitors = now;
        _wentMissing.Clear();

        if (appeared.Count == 0 || !StateStore.Current.AutoRestore) return;

        // パネル表示中ならモニタ構成が変わっているので閉じる
        _picker?.Close();

        var windows = WindowEnumerator.Enumerate(monitors);
        var targets = MemoryService.Resolve(windows).Values;
        var (ok, _, _) = MemoryService.RestoreTargets(targets, monitors, appeared);

        if (ok > 0 && _tray != null)
            _tray.ShowBalloonTip(3000, "WindowMover", $"モニタの再接続を検知し、{ok} 件のウィンドウを記憶した位置に戻しました。", Forms.ToolTipIcon.Info);
    }

    private void RestoreAllNow()
    {
        _picker?.Close();
        var monitors = MonitorService.Enumerate();
        var windows = WindowEnumerator.Enumerate(monitors);
        var targets = MemoryService.Resolve(windows).Values;
        var (ok, skipped, failed) = MemoryService.RestoreTargets(targets, monitors);

        string msg = $"{ok} 件のウィンドウを記憶した位置に戻しました。";
        if (skipped > 0) msg += $"\n記憶したときのモニタが見つからないため {skipped} 件は戻していません。";
        if (failed.Count > 0) msg += $"\n次の {failed.Count} 件は戻せませんでした（管理者権限のアプリの可能性があります）。\n"
                                     + string.Join("\n", failed.Take(10));
        MessageBox.Show(msg, "WindowMover", MessageBoxButton.OK,
            failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ウィンドウ一括移動を開く (Ctrl+Alt+W)", null, (_, _) => TogglePicker());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("記憶した位置にすべて戻す", null, (_, _) => RestoreAllNow());
        _autoRestoreItem = new Forms.ToolStripMenuItem("モニタ再接続時に自動で戻す")
        {
            CheckOnClick = true,
            Checked = StateStore.Current.AutoRestore
        };
        _autoRestoreItem.CheckedChanged += (_, _) =>
        {
            StateStore.Current.AutoRestore = _autoRestoreItem.Checked;
            StateStore.Save();
        };
        menu.Items.Add(_autoRestoreItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "WindowMover (Ctrl+Alt+W)",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => TogglePicker();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            TogglePicker();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void TogglePicker()
    {
        if (_picker != null)
        {
            _picker.Close();
            _picker = null;
            return;
        }

        _picker = new MainWindow();
        _picker.Closed += (_, _) => _picker = null;
        _picker.Show();
        _picker.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(_picker).Handle);
    }

    private void ExitApp()
    {
        _picker?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _displayDebounce?.Stop();

        if (_msgWindow != null)
        {
            Native.UnregisterHotKey(_msgWindow.Handle, HotkeyId);
            _msgWindow.RemoveHook(WndProc);
            _msgWindow.Dispose();
        }

        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
