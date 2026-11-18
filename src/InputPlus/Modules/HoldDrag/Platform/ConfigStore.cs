#nullable disable
#pragma warning disable CS0649, CS8632, CS0414, CA1416
﻿// HoldDrag · Platform/ConfigStore.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace InputPlus.Modules.HoldDrag;

internal sealed class AppConfig
{
	public string TriggerKeyId { get; set; }

	public bool RequireHold { get; set; }

	/// <summary>
	/// 触发模式：Hold = 按住才接管（默认，松手即退出）；Toggle = 点一下抓住、再点一下放下。
	/// 用字符串而不是 bool，是为了以后还能加第三种模式而不改配置格式。
	/// </summary>
	public string TriggerMode { get; set; }

	public int HoldMs { get; set; }

	public string ActionLeft { get; set; }

	public string ActionRight { get; set; }

	public string ActionMiddle { get; set; }

	public bool ShowActivationFrame { get; set; }

	public string FrameColorId { get; set; }

	/// <summary>外框颜色跟随系统强调色：开了以后 FrameColorId / FrameCustomColor 都不生效。</summary>
	public bool FrameColorFollowAccent { get; set; }

	/// <summary>自定义外框颜色（ARGB）；0 = 没设过，回落到 FrameColorId。</summary>
	public int FrameCustomColor { get; set; }

	public string FrameStyleId { get; set; }

	public int FrameThickness { get; set; }

	public int FrameOpacity { get; set; }

	public int FrameRadius { get; set; }

	public bool DoubleClickLeftMaximize { get; set; }

	public bool DoubleClickRightZone { get; set; }

	public bool RestoreMaximizedWhenMoved { get; set; }

	public bool BringToFront { get; set; }

	public bool AllowFullscreenWindows { get; set; }

	public bool AllowDragOwnWindows { get; set; }

	public bool ExitFullscreenOnTrigger { get; set; }

	public bool ShakeMinimizeOthers { get; set; }

	/// <summary>拖动时在光标旁显示尺寸 HUD（W × H）。</summary>
	public bool ShowDragHud { get; set; }

	/// <summary>HUD 里是否附带窗口坐标。</summary>
	public bool DragHudShowPosition { get; set; }

	/// <summary>触发键按住一会儿后浮出快捷键提示。</summary>
	public bool ShowTriggerHint { get; set; }

	/// <summary>拖动时吸附到屏幕/工作区边缘。</summary>
	public bool SnapToEdges { get; set; }

	/// <summary>吸附阈值（像素）。</summary>
	public int SnapThreshold { get; set; }

	/// <summary>触发键按住期间用方向键 / 数字键布局窗口。</summary>
	public bool KeyboardTiling { get; set; }

	/// <summary>拖完 5 秒内按 Ctrl+Z 可以撤销这次移动/缩放。</summary>
	public bool UndoEnabled { get; set; }

	/// <summary>按程序规则：exe=Mode;exe=Mode（见 Core/AppRules.cs）。</summary>
	public string AppRules { get; set; }

	/// <summary>首次启动引导是否已经看过。</summary>
	public bool OnboardingDone { get; set; }

	public int CornerPercent { get; set; }

	public string Blacklist { get; set; }

	/// <summary>
	/// 抽帧（照 AltDrag 的 MoveRate）：每几个鼠标移动事件才真的写一次窗口。
	/// 1 = 每个事件都写，最跟手；调大能把"目标程序写一次很慢"时的队列积压压下去。
	/// </summary>
	public int MoveRate { get; set; }

	/// <summary>缩放的抽帧倍数（照 AltDrag 的 ResizeRate），含义同上。</summary>
	public int ResizeRate { get; set; }

	public int DragThreshold { get; set; }

	public int DragOutThresholdPx { get; set; }

	public string AutoStart { get; set; }

	public bool RunAsAdmin { get; set; }

	public bool WarnNonAdmin { get; set; }

	public int SettingsWidth { get; set; }

	public int SettingsHeight { get; set; }

	/// <summary>启动时自动检查更新（默认开启）。</summary>
	public bool AutoCheckUpdate { get; set; }

	/// <summary>上一次已提示过的版本号，用于"每个新版只提示一次"。</summary>
	public string LastUpdateNotified { get; set; }

	/// <summary>界面语言：zh = 简体中文，en = English（v1.8.1 新增，缺省中文）。</summary>
	public string Language { get; set; }

	/// <summary>
	/// GitHub 个人访问令牌（可选，v1.7.0 新增）。
	/// 填了就带 Authorization 头请求 api.github.com：限额从匿名 60 次/小时提到令牌的 5000 次/小时。
	/// 只用于读公开仓库的发布信息，不需要任何写权限。
	/// </summary>
	public string GitHubToken { get; set; }

	/// <summary>
	/// 本版本不认识的配置字段（例如更新到新版本后又被旧版本打开）。
	/// 由 System.Text.Json 自动收集并原样写回，保证降级运行时不会把新字段悄悄抹掉。
	/// 程序本身**绝不读取**这里的内容，只当作"过路数据"。
	/// </summary>
	[System.Text.Json.Serialization.JsonExtensionData]
	public Dictionary<string, JsonElement> Extras { get; set; }

	public AppConfig()
	{
		TriggerKeyId = "K:0x0D";
		RequireHold = true;
		TriggerMode = "Hold";
		HoldMs = 300;
		ActionLeft = "Move";
		ActionRight = "Resize";
		ActionMiddle = "Maximize";
		ShowActivationFrame = true;
		FrameColorId = FrameColors.DefaultId;
		FrameColorFollowAccent = false;
		FrameCustomColor = 0;
		FrameStyleId = FrameStyles.DefaultId;
		FrameThickness = 3;
		FrameRadius = 13;
		FrameOpacity = 100;
		DoubleClickLeftMaximize = true;
		DoubleClickRightZone = true;
		RestoreMaximizedWhenMoved = true;
		BringToFront = true;
		AllowFullscreenWindows = false;
		AllowDragOwnWindows = true;
		ExitFullscreenOnTrigger = true;
		ShakeMinimizeOthers = true;
		ShowDragHud = true;
		DragHudShowPosition = true;
		ShowTriggerHint = true;
		SnapToEdges = true;
		SnapThreshold = 8;
		KeyboardTiling = true;
		UndoEnabled = true;
		OnboardingDone = false;
		AppRules = string.Empty;
		CornerPercent = 26;
		Blacklist = string.Empty;
		MoveRate = 1;
		ResizeRate = 1;
		DragThreshold = 1;
		DragOutThresholdPx = 20;
		AutoStart = "TaskScheduler";
		RunAsAdmin = true;
		WarnNonAdmin = true;
		SettingsWidth = 0;
		SettingsHeight = 0;
		AutoCheckUpdate = true;
		LastUpdateNotified = string.Empty;
		Language = Loc.Zh;
		GitHubToken = string.Empty;
	}

	public AppConfig Clone()
	{
		AppConfig appConfig = (AppConfig)MemberwiseClone();
		if (Extras != null)
		{
			appConfig.Extras = new Dictionary<string, JsonElement>(Extras);
		}
		return appConfig;
	}

	/// <summary>真正拿去画外框的颜色：跟随强调色 &gt; 自定义 &gt; 调色板。</summary>
	public Color EffectiveFrameColor => FrameColors.ResolveEffective(FrameColorId, FrameColorFollowAccent, FrameCustomColor);

	public void Normalize()
	{
		Language = Loc.Normalize(Language);
		if (SnapThreshold < 0)
		{
			SnapThreshold = 0;
		}
		if (SnapThreshold > 40)
		{
			SnapThreshold = 40;
		}
		if (string.IsNullOrEmpty(TriggerKeyId))
		{
			TriggerKeyId = "K:0x0D";
		}
		// 解析不出有效虚拟键码的触发键（如历史 bug 产出、或手改配置写出的 "K:0x00"）等于没有触发键，回落默认值
		if (KeysUtil.ToVirtualKey(TriggerKeyId) == 0)
		{
			TriggerKeyId = "K:0x0D";
		}
		if (!string.Equals(TriggerMode, "Toggle", StringComparison.OrdinalIgnoreCase))
		{
			TriggerMode = "Hold";
		}
		else
		{
			TriggerMode = "Toggle";
		}
		if (HoldMs < 100)
		{
			HoldMs = 100;
		}
		if (HoldMs > 3000)
		{
			HoldMs = 3000;
		}
		if (!Actions.IsValid(ActionLeft))
		{
			ActionLeft = "Move";
		}
		if (!Actions.IsValid(ActionRight))
		{
			ActionRight = "Resize";
		}
		if (!Actions.IsValid(ActionMiddle))
		{
			ActionMiddle = "Maximize";
		}
		if (!FrameColors.IsValid(FrameColorId))
		{
			FrameColorId = FrameColors.DefaultId;
		}
		// 透明色的自定义颜色等于没设：回落到调色板，免得画出"看不见的外框"。
		if (FrameCustomColor != 0 && ((FrameCustomColor >> 24) & 0xFF) == 0)
		{
			FrameCustomColor = 0;
		}
		if (!FrameStyles.IsValid(FrameStyleId))
		{
			FrameStyleId = FrameStyles.DefaultId;
		}
		if (FrameThickness < 1)
		{
			FrameThickness = 1;
		}
		if (FrameThickness > 8)
		{
			FrameThickness = 8;
		}
		if (FrameRadius < 0)
		{
			FrameRadius = 0;
		}
		if (FrameRadius > 28)
		{
			FrameRadius = 28;
		}
		if (FrameOpacity < 20)
		{
			FrameOpacity = 20;
		}
		if (FrameOpacity > 100)
		{
			FrameOpacity = 100;
		}
		if (CornerPercent < 5)
		{
			CornerPercent = 5;
		}
		if (CornerPercent > 45)
		{
			CornerPercent = 45;
		}
		if (Blacklist == null)
		{
			Blacklist = string.Empty;
		}
		if (Blacklist.Length > 2000)
		{
			Blacklist = Blacklist.Substring(0, 2000);
		}
		// 抽帧倍数（AltDrag 的 MoveRate / ResizeRate）：默认 1 = 每个鼠标事件都写窗口。
		// 老配置里没有这两个字段时走 C# 默认值 0，这里一并夹进来。
		if (MoveRate < 1)
		{
			MoveRate = 1;
		}
		if (MoveRate > 8)
		{
			MoveRate = 8;
		}
		if (ResizeRate < 1)
		{
			ResizeRate = 1;
		}
		if (ResizeRate > 8)
		{
			ResizeRate = 8;
		}
		if (DragThreshold < 0)
		{
			DragThreshold = 0;
		}
		if (DragThreshold > 2)
		{
			DragThreshold = 2;
		}
		if (DragOutThresholdPx < 0)
		{
			DragOutThresholdPx = 0;
		}
		if (DragOutThresholdPx > 200)
		{
			DragOutThresholdPx = 200;
		}
		if (AutoStart != "TaskScheduler" && AutoStart != "Registry" && AutoStart != "None")
		{
			AutoStart = "TaskScheduler";
		}
		int num = MaxUsableWindowWidth();
		int num2 = MaxUsableWindowHeight();
		if (SettingsWidth != 0)
		{
			if (SettingsWidth < 578)
			{
				SettingsWidth = 578;
			}
			if (SettingsWidth > num)
			{
				SettingsWidth = num;
			}
		}
		if (SettingsHeight != 0)
		{
			if (SettingsHeight < 420)
			{
				SettingsHeight = 420;
			}
			if (SettingsHeight > num2)
			{
				SettingsHeight = num2;
			}
		}
		// 令牌：去掉粘贴带进来的空白（移植进 Input Plus 后更新器由 Input Plus 自己管，这里只保留字段做兼容）。
		GitHubToken = (GitHubToken ?? string.Empty).Trim();
	}

	private static int MaxUsableWindowWidth()
	{
		try
		{
			int width = SystemInformation.VirtualScreen.Width;
			if (width >= 640)
			{
				return width;
			}
		}
		catch
		{
		}
		return 2400;
	}

	private static int MaxUsableWindowHeight()
	{
		try
		{
			int height = SystemInformation.VirtualScreen.Height;
			if (height >= 480)
			{
				return height;
			}
		}
		catch
		{
		}
		return 2400;
	}

}
internal static class AutoStartModes
{
	public const string TaskScheduler = "TaskScheduler";

	public const string Registry = "Registry";

	public const string None = "None";
}
internal sealed class ConfigStore
{
	private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
	{
		WriteIndented = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	public string Dir { get; private set; }

	public string FilePath { get; private set; }

	private string BakPath => FilePath + ".bak";

	private string Bak2Path => FilePath + ".bak2";

	public AppConfig Current { get; private set; }

	public event Action Changed;

	// Input Plus 版：不再碰磁盘，JSON 存在宿主 AppSettings 里（随 vault/云端备份一起走）。
	private readonly Func<string> _loadJson;

	private readonly Func<string, bool> _saveJson;

	private bool InMemory => this._saveJson != null;

	/// <summary>把 JSON 文本解析成配置（宿主界面用：读出来编辑）。解析失败给默认值。</summary>
	public static AppConfig Parse(string json)
	{
		AppConfig appConfig = null;
		try
		{
			if (!string.IsNullOrWhiteSpace(json))
			{
				appConfig = JsonSerializer.Deserialize<AppConfig>(json, Opts);
				Migrate(appConfig, json);
			}
		}
		catch (Exception ex)
		{
			Log.Error("解析按住拖拽配置失败 :: " + ex.Message);
			appConfig = null;
		}
		if (appConfig == null)
		{
			appConfig = new AppConfig();
		}
		appConfig.Normalize();
		return appConfig;
	}

	/// <summary>把配置写成 JSON 文本（宿主界面用：写回设置）。</summary>
	public static string Serialize(AppConfig cfg)
	{
		if (cfg == null)
		{
			return string.Empty;
		}
		cfg.Normalize();
		return JsonSerializer.Serialize(cfg, Opts);
	}

	public ConfigStore(string dir)
	{
		Dir = dir;
		FilePath = Path.Combine(dir, "config.json");
	}

	public ConfigStore(Func<string> loadJson, Func<string, bool> saveJson)
	{
		this._loadJson = loadJson;
		this._saveJson = saveJson;
		Dir = string.Empty;
		FilePath = string.Empty;
	}

	public void Load()
	{
		if (InMemory)
		{
			AppConfig mem = null;
			try
			{
				string raw = this._loadJson?.Invoke();
				if (!string.IsNullOrWhiteSpace(raw))
				{
					mem = JsonSerializer.Deserialize<AppConfig>(raw, Opts);
					Migrate(mem, raw);
				}
			}
			catch (Exception ex)
			{
				Log.Error("读取按住拖拽配置失败 :: " + ex.Message);
				mem = null;
			}
			if (mem == null)
			{
				mem = new AppConfig();
			}
			mem.Normalize();
			Current = mem;
			return;
		}
		AppConfig appConfig = TryRead(FilePath);
		bool flag = false;
		if (appConfig == null)
		{
			appConfig = TryRead(BakPath);
			if (appConfig != null)
			{
				flag = true;
			}
		}
		if (appConfig == null)
		{
			appConfig = TryRead(Bak2Path);
			if (appConfig != null)
			{
				flag = true;
			}
		}
		if (appConfig == null)
		{
			appConfig = new AppConfig();
			Log.Info("配置不存在或全部损坏，使用出厂默认值");
		}
		else if (flag)
		{
			Log.Info("主配置损坏，已从备份恢复");
		}
		appConfig.Normalize();
		Current = appConfig;
		if (flag)
		{
			try
			{
				Save(out _);
			}
			catch (Exception ex)
			{
				Log.Error(ex);
			}
		}
	}

	/// <summary>
	/// 老配置里没有这些字段（JSON 缺键 → 反序列化得到默认值），需要做一次性迁移：
	/// 已经用过这个软件的人不该再被"首次引导"拦一次。
	/// </summary>
	private static void Migrate(AppConfig cfg, string rawJson)
	{
		try
		{
			if (cfg == null || string.IsNullOrEmpty(rawJson))
			{
				return;
			}
			if (!rawJson.Contains("\"OnboardingDone\""))
			{
				cfg.OnboardingDone = true;
			}
		}
		catch
		{
		}
	}

	private static AppConfig TryRead(string path)
	{
		try
		{
			if (!File.Exists(path))
			{
				return null;
			}
			string text = File.ReadAllText(path);
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			AppConfig appConfig = JsonSerializer.Deserialize<AppConfig>(text, Opts);
			Migrate(appConfig, text);
			return appConfig;
		}
		catch (Exception ex)
		{
			Log.Error("读取配置失败 " + path + " :: " + ex.Message);
			return null;
		}
	}

	public bool Save(out string error)
	{
		error = string.Empty;
		if (InMemory)
		{
			try
			{
				Current.Normalize();
				string json = JsonSerializer.Serialize(Current, Opts);
				if (this._saveJson(json))
				{
					return true;
				}
				error = "宿主拒绝了保存";
				return false;
			}
			catch (Exception ex)
			{
				error = ex.Message;
				Log.Error("保存按住拖拽配置失败 :: " + ex.Message);
				return false;
			}
		}
		string text = FilePath + ".tmp";
		try
		{
			Current.Normalize();
			Directory.CreateDirectory(Dir);
			string contents = JsonSerializer.Serialize(Current, Opts);
			// 先写临时文件并强制落盘，再原子替换：断电/崩溃时主配置要么是旧的、要么是新的，不会留下半个文件。
			using (FileStream fileStream = new FileStream(text, FileMode.Create, FileAccess.Write, FileShare.None))
			using (StreamWriter streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
			{
				streamWriter.Write(contents);
				streamWriter.Flush();
				fileStream.Flush(flushToDisk: true);
			}
			RotateBackups();
			File.Move(text, FilePath, overwrite: true);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			Log.Error("保存配置失败 :: " + ex.Message);
			try
			{
				if (File.Exists(text))
				{
					File.Delete(text);
				}
			}
			catch
			{
			}
			return false;
		}
	}

	/// <summary>
	/// 轮换备份：只有**能解析**的现有文件才配进入 .bak / .bak2。
	/// 旧实现无条件复制，主配置损坏 → 恢复 → 立即保存 的路径会把仍可用的 .bak 覆盖成坏文件。
	/// </summary>
	private void RotateBackups()
	{
		if (InMemory)
		{
			return;
		}
		try
		{
			if (!File.Exists(FilePath) || TryRead(FilePath) == null)
			{
				return;
			}
			if (File.Exists(BakPath) && TryRead(BakPath) != null)
			{
				File.Copy(BakPath, Bak2Path, overwrite: true);
			}
			File.Copy(FilePath, BakPath, overwrite: true);
		}
		catch (Exception ex)
		{
			Log.Error("轮换配置备份失败（不影响本次保存） :: " + ex.Message);
		}
	}

	/// <summary>
	/// 保存编辑结果。成功才替换内存里的 <see cref="Current"/> 并广播 <see cref="Changed"/>：
	/// 写盘失败时保持"内存与磁盘一致"，界面上的改动仍留在表单里，用户可重试。
	/// 另外始终保存**副本**：调用方（设置窗口）还会继续改自己那份 _edit，
	/// 若直接存引用，"保存后取消"就会连正在生效的配置一起改掉。
	/// </summary>
	public bool Apply(AppConfig edited, out string error)
	{
		if (edited == null)
		{
			error = Loc.T("没有可保存的配置。");
			return false;
		}
		AppConfig appConfig = edited.Clone();
		appConfig.Normalize();
		AppConfig current = Current;
		Current = appConfig;
		if (!Save(out error))
		{
			Current = current;
			return false;
		}
		Changed?.Invoke();
		return true;
	}
}
