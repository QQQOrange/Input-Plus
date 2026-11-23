using InputPlus.Core.Gestures;
using InputPlus.Core.Settings;

namespace InputPlus;

/// <summary>
/// 自检：「按住右键连滚换标签」的会话状态机（<see cref="WheelGestureSession"/>）。
///
/// 为什么单独测这一层：用户要的是"右键不松、滚几格换几个标签"，而"第二格还会不会执行"
/// 这个问题只在**时序**里才看得见 —— 真装钩子、真换标签既不能自动化也不稳定。
/// 所以把"谁活着、谁结束、反方向怎么办"收进一个纯内存的状态机，在这里按时序断言；
/// GestureService 只负责把它的结论翻译成 Execute / 吞事件。
/// </summary>
public static partial class SelfTest
{
    private static void TestGestureWheelSession()
    {
        /* 手势库（假的）：方向按内置库那一套 —— 滚轮上 = 上一标签，滚轮下 = 下一标签。
         * 编码与 GestureService.OnWheel 一致：滚轮上 direction&gt;0 → -1，滚轮下 → +1
         * （内置库里的「上一标签」就是 Wheel=-1 → ctrl+shift+tab，「下一标签」是 Wheel=+1 → ctrl+tab）。 */
        var prev = new MouseGesture
        {
            Id = "tab-prev", Name = "上一标签", Button = "right", Kind = "wheel", Wheel = -1, Action = "customKey"
        };
        var next = new MouseGesture
        {
            Id = "tab-next", Name = "下一标签", Button = "right", Kind = "wheel", Wheel = 1, Action = "customKey"
        };
        MouseGesture? Find(int wheel) => wheel == -1 ? prev : wheel == 1 ? next : null;

        // ---- ① 右键按下 → 滚轮上 ×3 → 右键抬起：每一格都执行，一共 3 次 ----
        var session = new WheelGestureSession();
        session.Begin(0x02);   // 右键按下（按下被手势接管的那种）
        int executed = 0;
        for (int i = 1; i <= 3; i++)
        {
            var outcome = session.Wheel(-1, Find);
            CheckEqual($"滚轮会话：即使按住不放，第 {i} 格也要执行", outcome.Kind, WheelOutcomeKind.Execute);
            if (outcome.Kind == WheelOutcomeKind.Execute) executed++;
        }
        CheckEqual("滚轮会话：按住右键滚 3 格 = 执行 3 次", executed, 3);
        CheckEqual("滚轮会话：命中次数等于格子数", session.Hits, 3);
        Check("滚轮会话：这一下按下已经被记为「执行过动作」", session.Fired);
        Check("滚轮会话：右键还按着，会话就还活着", session.Active);

        Check("滚轮会话：右键抬起 = 会话结束（这次抬起要吞）", session.End(0x02));
        Check("滚轮会话：抬起之后没有会话了", !session.Active);
        CheckEqual("滚轮会话：松开右键以后再滚不执行", session.Wheel(-1, Find).Kind, WheelOutcomeKind.None);

        // ---- ② 方向反转：不在"净额"上抵消，每一格按自己的方向执行 ----
        var reverse = new WheelGestureSession();
        reverse.Begin(0x02);
        CheckEqual("滚轮会话：滚轮上 = 上一标签", reverse.Wheel(-1, Find).Hit?.Id, "tab-prev");
        CheckEqual("滚轮会话：滚轮下 = 下一标签（不是把上一格抵消掉）", reverse.Wheel(1, Find).Hit?.Id, "tab-next");
        CheckEqual("滚轮会话：再往上滚又是上一标签", reverse.Wheel(-1, Find).Hit?.Id, "tab-prev");
        CheckEqual("滚轮会话：方向反转不减少执行次数", reverse.Hits, 3);
        CheckEqual("滚轮会话：方向反转记了 2 次", reverse.Reversals, 2);

        // ---- ③ 反方向没绑手势：这一格不执行也不吞，会话保留，原方向照样连发 ----
        MouseGesture? OnlyUp(int wheel) => wheel == -1 ? prev : null;
        var oneWay = new WheelGestureSession();
        oneWay.Begin(0x02);
        CheckEqual("滚轮会话：绑了的上滚执行", oneWay.Wheel(-1, OnlyUp).Kind, WheelOutcomeKind.Execute);
        CheckEqual("滚轮会话：没绑的下滚不执行（也不吞，页面照常滚）", oneWay.Wheel(1, OnlyUp).Kind, WheelOutcomeKind.Unbound);
        CheckEqual("滚轮会话：反方向空转之后，上滚依旧执行", oneWay.Wheel(-1, OnlyUp).Kind, WheelOutcomeKind.Execute);
        CheckEqual("滚轮会话：只执行了绑过的那两格", oneWay.Hits, 2);
        Check("滚轮会话：中途空转也算「执行过动作」，松手不能补发右键", oneWay.Fired);

        // ---- ④ 同一个按下里重复 Begin 不能把计数清掉（第二格滚轮还会再进来） ----
        var again = new WheelGestureSession();
        again.Begin(0x02);
        again.Wheel(-1, Find);
        again.Begin(0x02);   // 第二格滚轮进来时会再"起手"一次，必须幂等
        again.Wheel(-1, Find);
        CheckEqual("滚轮会话：同一个按下重复起手不清零", again.Hits, 2);
        CheckEqual("滚轮会话：重复起手也不清方向记录", again.Reversals, 0);

        // ---- ⑤ 没有按下时滚轮不归会话管（角落 / 贴边 / 原生滚动继续往下走） ----
        var idle = new WheelGestureSession();
        CheckEqual("滚轮会话：没按下时滚动是 None", idle.Wheel(-1, Find).Kind, WheelOutcomeKind.None);
        Check("滚轮会话：None 不吞这一格", !idle.Fired);

        // ---- ⑥ 抬起的是别的键：会话不受影响 ----
        var other = new WheelGestureSession();
        other.Begin(0x02);
        Check("滚轮会话：别的键抬起不结束会话", !other.End(0x01) && other.Active);
        other.Reset();
        Check("滚轮会话：Reset 之后彻底干净",
            !other.Active && !other.Fired && other.Hits == 0 && other.Reversals == 0);

        // ---- ⑦ 一格都没执行时不算"执行过"（松手照旧走 Finish：补发右键菜单） ----
        var untouched = new WheelGestureSession();
        untouched.Begin(0x02);
        untouched.Wheel(1, OnlyUp);   // 只有上滚绑了手势，这一格是空转
        Check("滚轮会话：反方向空转不算「执行过动作」", !untouched.Fired);
        CheckEqual("滚轮会话：空转的格子不进命中数", untouched.Hits, 0);
        CheckEqual("滚轮会话：空转之后上滚照常执行", untouched.Wheel(-1, OnlyUp).Kind, WheelOutcomeKind.Execute);
        CheckEqual("滚轮会话：空转之后再执行，命中数还是 1", untouched.Hits, 1);
    }

    /// <summary>
    /// 只有滚轮手势的键：按下也要吞。
    ///
    /// 不吞的话，按住侧键滚一格、松手时前台程序会收到一次**凭空点击**
    /// （右键菜单弹出来 / 刚选中的东西被取消）。判定入口是
    /// <see cref="UI.GestureService.HasWheelGesture"/> —— 任意方向都算，别颗键不算。
    /// </summary>
    private static void TestWheelOnlyKeyPress()
    {
        var s = DefaultSettings.Create();
        s.Gestures.Clear();
        s.Gestures.Add(new MouseGesture
        {
            Id = "w-up", Name = "滚轮上", Button = "right", Kind = "wheel", Wheel = 1, Action = "customKey"
        });

        Check("滚轮-only：右键配了滚轮手势 → 按下要吞",
            UI.GestureService.HasWheelGesture(s, 0x02, ""));
        Check("滚轮-only：没配手势的键照旧不吞",
            !UI.GestureService.HasWheelGesture(s, 0x05, ""));

        s.Gestures.Clear();
        Check("滚轮-only：一条滚轮手势都没有时不吞",
            !UI.GestureService.HasWheelGesture(s, 0x02, ""));
    }
}
