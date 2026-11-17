using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowMover;

// =====================================================================
//  保存データのモデル
// =====================================================================

/// <summary>
/// 再起動後も同じウィンドウを見つけ直すための手がかり。
/// ウィンドウハンドルは再起動で変わるため、実行ファイル・クラス名・タイトルで照合する。
/// </summary>
internal sealed class WindowSignature
{
    public string ExePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>最後に一致したウィンドウハンドル。ウィンドウが開いたままなら、これで確実に特定できる。</summary>
    public long LastHandle { get; set; }

    public static WindowSignature From(WindowItem w) => new()
    {
        ExePath = w.ExePath,
        ProcessName = w.ProcessName,
        ClassName = w.ClassName,
        Title = w.Title,
        LastHandle = w.Handle.ToInt64()
    };
}

/// <summary>記憶した配置。座標は物理ピクセルのスクリーン座標（通常表示時の矩形）。</summary>
internal sealed class SavedPlacement
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }

    /// <summary>記憶したときに載っていたモニタ。抜き差しの判定と、復元先の存在確認に使う。</summary>
    public string MonitorDevice { get; set; } = "";
    public int MonLeft { get; set; }
    public int MonTop { get; set; }
    public int MonRight { get; set; }
    public int MonBottom { get; set; }

    public RECT Rect => new() { Left = Left, Top = Top, Right = Right, Bottom = Bottom };
    public RECT MonitorBounds => new() { Left = MonLeft, Top = MonTop, Right = MonRight, Bottom = MonBottom };
}

/// <summary>位置を記憶したウィンドウ1件（個別記憶とグループのメンバーで共通）。</summary>
internal sealed class SavedWindow
{
    public WindowSignature Signature { get; set; } = new();
    public SavedPlacement Placement { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

internal sealed class WindowGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<SavedWindow> Members { get; set; } = new();
    public DateTime SavedAt { get; set; }
}

internal sealed class AppState
{
    /// <summary>ウィンドウ単位で記憶した位置。</summary>
    public List<SavedWindow> Windows { get; set; } = new();

    public List<WindowGroup> Groups { get; set; } = new();

    /// <summary>モニタが再接続されたとき、そのモニタに記憶していたウィンドウを自動で戻す。</summary>
    public bool AutoRestore { get; set; } = true;
}

// =====================================================================
//  保存・読込（%LOCALAPPDATA%\WindowMover\state.json）
// =====================================================================

internal static class StateStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowMover");

    private static readonly string FilePath = Path.Combine(Dir, "state.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static AppState? _current;

    /// <summary>アプリ全体で1つの状態を共有する（初回アクセス時に読み込む）。</summary>
    public static AppState Current => _current ??= Load();

    private static AppState Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath), Options) ?? new AppState();
        }
        catch
        {
            // 壊れたファイルは退避して新規に始める（記憶が消えたことに気付けるよう残しておく）
            try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
        }
        return new AppState();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 保存失敗は致命的ではないので無視する（次回の保存で再試行される）
        }
    }
}

// =====================================================================
//  記憶したウィンドウと、いま開いているウィンドウの照合
// =====================================================================

internal static class WindowMatcher
{
    private const int ScoreSameHandle = 100_000;
    private const int ScoreSameTitle = 10_000;

    /// <summary>タイトルが変わっていても同じウィンドウとみなすのに必要な、前後一致の最小文字数。</summary>
    private const int MinFuzzyChars = 4;

    /// <summary>
    /// 記憶側と現在のウィンドウを1対1で対応付ける。
    /// 実行ファイルとクラス名が一致することを必須条件に、
    /// ①同じハンドル（開いたままのウィンドウ） ②タイトル完全一致 ③タイトルの前方・後方一致
    /// の順に強く結び付け、点数の高い組から確定させる。
    /// </summary>
    public static Dictionary<SavedWindow, WindowItem> Match(IEnumerable<SavedWindow> saved, IReadOnlyList<WindowItem> current)
    {
        var pairs = new List<(int Score, SavedWindow S, WindowItem W)>();
        foreach (var s in saved)
            foreach (var w in current)
            {
                int score = Score(s.Signature, w);
                if (score > 0) pairs.Add((score, s, w));
            }

        var result = new Dictionary<SavedWindow, WindowItem>();
        var usedWindows = new HashSet<IntPtr>();
        foreach (var (_, s, w) in pairs.OrderByDescending(p => p.Score))
        {
            if (result.ContainsKey(s) || usedWindows.Contains(w.Handle)) continue;
            result[s] = w;
            usedWindows.Add(w.Handle);
        }
        return result;
    }

    private static int Score(WindowSignature s, WindowItem w)
    {
        bool sameApp = s.ExePath.Length > 0 && w.ExePath.Length > 0
            ? string.Equals(s.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(s.ProcessName, w.ProcessName, StringComparison.OrdinalIgnoreCase);
        if (!sameApp || !string.Equals(s.ClassName, w.ClassName, StringComparison.Ordinal)) return 0;

        int score = 0;
        if (s.LastHandle != 0 && s.LastHandle == w.Handle.ToInt64()) score += ScoreSameHandle;

        if (string.Equals(s.Title, w.Title, StringComparison.Ordinal))
            score += ScoreSameTitle;
        else
        {
            int sim = CommonAffixLength(s.Title, w.Title);
            if (sim >= MinFuzzyChars) score += sim;
        }
        return score;
    }

    /// <summary>前方一致と後方一致の文字数の合計（例：「〇〇 - Google Chrome」どうしは後方が一致する）。</summary>
    private static int CommonAffixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        int pre = 0;
        while (pre < n && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < n - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        return pre + suf;
    }
}

// =====================================================================
//  配置の取得と復元
// =====================================================================

internal static class PlacementService
{
    private const int Tolerance = 2;

    /// <summary>ウィンドウの現在の配置を取得する（最小化・最大化中は通常表示時の矩形）。</summary>
    public static SavedPlacement? Capture(IntPtr hWnd, List<MonitorItem> monitors)
    {
        if (!Native.IsWindow(hWnd)) return null;

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!Native.GetWindowPlacement(hWnd, ref wp)) return null;

        RECT rect;
        bool maximized;
        if (Native.IsIconic(hWnd))
        {
            rect = WorkspaceToScreen(wp.rcNormalPosition, monitors);
            maximized = (wp.flags & Native.WPF_RESTORETOMAXIMIZED) != 0;
        }
        else if (Native.IsZoomed(hWnd))
        {
            rect = WorkspaceToScreen(wp.rcNormalPosition, monitors);
            maximized = true;
            // 通常時の矩形が別モニタに残っていることがあるので、最大化しているモニタ基準に置き直す
            IntPtr monNow = Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST);
            if (Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST) != monNow)
            {
                var m = monitors.FirstOrDefault(x => x.Handle == monNow);
                if (m != null) rect = FitInto(rect, m.Work);
            }
        }
        else
        {
            // スナップ（画面端への吸着）中は rcNormalPosition が吸着前の矩形になるため、見たままの矩形を使う
            if (!Native.GetWindowRect(hWnd, out rect)) return null;
            maximized = false;
        }

        IntPtr hMon = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
        var mon = monitors.FirstOrDefault(m => m.Handle == hMon);

        return new SavedPlacement
        {
            Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom,
            Maximized = maximized,
            MonitorDevice = mon?.Device ?? "",
            MonLeft = mon?.Bounds.Left ?? 0, MonTop = mon?.Bounds.Top ?? 0,
            MonRight = mon?.Bounds.Right ?? 0, MonBottom = mon?.Bounds.Bottom ?? 0
        };
    }

    /// <summary>記憶したときのモニタが今もつながっているか（同じ位置・解像度で判定）。</summary>
    public static bool IsMonitorPresent(SavedPlacement p, List<MonitorItem> monitors)
        => monitors.Any(m => SameRect(m.Bounds, p.MonitorBounds, 0));

    /// <summary>
    /// 記憶した配置に戻す。最小化中のウィンドウは最小化のまま、復元時の位置だけを書き換える。
    /// 戻り値 false は多くの場合 UIPI（昇格プロセスのウィンドウ）による失敗。
    /// </summary>
    public static bool Restore(IntPtr hWnd, SavedPlacement p, List<MonitorItem> monitors)
    {
        if (!Native.IsWindow(hWnd)) return false;
        RECT target = p.Rect;

        if (Native.IsIconic(hWnd))
        {
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!Native.GetWindowPlacement(hWnd, ref wp)) return false;
            wp.rcNormalPosition = ScreenToWorkspace(target, monitors);
            wp.flags = p.Maximized ? Native.WPF_RESTORETOMAXIMIZED : 0;
            wp.showCmd = Native.SW_SHOWMINNOACTIVE;
            return Native.SetWindowPlacement(hWnd, ref wp);
        }

        bool zoomed = Native.IsZoomed(hWnd);

        // すでに記憶どおりなら何もしない（無用な再描画・アクティブ化を避ける）
        if (!zoomed && !p.Maximized && Native.GetWindowRect(hWnd, out RECT cur0) && SameRect(cur0, target, Tolerance))
            return true;
        if (zoomed && p.Maximized
            && Native.MonitorFromWindow(hWnd, Native.MONITOR_DEFAULTTONEAREST)
               == Native.MonitorFromRect(ref target, Native.MONITOR_DEFAULTTONEAREST))
            return true;

        if (zoomed)
        {
            Native.ShowWindow(hWnd, Native.SW_RESTORE);
            Thread.Sleep(40);
        }

        if (!Native.SetWindowPos(hWnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
            return false;

        // 拡大率の異なるモニタへ移ると WM_DPICHANGED でアプリ側がリサイズすることがあるので1回だけ補正する
        Thread.Sleep(40);
        if (Native.GetWindowRect(hWnd, out RECT cur) && !SameRect(cur, target, Tolerance))
            Native.SetWindowPos(hWnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        if (p.Maximized)
        {
            Thread.Sleep(20);
            Native.ShowWindow(hWnd, Native.SW_SHOWMAXIMIZED);
        }
        return true;
    }

    public static bool SameRect(RECT a, RECT b, int tol)
        => Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Top - b.Top) <= tol
           && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol;

    // WINDOWPLACEMENT の座標は「ワークスペース座標」（メインモニタの作業領域の左上が原点）。
    // タスクバーが上や左にあるときだけスクリーン座標とずれるので、その分を補正する。
    private static (int dx, int dy) WorkspaceOffset(List<MonitorItem> monitors)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary);
        return primary == null ? (0, 0) : (primary.Work.Left - primary.Bounds.Left, primary.Work.Top - primary.Bounds.Top);
    }

    private static RECT WorkspaceToScreen(RECT r, List<MonitorItem> monitors)
    {
        var (dx, dy) = WorkspaceOffset(monitors);
        return new RECT { Left = r.Left + dx, Top = r.Top + dy, Right = r.Right + dx, Bottom = r.Bottom + dy };
    }

    private static RECT ScreenToWorkspace(RECT r, List<MonitorItem> monitors)
    {
        var (dx, dy) = WorkspaceOffset(monitors);
        return new RECT { Left = r.Left - dx, Top = r.Top - dy, Right = r.Right - dx, Bottom = r.Bottom - dy };
    }

    private static RECT FitInto(RECT r, RECT work)
    {
        int w = Math.Min(r.Width, work.Width);
        int h = Math.Min(r.Height, work.Height);
        int x = work.Left + (work.Width - w) / 2;
        int y = work.Top + (work.Height - h) / 2;
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }
}

// =====================================================================
//  記憶の登録と、まとめて戻す処理
// =====================================================================

internal static class MemoryService
{
    /// <summary>ウィンドウ単位で位置を記憶する（同じウィンドウの記憶があれば上書き）。成功件数を返す。</summary>
    public static int RememberWindows(IEnumerable<WindowItem> windows, List<MonitorItem> monitors)
    {
        var state = StateStore.Current;
        var list = windows.ToList();
        var existing = WindowMatcher.Match(state.Windows, list);
        var byHandle = existing.ToDictionary(kv => kv.Value.Handle, kv => kv.Key);

        int n = 0;
        foreach (var w in list)
        {
            var p = PlacementService.Capture(w.Handle, monitors);
            if (p == null) continue;
            if (byHandle.TryGetValue(w.Handle, out var entry))
            {
                entry.Signature = WindowSignature.From(w);
                entry.Placement = p;
                entry.SavedAt = DateTime.Now;
            }
            else
            {
                state.Windows.Add(new SavedWindow { Signature = WindowSignature.From(w), Placement = p, SavedAt = DateTime.Now });
            }
            n++;
        }
        StateStore.Save();
        return n;
    }

    /// <summary>ウィンドウ単位の記憶を解除する。</summary>
    public static int ForgetWindows(IEnumerable<WindowItem> windows)
    {
        var state = StateStore.Current;
        var matched = WindowMatcher.Match(state.Windows, windows.ToList());
        foreach (var s in matched.Keys) state.Windows.Remove(s);
        StateStore.Save();
        return matched.Count;
    }

    /// <summary>
    /// 現在の配置を記憶する。それまでのグループをすべて破棄し、いま開いているウィンドウを
    /// 表示中のモニタごとに1グループとして作り直す。作成したグループ数とウィンドウ数を返す。
    /// </summary>
    public static (int Groups, int Windows) SnapshotByMonitor(IReadOnlyList<WindowItem> current, List<MonitorItem> monitors)
    {
        var state = StateStore.Current;
        var now = DateTime.Now;
        var groups = new List<WindowGroup>();
        int n = 0;

        foreach (var m in monitors.OrderBy(m => m.DisplayNumber).ThenBy(m => m.Index))
        {
            var g = new WindowGroup { Name = m.Label, SavedAt = now };
            foreach (var w in current.Where(w => w.Monitor == m.Handle))
            {
                var p = PlacementService.Capture(w.Handle, monitors);
                if (p == null) continue;
                g.Members.Add(new SavedWindow { Signature = WindowSignature.From(w), Placement = p, SavedAt = now });
            }
            if (g.Members.Count == 0) continue;
            groups.Add(g);
            n += g.Members.Count;
        }

        state.Groups = groups;
        StateStore.Save();
        return (groups.Count, n);
    }

    /// <summary>
    /// 各ウィンドウに適用する記憶を決める。ウィンドウ単位の記憶（位置を記憶）とグループの記憶
    /// （現在の配置を記憶する）のうち、記憶したモニタがいま接続されているものの中で最も新しいものを使う。
    /// どれも接続されていなければ最も新しいものを返す（戻す処理で「モニタなし」として数える）。
    /// </summary>
    public static Dictionary<IntPtr, (SavedWindow Entry, WindowItem Window)> Resolve(
        IReadOnlyList<WindowItem> current, List<MonitorItem> monitors)
    {
        var state = StateStore.Current;
        var candidates = new Dictionary<IntPtr, List<(SavedWindow Entry, WindowItem Window)>>();

        void Add(IEnumerable<SavedWindow> saved)
        {
            foreach (var (s, w) in WindowMatcher.Match(saved, current))
            {
                if (!candidates.TryGetValue(w.Handle, out var list))
                    candidates[w.Handle] = list = new();
                list.Add((s, w));
            }
        }

        Add(state.Windows);
        foreach (var g in state.Groups) Add(g.Members);

        var result = new Dictionary<IntPtr, (SavedWindow, WindowItem)>();
        foreach (var (handle, list) in candidates)
        {
            var ordered = list.OrderByDescending(c => c.Entry.SavedAt).ToList();
            result[handle] = ordered.FirstOrDefault(c => PlacementService.IsMonitorPresent(c.Entry.Placement, monitors), ordered[0]);
        }
        return result;
    }

    /// <summary>
    /// 記憶した位置に戻す。appearedMonitors を渡すと、そのモニタに記憶していたウィンドウだけを戻す
    /// （モニタ再接続時の自動復元用。動かしていない他のウィンドウには触らない）。
    /// </summary>
    public static (int Ok, int Skipped, List<string> Failed) RestoreTargets(
        IEnumerable<(SavedWindow Entry, WindowItem Window)> targets, List<MonitorItem> monitors,
        IReadOnlyCollection<RECT>? appearedMonitors = null)
    {
        int ok = 0, skipped = 0;
        var failed = new List<string>();

        foreach (var (entry, w) in targets)
        {
            var p = entry.Placement;
            if (!PlacementService.IsMonitorPresent(p, monitors)) { skipped++; continue; }
            if (appearedMonitors != null && !appearedMonitors.Any(r => PlacementService.SameRect(r, p.MonitorBounds, 0)))
                continue;

            if (PlacementService.Restore(w.Handle, p, monitors))
            {
                ok++;
                entry.Signature.LastHandle = w.Handle.ToInt64();
                entry.Signature.Title = w.Title;
            }
            else failed.Add($"{w.Title}（{w.ProcessName}）");
        }

        StateStore.Save();
        return (ok, skipped, failed);
    }
}
