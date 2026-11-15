using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowMover;

/// <summary>グループ名の入力に使う、1行入力の小さなダイアログ。</summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _box;

    private InputDialog(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
        FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");

        _box = new TextBox
        {
            Text = initial,
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
            Margin = new Thickness(0, 6, 0, 14)
        };

        var ok = new Button { Content = "OK", Width = 84, Padding = new Thickness(0, 4, 0, 4), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "キャンセル", Width = 84, Padding = new Thickness(0, 4, 0, 4), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (_box.Text.Trim().Length == 0) { _box.Focus(); return; }
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 13 });
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
        PreviewKeyDown += (_, e) =>
        {
            // 親パネルのショートカット（数字キー等）に流さない
            if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
    }

    /// <summary>入力された文字列を返す（キャンセル時は null）。</summary>
    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dlg = new InputDialog(title, label, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._box.Text.Trim() : null;
    }
}
