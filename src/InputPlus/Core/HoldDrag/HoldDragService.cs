using System;
using InputPlus.Core.Settings;

namespace InputPlus.Core.HoldDrag;

/// <summary>
/// 「按住拖拽」的宿主：装钩子、把设置里的 JSON 交给引擎。
///
/// 引擎换成 altDrag 的同步内核之后，这里比旧版简单得多：
///   · 没有工程文件、没有工作线程、没有覆盖层 —— 只剩 HookEngine + AltDragSink + 看门狗；
///   · 引擎每次事件都现读 <see cref="CurrentConfig"/>，所以改参数（含触发键）不用重装钩子，
///     只有"关 → 开"这一条路会重新 Install()；
///   · 配置仍然只存在 <see cref="AppSettings.HoldDragJson"/> 里，跟着密码库 / 备份 / 导入导出走。
/// </summary>
public sealed class HoldDragService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings>? _persist;

    /// <summary>钩子线程与界面线程都会碰这一份，换配置时整体替换引用，绝不原地改。</summary>
    private readonly object _gate = new object();

    private Modules.HoldDrag.HookEngine? _hooks;
    private Modules.HoldDrag.AltDragSink? _sink;
    private Modules.HoldDrag.HookWatchdog? _watchdog;

    /// <summary>当前生效的配置（引擎每次事件现读这个引用）。</summary>
    private Modules.HoldDrag.AppConfig _config = new Modules.HoldDrag.AppConfig();

    private bool _applying;
    private bool _disposed;

    /// <summary>全局暂停（标题栏的「暂停」按钮）。</summary>
    private volatile bool _suspended;

    /// <summary>
    /// 全局暂停：钩子直接卸掉，连"按住触发键拖窗口"也不再拦。
    ///
    /// 暂停不改 <see cref="AppSettings.HoldDragEnabled"/> —— 恢复时按原设置装回来，
    /// 用户在页面上看到的那颗开关不会因为按了一次暂停就变成关。
    /// </summary>
    public bool Suspended
    {
        get => _suspended;
        set
        {
            if (_suspended == value) return;
            _suspended = value;
            ApplySettings();
        }
    }

    public HoldDragService(Func<AppSettings> settings, Action<AppSettings>? persist = null)
    {
        _settings = settings;
        _persist = persist;
    }

    /// <summary>钩子现在是不是装着（界面状态行用）。</summary>
    public bool Installed => _hooks != null;

    /// <summary>最后一次装钩子失败/异常的原因，空串表示没出过问题。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>
    /// 触发键此刻是不是按着。鼠标手势用它让路：按着触发键说明用户要拖窗口 / 改大小，
    /// 同一颗鼠标键不该再被当成手势起笔（按住 Enter 点右键 = 改大小，不是手势）。
    /// </summary>
    public bool IsTriggerHeld => _sink?.TriggerHeld == true;

    /// <summary>
    /// v1.6.0：把"这个键是哪台键盘按的"这件事交给内核（AppHost 接的是 KeyboardDeviceTracker）。
    /// 配置里选了设备时，只有同一台键盘发出的触发键才生效。
    /// </summary>
    public Func<int, string?>? DeviceResolver
    {
        set => Modules.HoldDrag.AltDragSink.DeviceResolver = value;
    }

    /// <summary>看门狗救回来的次数（钩子被系统摘掉后自动重装）。</summary>
    public int Rescues => _watchdog?.Rescues ?? 0;

    /// <summary>界面编辑用的副本（改完再交给 <see cref="SaveAndApply"/>）。</summary>
    internal Modules.HoldDrag.AppConfig EditableConfig()
    {
        lock (_gate)
        {
            return _config.Clone();
        }
    }

    /// <summary>把界面上改好的参数交给引擎：写回设置、落盘、替换生效配置。不重装钩子。</summary>
    internal bool ApplyEditedConfig(Modules.HoldDrag.AppConfig cfg)
    {
        if (cfg == null) return false;
        try
        {
            cfg.Normalize();
            string json = cfg.Serialize();
            AppSettings? settings = _settings();
            if (settings == null) return false;
            settings.HoldDragJson = json;
            lock (_gate)
            {
                _config = cfg.Clone();
            }
            _persist?.Invoke(settings);
            return true;
        }
        catch (Exception ex)
        {
            Report(ex, "应用按住拖拽参数");
            return false;
        }
    }

    /// <summary>
    /// 按当前设置装上 / 卸下引擎。幂等：开关与 JSON 没变就只是把配置重读一遍。
    /// </summary>
    public void ApplySettings()
    {
        if (_disposed || _applying) return;
        _applying = true;
        try
        {
            AppSettings? settings = _settings();
            if (settings == null || _suspended || !settings.HoldDragEnabled)
            {
                Stop();
                return;
            }

            Modules.HoldDrag.AppConfig fresh = Modules.HoldDrag.AppConfig.Parse(settings.HoldDragJson);
            lock (_gate)
            {
                _config = fresh;
            }

            if (_hooks == null)
            {
                Start();
            }
        }
        catch (Exception ex)
        {
            Report(ex, "装配按住拖拽");
            Stop();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>把界面上的改动写回设置并落盘（界面层会在之后统一保存，这里也保证引擎拿到新值）。</summary>
    internal void SaveAndApply(Modules.HoldDrag.AppConfig edited)
    {
        if (edited == null) return;
        ApplyEditedConfig(edited);
    }

    private void Start()
    {
        LastError = "";
        // v1.6.2：生产路径也要初始化 holddrag.log。
        // 之前只有 --drag-e2e 会 Log.Init，正常运行时 Log.Write 因为 _path 为空直接 return ——
        // 用户报「按住拖拽没反应」时，日志里一个字都没有，只能靠猜。
        try
        {
            if (string.IsNullOrEmpty(Modules.HoldDrag.Log.Path))
            {
                Modules.HoldDrag.Log.Init(Modules.HoldDrag.AppPaths.DataDir);
            }
        }
        catch
        {
        }

        var sink = new Modules.HoldDrag.AltDragSink(CurrentConfig, SetMouseHookWanted);
        var hooks = new Modules.HoldDrag.HookEngine(sink);
        // 钩子被看门狗真正重装时，重装缝隙里的按键都丢了：把 sink 的"按下过"状态清掉，
        // 否则会留下一个永远不会收到 up 的触发键，表现为"按住触发键点鼠标没反应"。
        hooks.HooksReinstalled += () =>
        {
            try
            {
                sink.Rearm();
            }
            catch (Exception ex)
            {
                Report(ex, "钩子重装后复位");
            }
        };

        _sink = sink;
        _hooks = hooks;

        if (!hooks.Install())
        {
            LastError = "钩子安装失败（可能被安全软件拦下了）";
            Modules.HoldDrag.Log.Error("按住拖拽：键盘钩子安装失败，Win32 错误码 " + hooks.LastError);
            Stop();
            return;
        }

        // 启动一行把"当前生效的是什么"记下来：现场排障时这一行往往就够定位了。
        Modules.HoldDrag.Log.Info("按住拖拽已启动：触发键=" + _config.TriggerKey + "（" + Modules.HoldDrag.KeysUtil.DisplayName(_config.TriggerKey) +
            "） 长按=" + _config.TriggerHoldMs + "ms 设备=" + Modules.HoldDrag.AppConfig.DeviceLabel(_config.Device) +
            " 左=" + _config.ActionLeft + " 中=" + _config.ActionMiddle + " 右=" + _config.ActionRight);

        var watchdog = new Modules.HoldDrag.HookWatchdog(hooks);
        watchdog.Start();
        _watchdog = watchdog;
    }

    /// <summary>
    /// 引擎要求装 / 卸鼠标钩子：长按计时一开始就装（兜底路径靠那一下点击），
    /// 松手、拖完、Esc、钩子重装之后卸掉 —— 和 altdrag 的 HookMouse()/UnhookMouse() 同一套时序。
    /// </summary>
    private void SetMouseHookWanted(bool wanted)
    {
        try
        {
            _hooks?.SetMouseHookWanted(wanted);
        }
        catch (Exception ex)
        {
            Report(ex, "装卸鼠标钩子");
        }
    }

    /// <summary>引擎每次事件调它取当前配置（引用是原子替换的，返回后不要修改）。</summary>
    private Modules.HoldDrag.AppConfig CurrentConfig()
    {
        return _config;
    }

    private void Stop()
    {
        Modules.HoldDrag.Log.Info("按住拖拽已停止（开关关闭 / 全局暂停 / 出错）");
        var watchdog = _watchdog;
        _watchdog = null;
        if (watchdog != null)
        {
            try { watchdog.Stop(); } catch { }
            try { watchdog.Dispose(); } catch { }
        }

        var hooks = _hooks;
        _hooks = null;
        if (hooks != null)
        {
            try { hooks.Uninstall(); } catch (Exception ex) { Report(ex, "卸下按住拖拽钩子"); }
            try { hooks.Dispose(); } catch { }
        }

        var sink = _sink;
        _sink = null;
        if (sink != null)
        {
            try { sink.Dispose(); } catch { }
        }
    }

    private void Report(Exception ex, string what)
    {
        LastError = what + "：" + ex.Message;
        try { CoreLog.Swallow(ex, what); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
