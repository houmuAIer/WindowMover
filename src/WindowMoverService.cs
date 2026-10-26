using System.Runtime.InteropServices;

namespace WindowMover;

internal static class WindowMoverService
{
    /// <summary>
    /// ウィンドウを指定モニタへ移動する。
    /// 移動元モニタの作業領域に対する相対位置・相対サイズを保って移動先に配置するため、
    /// 解像度や拡大率の異なるモニタ間でも体裁が崩れにくい。
    /// 戻り値 false は多くの場合 UIPI（昇格プロセスのウィンドウ）による失敗。
    /// </summary>
    public static bool MoveTo(WindowItem item, MonitorItem target)
    {
        IntPtr hWnd = item.Handle;
        if (!Native.IsWindow(hWnd)) return false;

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(hWnd, ref wp)) return false;

        bool wasMaximized = wp.showCmd == Native.SW_SHOWMAXIMIZED;
        bool wasMinimized = wp.showCmd == Native.SW_SHOWMINIMIZED;

        // 最大化・最小化のままでは座標指定が効かないので、いったん通常状態に戻す
        if (wasMaximized || wasMinimized)
        {
            Native.ShowWindow(hWnd, Native.SW_RESTORE);
            Thread.Sleep(40);
        }

        if (!Native.GetWindowRect(hWnd, out RECT r)) return false;

        RECT srcWork = GetWorkArea(Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST));
        RECT dstWork = target.Work;

        if (srcWork.Width <= 0 || srcWork.Height <= 0) return false;

        double scaleX = dstWork.Width / (double)srcWork.Width;
        double scaleY = dstWork.Height / (double)srcWork.Height;

        double relX = (r.Left - srcWork.Left) / (double)srcWork.Width;
        double relY = (r.Top - srcWork.Top) / (double)srcWork.Height;

        int newW = Clamp((int)Math.Round(r.Width * scaleX), 160, dstWork.Width);
        int newH = Clamp((int)Math.Round(r.Height * scaleY), 120, dstWork.Height);

        int newX = dstWork.Left + (int)Math.Round(relX * dstWork.Width);
        int newY = dstWork.Top + (int)Math.Round(relY * dstWork.Height);

        // 移動先の作業領域からはみ出さないように収める
        newX = Clamp(newX, dstWork.Left, Math.Max(dstWork.Left, dstWork.Right - newW));
        newY = Clamp(newY, dstWork.Top, Math.Max(dstWork.Top, dstWork.Bottom - newH));

        bool ok = Native.SetWindowPos(hWnd, IntPtr.Zero, newX, newY, newW, newH,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        if (!ok) return false;

        // 拡大率の異なるモニタへ移動すると WM_DPICHANGED でアプリ側が自分をリサイズし、
        // 位置がずれることがある。1回だけ位置を再適用して補正する。
        Thread.Sleep(40);
        if (Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST) != target.Handle
            || (Native.GetWindowRect(hWnd, out RECT r2) && r2.Left != newX))
        {
            Native.GetWindowRect(hWnd, out RECT cur);
            int fixW = Math.Min(cur.Width, dstWork.Width);
            int fixH = Math.Min(cur.Height, dstWork.Height);
            int fixX = Clamp(newX, dstWork.Left, Math.Max(dstWork.Left, dstWork.Right - fixW));
            int fixY = Clamp(newY, dstWork.Top, Math.Max(dstWork.Top, dstWork.Bottom - fixH));
            Native.SetWindowPos(hWnd, IntPtr.Zero, fixX, fixY, fixW, fixH,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        if (wasMaximized)
        {
            Thread.Sleep(20);
            Native.ShowWindow(hWnd, Native.SW_SHOWMAXIMIZED);
        }

        return true;
    }

    private static RECT GetWorkArea(IntPtr hMonitor)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (Native.GetMonitorInfo(hMonitor, ref mi)) return mi.rcWork;
        return new RECT();
    }

    private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
}
