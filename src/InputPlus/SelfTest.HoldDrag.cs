using System;
using InputPlus.Core.HoldDrag;
using InputPlus.Core.Settings;
using InputPlus.Modules.HoldDrag;

namespace InputPlus;

/// <summary>
/// 自检：并进来的「按住拖拽」。
///
/// 引擎本体（钩子、拖窗）在这里**不启动** —— 自检跑在用户桌面上，
/// 装一个全局鼠标钩子会把用户的鼠标拖歪。所以只验到"配置这一层"：
/// 默认关、JSON 往返、越界值被收敛、关着的时候绝不装钩子。
/// 之所以强调默认关：触发键默认是 Enter，装上就等于抢走回车，
/// 用户只会觉得键盘坏了。
/// </summary>
public static partial class SelfTest
{
    private static void TestHoldDrag()
    {
        var fresh = new AppSettings();
        Check("按住拖拽：默认是关的", !fresh.HoldDragEnabled);
        CheckEqual("按住拖拽：默认没存过配置", fresh.HoldDragJson, "");

        // ---- 空串要能解析成一份能用的默认配置 ----
        var cfg = ConfigStore.Parse("");
        Check("按住拖拽：空配置解析得出默认值", cfg != null);
        if (cfg is null) cfg = new AppConfig();
        CheckEqual("按住拖拽：默认触发键是 Enter", cfg.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：默认长按时长是 600ms", cfg.HoldMs == 600, cfg.HoldMs.ToString());
        CheckEqual("按住拖拽：左键默认拖", cfg.ActionLeft, Actions.Move);
        CheckEqual("按住拖拽：右键默认缩放", cfg.ActionRight, Actions.Resize);
        CheckEqual("按住拖拽：中键默认最大化", cfg.ActionMiddle, Actions.Maximize);

        // ---- JSON 往返：存下来的东西能原样读回来 ----
        cfg.TriggerKeyId = "K:0x20";
        cfg.TriggerMode = "Toggle";
        cfg.HoldMs = 450;
        cfg.FrameStyleId = "Glow";
        cfg.FrameColorId = "Accent";
        cfg.SnapThreshold = 12;
        cfg.Blacklist = "game.exe";
        string json = ConfigStore.Serialize(cfg);
        var back = ConfigStore.Parse(json);
        CheckEqual("按住拖拽：往返保住触发键", back.TriggerKeyId, "K:0x20");
        CheckEqual("按住拖拽：往返保住触发方式", back.TriggerMode, "Toggle");
        Check("按住拖拽：往返保住长按时长", back.HoldMs == 450, back.HoldMs.ToString());
        CheckEqual("按住拖拽：往返保住外框样式", back.FrameStyleId, "Glow");
        CheckEqual("按住拖拽：往返保住外框颜色", back.FrameColorId, "Accent");
        Check("按住拖拽：往返保住吸附阈值", back.SnapThreshold == 12, back.SnapThreshold.ToString());
        CheckEqual("按住拖拽：往返保住黑名单", back.Blacklist, "game.exe");

        // ---- 坏值一律收敛，不许把引擎带崩 ----
        var wild = ConfigStore.Parse("");
        wild.HoldMs = 99999;
        wild.FrameThickness = 0;
        wild.FrameOpacity = 500;
        wild.SnapThreshold = 400;
        wild.TriggerKeyId = "K:0x00";
        wild.FrameStyleId = "不存在的样式";
        wild.ActionLeft = "不存在的动作";
        var fixedUp = ConfigStore.Parse(ConfigStore.Serialize(wild));
        Check("按住拖拽：长按时长被夹回 3000", fixedUp.HoldMs == 3000, fixedUp.HoldMs.ToString());
        Check("按住拖拽：粗细被夹到最小 1", fixedUp.FrameThickness == 1, fixedUp.FrameThickness.ToString());
        Check("按住拖拽：不透明度被夹回 100", fixedUp.FrameOpacity == 100, fixedUp.FrameOpacity.ToString());
        Check("按住拖拽：吸附阈值被夹回 40", fixedUp.SnapThreshold == 40, fixedUp.SnapThreshold.ToString());
        CheckEqual("按住拖拽：无效触发键回落 Enter", fixedUp.TriggerKeyId, "K:0x0D");
        Check("按住拖拽：无效样式回落默认样式",
            string.Equals(fixedUp.FrameStyleId, FrameStyles.DefaultId, StringComparison.Ordinal), fixedUp.FrameStyleId);
        CheckEqual("按住拖拽：无效动作回落拖动", fixedUp.ActionLeft, Actions.Move);

        // ---- 关着的时候：装配一次也不许装钩子 ----
        var settings = new AppSettings();
        using (var service = new HoldDragService(() => settings))
        {
            service.ApplySettings();
            Check("按住拖拽：关着时不会装钩子", !service.Installed);
            Check("按住拖拽：关着时也不报错", string.IsNullOrEmpty(service.LastError), service.LastError);

            var editable = service.EditableConfig();
            Check("按住拖拽：没配置时也能给出可编辑的默认值", editable != null && editable.HoldMs == 600);
            if (editable is null) editable = new AppConfig();

            editable.HoldMs = 520;
            editable.TriggerKeyId = "K:0x20";
            int persisted = 0;
            var withSave = new HoldDragService(() => settings, _ => persisted++);
            Check("按住拖拽：改参数能写回设置", withSave.ApplyEditedConfig(editable));
            withSave.Dispose();
            Check("按住拖拽：写回时通知了宿主保存", persisted == 1, persisted.ToString());

            var saved = ConfigStore.Parse(settings.HoldDragJson);
            Check("按住拖拽：写回的参数读得出来", saved.HoldMs == 520, saved.HoldMs.ToString());
            CheckEqual("按住拖拽：写回的触发键读得出来", saved.TriggerKeyId, "K:0x20");
            Check("按住拖拽：改参数不会顺手把功能打开", !settings.HoldDragEnabled);
            Check("按住拖拽：写回之后钩子仍然没装", !withSave.Installed);
        }

        // ---- 触发键不能被配成一个普通字母键（否则打字都变成拖窗） ----
        Check("按住拖拽：候选触发键都解析得出虚拟键码",
            KeysUtil.ToVirtualKey("K:0x0D") != 0 && KeysUtil.ToVirtualKey("K:0x20") != 0
            && KeysUtil.ToVirtualKey("K:LShift") != 0);
        Check("按住拖拽：Enter 显示成可读名字而不是裸 id",
            KeysUtil.DisplayName("K:0x0D").Length > 0 && KeysUtil.DisplayName("K:0x0D") != "K:0x0D");


        // ---- 触发键候选表覆盖住了要显示的名字（下拉框不能出现裸 id） ----
        Check("按住拖拽：候选触发键都有可读名字",
            KeysUtil.DisplayName("M:1").Length > 0 && KeysUtil.DisplayName("K:LShift").Length > 0);
    }
}
