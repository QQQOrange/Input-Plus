using System;
using InputPlus.Core.Settings;

namespace InputPlus.Core.HoldDrag;

/// <summary>
/// 「按住拖拽」模块的宿主。
///
/// 引擎（Coordinator + HookEngine）原本属于独立程序 HoldDrag：它自己读 config.json、
/// 自己装键盘/鼠标钩子、自己开一个钩子线程。并入 Input Plus 之后只换了两件事：
///
///   1. 配置不再落 config.json —— 换成 <see cref="Modules.HoldDrag.ConfigStore"/> 的
///      委托后端，JSON 存在 <see cref="AppSettings.HoldDragJson"/> 里，于是它跟着
///      密码库/云端备份/整份导入导出一起走；原来的 config.json / .bak 轮换逻辑仍然可用。
///   2. 生命周期挂到 <see cref="AppHost"/> 的设置变更上 —— 开关一关就卸钩子，
///      参数一改就重装（换触发键必须重装，旧键的 up 事件不会再来）。
///
/// 钩子线程归 HookEngine 自己管，和 Input Plus 的 KeyboardHook 互不影响：
/// 两边都是 WH_KEYBOARD_LL / WH_MOUSE_LL，链上先后顺序由系统决定，各自只处理自己认的事件。
/// </summary>
public sealed class HoldDragService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings>? _persist;

    private Modules.HoldDrag.ConfigStore? _store;
    private Modules.HoldDrag.Coordinator? _coord;
    private Modules.HoldDrag.HookEngine? _hooks;
    private Modules.HoldDrag.HookWatchdog? _watchdog;

    /// <summary>当前装着的是哪一份 JSON：内容没变就不重装钩子（改样式不该打断拖动）。</summary>
    private string _loadedBlob = "";

    private bool _applying;
    private bool _disposed;

    public HoldDragService(Func<AppSettings> settings, Action<AppSettings>? persist = null)
    {
        _settings = settings;
        _persist = persist;
    }

    /// <summary>钩子现在是不是装着（界面状态行用）。</summary>
    public bool Installed => _hooks != null;

    /// <summary>最后一次装钩子失败/异常的原因，空串表示没出过问题。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>看门狗救回来的次数（钩子被系统摘掉后自动重装）。</summary>
    public int Rescues => _watchdog?.Rescues ?? 0;

    /// <summary>
    /// 界面编辑用的配置副本：引擎在跑就给它当前生效那份的深拷贝，
    /// 没启用就从设置里的 JSON 解析（解析失败给默认值）——两种情况都能直接改。
    /// </summary>
    internal Modules.HoldDrag.AppConfig EditableConfig()
    {
        var live = _store?.Current;
        if (live != null)
        {
            try { return live.Clone(); } catch { }
        }
        return Modules.HoldDrag.ConfigStore.Parse(_settings()?.HoldDragJson ?? "");
    }

    /// <summary>
    /// 把界面上改好的参数交给引擎。**不重装钩子**：触发键、阈值、黑名单这些都是
    /// 引擎每次事件现读 <c>store.Current</c>，重新解析一遍 + OnConfigChanged 就够了；
    /// 真要重装钩子只有"关→开"这一条路（见 <see cref="ApplySettings"/>）。
    /// </summary>
    internal bool ApplyEditedConfig(Modules.HoldDrag.AppConfig cfg)
    {
        if (cfg == null) return false;
        try
        {
            string json = Modules.HoldDrag.ConfigStore.Serialize(cfg);
            AppSettings? settings = _settings();
            if (settings == null) return false;
            settings.HoldDragJson = json;
            // 先记下"现在生效的是哪一份"，再通知宿主落盘：否则保存触发的
            // SettingsChanged → ApplySettings 会以为配置变了，把钩子重装一遍，
            // 正在拖的窗口就断了。通知顺序反过来也成立。
            _loadedBlob = json;
            _persist?.Invoke(settings);
            if (_store != null && _coord != null)
            {
                _store.Load();
                _coord.OnConfigChanged();
            }
            return true;
        }
        catch (Exception ex)
        {
            Report(ex, "应用按住拖拽参数");
            return false;
        }
    }

    /// <summary>
    /// 按当前设置装上/卸下/重装引擎。设置页每次改动、以及启动时的首次装配都走这里，
    /// 所以它必须幂等：同样的开关状态 + 同样的 JSON 内容调用多少次都只是空转。
    /// </summary>
    public void ApplySettings()
    {
        if (_disposed || _applying) return;
        _applying = true;
        try
        {
            AppSettings? settings = _settings();
            if (settings == null || !settings.HoldDragEnabled)
            {
                Stop();
                return;
            }

            string blob = settings.HoldDragJson ?? "";
            if (_hooks != null)
            {
                if (string.Equals(blob, _loadedBlob, StringComparison.Ordinal))
                {
                    // 参数没变（比如用户只是改了别的页）：让引擎把黑名单/程序规则重新读一遍即可。
                    try { _coord?.OnConfigChanged(); } catch (Exception ex) { Report(ex, "刷新按住拖拽配置"); }
                    return;
                }
                Stop();
            }
            Start(blob);
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

    /// <summary>把界面上的改动写回设置并落盘，然后按新参数重装。</summary>
    internal void SaveAndApply(Modules.HoldDrag.AppConfig edited)
    {
        if (edited == null) return;
        try
        {
            _store?.Apply(edited, out _);
        }
        catch (Exception ex)
        {
            Report(ex, "保存按住拖拽配置");
        }
        ApplySettings();
    }

    private void Start(string blob)
    {
        LastError = "";
        var store = new Modules.HoldDrag.ConfigStore(
            () => _settings()?.HoldDragJson ?? "",
            json => Persist(json));
        store.Load();

        var coord = new Modules.HoldDrag.Coordinator(store);
        coord.PrecreateOverlays();
        var hooks = new Modules.HoldDrag.HookEngine(coord);
        // 钩子被看门狗真正重装时，重装缝隙里的事件都丢了：鼠标三本账、触发状态、
        // 欠着的合成抬起必须一起收拾，否则会出现"按住触发键点鼠标没反应"。
        hooks.HooksReinstalled += () =>
        {
            try
            {
                coord.OnHooksReinstalled();
                coord.Trigger.Reset("钩子重装");
            }
            catch (Exception ex) { Report(ex, "钩子重装后复位"); }
        };
        // 激活瞬间要抢到钩子链头，否则别的程序先吃掉鼠标按键就拖不动了。
        coord.ReinstallHooksForPriority = () =>
        {
            try { hooks.ReinstallMouseForChainPriority(); } catch (Exception ex) { Report(ex, "抢占钩子链头"); }
        };

        _store = store;
        _coord = coord;
        _hooks = hooks;
        _loadedBlob = blob;

        bool ok = hooks.Install();
        if (!ok)
        {
            LastError = "钩子安装失败（可能被安全软件拦下了）";
            Stop();
            return;
        }

        var watchdog = new Modules.HoldDrag.HookWatchdog(hooks);
        watchdog.Start();
        _watchdog = watchdog;
    }

    private bool Persist(string json)
    {
        try
        {
            AppSettings? settings = _settings();
            if (settings == null) return false;
            settings.HoldDragJson = json ?? "";
            _persist?.Invoke(settings);
            _loadedBlob = settings.HoldDragJson;
            return true;
        }
        catch (Exception ex)
        {
            Report(ex, "保存按住拖拽配置");
            return false;
        }
    }

    private void Stop()
    {
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

        var coord = _coord;
        _coord = null;
        if (coord != null)
        {
            try { coord.Dispose(); } catch (Exception ex) { Report(ex, "关闭按住拖拽引擎"); }
        }

        _store = null;
        _loadedBlob = "";
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
