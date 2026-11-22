using System.Diagnostics;
using System.IO;

namespace InputPlus.Core.FileDialog;

/// <summary>
/// 第一层：发现候选对话框窗口。
///
/// 用 <c>SetWinEventHook(EVENT_OBJECT_CREATE/SHOW/DESTROY/LOCATIONCHANGE)</c>，
/// 标志 <c>WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS</c>。
///
/// 这一层**不注入、不挂全局钩子**：OUTOFCONTEXT 的回调由系统在安装线程的
/// 消息循环里投递，进程本身完全不被碰。SkipOwnProcess 保证不会把自己的窗口
/// 当成候选（WPF 的 SaveFileDialog 也带 #32770，不跳过就会自己套自己）。
///
/// 去抖：同一个 HWND 在 500ms 内只处理一次。CREATE 和 SHOW 往往前后脚到，
/// 不去抖就会建两次浮层（第二次把第一次的位置/输入状态覆盖掉）。
///
/// 兜底：SHOW 事件在跨会话/远程桌面下偶发丢失，服务层可以每 300ms 调一次
/// <see cref="PollOnce"/>。这里**只枚举顶层窗口**，绝不递归遍历子窗口。
/// </summary>
public sealed class FileDialogWatcher : IDisposable
{
    /// <summary>
    /// 同一 HWND 的重复 SHOW 合并窗口。
    ///
    /// 以前是 500ms：对话框常常先发一次 SHOW（窗口还没就位/还没到前台），真正的 SHOW 在
    /// 500ms 内被吞掉 —— 表现就是"有时候出现，有时候不出现"。现在压到 250ms，
    /// 并且服务层对"已经挂着浮层"的重复 SHOW 是幂等的，所以放过一次不会闪。
    /// </summary>
    private const int DebounceMs = 250;

    private readonly object _gate = new();
    private readonly Dictionary<nint, long> _lastSeen = new();
    private readonly HashSet<nint> _tracked = new();

    private DialogNative.WinEventDelegate? _proc;   // 必须保存引用，否则回调被 GC 回收
    private nint _hook;

    /// <summary>发现（或重新显示）一个 #32770 对话框。</summary>
    public event Action<DialogWindowInfo>? Appeared;

    /// <summary>对话框销毁。参数是原始 HWND；此时窗口已经不可用，不要再去读它。</summary>
    public event Action<nint>? Destroyed;

    /// <summary>对话框移动/缩放（未节流；浮层自己做 16ms 节流）。</summary>
    public event Action<nint, DialogRect>? BoundsChanged;

    public bool IsRunning => _hook != 0;

    /// <summary>当前跟踪中的窗口数量（销毁后必须归零，验收用例 14 靠它）。</summary>
    public int TrackedCount
    {
        get { lock (_gate) return _tracked.Count; }
    }

    public IReadOnlyList<nint> Tracked
    {
        get { lock (_gate) return _tracked.ToList(); }
    }

    public bool Start()
    {
        if (_hook != 0) return true;

        try
        {
            _proc = OnWinEvent;
            _hook = DialogNative.SetWinEventHook(
                DialogNative.EVENT_OBJECT_CREATE,
                DialogNative.EVENT_OBJECT_LOCATIONCHANGE,
                0, _proc, 0, 0,
                DialogNative.WINEVENT_OUTOFCONTEXT | DialogNative.WINEVENT_SKIPOWNPROCESS);

            if (_hook == 0)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                CoreLog.Warn($"[文件对话框] SetWinEventHook 安装失败（Win32 错误 {err}），文件对话框浮层不可用");
                _proc = null;
                return false;
            }

            CoreLog.Info("[文件对话框] 窗口监听已安装（OUTOFCONTEXT + SKIPOWNPROCESS）");
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 窗口监听安装异常：" + ex.Message);
            _proc = null;
            return false;
        }
    }

    /// <summary>
    /// 兜底发现：只枚举顶层 #32770。300ms 调一次，正常情况什么都不做。
    /// 返回值 = 本次新发现的窗口数（测试与日志用）。
    /// </summary>
    public int PollOnce()
    {
        int found = 0;
        foreach (nint hwnd in DialogProbe.TopLevelDialogWindows())
        {
            if (IsTracked(hwnd)) continue;
            if (!TryMarkSeen(hwnd)) continue;
            if (RaiseAppeared(hwnd)) found++;
        }
        return found;
    }

    /// <summary>
    /// 把窗口登记成"已跟踪"。只对 #32770 生效 —— 这一层不猜类型，
    /// 类名不对的一律不进来（类型判定是 Classifier 的事）。
    /// </summary>
    public void Track(nint hwnd)
    {
        if (hwnd == 0) return;
        if (DialogNative.ClassOf(hwnd) != DialogProbe.DialogClass) return;
        if (!TryMarkSeen(hwnd)) return;
        RaiseAppeared(hwnd);
    }

    /// <summary>窗口销毁时清理。服务层在 Destroyed 里也会调一次，幂等。</summary>
    public void Forget(nint hwnd)
    {
        lock (_gate)
        {
            _tracked.Remove(hwnd);
            _lastSeen.Remove(hwnd);
        }
    }

    public bool IsTracked(nint hwnd)
    {
        lock (_gate) return _tracked.Contains(hwnd);
    }

    private bool TryMarkSeen(nint hwnd)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastSeen.TryGetValue(hwnd, out long last) && now - last < DebounceMs)
                return false;
            _lastSeen[hwnd] = now;
            _tracked.Add(hwnd);
        }
        return true;
    }

    private bool RaiseAppeared(nint hwnd)
    {
        try
        {
            DialogWindowInfo? info = Describe(hwnd);
            if (info == null) return false;
            Appeared?.Invoke(info);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("[文件对话框] 处理新窗口失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>构造窗口信息快照（进程名/路径/标题只在这里读一次）。</summary>
    public static DialogWindowInfo? Describe(nint hwnd)
    {
        if (hwnd == 0 || !DialogNative.IsWindow(hwnd)) return null;

        string cls = DialogNative.ClassOf(hwnd);
        if (cls.Length == 0) return null;

        uint tid = DialogNative.GetWindowThreadProcessId(hwnd, out uint pid);
        string path = DialogNative.ProcessPathOf(pid);
        string exe = "";
        try { exe = path.Length > 0 ? Path.GetFileName(path).ToLowerInvariant() : ""; } catch { }

        return new DialogWindowInfo
        {
            Hwnd = hwnd,
            ThreadId = tid,
            ProcessId = pid,
            ProcessName = exe,
            ProcessPath = path,
            ClassName = cls,
            Title = DialogNative.TitleOf(hwnd),
            FirstSeen = DateTimeOffset.Now,
        };
    }

    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // 只要窗口自身的事件；菜单、光标、子控件的事件全部忽略
            if (idObject != 0 || idChild != 0 || hwnd == 0) return;

            switch (evt)
            {
                case DialogNative.EVENT_OBJECT_CREATE:
                case DialogNative.EVENT_OBJECT_SHOW:
                {
                    if (DialogNative.ClassOf(hwnd) != DialogProbe.DialogClass) return;
                    if (!DialogNative.IsWindowVisible(hwnd)) return;
                    if (!TryMarkSeen(hwnd)) return;
                    RaiseAppeared(hwnd);
                    break;
                }

                case DialogNative.EVENT_OBJECT_DESTROY:
                case DialogNative.EVENT_OBJECT_HIDE:
                {
                    bool wasTracked;
                    lock (_gate) wasTracked = _tracked.Remove(hwnd);
                    lock (_gate) _lastSeen.Remove(hwnd);
                    if (!wasTracked) return;
                    // #32770 里嵌套的对话框被销毁时也走这里；服务层按当前 HWND 判断即可
                    if (evt == DialogNative.EVENT_OBJECT_DESTROY || !DialogNative.IsWindow(hwnd))
                        Destroyed?.Invoke(hwnd);
                    break;
                }

                case DialogNative.EVENT_OBJECT_LOCATIONCHANGE:
                {
                    /* 某些系统/远程桌面下 SHOW 会丢，只来 LOCATIONCHANGE：
                     * 窗口还没进跟踪表、但确实是可见的 #32770 → 当成"出现"处理。 */
                    if (!IsTracked(hwnd))
                    {
                        if (DialogNative.ClassOf(hwnd) != DialogProbe.DialogClass) return;
                        if (!DialogNative.IsWindowVisible(hwnd)) return;
                        if (!TryMarkSeen(hwnd)) return;
                        RaiseAppeared(hwnd);
                        return;
                    }
                    DialogRect r = DialogNative.FrameBounds(hwnd);
                    if (r.IsEmpty) return;
                    BoundsChanged?.Invoke(hwnd, r);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 回调里绝不能抛：异常穿回系统会让钩子被摘掉
            CoreLog.Warn("[文件对话框] 窗口事件处理失败：" + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            try { DialogNative.UnhookWinEvent(_hook); } catch { }
            _hook = 0;
        }
        _proc = null;
        lock (_gate)
        {
            _tracked.Clear();
            _lastSeen.Clear();
        }
        CoreLog.Info("[文件对话框] 窗口监听已卸载");
    }
}
