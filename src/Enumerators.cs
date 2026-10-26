using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowMover;

internal static class MonitorService
{
    public static List<MonitorItem> Enumerate()
    {
        var list = new List<MonitorItem>();

        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (Native.GetMonitorInfo(hMon, ref mi))
                {
                    list.Add(new MonitorItem
                    {
                        Handle = hMon,
                        Bounds = mi.rcMonitor,
                        Work = mi.rcWork,
                        IsPrimary = (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                        Device = mi.szDevice ?? ""
                    });
                }
                return true;
            }, IntPtr.Zero);

        // 物理的な並び（左→右、上→下）
        var ordered = list
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Index = i + 1;
            // "\\.\DISPLAY2" → 2 。Windows の「ディスプレイ設定」の番号と一致させる
            ordered[i].DisplayNumber = ParseDisplayNumber(ordered[i].Device, i + 1);
        }

        // 表示番号が重複・欠落していたら並び順の番号に戻す
        if (ordered.Select(m => m.DisplayNumber).Distinct().Count() != ordered.Count)
            foreach (var m in ordered) m.DisplayNumber = m.Index;

        return ordered;
    }

    private static int ParseDisplayNumber(string device, int fallback)
    {
        int i = device.Length - 1;
        while (i >= 0 && char.IsDigit(device[i])) i--;
        string digits = device[(i + 1)..];
        return int.TryParse(digits, out int n) && n > 0 ? n : fallback;
    }

    public static MonitorItem UnderCursor(List<MonitorItem> monitors)
    {
        if (Native.GetCursorPos(out POINT p))
        {
            IntPtr h = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
            var hit = monitors.FirstOrDefault(m => m.Handle == h);
            if (hit != null) return hit;
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }
}

internal static class WindowEnumerator
{
    /// <summary>Alt+Tab に出るのとほぼ同じ基準でトップレベルウィンドウを列挙する。</summary>
    public static List<WindowItem> Enumerate(List<MonitorItem> monitors)
    {
        var result = new List<WindowItem>();
        uint myPid = (uint)Environment.ProcessId;
        IntPtr shell = Native.GetShellWindow();
        var sb = new StringBuilder(512);

        Native.EnumWindows((hWnd, _) =>
        {
            if (hWnd == shell) return true;
            if (!Native.IsWindowVisible(hWnd)) return true;

            // オーナーを持つウィンドウ（ダイアログ等）は除外
            if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero) return true;

            long ex = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;

            if (Native.GetWindowTextLength(hWnd) == 0) return true;

            // UWP のゴーストウィンドウ（クローク状態）を除外
            if (Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
                && cloaked != 0)
                return true;

            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == myPid) return true;

            sb.Clear();
            sb.EnsureCapacity(512);
            Native.GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length == 0) return true;

            bool minimized = Native.IsIconic(hWnd);

            // 最小化中の GetWindowRect は (-32000,-32000) 付近の見かけ上の矩形を返すため、
            // 復元後の位置（rcNormalPosition）で判定する
            RECT rect;
            if (minimized)
            {
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
                if (!Native.GetWindowPlacement(hWnd, ref wp)) return true;
                rect = wp.rcNormalPosition;
            }
            else
            {
                if (!Native.GetWindowRect(hWnd, out rect)) return true;
                // 極端に小さいウィンドウは実体のない補助ウィンドウとみなす
                if (rect.Width < 80 || rect.Height < 60) return true;
            }

            string proc = "";
            try { proc = Process.GetProcessById((int)pid).ProcessName; }
            catch { /* アクセスできないプロセスは名前なしで続行 */ }

            IntPtr mon = minimized
                ? Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST)
                : Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);

            result.Add(new WindowItem
            {
                Handle = hWnd,
                Title = title,
                ProcessName = proc,
                IsMinimized = minimized,
                IsMaximized = Native.IsZoomed(hWnd),
                IconHandle = minimized ? GetWindowIcon(hWnd) : IntPtr.Zero,
                Monitor = mon,
                MonitorLabel = monitors.FirstOrDefault(m => m.Handle == mon)?.Label ?? "不明"
            });

            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(w => w.MonitorLabel, StringComparer.Ordinal)
            .ThenBy(w => w.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>最小化中のカードに出すためのアプリアイコンを取得する（取れなければ IntPtr.Zero）。</summary>
    private static IntPtr GetWindowIcon(IntPtr hWnd)
    {
        foreach (int type in new[] { Native.ICON_BIG, Native.ICON_SMALL2, Native.ICON_SMALL })
        {
            if (Native.SendMessageTimeout(hWnd, Native.WM_GETICON, new IntPtr(type), IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 200, out IntPtr res) != IntPtr.Zero && res != IntPtr.Zero)
                return res;
        }

        IntPtr h = Native.GetClassLongPtr(hWnd, Native.GCLP_HICON);
        if (h != IntPtr.Zero) return h;
        return Native.GetClassLongPtr(hWnd, Native.GCLP_HICONSM);
    }
}
