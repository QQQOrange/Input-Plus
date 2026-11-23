using InputPlus.Core.Macro;
using InputPlus.Core.Settings;
using InputPlus.Core.Windows;
using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Input;

/// <summary>
/// 触发键：VK 集合缓存、按下/抬起/长按/轻触判定与短按补发。
/// </summary>
public sealed partial class HotkeyEngine
{
    /* DEBUG-SNIP：`CLP_SNIPDBG=1` 时把片段判定的每一步写进日志。
     * 只在排查"别名为什么不展开"时打开，平时零开销（一次静态读）。 */
    private static readonly bool SnippetDebugOn =
        Environment.GetEnvironmentVariable("CLP_SNIPDBG") == "1";

    private string SnippetModsText()
        => (_ctrlDown ? "C" : "-") + (_shiftDown ? "S" : "-")
           + (_laltDown ? "A" : "-") + (_lwinDown || _rwinDown ? "W" : "-");

    /// <summary>
    /// 触发键的虚拟键码集合（缓存）。
    ///
    /// <see cref="IsTrigger"/> 在**每一个按键**的钩子回调里都要跑一次。
    /// 原实现每次都遍历 Triggers 并对每个 Key 做一次
    /// `KeyName.VkOf()`（字符串 → 字典查找），也就是每次按键都做
    /// 1~N 次字符串比较 + 字典查找 —— 纯浪费，而钩子回调对延迟极其敏感
    /// （超过 LowLevelHooksTimeout 会被系统静默摘钩）。
    ///
    /// 失效判据用**内容指纹**而不是"列表实例 + 条数"：
    /// 用户在界面上改触发键的 Key 时列表实例和条数都不变，
    /// 只看那两项会导致"改了触发键不生效"。指纹里带上每个键名和启用状态。
    /// </summary>
    private List<TriggerMatch>? _triggerMatchCache;
    private string _triggerVkCacheStamp = "";

    private static string TriggerStamp(List<TriggerKey> list)
    {
        // 参数很少（通常 1~3 个），拼串开销远小于每次按键都查字典
        var sb = new System.Text.StringBuilder(32);
        foreach (var t in list)
        {
            sb.Append(t.Enabled ? '1' : '0').Append(t.Key).Append('|');
        }
        return sb.ToString();
    }

    /// <summary>
    /// 一个触发键的匹配条件。
    ///
    /// 触发键**只允许单一按键**：一个物理键按下即进层。
    /// 组合键（Ctrl+K 之类）不能当触发键 —— 那会和"层内修饰键子层"
    /// 语义冲突：同样是 Ctrl+K，到底算触发还是算子层绑定？
    ///
    /// 组合键保留给**层内绑定**（进层后按住 Ctrl 再按 K），
    /// 两套机制职责清晰。
    /// </summary>
    private readonly record struct TriggerMatch(int Vk)
    {
        /// <summary>
        /// 用 <see cref="SameKey"/> 而不是直接比大小：
        /// 有些驱动发通用修饰码（0x12），有些发左右专用码（0xA5），
        /// 触发键配置里写的又是其中一种 —— 只比大小会"配置对了却按不出来"。
        /// </summary>
        public bool Matches(int vk) => SameKey(Vk, vk);
    }

    private List<TriggerMatch> TriggerMatchList(AppSettings s)
    {
        var list = s.Triggers;
        string stamp = TriggerStamp(list);
        if (_triggerMatchCache != null && _triggerVkCacheStamp == stamp)
            return _triggerMatchCache;

        var matches = new List<TriggerMatch>();
        foreach (var t in list)
        {
            if (!t.Enabled) continue;
            if (ParseTrigger(t.Key) is { } m) matches.Add(m);
        }

        _triggerMatchCache = matches;
        _triggerVkCacheStamp = stamp;
        return matches;
    }

    /// <summary>
    /// 把触发键字符串解析成匹配条件。
    ///
    /// **只接受单一按键**：如果配置里写了组合键（如 <c>caps_ctrl_k</c>），
    /// 这里会取出主键 K 并当作单键触发 —— 这样老配置不会彻底失效，
    /// 但也明确表达"组合触发已不再支持"。
    /// </summary>
    /// <summary>
    /// 诊断用：把外部配置里的键名解析结果描述出来（--dump-triggers）。
    /// 返回人类可读的结论，顺便回答"这个触发键会不会匹配 Tab(0x09)"。
    /// </summary>
    internal static string DescribeTriggerForDiag(string? key)
    {
        var m = ParseTrigger(key);
        if (m == null) return "解析失败（键名认不出来，永远不会触发）";
        return m.Value.Matches(0x09) ? "解析成功，且匹配 Tab(0x09)" : "解析成功";
    }

    private static TriggerMatch? ParseTrigger(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        // 组合键 ID 形式：取主键，忽略修饰部分
        var parsed = KeyName.ParseCombo(key);
        if (parsed != null)
        {
            var (_, k) = parsed.Value;
            if (KeyName.VkOf(k) is int vk && vk != 0) return new TriggerMatch(vk);
        }

        if (KeyName.VkOf(key) is int single && single != 0)
            return new TriggerMatch(single);

        return null;
    }

    /// <summary>
    /// 同步修饰键的按下/抬起状态。
    ///
    /// 必须对**左右两个键码和通用码**都认：有些键盘/驱动只发 0x11/0x10/0x12，
    /// 完全不发 LControl/RControl 这些具体码。
    /// </summary>
    private void UpdateModifierState(int vk, bool down)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = down;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = down;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = down;
                break;
            case VK.LWin:
                _lwinDown = down;
                break;
            case VK.RWin:
                _rwinDown = down;
                break;
            default:
                return;      // 不是修饰键，什么都不做
        }

        /* 这里**不能**从 _swallowedUp 里删。
         *
         * 层内按下 Ctrl/Shift/Alt 时，OnKeyDownInLayer 会把它记进 _swallowedUp，
         * 抬起必须由 OnKeyUpInLayer 原子地取走并吞掉；本方法在按键处理链的最前面
         * 执行，先删掉的话 OnKeyUpInLayer 查不到 → 前台收到一个"从没按下过的抬起"，
         * 游戏 / 远程桌面会判定输入异常。 */
    }

    /// <summary>
    /// 把某个键对应的修饰键标记清零（不管它当前是什么状态）。
    ///
    /// 专供"触发键本身就是修饰键"的场景：右 Alt 按住期间的自动重复
    /// 不能把自己算成"按住的 Alt"，否则层内绑定全部查不到。
    /// </summary>
    private void ClearModifierFlag(int vk)
    {
        switch (vk)
        {
            case VK.LControl or VK.RControl or 0x11:
                _ctrlDown = false;
                break;
            case VK.LShift or VK.RShift or 0x10:
                _shiftDown = false;
                break;
            case VK.LMenu or VK.RMenu or 0x12:
                _laltDown = false;
                break;
            case VK.LWin:
                _lwinDown = false;
                break;
            case VK.RWin:
                _rwinDown = false;
                break;
        }
    }

    /// <summary>
    /// 两个键码是否指同一个物理键。
    ///
    /// 修饰键有三种写法：左右专用码（0xA4 / 0xA5）、通用码（0x12）。
    /// 不同键盘 / 驱动 / 远程桌面会话发的并不一致 ——
    /// 触发键配的是 "ralt"（0xA5），某些环境却只发 0x12，
    /// 或者按下发专用码、抬发送通用码。
    ///
    /// 只做**同一修饰键内部**的等价：左 Alt 和右 Alt 仍然互不相等，
    /// 用户想分别绑两个键的需求不受影响。
    /// </summary>
    private static bool SameKey(int a, int b) => a == b || (a, b) switch
    {
        (0x12, 0xA4) or (0xA4, 0x12) => true,       // Alt   ↔ 左 Alt
        (0x12, 0xA5) or (0xA5, 0x12) => true,       // Alt   ↔ 右 Alt
        (0x11, 0xA2) or (0xA2, 0x11) => true,       // Ctrl  ↔ 左 Ctrl
        (0x11, 0xA3) or (0xA3, 0x11) => true,       // Ctrl  ↔ 右 Ctrl
        (0x10, 0xA0) or (0xA0, 0x10) => true,       // Shift ↔ 左 Shift
        (0x10, 0xA1) or (0xA1, 0x10) => true,       // Shift ↔ 右 Shift
        _ => false,
    };





    private bool IsTrigger(AppSettings s, int vk)
    {
        return TriggerMatchList(s).Any(m => m.Matches(vk));
    }

    /// <summary>找出这个 VK 对应的触发键配置（组合键也认）。</summary>
    private static TriggerKey? FindTrigger(AppSettings s, int vk)
        => s.Triggers.FirstOrDefault(t =>
            t.Enabled && ParseTrigger(t.Key) is { } m && m.Matches(vk));

    private void OnTriggerDown(AppSettings s, int vk, bool wasPhysicallyDown)
    {
        /* 自动重复：按住触发键时系统会反复发 down。
         *
         * 这里必须先把这个键的修饰键状态清掉 —— 触发键若是右 Alt / 右 Ctrl，
         * 重复的 down 会把 `_laltDown` 又置成 true（上面虽然拦了一道，
         * 但状态可能被其它路径写脏，这里再兜一次底），
         * 层内绑定就会被拼成 caps_lalt_xxx 而永远查不到。
         *
         * ⚠ 但"按住"这个判断本身可能是假的：上一次的抬起事件如果丢了，
         * `_triggerHeld` 会卡在 true，于是**这一次真实的按下被当成自动重复
         * 直接吞掉** —— 用户感受到的就是"触发键偶尔按一下没反应"。
         * 所以先拿物理状态核对：这个键现在其实没被按住 → 按全新按下处理。 */
        long nowTick = Environment.TickCount64;

        if (_triggerHeld)
        {
            /* 区分"系统自动重复"和"用户重新按下"。
             *
             * 主判据改为**物理按下状态**：钩子已经看到过这个键的 down、
             * 还没看到 up（wasPhysicallyDown == true），那它就是按住不放
             * 产生的重复 down，与键盘重复速率无关。
             *
             * 旧实现只看"两次 down 间隔 < 100ms"：Windows 的首次重复延迟
             * 默认 250~500ms 且可调，用户把重复速率调慢后，第二次起的 down
             * 全部落在 100ms 之外，被误判成"重新按下"→ 复位 → 重新进层，
             * 表现就是"层反复被踢掉、停不在目标层"。
             *
             * 兜底：极少数环境里 up 事件会丢，物理状态还残留；但 100ms 内
             * 不可能有人真的"松开再按下"，这种情况仍按重复处理。 */
            bool isAutoRepeat = wasPhysicallyDown
                                || (_lastTriggerDownTick != 0
                                    && nowTick - _lastTriggerDownTick < 100);

            if (isAutoRepeat)
            {
                _lastTriggerDownTick = nowTick;
                ClearModifierFlag(vk);
                return;      // 真的自动重复
            }

            CoreLog.Info($"触发键 0x{_activeTriggerVk:X2} 重新按下（上次会话没有正常结束），复位后重新开始");
            ResetTriggerState("触发键重新按下");
        }

        _lastTriggerDownTick = nowTick;

        /* ⚠ 先确定是哪个触发键，**再**清零修饰键状态。
         *
         * 组合键触发（Ctrl+Shift+K）依赖"当前按住哪些修饰键"来判断，
         * 一旦先清零就永远匹配不上 —— 组合触发键会彻底失效。 */
        var trigger = FindTrigger(s, vk);

        _triggerHeld = true;
        _triggerDownTime = DateTime.Now;
        RequestFastHoldTick();
        _usedCombo = false;
        _usedOtherKey = false;
        _tapHandled = false;

        /* 只清**自己**的修饰键标记，不能把五个全清。
         *
         * 早先这里是 `_laltDown = _lwinDown = ... = false` ——
         * 用户先按住 Shift 再按触发键时，Shift 明明还按着，
         * 却被这一行抹掉了，层内的 caps_shift_xxx 绑定全部查不到。
         * 而 Shift 的 down 早就放行给了系统，用户以为自己在按 Shift。 */
        ClearModifierFlag(vk);

        _swallowedDown.Clear();          // 上一次会话的"重复判定"作废
        /* _swallowedUp 不能整体清：它是"按下被吞、抬起还没来"的欠账。
         * 清掉之后，这些键的 up 会漏给前台（见 OnKeyEvent 第 3 步）。
         * 只在异常堆积（up 真的丢了）时兜底。 */
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();

        _activeTriggerVk = vk;
        _activeTrigger = trigger;
        _activeLayer = trigger?.LayerId ?? 1;

        // 自检模式：立刻进层，不等真实时间流逝
        if (EnterLayerImmediatelyForTest && !SimulateShortPressForTest)
            OnHoldThresholdReached();
        else
            _layerActive = false;

        TriggerHeldChanged?.Invoke(true);
    }

    private void OnTriggerUp(AppSettings s, int vk)
    {
        /* 先清掉自己的修饰键标记：触发键是修饰键时（右 Alt / 右 Ctrl），
         * 抬起绝不能被当成"释放了一个层内修饰键"。 */
        ClearModifierFlag(vk);

        // 允许"按下 0xA5、抬起 0x12"这种混发（见 SameKey）
        if (!_triggerHeld || !SameKey(vk, _activeTriggerVk)) return;

        /* ---- 松手前补做最后一次长按判定 ----
         *
         * 长按判定有两路：钩子在每个按键事件上比对时间戳（主判据）、
         * 界面线程每 20ms 轮询（纯按住时的兜底）。但"按住之后一个键都不按"
         * 的时候只有计时器在跑，界面一忙它就晚到 —— 于是明明按满了阈值，
         * 松手这一刻 _layerActive 仍是 false，被当成短按补发一次原键
         * （用户看到"切完层松手，焦点又跳一下"）。
         *
         * 在清掉 _triggerHeld **之前**补判一次：按满阈值就一定算长按，
         * 该吞的键一个都不会漏出去；也不会出现"长按了却什么都没发生"。
         */
        int holdLimit = _activeTrigger?.HoldMs ?? 200;
        if (!_layerActive &&
            (DateTime.Now - _triggerDownTime).TotalMilliseconds >= holdLimit)
        {
            OnHoldThresholdReached();      // 置 _layerActive / _tapHandled
        }

        _triggerHeld = false;

        var trigger = _activeTrigger;

        /* 这一按算不算"干净短按"。
         *
         * 判据就是"有没有真的进层"：进过层（_layerActive）或已按长按处理过
         * （_tapHandled）就绝不补发；只有从头到尾没进层，才把原键补发出去。
         *
         * 于是"短按发 Tab、长按一个 Tab 都不发"是**同一条判定**推出来的结果，
         * 不需要再额外拿时长去猜 —— 时长只用来决定"进不进层"。 */
        bool cleanTap = !_usedCombo && !_usedOtherKey && !_layerActive;

        // 未进层的干净抬起 = 短按
        if (cleanTap && !_tapHandled && trigger != null)
        {
            /* 短按：**补发一次真实的按下+抬起**，让原功能生效。
             *
             * 因为整个按住过程都被我们吞掉了（见 OnKeyEvent 的注释），
             * 系统完全没收到这个键，所以这里必须自己补一次。
             *
             * 特殊处理「短按动作」：若用户把短按行为改成了别的动作
             * （比如改成 Esc、打开 Qbar），那就执行那个动作，不补发原键；
             * 若短按行为是"切换大小写"（默认），补发真实 CapsLock 即可 ——
             * 效果完全一致，但系统的大写状态是真实翻转的，
             * 不像自己合成按键那样可能与实际状态脱节。
             */
            string tapAction = trigger.TapAction ?? "";

            /* 双击判定必须在"补发原键"之前。
             *
             * 早先的写法把双击判定放在补发分支里面，导致默认配置
             * （TapAction = toggleCapsLock，会走补发原键那条路）时
             * **永远进不到双击分支** —— 双击打开 Qbar 因此失效。 */
            bool doubleTap = !string.IsNullOrEmpty(trigger.DoubleTapAction)
                             && _hasLastTap
                             && (DateTime.Now - _lastTapTime).TotalMilliseconds < DoubleTapWindowMs
                             && (DateTime.Now - _triggerDownTime).TotalMilliseconds < DoubleTapWindowMs;

            if (doubleTap)
            {
                /* 第一次短按如果补发过 CapsLock，真实的大写状态已经被翻转了一次。
                 * 双击动作（典型是打开 Qbar）执行前先补一次，把状态还原；
                 * 否则用户双击唤出面板时，输入法/键盘已经处于大写。 */
                bool undoFirstTapCaps = _lastTapReplayed && vk == 0x14;
                _hasLastTap = false;            // 双击只算一次，避免三连击继续触发
                _lastTapReplayed = false;
                string dt = trigger.DoubleTapAction;
                if (undoFirstTapCaps)
                {
                    _queue.Post(() =>
                    {
                        InputSender.Tap(0x14);
                        RunTapAction(dt);
                    });
                }
                else
                {
                    _queue.Post(() => RunTapAction(dt));
                }
            }
            else
            {
                _lastTapTime = DateTime.Now;
                _hasLastTap = true;
                _lastTapReplayed = false;

                /* 短按行为的判定。
                 *
                 * 新增 "keepKey" = **保留按键原功能**（显式选项）。
                 * 以前只有 toggleCapsLock / 空 / doNothing 才走补发原键那条路，
                 * 用户把 F5、问号这类键设成触发键时，短按就不知道该干嘛了
                 * （默认值还是 toggleCapsLock，跟那些键完全不搭）。
                 * 现在「触发键与层」页可以直接选"按键原功能"，
                 * 语义明确：短按 = 当这个键没被改造过一样用。 */
                if (tapAction is "toggleCapsLock" or "" or "doNothing" or "keepKey")
                {
                    _lastTapReplayed = true;

                    /* 补发原键：对 CapsLock 是翻转大写，对 F5 是打开刷新，
                     * 对问号是正常输入 "?" —— 系统状态是真实产生的。 */
                    int vkToSend = vk;
                    _queue.Post(() =>
                    {
                        /* 只挡"层已经生效"这一种情况 —— 那是唯一会让原键
                         * 从层里漏出去的时刻。
                         *
                         * 不能挡 _triggerHeld：用户快速连按两下时，第二次按下
                         * 会把第一次的补发一起吞掉，表现为"按了没反应"。
                         * 补发属于**已经结束的那一次**按键，与新的按住无关。 */
                        if (_layerActive)
                        {
                            _lastTapReplayed = false;
                            return;
                        }
                        TapReplayCountForTest++;
                        InputSender.Tap(vkToSend);
                    });
                }
                else
                {
                    string ta = tapAction;
                    _queue.Post(() => RunTapAction(ta));
                }
            }
        }

        _layerActive = false;
        _activeTriggerVk = -1;
        _activeTrigger = null;
        _activeLayer = -1;
        /* 只清"重复判定"表。欠着的抬起继续留在 _swallowedUp 里，
         * 等它自己的 up 到达时由 OnKeyEvent 第 3 步吞掉 ——
         * 否则"先松触发键、后松层内键"就会漏一个孤儿 keyup 给前台。 */
        _swallowedDown.Clear();
        if (_swallowedUp.Count > MaxPendingUps) _swallowedUp.Clear();
        /* 子层状态跟着触发键一起退出：否则松开触发键后 ActiveSubLayer
         * 还停在 "tab"，界面会显示一个不存在的子层。 */
        if (!string.IsNullOrEmpty(_subLayerKeyDown))
        {
            _subLayerKeyDown = "";
            SubLayerChanged?.Invoke("");
        }
        ClearContinuousRepeat();      // 层都没了，连续调节当然要停
        TriggerHeldChanged?.Invoke(false);
    }

    /// <summary>
    /// 长按判定：由 UI 线程的计时器调用（钩子回调里不能做定时）。
    /// 超过阈值就进入层模式，并触发一次短按动作（因为用户没松手，
    /// 不会再有机会走"抬起时短按"分支）。
    /// </summary>
    private void OnHoldThresholdReached()
    {
        if (!_triggerHeld || _layerActive) return;
        _layerActive = true;
        _tapHandled = true;      // 已经算长按，抬起时不再补发原功能

        /* ⚠ 这里**不能**执行短按动作。
         *
         * 早先的实现除了切层，还顺手跑了一次 TapAction（切换大小写）——
         * 结果就是"每次长按进层，大写状态都被莫名翻一次"。
         * 长按的语义就是"这个键变成功能键前缀"，它本身什么都不该做。 */
    }

    /// <summary>执行短按动作。统一走动作表，便于在按键映射界面里改。</summary>
    private void RunTapAction(string action)
    {
        try
        {
            if (!string.IsNullOrEmpty(action) && action != "doNothing")
                LogAction(action, "");
            _invoke(action, "");
        }
        catch (Exception ex) { CoreLog.Error("短按动作失败", ex); }
    }

    /// <summary>层模式下按下某个键。返回 true 表示吞掉。</summary>
    /// <summary>
    /// 叠加层：前台命中"按应用启用"的规则时，先查这一层有没有绑定。
    ///
    /// 与触发键的层的**根本区别**：这里不接管未绑定的键。
    /// 触发层的语义是"键盘交给这一层"，而叠加层是"在原有键盘上多几个快捷键"——
    /// 否则在 IDE 里打字会被整片吞掉。
    /// </summary>
    private bool TryRunAppLayerBinding(AppSettings s, int vk)
    {
        int layer = _appLayerId;
        if (layer <= 0) return false;

        // 系统自动重复：已经执行过，吞掉但不再触发
        if (_swallowedDown.ContainsKey(vk)) return true;

        string? keyName = KeyName.NameOf(vk);
        if (keyName == null) return false;

        var mods = KeyModifiers.None;
        if (_laltDown) mods |= KeyModifiers.Alt;
        if (_lwinDown || _rwinDown) mods |= KeyModifiers.Win;
        if (_ctrlDown) mods |= KeyModifiers.Ctrl;
        if (_shiftDown) mods |= KeyModifiers.Shift;

        var binding = FindInLayer(s, layer, KeyName.BuildComboEx(mods, keyName));
        if (binding == null || !binding.IsBound) return false;   // 没绑 → 直通

        string action = binding.Action;
        string arg = binding.Arg;

        _swallowedDown.TryAdd(vk, 0);
        _swallowedUp.TryAdd(vk, 0);

        _queue.Post(() =>
        {
            try
            {
                _invoke(action, arg);
                ActionTriggered?.Invoke(action);
            }
            catch (Exception ex) { CoreLog.Error($"动作 {action} 执行失败", ex); }
        });
        return true;
    }



    /// <summary>
    /// 文本片段：把这次按键折算成**文本事件**喂给缓冲，命中就展开。
    ///
    /// 与旧实现的三处关键差别（都是为了"打错 → 退格改对 → 仍然能触发"）：
    ///   · 退格/删除让缓冲**回退**，不再清空；
    ///   · 方向键只移动插入符，不清缓冲；
    ///   · 认不出、或不被前台收到的编辑只把缓冲标成"不可信"，内容留着，
    ///     等下一次 token 边界回读控件真实文本来重新对齐。
    ///
    /// 保守的那条底线没变：被引擎吞掉的键（触发键/手势/替换层/改键/层内）
    /// 仍然不参与拼接 —— 只是从"清空"改成了"标记不可信"。
    /// </summary>
    /// <returns>true = 这次按键已被片段展开消费，调用方应吞掉它。</returns>
    private bool ObserveSnippet(AppSettings s, int vk)
    {
        /* ① 一条别名都没配：立刻返回。
         *
         * 这一段在**每个按键**上都会跑，是"打字不涩"的前提：
         * 不读控件文本、不分配、不问设备。 */
        var all = s.Global.Snippets;
        if (all == null || all.Count == 0)
        {
            _snippetBuffer.Reset();
            return false;
        }

        /* ② 前缀过滤。前缀可以配好几个（`;` `/` `、` 都行），比较时全角/半角等价
         * —— 触发词按屏上字符写，缓冲区按物理键记，折叠之后才对得上。 */
        var opt = s.Hotstring ?? new HotstringOptions();
        var prefixes = opt.EffectivePrefixes;
        var list = prefixes.Count == 0
            ? all
            : all.Where(x => SnippetMatcher.StartsWithAnyPrefix(x.Trigger, prefixes)).ToList();

        if (list.Count == 0)
        {
            _snippetBuffer.Reset();
            return false;
        }

        /* 换控件就把缓冲清干净：上一个输入框的尾巴不能拼到这一框的触发词前面。
         * 见 _snippetFocusHwnd 的说明 —— 这也是"第一次打不生效"的另一半原因。 */
        NoteSnippetFocus();

        /* 记下"输入法刚提交过"的时刻，供下面的组字判定用。 */
        if (vk == 0xE5) _snippetImeTick = Environment.TickCount64;

        if (SnippetDebugOn)
        {
            CoreLog.Info($"[片段DBG] vk=0x{vk:X2} mods={SnippetModsText()} " +
                         $"buf=[{_snippetBuffer.Snapshot()}] trusted={_snippetBuffer.Trusted} " +
                         $"cand={list.Count} fg={ForegroundProcess ?? "-"} composing={ImeHasCompositionString()}");
        }

        /* ③ 输入法组字保护（新增）。
         *
         * 组字状态下的退格删的是**拼音**，屏幕上少掉的是一个字母、而不是一个汉字，
         * 把它当成"删掉屏幕上的一个字"会让缓冲和屏幕立刻错位（甚至删掉触发词的一部分）。
         * 这种情况退格原样交给输入法，缓冲只标记不可信，等提交后再回读对齐。 */
        if (vk is 0x08 or 0x2E && IsImeComposing(vk))
        {
            _snippetBuffer.MarkUntrusted();
            return false;
        }

        /* 回读之前的物理缓冲快照。
         *
         * 中文输入法把字母收进组字串时，控件里只有前缀那一个字（`；`），
         * 而 ④ 的回读是"权威"的、会把缓冲冲成 `；` —— 之后 ⑤ 自然匹配不到任何触发词。
         * 这份快照是"用户确实打了 ;lsdv"的唯一证据，命中时按"被输入法吞了"处理。 */
        string physicalBeforeMirror = vk == 0x20 ? _snippetBuffer.Snapshot() : "";

        /* ---- ④ 镜像回读（权威判据） ----
         *
         * 输入法组字对键盘钩子是隐形的：钩子只看得到物理键（拼音字母），
         * 「中文」这两个字要等输入法提交才落进编辑框。所以配置里存在非 ASCII
         * 触发词时，在"文字已经落进控件"的那几个键上（空格 / 回车 / 输入法提交
         * 0xE5）把焦点控件的尾巴读回来，用真实文本匹配。
         *
         * 现在这条路径放宽到四种情况（见 ShouldReadMirror）：缓冲不可信、
         * 刚被清空过、有中文触发词、以及「用控件文本校验」开着且缓冲非空。
         * 放宽的意义是**纠错**：缓冲到底对不对，只有控件里的字说了算。
         *
         * 读不到（自绘控件、权限更高的窗口）就维持原状，走原来的精确匹配 ——
         * 不能因为某个程序不响应 WM_GETTEXT 就把打字卡住。 */
        if (vk is 0x20 or 0x0D or 0xE5 && ShouldReadMirror(list, opt))
        {
            /* 每个 token 边界最多读一次：同一个缓冲状态重复读只是白等 40ms 超时。 */
            int genBefore = _snippetBuffer.Generation;
            if (_mirrorLatchGen != genBefore)
            {
                _mirrorLatchGen = genBefore;
                TextMirrorReadsForTest++;
                if (TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string mirror) && mirror.Length > 0)
                    ApplyMirror(list, SnippetMatcher.Trim(mirror));
            }
        }

        /* ⑤ 空格触发：打完触发词**再按空格**才替换（默认）。 */
        if (opt.ExpandOnSpace && vk == 0x20)
        {
            var ready = SnippetMatcher.MatchSpan(list, _snippetBuffer.Snapshot(), ForegroundProcess);
            if (SnippetDebugOn)
            {
                CoreLog.Info($"[片段DBG] 空格判定：buf=[{_snippetBuffer.Snapshot()}] trusted={_snippetBuffer.Trusted} " +
                             $"命中={(ready == null ? "null" : ready.Value.Snippet.Trigger)} " +
                             $"删除数={(ready == null ? -1 : ready.Value.ConsumedLength)}");
            }
            bool fromImeComposition = false;
            if (ready == null && physicalBeforeMirror.Length > 0)
            {
                /* 回读把缓冲冲掉了：再拿回读之前的物理缓冲判一次。
                 * 命中就说明"字母确实打了、但没落进控件"= 被输入法吃进组字串。 */
                ready = SnippetMatcher.MatchSpan(list, physicalBeforeMirror, ForegroundProcess);
                if (ready != null)
                {
                    fromImeComposition = true;
                    if (SnippetDebugOn)
                        CoreLog.Info($"[片段DBG] 回读冲掉了缓冲，用回读前的物理缓冲命中 {ready.Value.Snippet.Trigger}");
                }
            }

            if (ready == null)
            {
                /* 空格是硬边界：这句话到此为止，下一个 token 从零开始。
                 * 以前这里只是清空，现在同样是清空 —— 但清空之后
                 * 下次判定会先回读控件文本，所以"屏幕上有、缓冲里没有"也不会漏。 */
                _snippetBuffer.Reset();
                return false;               // 没命中 → 空格照常输入
            }

            /* 输入法正在组字（中文/拼音模式）：触发词的字母还在输入法里，
             * 屏幕上只有前缀那一个字。交给展开逻辑"取消组字、只删 1 个字"，
             * 否则会按触发词长度去删正文，看起来就是"别名坏了"。
             *
             * 判据要**认得出这条触发词**：只要"有组字串"就算组字，会把用户正在打的
             * 别的拼音一起取消掉。所以看组字串里是不是正在拼这个触发词的字母。 */
            string composition = ImeCompositionText();
            bool composing = fromImeComposition
                             || CompositionCarriesTrigger(composition, ready.Value.Snippet)
                             || SnippetLooksSwallowedByIme(ready.Value.Snippet);

            if (composing && !CanHandleImeComposition())
            {
                /* 读不到控件文本、也没有 IMM 上下文：这次展开没有任何安全做法
                 * （既算不出该删几个字，也不能取消组字），而**绝不能**退回去发 Esc ——
                 * 微信 / QQ 用 Esc 关当前对话窗口。空格照常交给输入法（不吞），
                 * 用户看到的还是 `;lsdv` 被提交成字，而不是"空格没反应"。 */
                if (SnippetDebugOn)
                    CoreLog.Info($"[片段DBG] 组字且无可用手段（{ForegroundProcess ?? "-"}）→ 不吞空格，放弃展开");
                _snippetBuffer.Reset();
                return false;
            }

            if (SnippetDebugOn && composing)
                CoreLog.Info($"[片段DBG] 空格命中且组字串=[{composition}] → 取消组字（IMM32）后展开");
            ExpandSnippet(ready.Value, composing, composition.Length);
            return true;                    // 通知调用方把这次空格吞掉
        }

        /* ⑥ 其它终止符（回车 / 输入法提交）：只标一个 token 边界，不写缓冲。
         *
         * 这里**不展开** —— 改造前的结论就是"只有可打印字符和（空格模式下的）
         * 空格会触发"，保持不变。挂起中的展开会因为没有新字符而由计时器收尾，
         * 或者因为这里复位了缓冲而作废（两种结果都与改造前一致）。 */
        if (vk is 0x20 or 0x0D or 0xE5)
        {
            _snippetBuffer.Apply(TextEventKind.Terminator);
            _snippetBuffer.Reset();
            return false;
        }

        /* ⑦ 编辑键：删就真的删，移就只移 —— 都不清空缓冲。 */
        if (vk == 0x08) { _snippetBuffer.Apply(TextEventKind.DeleteBack); return false; }
        if (vk == 0x2E) { _snippetBuffer.Apply(TextEventKind.DeleteForward); return false; }

        switch (vk)
        {
            case 0x25: _snippetBuffer.Apply(TextEventKind.CaretLeft); return false;
            case 0x27: _snippetBuffer.Apply(TextEventKind.CaretRight); return false;
            case 0x24: _snippetBuffer.Apply(TextEventKind.CaretHome); return false;
            case 0x23: _snippetBuffer.Apply(TextEventKind.CaretEnd); return false;
        }

        /* ⑧ 可打印字符。 */
        char? ch = AsciiCharOf(vk, _shiftDown);
        if (ch == null)
        {
            /* 认不出的键（Esc / Tab / 上下方向键 / 功能键 / 非 US 布局字符）：
             * 缓冲可能已经对不上屏幕，但**不清空** —— 下次 token 边界回读校对。
             * 这一条正是"删到空、再退格、再打对"能命中的原因。 */
            _snippetBuffer.MarkUntrusted();
            return false;
        }

        _snippetBuffer.Apply(TextEventKind.Insert, ch.Value);

        /* ⚠ 这里**不碰**用户的输入语言。
         *
         * 曾经在这里"前缀一出现就把输入语言切到英文"，想让后面的字母老老实实落进控件
         * （中文输入法会把它们收进组字串）。代价是软件替用户换了输入法 ——
         * 用户的原话是「input plus 不应该能够自己换输入法」。
         * 组字那条路本来就有解：展开时先取消组字、只回删前缀那一个字
         * （见 ExpandSnippet 的 composing 分支），不需要动布局。 */

        /* 空格触发模式：命中先攒着，等空格；不在这里展开。 */
        if (opt.ExpandOnSpace) return false;

        var typed = _snippetBuffer.Snapshot();
        var hit = SnippetMatcher.MatchSpan(list, typed, ForegroundProcess);
        if (hit == null) return false;

        /* 有更长的候选（例如配了 ;m 和 ;mail，现在命中 ;m）就先别展开：
         * 等 120ms，用户接着打 'a' 就说明他要的是 ;mail。
         * 没有更长的候选时立刻展开 —— 常规场景保持"打完就变"的手感。 */
        if (SnippetMatcher.HasLongerCandidate(list, typed, ForegroundProcess))
        {
            _snippetPending = hit;
            _snippetPendingAt = DateTime.Now;
            _snippetPendingGen = _snippetBuffer.Generation;
            string pendingComposition = ImeCompositionText();
            _snippetPendingComposing = CompositionCarriesTrigger(pendingComposition, hit.Value.Snippet);
            _snippetPendingCompositionChars = pendingComposition.Length;
            return false;
        }

        string compositionNow = ImeCompositionText();
        ExpandSnippet(hit.Value, CompositionCarriesTrigger(compositionNow, hit.Value.Snippet), compositionNow.Length);
        return false;
    }

    /// <summary>
    /// 把回读到的控件文本落到缓冲上。
    ///
    /// **两种用途，语气完全不同**：
    ///   · 缓冲已经不可信 / 是空的 / 配置里有中文触发词 → 这次读到的就是**权威**，
    ///     直接覆盖（中文别名只能走这条路，改造前就是这样）；
    ///   · 缓冲可信且非空 → 这次读到的只是**校验**：必须和我们记的尾巴对得上才采纳。
    ///
    /// 为什么校验要"对得上"才采纳：控件的 WM_GETTEXT 并不总是我们以为的那个文本框。
    /// 控制台、终端、部分自绘/富文本控件返回的是一大段与输入行无关的文本 ——
    /// 无条件采纳会把一份**本来是对的**缓冲冲掉，结果是"改造前能展开的别名突然不展开了"。
    /// 对不上就当这次没读到，维持改造前的行为（失败即降级）。
    /// </summary>
    private void ApplyMirror(List<Snippet> list, string mirror)
    {
        /* 读回来的是窗口标题（Chromium/Electron 的 hwndFocus 就是顶层窗口）：
         * 这不是用户打的字，灌进缓冲只会把一份对的缓冲冲坏。 */
        if (TextMirror.LooksLikeWindowTitle(mirror)) return;

        /* 什么时候"读到的就是真理"：
         *   · 缓冲是空的 —— 没有别的可依据；
         *   · 配置里有非 ASCII 触发词 —— 中文/日文别名只可能出现在控件文本里，
         *     拼音字母和屏幕上的汉字根本不是同一串，只能无条件采纳（改造前就是这样）。
         *
         * **注意这里故意不再把"缓冲不可信"算作权威**：以前只要 Trusted=false 就直接覆盖，
         * 而 WM_GETTEXT 在很多程序里返回的根本不是输入行（控制台整屏、Electron 的
         * 顶层窗口、某些自绘控件）—— 一份"不可信但正确"的物理缓冲会被冲成一段无关文本，
         * 于是同一个别名**第一次打不出来、第二次才成功**（第二次 Trusted 已被上一步修好）。
         * 不可信但非空时改走下面的后缀校验：读回来的确实以刚打的字结尾才采纳。 */
        bool authoritative = _snippetBuffer.Length == 0 || HasNonAsciiTrigger(list);
        if (authoritative)
        {
            _snippetBuffer.ReplaceWith(mirror, trusted: true);
            return;
        }

        string typed = _snippetBuffer.Snapshot();
        if (SnippetMatcher.Fold(mirror).EndsWith(SnippetMatcher.Fold(typed), StringComparison.Ordinal))
            _snippetBuffer.ReplaceWith(mirror, trusted: true);
    }

    /// <summary>缓冲空了多久之后就值得回读一次控件文本（毫秒）。</summary>
    private const int MirrorStaleMs = 1500;

    /// <summary>
    /// B5：这次 token 边界该不该回读控件里的真实文本。
    ///
    /// 四条任选其一（顺序即优先级，都是"能读到就一定比猜准"的情形）：
    ///   ① 缓冲已经不可信（发生过建模不了的编辑）；
    ///   ② 缓冲是空的、但刚刚有过编辑（用户可能整段删了重打）；
    ///   ③ 候选里有非 ASCII 触发词 —— 中文别名只能靠控件文本，
    ///      这是改造前就有的行为，必须保留；
    ///   ④ 「用控件文本校验」开着且缓冲非空（新设置，默认开）。
    /// </summary>
    private bool ShouldReadMirror(List<Snippet> list, HotstringOptions opt)
    {
        if (!_snippetBuffer.Trusted) return true;

        if (_snippetBuffer.Length == 0
            && Environment.TickCount64 - _snippetBuffer.LastChangeTick <= MirrorStaleMs) return true;

        if (HasNonAsciiTrigger(list)) return true;

        if (opt.VerifyWithControlText && _snippetBuffer.Length > 0) return true;

        return false;
    }

    /// <summary>现在是不是"输入法正在组字"（B3③ 的判定顺序）。</summary>
    private bool IsImeComposing(int vk)
    {
        if (vk == 0xE5) return true;                  // 这次按键本身就是输入法提交键

        if (ImeHasCompositionString()) return true;    // 输入上下文里确实有组字串

        return Environment.TickCount64 - _snippetImeTick <= 300;   // 刚提交过，退格很可能还在改拼音
    }

    /* 【已删除】_aliasImeHwnd / _aliasImePrevLayout / MaybeSwitchToEnglishForAlias /
     * RestoreAliasImeIfNeeded / RestoreAliasImeAfterExpand。
     *
     * 它们做的事是：别名前缀一出现就把**前台程序的输入语言**切到英文（美国），
     * 展开完或普通空格再切回去。用户的评价是「这是个很严重的 bug」——
     * 软件不该替人换输入法。切换还是异步的，落在打字中途会把输入法打断，
     * 触发词的字母一半进了组字串、一半进了控件，"第一次输入不触发"就是这么来的。
     *
     * 现在这条路整条不存在：组字由 ExpandSnippet 的 composing 分支处理
     * （取消组字 + 只回删前缀），文字注入走 Unicode/剪贴板，都不碰布局。 */

    /// <summary>
    /// 焦点控件变了就把片段缓冲清空（换输入框 = 换一段文本）。
    ///
    /// 为什么必须清：缓冲里记的是"屏幕上这一串字符的尾巴"，它只对**同一个控件**有效。
    /// 用户点了另一个输入框再打别名，尾巴却还是上一个框的最后几个字母 ——
    /// 匹配器看到"字母 + ;mail"，以前会以"不在词边界"为由拒绝，
    /// 于是第一次不展开；用户按下空格（失败）后缓冲被清空，第二遍才成功。
    ///
    /// 拿不到焦点（受保护进程 / 正在切桌面）时**不猜**：保持原样，
    /// 让回读与匹配照旧工作 —— 误清空只损失一次展开，误拼接会连累下一框。
    /// </summary>
    private void NoteSnippetFocus()
    {
        IntPtr focus = ForegroundFocusHandle();
        if (focus == IntPtr.Zero) return;
        if (_snippetFocusHwnd == focus) return;

        /* 第一次记录（启动后的第一个键）没有"上一个控件"，不需要清；
         * 之后每次真正换控件都清 —— 里面记的内容属于别的输入框。 */
        if (_snippetFocusHwnd != IntPtr.Zero) _snippetBuffer.Reset();
        _snippetFocusHwnd = focus;
    }

    /// <summary>前台线程的焦点控件；拿不到就退回前台窗口本身，都没有返回 Zero。</summary>
    private static IntPtr ForegroundFocusHandle()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;

            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid == 0) return fg;

            var info = new GUITHREADINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
            };
            if (GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero) return info.hwndFocus;
            return fg;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>问 IMM32：前台窗口的输入上下文里有没有组字串。</summary>
    private static bool ImeHasCompositionString()
    {
        IntPtr fg = IntPtr.Zero;
        IntPtr focus = IntPtr.Zero;
        try
        {
            fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            /* 组字属于**有焦点的那个子控件**，不属于顶层窗口。
             * 以前只看 GetForegroundWindow()：WinForms/WPF/编辑器里的文本框
             * 全都拿不到上下文（顶层窗口没有 IMM 上下文），于是"正在组字"
             * 永远判成 false —— 中文模式下别名 / 退格保护全都不生效。 */
            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid != 0)
            {
                var info = new GUITHREADINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
                };
                if (GetGUIThreadInfo(tid, ref info)) focus = info.hwndFocus;
            }

            return CompositionPending(focus) || CompositionPending(fg);
        }
        catch
        {
            /* 预期内：探针，拿不到就按"没在组字"处理（退格照旧记进缓冲）。 */
            return false;
        }
    }

    /// <summary>
    /// 取前台焦点控件（拿不到就顶层窗口）当前的输入法组字串。
    /// 空 = 没在组字 / 这个窗口不提供 IMM 上下文（TSF-only）。
    /// </summary>
    private static string ImeCompositionText()
    {
        IntPtr fg = IntPtr.Zero;
        IntPtr focus = IntPtr.Zero;
        try
        {
            fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return "";

            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid != 0)
            {
                var info = new GUITHREADINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
                };
                if (GetGUIThreadInfo(tid, ref info)) focus = info.hwndFocus;
            }

            string text = CompositionTextOn(focus);
            if (text.Length == 0 && focus != fg) text = CompositionTextOn(fg);
            return text;
        }
        catch { return ""; }
    }

    /// <summary>这个窗口的组字串（没有就空串）。</summary>
    private static string CompositionTextOn(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";

        IntPtr imc = IntPtr.Zero;
        try
        {
            imc = ImmGetContext(hwnd);
            if (imc == IntPtr.Zero) return "";         // TSF-only 窗口拿不到上下文

            int bytes = ImmGetCompositionStringW(imc, GcsCompStr, null, 0);
            if (bytes <= 0) return "";

            /* 组字串是天量小数据，但上限得有：一个坏掉的输入法不能让这里申请 2 GB。 */
            if (bytes > 2048) bytes = 2048;
            var buf = new byte[bytes];
            int got = ImmGetCompositionStringW(imc, GcsCompStr, buf, bytes);
            if (got <= 0) return "";
            return System.Text.Encoding.Unicode.GetString(buf, 0, Math.Min(got, bytes));
        }
        catch { return ""; }
        finally
        {
            if (imc != IntPtr.Zero)
            {
                try { ImmReleaseContext(hwnd, imc); } catch { /* 释放失败不影响判定 */ }
            }
        }
    }

    /// <summary>
    /// 组字串里是不是正在拼这条触发词 —— 也就是"触发词的字母还没上屏"。
    ///
    /// 触发词带前缀（`;lsdv`），而输入法只拿走了字母（`lsdv`，前缀那一个字符
    /// 已经上屏成 `；`），所以看的是"触发词以组字串结尾"，或者组字串本身包含整条触发词。
    /// 认不出就当没在拼它：多取消一次会把用户正在打的别的拼音吞掉。
    /// </summary>
    private static bool CompositionCarriesTrigger(string composition, Snippet hit)
    {
        string f = SnippetMatcher.Fold(SnippetMatcher.Trim(composition));
        string t = SnippetMatcher.Fold(hit.Trigger ?? "");
        if (f.Length == 0 || t.Length == 0) return false;
        return t.EndsWith(f, StringComparison.Ordinal) || f.Contains(t);
    }

    /// <summary>
    /// 能不能用 IMM32 取消组字：焦点控件（或顶层窗口）有输入上下文就行。
    /// 返回 false 的窗口（纯 TSF 程序）只能"不展开"，因为替代手段只有发 Esc。
    /// </summary>
    private static bool CanCancelImeComposition()
    {
        IntPtr fg = IntPtr.Zero;
        IntPtr focus = IntPtr.Zero;
        try
        {
            fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid != 0)
            {
                var info = new GUITHREADINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
                };
                if (GetGUIThreadInfo(tid, ref info)) focus = info.hwndFocus;
            }

            return HasImeContext(focus) || HasImeContext(fg);
        }
        catch { return false; }
    }

    private static bool HasImeContext(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr imc = IntPtr.Zero;
        try
        {
            imc = ImmGetContext(hwnd);
            return imc != IntPtr.Zero;
        }
        catch { return false; }
        finally
        {
            if (imc != IntPtr.Zero)
            {
                try { ImmReleaseContext(hwnd, imc); } catch { /* 同上 */ }
            }
        }
    }

    /// <summary>
    /// 取消输入法组字：只走 <c>ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL)</c>，
    /// **不向前台发任何按键**。
    ///
    /// 为什么不能用 Esc：Esc 是应用自己的快捷键 —— 微信 / QQ 的对话窗口、
    /// 不少编辑器的弹层都用它关闭。替用户按 Esc 的后果就是"打个别名，
    /// 聊天窗口直接没了"（v1.5.8.2 现场事故，2026-11-23）。
    ///
    /// 返回 true = 现在确实没有组字串了；
    /// false = 这个窗口没有输入上下文，或不是 IMM 输入法。
    ///
    /// <paramref name="hadComposition"/>：我们**亲手取消掉**了一个非空组字串。
    /// 光看"现在的组字串是空的"分不清两种情况，而它们的删除数完全不同：
    ///   · 取消成功 → 那几个字母从没上过屏，屏幕上只剩前缀 → 只回删前缀；
    ///   · 取消时本来就没有组字串 → 字母是**输入法已经提交**到控件里的 →
    ///     屏幕上确实有 `ipdv`，必须按触发词长度回删。
    /// 以前一律当成前一种，于是"提交"那一次只删掉 1 个字、`ipdv` 留在原地 ——
    /// 用户报的「第一次输入失败了」正是这个画面。
    /// </summary>
    private static bool CancelImeComposition(out bool hadComposition)
    {
        hadComposition = false;
        IntPtr fg = IntPtr.Zero;
        IntPtr focus = IntPtr.Zero;
        try
        {
            fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            uint tid = GetWindowThreadProcessId(fg, out _);
            if (tid != 0)
            {
                var info = new GUITHREADINFO
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>(),
                };
                if (GetGUIThreadInfo(tid, ref info)) focus = info.hwndFocus;
            }

            if (CancelCompositionOn(focus, ref hadComposition)) return true;
            if (focus != fg && CancelCompositionOn(fg, ref hadComposition)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>在一个窗口的输入上下文里取消组字。</summary>
    private static bool CancelCompositionOn(IntPtr hwnd, ref bool hadComposition)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr imc = IntPtr.Zero;
        try
        {
            imc = ImmGetContext(hwnd);
            if (imc == IntPtr.Zero) return false;

            /* 已经没有组字串 = 这个窗口没什么可取消的。
             * 注意**不能**记成"取消成功"：字母可能已经被输入法提交到控件里了
             * （见 CancelImeComposition 的说明）。 */
            if (ImmGetCompositionStringW(imc, GcsCompStr, null, 0) <= 0) return true;

            hadComposition = true;
            return ImmNotifyIME(imc, NiCompositionStr, CpsCancel, 0);
        }
        catch { return false; }
        finally
        {
            if (imc != IntPtr.Zero)
            {
                try { ImmReleaseContext(hwnd, imc); } catch { /* 同上 */ }
            }
        }
    }

    /// <summary>这个窗口的输入上下文里有没有组字串。</summary>
    private static bool CompositionPending(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        IntPtr imc = IntPtr.Zero;
        try
        {
            imc = ImmGetContext(hwnd);
            if (imc == IntPtr.Zero) return false;      // TSF-only 窗口拿不到上下文
            return ImmGetCompositionStringW(imc, GcsCompStr, null, 0) > 0;
        }
        catch { return false; }
        finally
        {
            if (imc != IntPtr.Zero)
            {
                try { ImmReleaseContext(hwnd, imc); } catch { /* 释放失败不影响判定 */ }
            }
        }
    }

    /// <summary>
    /// TSF 程序（浏览器 / Electron / 新版记事本）拿不到 IMM 上下文，
    /// 只能从**屏幕上的字**反推：控件尾巴里看不到触发词、却只剩前缀那一个字，
    /// 说明字母被输入法收进了组字串。
    /// </summary>
    private bool SnippetLooksSwallowedByIme(Snippet hit)
    {
        try
        {
            string trigger = hit.Trigger ?? "";
            if (trigger.Length == 0) return false;
            if (!TextMirror.TryReadTail(SnippetMatcher.MaxBuffer, out string tail) || tail.Length == 0)
                return false;

            /* 标题栏文字不是"屏幕上的正文"：拿它判"触发词没上屏"会把
             * 一次正常的展开误判成组字，最后只删 1 个字（`;ipdv` 留在原地）。 */
            if (TextMirror.LooksLikeWindowTitle(tail)) return false;

            string foldedTail = SnippetMatcher.Fold(SnippetMatcher.Trim(tail));
            string foldedTrigger = SnippetMatcher.Fold(trigger);
            if (foldedTrigger.Length == 0) return false;
            if (foldedTail.EndsWith(foldedTrigger, StringComparison.Ordinal)) return false;   // 触发词就在屏幕上

            /* 屏幕上只剩前缀那一个字（`；` / `;` / `、` …）→ 字母确实没落进控件。 */
            return foldedTail.EndsWith(SnippetMatcher.Fold(trigger.Substring(0, 1)), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>IMM32：GCS_COMPSTR（"取组字串"）。</summary>
    private const int GcsCompStr = 0x0008;

    /* NativeMethods 里没有这一个，而白名单不允许动那个文件 ——
     * 就地声明在 partial 类里，效果完全一样。 */
    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    private static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, byte[]? lpBuf, int dwBufLen);

    /* 取消组字：这是**唯一**正确的做法。
     * 以前这里是 InputSender.Tap(0x1B)（替用户按 Esc），结果微信里一展开别名就把
     * 对话窗口关了 —— Esc 是应用自己的快捷键，不能借来清输入法。 */
    [System.Runtime.InteropServices.DllImport("imm32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ImmNotifyIME(IntPtr hIMC, uint dwAction, uint dwIndex, uint dwValue);

    /// <summary>ImmNotifyIME 的动作：对组字串做点什么。</summary>
    private const uint NiCompositionStr = 0x0015;

    /// <summary>ImmNotifyIME 的参数：取消组字。</summary>
    private const uint CpsCancel = 0x0004;


    /// <summary>这批候选里有没有非 ASCII 触发词（中文/日文/全角符号）。</summary>
    private static bool HasNonAsciiTrigger(List<Snippet> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            string trigger = list[i].Trigger ?? "";
            for (int j = 0; j < trigger.Length; j++)
            {
                if (trigger[j] > 0x7F) return true;
            }
        }
        return false;
    }

}
