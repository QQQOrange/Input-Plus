using static InputPlus.Core.Interop.NativeMethods;

namespace InputPlus.Core.Input;

/// <summary>
/// 输入法 / 键盘布局的**只读**探针。
///
/// ## 这个类刻意不再提供任何"切换"能力（2026-11-23）
///
/// 以前它有一整套 <c>SwitchTo</c> / <c>EnsureEnglish</c> / <c>EnsureComposing</c> /
/// <c>Restore</c>：「输入文本」走真实按键那一刻把目标窗口的**输入语言**切到英文（美国），
/// 打完再切回来；别名前缀一出现也偷偷切一次。用户的原话是
/// 「input plus 不应该能够自己换输入法，这是个很严重的 bug」。
///
/// 为什么这种"切了再切回来"注定不可靠：切换是**异步**的
/// （<c>WM_INPUTLANGCHANGEREQUEST</c> → DefWindowProc → ActivateKeyboardLayout），
/// 只能 post 完再轮询 <c>GetKeyboardLayout</c> 等它生效。目标程序不理会这条消息、
/// 或者切回时用户已经换了窗口 / 目标线程正忙，人就被**永久留在英文布局**上 ——
/// 而他往往不知道该去哪儿改回来。别名那条路更糟：切换落在打字中途，
/// 输入法会被打断，触发词的字母一半进了组字串、一半进了控件，展开时怎么删都错。
///
/// 现在的原则：**软件只读输入法状态，绝不改它**。
///   · 需要知道"当前布局会不会把字母拼成候选" → <see cref="IsComposing"/>（纯判断）；
///   · 需要按用户自己的布局把字符映射成虚拟键 → 直接用
///     <c>VkKeyScanEx(ch, GetKeyboardLayout(tid))</c>，不激活任何布局；
///   · 输入法开着时要打纯 ASCII 文字 → 改走 Unicode 注入（不经过布局与输入法，
///     中文 / emoji 一样能落字），见 <see cref="InputSender.TryTextAsKeys"/>。
///
/// 于是整个程序里**没有任何一条路径**能触发 WM_INPUTLANGCHANGEREQUEST /
/// ActivateKeyboardLayout / ImmSetConversionStatus —— 这条性质由自检钉住
/// （「输入法：ImeGuard 不再有切换输入语言的入口」）。
/// </summary>
internal static class ImeGuard
{
    /// <summary>英文（美国）布局的 KLID。所有 Windows 都带 kbdus.dll。</summary>
    private const string EnglishLayoutId = "00000409";

    /// <summary>
    /// 这些输入语言的字母键会被输入法拼成候选：
    /// 中文（简/繁/港/新加坡）、日文、韩文、越南文。
    ///
    /// 不用 <c>ImmIsIME</c> 判定的原因：实测它对**所有**已加载布局都返回 TRUE
    /// （连德语、法语布局都算输入法），没法当判据。
    /// 用语言 ID 至少是"宁可多走一次 Unicode 注入"：误判的代价只是少走一条
    /// 兼容性路径，漏判的代价是字母被输入法吃掉、一个字都打不出来。
    /// </summary>
    private static readonly ushort[] ComposingLanguages = new ushort[]
        { 0x0804, 0x0404, 0x0C04, 0x1004, 0x0411, 0x0412, 0x042A };

    private static IntPtr _english;
    private static bool _englishLoaded;

    /// <summary>
    /// 英文（美国）布局的 HKL；加载失败返回 <see cref="IntPtr.Zero"/>。
    ///
    /// **只用于把字符映射成虚拟键**（自检里核对 Shift 位与扫描码），
    /// 绝不拿它去 <c>ActivateKeyboardLayout</c> —— 加载一个布局到进程里
    /// 不会改变任何窗口的输入语言，那是 HKL 的用法边界。
    /// </summary>
    public static IntPtr EnglishLayout
    {
        get
        {
            if (_englishLoaded) return _english;
            _englishLoaded = true;
            try
            {
                _english = LoadKeyboardLayout(EnglishLayoutId, 0);
            }
            catch
            {
                _english = IntPtr.Zero;   /* 预期内：系统没有该布局时返回零，调用方跳过映射 */
            }
            return _english;
        }
    }

    /// <summary>这个布局下的字母键会不会被输入法拼成候选。</summary>
    public static bool IsComposing(IntPtr layout)
    {
        if (layout == IntPtr.Zero) return false;

        // HKL 的低 16 位是语言 ID，高 16 位是设备句柄 —— 只看低 16 位
        int lang = (int)(layout.ToInt64() & 0xFFFF);
        foreach (ushort candidate in ComposingLanguages)
            if (lang == candidate) return true;
        return false;
    }
}
