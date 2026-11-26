using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using InputPlus.Core;
using InputPlus.Core.Actions;
using InputPlus.Core.Icons;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using InputPlus.Core.Qbar;
using InputPlus.Core.Settings;
using InputPlus.Core.Text;
using InputPlus.Core.Update;
using InputPlus.Core.Windows;

namespace InputPlus;

/// <summary>
/// 自检：不依赖界面，验证核心逻辑。
/// 用 `InputPlus.exe --selftest` 运行，退出码 0 表示全部通过。
/// </summary>
public static partial class SelfTest
{

    /* 这是一个 partial 类：按业务域拆成了 SelfTest.<域>.cs。
     * 这里只放**调度与基础设施**（Run/RunAll、断言、DoEvents…），
     * 具体用例在同名 partial 文件里。拆分的唯一目的是让单文件别再是
     * 七千多行 —— 找一个用例要先滚三屏的那种。 */

    private static int _passed;

    private static int _failed;

    private static readonly StringBuilder Log = new();

    /// <summary>
    /// `--selftest --only=片段` 的子集过滤串。空串 = 跑全量（默认）。
    ///
    /// 为什么要它：全量自检 7000 多项，改一处索引后端要先等十几分钟才知道对不对，
    /// 一轮改造要跑几十次门槛，时间全烧在跟本次改动无关的输入法用例上。
    /// 过滤按**用例方法名**（`TestQbar*` 这种）做包含匹配，不按断言名 ——
    /// 断言名是给人读的句子，方法名才是用例的身份。
    /// </summary>
    private static string _only = "";

    /// <summary>本次过滤下实际执行的用例数（用来在报告末尾说清"这是子集，不是全量"）。</summary>
    private static int _ran;

    /// <summary>还没打印的章节名。过滤时整章一个用例都不跑，就不该留一个空标题。</summary>
    private static string _pendingSection = "";

    /// <summary>由命令行入口在 <see cref="Run"/> 之前调用一次。</summary>
    public static void Configure(string? only)
    {
        _only = (only ?? "").Trim();
    }

    /// <summary>本次是否子集模式。</summary>
    internal static bool SubsetMode => _only.Length > 0;

    public static int Run()
    {
        // 整体包一层：自检里任何一步意外抛异常，也要保证写出报告并返回退出码，
        // 否则命令行会一直等（之前写 %TEMP% 失败就出现过挂死）。
        try
        {
            RunAll();
        }
        catch (Exception ex)
        {
            _failed++;
            Log.AppendLine();
            Log.AppendLine($"[FATAL] 自检中断：{ex.GetType().Name}: {ex.Message}");
            Log.AppendLine(ex.StackTrace);
            CoreLog.Error("自检中断", ex);
        }

        Log.AppendLine();
        if (SubsetMode)
            Log.AppendLine($"（子集模式 --only={_only}：执行 {_ran} 个用例方法，跳过其余）");
        Log.AppendLine($"==== 通过 {_passed} 项，失败 {_failed} 项 ====");
        WriteReport();
        return _failed == 0 ? 0 : 1;
    }

    private static void RunAll()
    {
        Section("环境");
        Check("Windows 版本 >= 10", Environment.OSVersion.Version.Major >= 10);
        // 早期只有 x64 一条线，这里断言"必须是 64 位进程"。现在多了 32 位专线
        // （net7/win-x86），位宽本身不再是正确性标准 —— 改成信息性断言：
        // 真出现"构建与进程位宽不符"这类灵异事件时，detail 一眼可见。
        Check("进程位宽与 IntPtr 一致",
            Environment.Is64BitProcess == (IntPtr.Size == 8),
            $"IntPtr.Size={IntPtr.Size}, Is64BitProcess={Environment.Is64BitProcess}");
        Check("数据目录可创建", TryCreateDataDir());

        Section("按键模型");
        T(nameof(TestKeyNames), TestKeyNames);

        Section("鼠标手势");
        T(nameof(TestGestures), TestGestures);
        T(nameof(TestGestureEngine), TestGestureEngine);
        T(nameof(TestWGesturesEngine), TestWGesturesEngine);
        T(nameof(TestWg2Layer), TestWg2Layer);
        T(nameof(TestWg2Plus), TestWg2Plus);
        T(nameof(TestGestureListOrder), TestGestureListOrder);
        /* 滚轮手势（v1.5.9.1）在 300382d 那一版还不存在 —— 回滚手势代码时
         * 连同它的自测一起摘掉了，这里不留空洞。 */

        Section("中键菜单");
        T(nameof(TestMiddleMenu), TestMiddleMenu);
        T(nameof(TestAttachMenuLayout), TestAttachMenuLayout);
        T(nameof(TestThinScrollBars), TestThinScrollBars);
        T(nameof(TestFolderCascade), TestFolderCascade);
        T(nameof(TestFolderOpenPaths), TestFolderOpenPaths);
        T(nameof(TestFavorites), TestFavorites);

        Section("收藏当前文件");
        T(nameof(TestFavoriteFile), TestFavoriteFile);

        Section("笔画平滑");
        T(nameof(TestSmoothing), TestSmoothing);
        T(nameof(TestQbarIndex), TestQbarIndex);
        T(nameof(TestQbarIndexQuery), TestQbarIndexQuery);

        Section("轨迹校正");
        T(nameof(TestGestureCanonical), TestGestureCanonical);

        Section("平滑滚动");
        T(nameof(TestSmoothScroll), TestSmoothScroll);

        Section("按住拖拽");
        T(nameof(TestHoldDrag), TestHoldDrag);
        T(nameof(TestHoldDragOwnWindow), TestHoldDragOwnWindow);

        Section("搜索引擎");
        T(nameof(TestSearchEngines), TestSearchEngines);
        T(nameof(TestEngineMigration), TestEngineMigration);

        Section("Qbar 图标");
        T(nameof(TestLegacyIconPathRevival), TestLegacyIconPathRevival);
        T(nameof(TestSearchSiteDedupe), TestSearchSiteDedupe);

        Section("表达式求值");
        T(nameof(TestEvaluator), TestEvaluator);

        Section("Qbar 检索");
        T(nameof(TestQbar), TestQbar);
        T(nameof(TestCloudPlaceholderSkip), TestCloudPlaceholderSkip);

        Section("Qbar 标签");
        T(nameof(TestChips), TestChips);

        Section("Qbar 语义层");
        T(nameof(TestQbarSemantics), TestQbarSemantics);

        Section("Qbar 展开窗口");
        T(nameof(TestQbarExpandSort), TestQbarExpandSort);
        T(nameof(TestQbarTitleSegments), TestQbarTitleSegments);

        Section("设置存取");
        T(nameof(TestSettings), TestSettings);
        T(nameof(TestNotificationStyle), TestNotificationStyle);
        T(nameof(TestDataLocationAndEncryptedExport), TestDataLocationAndEncryptedExport);

        Section("开机自启");
        T(nameof(TestStartupModeDecision), TestStartupModeDecision);
        T(nameof(TestStartupTaskXml), TestStartupTaskXml);
        T(nameof(TestStartupTaskCommands), TestStartupTaskCommands);

        Section("取词代理");
        T(nameof(TestSelectionProxyArgs), TestSelectionProxyArgs);
        T(nameof(TestUiaReaderTimeout), TestUiaReaderTimeout);

        Section("剪贴板快照");
        T(nameof(TestClipboardBackup), TestClipboardBackup);
        T(nameof(TestSelfTestDoesNotLeakSentinel), TestSelfTestDoesNotLeakSentinel);
        T(nameof(TestLeaseYieldsToUserCopy), TestLeaseYieldsToUserCopy);
        T(nameof(TestLeaseSurvivesManagerRewrite), TestLeaseSurvivesManagerRewrite);

        Section("失败通知");
        T(nameof(TestActionFailureText), TestActionFailureText);

        Section("基础改键");
        T(nameof(TestBaseRemap), TestBaseRemap);

        Section("使用统计");
        T(nameof(TestUsageStats), TestUsageStats);

        Section("按应用层");
        T(nameof(TestAppLayerOverlay), TestAppLayerOverlay);

        Section("双击动作");
        T(nameof(TestDoubleTapGesture), TestDoubleTapGesture);

        Section("文本片段");
        T(nameof(TestSnippets), TestSnippets);
        T(nameof(TestAliasListSearch), TestAliasListSearch);

        Section("组合键");

        Section("Qbar 搜动作");
        T(nameof(TestQbarActionSearch), TestQbarActionSearch);


        Section("片段可编辑性");
        T(nameof(TestEditableTargetRule), TestEditableTargetRule);

        Section("宏的流程控制");
        T(nameof(TestMacroFlow), TestMacroFlow);

        Section("宏的等待与重试");
        T(nameof(TestMacroWaitAndRetry), TestMacroWaitAndRetry);

        Section("宏的变量与循环计数");
        T(nameof(TestMacroVars), TestMacroVars);
        Section("宏的动作步骤");
        T(nameof(TestMacroActionStep), TestMacroActionStep);


        Section("键盘设备");
        T(nameof(TestKeyboardDevices), TestKeyboardDevices);

        Section("按设备改键");
        T(nameof(TestDeviceRemap), TestDeviceRemap);

        Section("设备方案");
        T(nameof(TestDeviceProfiles), TestDeviceProfiles);
        T(nameof(TestDeviceAliasAndDefaults), TestDeviceAliasAndDefaults);

        Section("原键替换层");
        T(nameof(TestReplacementLayer), TestReplacementLayer);

        Section("动作分类");
        T(nameof(TestKeyAndMouseCategories), TestKeyAndMouseCategories);

        Section("别名前缀与找回");
        T(nameof(TestSnippetPrefixes), TestSnippetPrefixes);
        T(nameof(TestHotstringGroupRules), TestHotstringGroupRules);

        /* 剪贴板 → 别名（任务 A）与流式尾部匹配（任务 B）。
         * 两个都要能单独跑：--only=SnippetDraft / --only=SnippetBuffer / --only=SnippetSpan */
        Section("剪贴板转别名");
        T(nameof(TestSnippetDraft), TestSnippetDraft);

        Section("别名流式匹配");
        T(nameof(TestSnippetBuffer), TestSnippetBuffer);
        T(nameof(TestSnippetSpan), TestSnippetSpan);
        T(nameof(TestSnippetStreamEngine), TestSnippetStreamEngine);

        Section("启动密码");
        T(nameof(TestStartupPassword), TestStartupPassword);

        Section("旧版 ini 迁移");
        T(nameof(TestLegacyImport), TestLegacyImport);

        Section("动作目录");
        T(nameof(TestActionCatalog), TestActionCatalog);

        Section("录键窗口");
        T(nameof(TestKeyCaptureMouseGuard), TestKeyCaptureMouseGuard);

        Section("帮助页");
        T(nameof(TestHelpActionLine), TestHelpActionLine);

        /* 「快速上手」页那 8 个组合的取数规则（以前是写死的文案，
         * 用户改完按键映射它不跟着变）。 */
        Section("快速上手页");
        T(nameof(TestGuideQuickKeys), TestGuideQuickKeys);
        T(nameof(TestGuideBannerKeycaps), TestGuideBannerKeycaps);

        Section("窗口绑定");
        T(nameof(TestWindowBindings), TestWindowBindings);
        T(nameof(TestCapsloxLayer), TestCapsloxLayer);
        T(nameof(TestScreenPlacement), TestScreenPlacement);
        T(nameof(TestWindowSearchCache), TestWindowSearchCache);
        T(nameof(TestCloseBehavior), TestCloseBehavior);

        Section("打开文件夹");
        T(nameof(TestFolderLeafName), TestFolderLeafName);

        /* 外部程序的工作目录（2026-11-26 wb2api 事故的回归守卫）：
         * 子进程默认继承父进程的当前目录，忘了给就会把配置写到 InputPlus 脚边。 */
        Section("外部程序启动");
        T(nameof(TestExternalLaunch), TestExternalLaunch);

        Section("窗口激活与最小化");
        T(nameof(TestWindowActivateAndMinimize), TestWindowActivateAndMinimize);

        Section("主题");
        T(nameof(TestTheme), TestTheme);

        Section("界面字体");
        T(nameof(TestFonts), TestFonts);

        Section("界面文案");
        T(nameof(TestUiText), TestUiText);
        T(nameof(TestQbarMaterial), TestQbarMaterial);
        T(nameof(TestThemeMetrics), TestThemeMetrics);
        T(nameof(TestKeyboardFit), TestKeyboardFit);

        Section("界面语言");
        T(nameof(TestLocalization), TestLocalization);

        Section("关于页 · GitHub 令牌");
        T(nameof(TestAboutTokenUi), TestAboutTokenUi);

        Section("Qbar 引擎过滤");
        T(nameof(TestQbarEntryFilter), TestQbarEntryFilter);

        Section("触发键配置");
        T(nameof(TestTriggerConfig), TestTriggerConfig);

        Section("按键名表");
        T(nameof(TestKeyNameTable), TestKeyNameTable);

        Section("组合键往返");
        T(nameof(TestComboRoundTrip), TestComboRoundTrip);
        T(nameof(TestHoldOnOtherKey), TestHoldOnOtherKey);
        T(nameof(TestModifierTracker), TestModifierTracker);

        Section("触发键展示");
        T(nameof(TestTriggerDisplayProps), TestTriggerDisplayProps);

        Section("提权");
        T(nameof(TestElevation), TestElevation);
        T(nameof(TestElevationRoute), TestElevationRoute);

        Section("提权取词");
        T(nameof(TestSelectionElevated), TestSelectionElevated);

        Section("手势·shell 浮层");
        T(nameof(TestShellOverlay), TestShellOverlay);

        Section("热路径缓存");
        T(nameof(TestHotPathCaching), TestHotPathCaching);

        Section("宏与速查表展示");
        T(nameof(TestMacroDisplayProps), TestMacroDisplayProps);
        T(nameof(TestMacroVarUi), TestMacroVarUi);
        T(nameof(TestHelpCoversAllLayers), TestHelpCoversAllLayers);

        Section("宏绑定与 keepKey");
        T(nameof(TestMacroBindableInKeysView), TestMacroBindableInKeysView);
        T(nameof(TestKeepKeyTapAction), TestKeepKeyTapAction);
        T(nameof(TestQbarEngineActions), TestQbarEngineActions);
        T(nameof(TestFilterActions), TestFilterActions);
        T(nameof(TestDictionaryActions), TestDictionaryActions);
        T(nameof(TestTriggerKeyPosition), TestTriggerKeyPosition);
        T(nameof(TestKeyboardLayoutGeometry), TestKeyboardLayoutGeometry);
        T(nameof(TestEditorDensity), TestEditorDensity);
        T(nameof(TestEditorButtonWidths), TestEditorButtonWidths);
        T(nameof(TestResetLayerOne), TestResetLayerOne);
        T(nameof(TestArbitraryWindowBinding), TestArbitraryWindowBinding);
        T(nameof(TestWinBindRestoreLogic), TestWinBindRestoreLogic);
        T(nameof(TestWebPageCategory), TestWebPageCategory);
        T(nameof(TestSmartOpen), TestSmartOpen);
        T(nameof(TestKeycapVisualStates), TestKeycapVisualStates);
        T(nameof(TestProductIdentity), TestProductIdentity);
        T(nameof(TestWebTitleExtract), TestWebTitleExtract);
        T(nameof(TestNoDuplicateKeycaps), TestNoDuplicateKeycaps);
        T(nameof(TestKeycapShortTitles), TestKeycapShortTitles);
        T(nameof(TestWinBindCustomName), TestWinBindCustomName);
        T(nameof(TestQbarThemeContrast), TestQbarThemeContrast);
        T(nameof(TestQbarChipTextContrast), TestQbarChipTextContrast);
        T(nameof(TestQbarTabContrast), TestQbarTabContrast);
        T(nameof(TestQbarNativeIndex), TestQbarNativeIndex);
        T(nameof(TestQbarRecentTargets), TestQbarRecentTargets);
        T(nameof(TestQbarOutsideClick), TestQbarOutsideClick);
        T(nameof(TestComplexTriggerKey), TestComplexTriggerKey);
        T(nameof(TestTriggerBadgeFollowsLayer), TestTriggerBadgeFollowsLayer);
        T(nameof(TestSubLayers), TestSubLayers);
        T(nameof(TestSubLayerCombos), TestSubLayerCombos);
        T(nameof(TestSubLayerRepeatAndState), TestSubLayerRepeatAndState);
        T(nameof(TestSubLayerKeyEntersLayerBeforeHold), TestSubLayerKeyEntersLayerBeforeHold);
        T(nameof(TestHoldThresholdWithoutUiTimer), TestHoldThresholdWithoutUiTimer);
        T(nameof(TestOrphanKeyUpIsSwallowed), TestOrphanKeyUpIsSwallowed);
        T(nameof(TestHoldNotMistakenAsTap), TestHoldNotMistakenAsTap);
        T(nameof(TestTriggerTapHoldSemantics), TestTriggerTapHoldSemantics);
        T(nameof(TestErrorFailClosedPolicy), TestErrorFailClosedPolicy);
        T(nameof(TestDirectSearch), TestDirectSearch);
        T(nameof(TestCommandSearchAction), TestCommandSearchAction);
        T(nameof(TestTypeTextAction), TestTypeTextAction);
        T(nameof(TestImeGuard), TestImeGuard);
        T(nameof(TestClipboardPaste), TestClipboardPaste);

        Section("自定义按键");
        T(nameof(TestCustomKey), TestCustomKey);

        Section("音量与亮度");
        T(nameof(TestVolumeCore), TestVolumeCore);
        T(nameof(TestBrightnessMath), TestBrightnessMath);
        T(nameof(TestBrightnessPumpStarts), TestBrightnessPumpStarts);
        T(nameof(TestOsdAndTextInput), TestOsdAndTextInput);
        T(nameof(TestOsdAlwaysOnTop), TestOsdAlwaysOnTop);
        T(nameof(TestOsdIconFollowsBrightness), TestOsdIconFollowsBrightness);
        T(nameof(TestOsdIconAnimates), TestOsdIconAnimates);
        T(nameof(TestOsdAboveShellMenu), TestOsdAboveShellMenu);
        T(nameof(TestOsdScalesAcrossDisplays), TestOsdScalesAcrossDisplays);

        Section("托盘");
        T(nameof(TestTrayDoubleClick), TestTrayDoubleClick);

        T(nameof(TestToastCoalescing), TestToastCoalescing);
        T(nameof(TestDirectSearchCoversAllEngines), TestDirectSearchCoversAllEngines);
        T(nameof(TestCopyChordEndToEnd), TestCopyChordEndToEnd);
        T(nameof(TestSelectionWatcher), TestSelectionWatcher);
        T(nameof(TestContextHasSelectionWatcher), TestContextHasSelectionWatcher);

        Section("审计修复");
        T(nameof(TestAuditFindings), TestAuditFindings);

        Section("引擎复查修复");
        T(nameof(TestEngineAuditFixes), TestEngineAuditFixes);

        Section("引擎回归（第二轮）");
        T(nameof(TestEngineRegressions2), TestEngineRegressions2);
        T(nameof(TestAuditFixes), TestAuditFixes);
        Section("本地文件加密");
        T(nameof(TestLocalEncryption), TestLocalEncryption);
        T(nameof(TestVaultRecovery), TestVaultRecovery);

        Section("宏可绑定");
        T(nameof(TestMacroIsBindable), TestMacroIsBindable);
        T(nameof(TestActionCategories), TestActionCategories);

        Section("窗口绑定未启动时先启动");
        T(nameof(TestWindowBindingLaunch), TestWindowBindingLaunch);

        Section("窗口点选");
        T(nameof(TestWindowPreview), TestWindowPreview);
        T(nameof(TestPickerWithOverlayVisible), TestPickerWithOverlayVisible);
        T(nameof(TestPickerCommitKeepsResult), TestPickerCommitKeepsResult);

        Section("文字度量");
        T(nameof(TestTextMetrics), TestTextMetrics);

        Section("Qbar 网址与路径");
        T(nameof(TestQbarUrlAndPath), TestQbarUrlAndPath);

        Section("键盘层");
        T(nameof(TestLayers), TestLayers);
        T(nameof(TestLayerIsolation), TestLayerIsolation);
        T(nameof(TestHoldThreshold), TestHoldThreshold);
        T(nameof(TestLayerSurvivesIdle), TestLayerSurvivesIdle);
        T(nameof(TestModifierOnlyBinding), TestModifierOnlyBinding);
        T(nameof(TestModifierTriggerWithRepeat), TestModifierTriggerWithRepeat);
        T(nameof(TestContinuousVolumeRepeat), TestContinuousVolumeRepeat);
        T(nameof(TestRepeatSurvivesPhysicalJitter), TestRepeatSurvivesPhysicalJitter);
        T(nameof(TestTwoContinuousKeysIndependent), TestTwoContinuousKeysIndependent);
        T(nameof(TestTriggerRecovery), TestTriggerRecovery);
        T(nameof(TestSubLayerWindowLayout), TestSubLayerWindowLayout);

        Section("宏");
        T(nameof(TestMacroRecorder), TestMacroRecorder);
        T(nameof(TestMacroLiveRecording), TestMacroLiveRecording);
        T(nameof(TestComboCapture), TestComboCapture);
        T(nameof(TestExtendedCombos), TestExtendedCombos);
        T(nameof(TestMacroStepEditing), TestMacroStepEditing);

        Section("备份加密");
        T(nameof(TestCrypto), TestCrypto);

        Section("导入导出");
        T(nameof(TestExportGroups), TestExportGroups);
        T(nameof(TestExportRoundTrip), TestExportRoundTrip);
        T(nameof(TestExportImport), TestExportImport);
        T(nameof(TestExportImportOnDisk), TestExportImportOnDisk);

        Section("WebDAV");
        T(nameof(TestWebDav), TestWebDav);

        Section("同步载荷");
        T(nameof(TestSyncPayload), TestSyncPayload);
        T(nameof(TestTokenInBackup), TestTokenInBackup);

        Section("备份策略");
        T(nameof(TestBackupPolicy), TestBackupPolicy);
        T(nameof(TestBackupCleanup), TestBackupCleanup);

        Section("窗口与进程");
        T(nameof(TestWindows), TestWindows);

        Section("输入注入");
        T(nameof(TestInput), TestInput);

        Section("输入文本标签与本地加密");
        T(nameof(TestTypeTextAndVault), TestTypeTextAndVault);

        Section("单实例");
        T(nameof(TestSingleInstance), TestSingleInstance);

        Section("键盘钩子");
        T(nameof(TestHook), TestHook);

        Section("组合键引擎");
        T(nameof(TestEngine), TestEngine);

        Section("文件对话框");
        T(nameof(TestFileDialogBar), TestFileDialogBar);

        Section("搜索前建议");
        T(nameof(TestSuggestions), TestSuggestions);
        T(nameof(TestSuggestionFolderExpand), TestSuggestionFolderExpand);

        Section("更新");
        T(nameof(TestUpdateDigest), TestUpdateDigest);
        T(nameof(TestUpdateAssetPick), TestUpdateAssetPick);
        T(nameof(TestUpdateCancellation), TestUpdateCancellation);
        T(nameof(TestGitHubToken), TestGitHubToken);
    }

    /// <summary>写出自检报告（数据目录 + 控制台可用时也打印）。</summary>
    private static void WriteReport()
    {
        string report = Log.ToString();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "selftest.log"), report);
        }
        catch (Exception ex)
        {
            CoreLog.Warn("写自检报告失败：" + ex.Message);
        }

        try
        {
            if (NativeConsoleAvailable()) Console.WriteLine(report);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static bool NativeConsoleAvailable() => GetConsoleWindow() != IntPtr.Zero;

    private static void Section(string name)
    {
        _pendingSection = name;
    }

    /// <summary>
    /// 跑一个用例方法。过滤串非空时，方法名不含过滤串就整个跳过 ——
    /// 连用例体一起跳过，不是"跑了再丢结果"：这些用例会真起窗口、真装钩子、
    /// 真扫盘，跳过结果但不跳过执行等于一点时间都没省。
    /// </summary>
    private static void T(string name, Action body)
    {
        if (_only.Length > 0 &&
            name.IndexOf(_only, StringComparison.OrdinalIgnoreCase) < 0)
            return;

        _ran++;
        FlushSection();
        body();
    }

    private static void FlushSection()
    {
        string pending = _pendingSection;
        if (pending.Length == 0) return;
        _pendingSection = "";
        Log.AppendLine();
        Log.AppendLine($"── {pending} ──");
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        FlushSection();
        if (ok) { _passed++; Log.AppendLine($"  [PASS] {name}"); }
        else { _failed++; Log.AppendLine($"  [FAIL] {name}{(detail == null ? "" : "  → " + detail)}"); }
    }

    private static void CheckEqual<T>(string name, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Check(name, ok, ok ? null : $"期望 {expected}，实际 {actual}");
    }

    /// <summary>
    /// 浮点近似比较。磨砂那些 0.306 / 0.485 / 0.29376 都是乘出来的，
    /// 用 == 钉会因为最后一位差一个 ULP 莫名其妙变红。
    /// </summary>
    private static void CheckNear(string name, float actual, float expected, float eps = 1e-4f)
    {
        bool ok = Math.Abs(actual - expected) <= eps;
        Check(name, ok, ok ? null : $"期望 {expected}（±{eps}），实际 {actual}");
    }

    private static void CheckNear(string name, System.Numerics.Vector3 actual, System.Numerics.Vector3 expected,
        float eps = 1e-4f)
    {
        bool ok = (actual - expected).Length() <= eps;
        Check(name, ok, ok ? null : $"期望 {expected}（±{eps}），实际 {actual}");
    }

    private static bool TryCreateDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 抽空 WPF 消息队列若干毫秒。
    /// 自检跑在界面线程上，Show() 之后必须让消息循环转一会儿，
    /// Loaded 才会触发、窗口样式才真正应用。
    /// </summary>
    /// <summary>
    /// 等一个条件成立（边等边抽消息），最多等 <paramref name="timeoutMs"/>。
    ///
    /// 为什么需要：动作是投递到队列线程上执行的，**"投递完 60ms 后一定已经跑完"
    /// 这个假设只在 Debug 下偶然成立** —— Release 里 JIT 更积极、线程调度更紧，
    /// 同一份代码就会偶发失败。所以凡是"等异步结果"的断言都改用这个，
    /// 条件成立就立刻返回，整体并不会变慢。
    /// </summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < end)
        {
            if (condition()) return true;
            DoEvents(15);
        }
        return condition();
    }

    private static void DoEvents(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(20, ms)),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static bool IsOwnProcess(IntPtr hwnd)
    {
        WindowService.GetWindowThreadProcessIdForTest(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>"#AARRGGBB" / "#RRGGBB" → 0~1 的 RGB。</summary>
    private static (double R, double G, double B) ParseRgb(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return (0, 0, 0);
        string s = hex.TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length != 6) return (0, 0, 0);
        try
        {
            return (Convert.ToInt32(s[..2], 16) / 255.0,
                    Convert.ToInt32(s.Substring(2, 2), 16) / 255.0,
                    Convert.ToInt32(s.Substring(4, 2), 16) / 255.0);
        }
        catch { return (0, 0, 0); }
    }
}