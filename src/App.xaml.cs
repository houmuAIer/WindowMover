using System.Windows;
using System.Windows.Interop;
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
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("ウィンドウ一括移動を開く (Ctrl+Alt+W)", null, (_, _) => TogglePicker());
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
