namespace WindowMover;

/// <summary>移動対象となるトップレベルウィンドウ1件。</summary>
internal sealed class WindowItem
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public bool IsMinimized { get; init; }
    public bool IsMaximized { get; init; }
    public IntPtr IconHandle { get; init; }
    public IntPtr Monitor { get; init; }
    public string MonitorLabel { get; set; } = "";
}

/// <summary>接続されているモニタ1台。座標はすべて物理ピクセル。</summary>
internal sealed class MonitorItem
{
    public IntPtr Handle { get; init; }
    public RECT Bounds { get; init; }
    public RECT Work { get; init; }
    public bool IsPrimary { get; init; }
    public string Device { get; init; } = "";

    /// <summary>Windows の「ディスプレイ設定」に表示される番号（\\.\DISPLAYn 由来）。</summary>
    public int DisplayNumber { get; set; }

    /// <summary>左上からの並び順（表示番号が取得できなかった場合の予備）。</summary>
    public int Index { get; set; }

    public string Label => IsPrimary
        ? $"モニタ{DisplayNumber}（メイン）"
        : $"モニタ{DisplayNumber}";

    public string Detail => $"{Bounds.Width} x {Bounds.Height}";
}
