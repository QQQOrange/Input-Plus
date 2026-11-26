using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Resources;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Icons;
using InputPlus.Core.Input;
using InputPlus.Core.Interop;
using InputPlus.Core.Localization;
using InputPlus.Core.Qbar;
using InputPlus.Core.Qbar.Search;
using InputPlus.Core.Settings;
using InputPlus.Core.Suggestions;
using InputPlus.Core.Text;
using InputPlus.Core.Windows;
using InputPlus.UI.Theme;
using Microsoft.Win32;

namespace InputPlus.UI.Windows;

public partial class QbarWindow : Window, IComponentConnector
{
	private struct AccentPolicy
	{
		public int AccentState;

		public int AccentFlags;

		public int GradientColor;

		public int AnimationId;
	}

	private struct WindowCompositionAttributeData
	{
		public int Attribute;

		public nint Data;

		public int SizeOfData;
	}

	private sealed class QbarItem : INotifyPropertyChanged
	{
		private ImageSource? _iconSource;

		private bool _hasIcon;

		private const int IconDip = 28;

		public IReadOnlyList<QbarTitleSegment> TitleSegments { get; }

		public QbarResult Result { get; }

		public string IconGlyph { get; }

		public string Badge { get; }

		public ImageSource? IconSource => _iconSource;

		public bool HasIcon => _hasIcon;

		public bool HasLetter { get; }

		public string Letter { get; } = "";

		public Brush LetterBrush { get; } = Brushes.Transparent;

		public string Title => Result.Title;

		public string Subtitle => Result.Subtitle;

		public event PropertyChangedEventHandler? PropertyChanged;

		public QbarItem(QbarResult result, bool showIcons = true)
		{
			Result = result;
			QbarChip chip = result.Chip;
			if (showIcons)
			{
				if (chip != null && chip.HasIcon)
				{
					SetIcon(chip.Icon);
				}
				else if (chip != null)
				{
					Letter = chip.Initial;
					LetterBrush = new SolidColorBrush(chip.Accent);
					HasLetter = true;
					TryResolveIcon(result);
				}
				else
				{
					TryResolveIcon(result);
				}
			}
			string text = result.Glyph;
			if (text == null)
			{
				text = result.Kind switch
				{
					QbarKind.Search => "\ue721", 
					QbarKind.Web => "\ue774", 
					QbarKind.Run => "\ue756", 
					QbarKind.Window => "\ue737", 
					QbarKind.Math => "\ue8ef", 
					QbarKind.Dictionary => "\ue82d", 
					QbarKind.Clipboard => "\ue77f", 
					QbarKind.File => "\ue8b7", 
					_ => "\ue756", 
				};
			}
			IconGlyph = text;
			Badge = ((result.Suggestion.Length > 0 && result.Badge.Length > 0) ? result.Badge : ((result.Kind == QbarKind.Command) ? result.Payload : result.KindLabel));
			TitleSegments = BuildTitleSegments(result);
		}

		private static IReadOnlyList<QbarTitleSegment> BuildTitleSegments(QbarResult result)
		{
			return QbarTitleHighlight.Segments(result.Title, result.Highlights);
		}

		private void TryResolveIcon(QbarResult result)
		{
			double num = UiDpiScale();
			int num2 = ShellIcons.PickPixels(28, num);
			List<string> list = IconCandidates(result);
			foreach (string item in list)
			{
				ImageSource imageSource = ShellIcons.TryGetCached(item, num2, num);
				if (imageSource != null)
				{
					SetIcon(imageSource);
					return;
				}
			}
			if (list.Count == 0)
			{
				WebFaviconFallback(result);
			}
			else
			{
				ResolveIconAsync(list, 0, num2, num, result);
			}
		}

		private void ResolveIconAsync(List<string> candidates, int index, int px, double dpi, QbarResult result)
		{
			if (index >= candidates.Count)
			{
				WebFaviconFallback(result);
				return;
			}
			ShellIcons.Request(candidates[index], px, dpi, (ImageSource? image) =>
			{
				if (image != null)
				{
					Application current = Application.Current;
					if (current != null)
					{
						((DispatcherObject)current).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
						{
							SetIcon(image);
						}), Array.Empty<object>());
					}
				}
				else
				{
					ResolveIconAsync(candidates, index + 1, px, dpi, result);
				}
			});
		}

		private void WebFaviconFallback(QbarResult result)
		{
			if (result.Kind != QbarKind.Web || string.IsNullOrWhiteSpace(result.Payload))
			{
				return;
			}
			IconCache.LoadWebIconAsync(result.Payload, (ImageSource? image) =>
			{
				if (image != null)
				{
					Application current = Application.Current;
					if (current != null)
					{
						((DispatcherObject)current).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
						{
							SetIcon(image);
						}), Array.Empty<object>());
					}
				}
			});
		}

		private static List<string> IconCandidates(QbarResult result)
		{
			List<string> list = new List<string>();
			AddCandidate(list, result.IconPath);
			string text = ShellIcons.NormalizePath(result.Payload);
			if (text.Length > 0)
			{
				if (result.Kind == QbarKind.File)
				{
					AddCandidate(list, text);
				}
				else if (result.Kind == QbarKind.Run)
				{
					AddCandidate(list, ShellIcons.ResolveFilesystemPath(text) ?? text);
				}
			}
			return list;
		}

		private static void AddCandidate(List<string> list, string? path)
		{
			string text = ShellIcons.NormalizePath(path);
			if (text.Length == 0)
			{
				return;
			}
			foreach (string item in list)
			{
				if (string.Equals(item, text, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}
			}
			list.Add(text);
		}

		private static double UiDpiScale()
		{
			try
			{
				WindowCollection windowCollection = Application.Current?.Windows;
				if (windowCollection != null)
				{
					foreach (Window item in windowCollection)
					{
						if (item is QbarWindow { IsVisible: not false } qbarWindow)
						{
							return VisualTreeHelper.GetDpi(qbarWindow).DpiScaleX;
						}
					}
				}
			}
			catch
			{
			}
			return ShellIcons.SystemDpiScale();
		}

		private void SetIcon(ImageSource? icon)
		{
			if (icon != null)
			{
				_iconSource = icon;
				_hasIcon = true;
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconSource"));
				PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HasIcon"));
			}
		}
	}

	private const string GlyphSample = "汉字Agqy";

	private readonly AppHost _host;

	private List<QbarItem> _items = new List<QbarItem>();

	private QbarChip? _chip;

	private bool _closing;

	private bool _suppressTextChanged;

	private bool _imeApplied;

	private string _imeKey = "";

	private DateTime _imeAt = DateTime.MinValue;

	private int _imeRetry;

	private string _imeLoggedMode = "";

	private DispatcherTimer? _imeTimer;

	private DispatcherTimer? _imeVerify;

	private const int SuggestDebounceMs = 250;

	private readonly DispatcherTimer _suggestTimer;

	private CancellationTokenSource? _suggestCts;

	private int _suggestSeq;

	private bool _suggestVisible;

	private bool _dragging;

	private bool _dragMoved;

	private NativeMethods.POINT _dragCursor;

	private NativeMethods.RECT _dragWindowRect;

	private DispatcherTimer? _dragPoll;

	private const int VK_LBUTTON = 1;

	private const int VK_ESCAPE = 27;

	private QbarItem? _menuFile;

	private List<QbarItem> _menuReturnItems = new List<QbarItem>();

	private int _menuReturnIndex;

	private readonly Dictionary<string, Action> _menuActions = new Dictionary<string, Action>(StringComparer.Ordinal);

	private const int QueryDebounceMs = 45;

	private readonly DispatcherTimer _queryTimer;

	private int _querySeq;

	private bool _imeComposing;

	private bool _pendingQueryAfterComposition;

	private QbarFilter? _appliedFilter;

	private QbarSearchOutcome? _lastOutcome;

	private string _lastQueryText = "";

	private const string FooterDefaultText = "↑↓ 选择    Enter 执行    Tab 补全    Esc 关闭";

	private nint _hwnd;

	private DispatcherTimer? _focusTimer;

	private int _focusTries;

	private DateTime _openedAt = DateTime.MinValue;

	private const int OpenGraceMs = 320;

	// ---- 点击面板外面就收起：自己养一颗钩子，不依赖 Engine ----
	/// <summary>
	/// Qbar 自己的鼠标钩子。
	///
	/// 为什么不只订阅 <c>_host.Engine.MouseDownEvent</c>：宿主模式（--hookhost）下
	/// 钩子在最小进程里，主进程的 Engine 根本没装钩子
	/// （AppHost: <c>Engine.Start(installHook: !hookHostReady)</c>）—— 订阅它等于订阅空气，
	/// 面板只剩 Deactivated 一条路，点到"不换前台"的地方或前台切换被权限挡住时就收不起来。
	/// 手势服务踩过同一个坑，见 KeyboardHook 的注释。
	/// </summary>
	private KeyboardHook? _outsideHook;

	/// <summary>前台兜底巡检：钩子被系统摘掉、前台切换没通知时靠它收面板。</summary>
	private DispatcherTimer? _outsideTimer;

	/// <summary>面板至少成为过一次前台；没成为过就不按"前台变了"收，免得抢前台失败时闪一下就没。</summary>
	private bool _wasForeground;

	private SolidColorBrush? _panelBrush;

	private string _styleStamp = "";

	private Color _panelColor;

	private const uint FrostTintAlpha = 64u;

	private const double FrostBaseAlpha = 0.7;

	private const int WcaAccentPolicy = 19;

	private const int AccentNormal = 0;

	private const int AccentAcrylicBlur = 4;

	private Func<int, bool>? _escInterceptor;

	private static Cursor? _grabOpenCursor;

	private static Cursor? _grabClosedCursor;

	private const string GlyphDictionary = "\ue82d";

	private static Cursor GrabOpenCursor => _grabOpenCursor ?? (_grabOpenCursor = LoadCursorAsset("Assets/cursors/grab-open.cur", Cursors.Hand));

	private static Cursor GrabClosedCursor => _grabClosedCursor ?? (_grabClosedCursor = LoadCursorAsset("Assets/cursors/grab-closed.cur", Cursors.SizeAll));

	private static bool IsDown(int vk)
	{
		return (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
	}

	private void ApplyPanelClip(Size size)
	{
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		if (!(size.Width <= 0.0) && !(size.Height <= 0.0))
		{
			Panel.Clip = new RectangleGeometry(new Rect(size), 8.0, 8.0);
		}
	}

	public QbarWindow(AppHost host)
	{
		//IL_013d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0148: Unknown result type (might be due to invalid IL or missing references)
		//IL_014d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0163: Expected Obj, but got Unknown
		//IL_017b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Expected Obj, but got Unknown
		_host = host;
		InitializeComponent();
		Deactivated += OnDeactivated;
		Activated += (object? _, EventArgs _) =>
		{
			FocusInputNow();
		};
		PreviewKeyDown += OnWindowKeyDown;
		Results.SelectionChanged += (object _, SelectionChangedEventArgs _) =>
		{
			UpdateHint();
			SyncSuggestionSelection();
		};
		if (_host.Suggestions != null)
		{
			_host.Suggestions.Changed += OnSuggestionsChanged;
		}
		Panel.PreviewMouseLeftButtonDown += OnPanelMouseDown;
		Panel.PreviewMouseMove += OnPanelMouseMove;
		Panel.SizeChanged += (object _, SizeChangedEventArgs e) =>
		{
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			ApplyPanelClip(e.NewSize);
		};
		ApplyPanelClip(Panel.RenderSize);
		_suggestTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(250L)
		};
		_suggestTimer.Tick += OnSuggestTimerTick;
		_queryTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(45L)
		};
		_queryTimer.Tick += OnQueryTimerTick;
		TextCompositionManager.AddPreviewTextInputStartHandler((DependencyObject)(object)Input, OnCompositionStart);
		TextCompositionManager.AddPreviewTextInputUpdateHandler((DependencyObject)(object)Input, OnCompositionUpdate);
		TextCompositionManager.AddPreviewTextInputHandler((DependencyObject)(object)Input, OnCompositionEnd);
		ApplyStyle();
	}

	private void FocusInputNow()
	{
		try
		{
			if (!IsActive)
			{
				Activate();
			}
			Focus();
			Keyboard.Focus(Input);
			Input.CaretIndex = Input.Text.Length;
			if (!_imeApplied && IsActive)
			{
				_imeApplied = ApplyImePreference();
				if (!_imeApplied)
				{
					QueueImePreference(180);
				}
			}
		}
		catch
		{
		}
	}

	private bool ApplyImePreference(bool force = false)
	{
		try
		{
			if (_chip == null)
			{
				if (!InputMethod.GetIsInputMethodEnabled((DependencyObject)(object)Input))
				{
					InputMethod.SetIsInputMethodEnabled((DependencyObject)(object)Input, value: true);
				}
				return true;
			}
			string want = "zh:" + _chip.Key;
			if (!force && _imeKey == want && (DateTime.UtcNow - _imeAt).TotalMilliseconds < 800.0)
			{
				return true;
			}
			InputMethod.SetIsInputMethodEnabled((DependencyObject)(object)Input, value: true);
			if (!_host.Settings.Qbar.SwitchBackOnEngine)
			{
				return Done(ok: true);
			}
			return Done(ShiftImeTo(chinese: true));
			bool Done(bool ok)
			{
				if (ok)
				{
					_imeKey = want;
					_imeAt = DateTime.UtcNow;
				}
				return ok;
			}
		}
		catch (Exception ex)
		{
			CoreLog.Swallow(ex, "切换输入法");
			return false;
		}
	}

	private bool ShiftImeTo(bool chinese)
	{
		InputMethod.SetPreferredImeState((DependencyObject)(object)Input, InputMethodState.On);
		InputMethod current = InputMethod.Current;
		if (current == null)
		{
			return false;
		}
		if (current.ImeState != InputMethodState.On)
		{
			current.ImeState = InputMethodState.On;
		}
		ImeConversionModeValues imeConversionMode = current.ImeConversionMode;
		bool flag = (imeConversionMode & ImeConversionModeValues.Native) != 0;
		bool flag2 = (imeConversionMode & ImeConversionModeValues.Alphanumeric) != 0;
		string text = imeConversionMode.ToString();
		if (_imeLoggedMode != text)
		{
			_imeLoggedMode = text;
			CoreLog.Info("[Qbar] IME 当前=" + text + "，目标=" + (chinese ? "中文" : "英文"));
		}
		if (chinese ? flag : flag2)
		{
			return true;
		}
		if (!flag && !flag2)
		{
			return false;
		}
		InputSender.Tap(16);
		try
		{
			ImeConversionModeValues imeConversionModeValues = (chinese ? ImeConversionModeValues.Native : ImeConversionModeValues.Alphanumeric);
			InputMethod.SetPreferredImeConversionMode((DependencyObject)(object)Input, imeConversionModeValues);
			current.ImeConversionMode = imeConversionModeValues;
		}
		catch
		{
		}
		return true;
	}

	private void QueueImePreference(int delayMs = 140)
	{
		_imeRetry = 0;
		StartImeTimer(delayMs);
	}

	private void StartImeTimer(int delayMs)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected Obj, but got Unknown
		DispatcherTimer? imeTimer = _imeTimer;
		if (imeTimer != null)
		{
			imeTimer.Stop();
		}
		_imeTimer = new DispatcherTimer((DispatcherPriority)5)
		{
			Interval = TimeSpan.FromMilliseconds(Math.Max(30, delayMs))
		};
		_imeTimer.Tick += (object? _, EventArgs _) =>
		{
			DispatcherTimer? imeTimer2 = _imeTimer;
			if (imeTimer2 != null)
			{
				imeTimer2.Stop();
			}
			if (IsVisible)
			{
				_imeApplied = ApplyImePreference(force: true);
				if (!_imeApplied && _imeRetry++ < 5)
				{
					StartImeTimer(250);
				}
				else if (_imeApplied)
				{
					ScheduleImeVerify();
				}
			}
		};
		_imeTimer.Start();
	}

	private void ScheduleImeVerify()
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_0018: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Expected Obj, but got Unknown
		DispatcherTimer? imeVerify = _imeVerify;
		if (imeVerify != null)
		{
			imeVerify.Stop();
		}
		_imeVerify = new DispatcherTimer((DispatcherPriority)4)
		{
			Interval = TimeSpan.FromMilliseconds(700L)
		};
		_imeVerify.Tick += (object? _, EventArgs _) =>
		{
			DispatcherTimer? imeVerify2 = _imeVerify;
			if (imeVerify2 != null)
			{
				imeVerify2.Stop();
			}
			if (IsVisible)
			{
				_imeApplied = ApplyImePreference(force: true);
			}
		};
		_imeVerify.Start();
	}

	private void BeginForceFocus()
	{
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Expected Obj, but got Unknown
		try
		{
			_hwnd = new WindowInteropHelper(this).Handle;
		}
		catch
		{
			_hwnd = IntPtr.Zero;
		}
		Topmost = true;
		FocusInputNow();
		if (_hwnd == IntPtr.Zero || !WindowService.ForegroundIs(_hwnd))
		{
			_focusTries = 0;
			if (_focusTimer == null)
			{
				_focusTimer = new DispatcherTimer((DispatcherPriority)10)
				{
					Interval = TimeSpan.FromMilliseconds(20L)
				};
			}
			_focusTimer.Tick -= OnFocusTick;
			_focusTimer.Tick += OnFocusTick;
			_focusTimer.Start();
		}
	}

	private void OnFocusTick(object? sender, EventArgs e)
	{
		if (!IsVisible || _closing)
		{
			StopFocusTimer();
			return;
		}
		if (_hwnd == IntPtr.Zero)
		{
			try
			{
				_hwnd = new WindowInteropHelper(this).Handle;
			}
			catch
			{
			}
		}
		if (_hwnd != IntPtr.Zero && WindowService.ForegroundIs(_hwnd))
		{
			FocusInputNow();
			StopFocusTimer();
			return;
		}
		if (++_focusTries > 20)
		{
			StopFocusTimer();
			CoreLog.Warn("Qbar 抢前台失败（前台锁定或被权限更高的程序占着），已放弃");
			return;
		}
		bool allowUnlockTimeout = _focusTries >= 6;
		if (_hwnd != IntPtr.Zero)
		{
			WindowService.ForceForeground(_hwnd, allowUnlockTimeout);
		}
		FocusInputNow();
	}

	private void StopFocusTimer()
	{
		try
		{
			DispatcherTimer? focusTimer = _focusTimer;
			if (focusTimer != null)
			{
				focusTimer.Stop();
			}
			if (_focusTimer != null)
			{
				_focusTimer.Tick -= OnFocusTick;
			}
		}
		catch
		{
		}
	}

	private void OnDeactivated(object? sender, EventArgs e)
	{
		if (ShouldHideOnDeactivate((int)(DateTime.Now - _openedAt).TotalMilliseconds))
		{
			HideQbar();
		}
	}

	internal static bool ShouldHideOnDeactivate(int elapsedMs)
	{
		return elapsedMs >= 320;
	}

	private void StartOutsideWatch()
	{
		_wasForeground = false;

		/* 自己的钩子：面板打开期间必须有。宿主模式下 Engine 那条是空的（见字段注释），
		 * 只有这一条能真正看到"点在哪"。 */
		_outsideHook ??= new KeyboardHook();
		_outsideHook.MouseDownEvent -= OnGlobalMouseDown;
		_outsideHook.MouseDownEvent += OnGlobalMouseDown;
		if (!_outsideHook.IsRunning)
		{
			try
			{
				_outsideHook.Start(mouseOnly: true);
			}
			catch (Exception ex)
			{
				CoreLog.Swallow(ex, "Qbar 外部点击钩子");
			}
		}

		/* 拿不到宿主 READY 时主进程自己会装钩子，这条顺带留着；
		 * 两边都回调也没关系 —— HideQbar 自己是幂等的。 */
		_host.Engine.MouseDownEvent -= OnGlobalMouseDown;
		_host.Engine.MouseDownEvent += OnGlobalMouseDown;

		/* 兜底：前台一旦不是面板自己、又属于别的进程，直接收。
		 * 覆盖"钩子被系统摘掉"和"点击没换前台但前台确实丢了"这两种情况。 */
		_outsideTimer ??= new DispatcherTimer(DispatcherPriority.Background)
		{
			Interval = TimeSpan.FromMilliseconds(120L)
		};
		_outsideTimer.Tick -= OnOutsideTick;
		_outsideTimer.Tick += OnOutsideTick;
		_outsideTimer.Start();
	}

	private void StopOutsideWatch()
	{
		_host.Engine.MouseDownEvent -= OnGlobalMouseDown;
		if (_outsideHook != null)
		{
			_outsideHook.MouseDownEvent -= OnGlobalMouseDown;
		}
		DispatcherTimer? outsideTimer = _outsideTimer;
		if (outsideTimer != null)
		{
			outsideTimer.Stop();
			outsideTimer.Tick -= OnOutsideTick;
		}
	}

	/// <summary>前台兜底巡检：只在面板可见时跑，命中即收。</summary>
	private void OnOutsideTick(object? sender, EventArgs e)
	{
		if (!IsVisible || _closing)
		{
			return;
		}
		if (_hwnd == IntPtr.Zero)
		{
			try
			{
				_hwnd = new WindowInteropHelper(this).Handle;
			}
			catch
			{
				return;
			}
		}
		if (WindowService.ForegroundIs(_hwnd))
		{
			_wasForeground = true;
			return;
		}
		if (!_wasForeground)
		{
			return;
		}
		if ((DateTime.Now - _openedAt).TotalMilliseconds < (double)OpenGraceMs)
		{
			return;
		}
		IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
		if (foregroundWindow != IntPtr.Zero)
		{
			NativeMethods.GetWindowThreadProcessId(foregroundWindow, out uint lpdwProcessId);
			if (lpdwProcessId == NativeMethods.GetCurrentProcessId())
			{
				return;
			}
		}
		HideQbar();
	}

	private void OnGlobalMouseDown(int x, int y, int mouseMessage)
	{
		if (_hwnd == IntPtr.Zero)
		{
			return;
		}
		nint num = NativeMethods.WindowFromPoint(new NativeMethods.POINT
		{
			X = x,
			Y = y
		});
		if (num != IntPtr.Zero)
		{
			NativeMethods.GetWindowThreadProcessId(num, out var lpdwProcessId);
			if (lpdwProcessId == NativeMethods.GetCurrentProcessId())
			{
				return;
			}
		}
		if (NativeMethods.GetWindowRect(_hwnd, out var lpRect) && IsOutsidePanel(x, y, lpRect.Left, lpRect.Top, lpRect.Right, lpRect.Bottom) && !((DateTime.Now - _openedAt).TotalMilliseconds < 320.0))
		{
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)new Action(HideQbar), Array.Empty<object>());
		}
	}

	internal static bool IsOutsidePanel(int x, int y, int left, int top, int right, int bottom)
	{
		if (x >= left && x < right && y >= top)
		{
			return y >= bottom;
		}
		return true;
	}

	private static bool IsLightColor(Color c)
	{
		return (0.2126 * (double)(int)c.R + 0.7152 * (double)(int)c.G + 0.0722 * (double)(int)c.B) / 255.0 > 0.55;
	}

	private static Color Mix(Color a, Color b, double t)
	{
		return Color.FromRgb((byte)((double)(int)a.R + (double)(b.R - a.R) * t), (byte)((double)(int)a.G + (double)(b.G - a.G) * t), (byte)((double)(int)a.B + (double)(b.B - a.B) * t));
	}

	private static double RelativeLuminance(Color c)
	{
		return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
		static double Ch(byte v)
		{
			double num = (double)(int)v / 255.0;
			if (!(num <= 0.03928))
			{
				return Math.Pow((num + 0.055) / 1.055, 2.4);
			}
			return num / 12.92;
		}
	}

	internal static double Contrast(Color a, Color b)
	{
		double val = RelativeLuminance(a);
		double val2 = RelativeLuminance(b);
		return (Math.Max(val, val2) + 0.05) / (Math.Min(val, val2) + 0.05);
	}

	internal static Color BestTextOn(Color background)
	{
		Color white = Colors.White;
		Color black = Colors.Black;
		if (!(Contrast(white, background) >= Contrast(black, background)))
		{
			return black;
		}
		return white;
	}

	internal static Color Overlay(Color baseColor, Color veil, double alpha)
	{
		return Mix(baseColor, veil, alpha);
	}

	internal static Color PrimaryOn(Color panel)
	{
		if (!IsLightColor(panel))
		{
			return Colors.White;
		}
		return Color.FromRgb(22, 26, 35);
	}

	private void ApplyForegroundPalette(Color panelColor)
	{
		bool flag = IsLightColor(panelColor);
		Color c = PrimaryOn(panelColor);
		Color c2 = (flag ? Color.FromRgb(91, 100, 120) : Color.FromRgb(201, 214, 236));
		Color c3 = (flag ? Color.FromRgb(138, 147, 166) : Color.FromRgb(159, 176, 204));
		Color c4 = (flag ? Color.FromRgb(168, 176, 191) : Color.FromRgb(127, 141, 168));
		Color color = (flag ? Colors.Black : Colors.White);
		SolidColorBrush solidColorBrush = Frozen(c);
		SolidColorBrush solidColorBrush2 = Frozen(c2);
		SolidColorBrush foreground = Frozen(c3);
		SolidColorBrush value = Frozen(c4);
		SolidColorBrush background = Frozen(Color.FromArgb(15, color.R, color.G, color.B));
		SolidColorBrush background2 = Frozen(Color.FromArgb(26, color.R, color.G, color.B));
		SolidColorBrush background3 = Frozen(Color.FromArgb(34, color.R, color.G, color.B));
		SolidColorBrush borderBrush = Frozen(Color.FromArgb(51, color.R, color.G, color.B));
		ModeIcon.Foreground = solidColorBrush2;
		Input.Foreground = solidColorBrush;
		Input.CaretBrush = solidColorBrush;
		ChipLabel.Foreground = solidColorBrush;
		ChipClose.Foreground = foreground;
		ChipBox.Background = background3;
		ChipBox.BorderBrush = borderBrush;
		ChipInitialBox.Background = background3;
		ChipInitial.Foreground = Frozen(BestTextOn(_chip?.Accent ?? Colors.SteelBlue));
		HintText.Foreground = foreground;
		if (ModeIcon.Parent is Border border)
		{
			border.Background = background3;
		}
		Resources["Brush.OverlayText"] = solidColorBrush;
		Resources["Brush.OverlayTextSecondary"] = solidColorBrush2;
		Resources["Brush.OverlayTextTertiary"] = value;
		ModeIcon.Foreground = solidColorBrush2;
		Input.Foreground = solidColorBrush;
		ChipLabel.Foreground = solidColorBrush;
		ChipClose.Foreground = foreground;
		HintText.Foreground = foreground;
		foreach (Border item in FindAll<Border>((DependencyObject)(object)this))
		{
			double topLeft = item.CornerRadius.TopLeft;
			if (topLeft > 4.0 && topLeft < 10.0)
			{
				topLeft = item.Width;
				if (topLeft > 0.0 && topLeft < 30.0)
				{
					item.Background = background2;
				}
			}
		}
		foreach (Border item2 in FindAll<Border>((DependencyObject)(object)this))
		{
			if (Math.Abs(item2.Width - 30.0) < 1.0 && Math.Abs(item2.Height - 30.0) < 1.0)
			{
				item2.Background = background;
			}
		}
	}

	private static SolidColorBrush Frozen(Color c)
	{
		SolidColorBrush solidColorBrush = new SolidColorBrush(c);
		((Freezable)solidColorBrush).Freeze();
		return solidColorBrush;
	}

	private static Color ParseColor(string s)
	{
		try
		{
			return (Color)ColorConverter.ConvertFromString(s);
		}
		catch
		{
			return (Color)ColorConverter.ConvertFromString("#1C1F27");
		}
	}

	private Color ThemePanelColor()
	{
		try
		{
			if (Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush solidColorBrush)
			{
				return solidColorBrush.Color;
			}
		}
		catch
		{
		}
		return (Color)ColorConverter.ConvertFromString("#1C1F27");
	}

	private static string ThemeStamp()
	{
		try
		{
			return (Application.Current?.TryFindResource("Brush.SurfaceAlt") is SolidColorBrush { Color: var color }) ? color.ToString() : "?";
		}
		catch
		{
			return "?";
		}
	}

	private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
	{
		int n = VisualTreeHelper.GetChildrenCount(root);
		for (int i = 0; i < n; i++)
		{
			DependencyObject child = VisualTreeHelper.GetChild(root, i);
			T val = (T)(object)((child is T) ? child : null);
			if (val != null)
			{
				yield return val;
			}
			foreach (T item in FindAll<T>(child))
			{
				yield return item;
			}
		}
	}

	private void ApplyStyle()
	{
		QbarOptions qbar = _host.Settings.Qbar;
		try
		{
			Color color = ((string.IsNullOrWhiteSpace(qbar.PanelColor) || qbar.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase)) ? ThemePanelColor() : ParseColor(qbar.PanelColor));
			string text = $"{qbar.AccentColor}|{color}|{qbar.PanelOpacity}|{qbar.BlurStrength}|{qbar.TextFontSize}|{qbar.LineHeight}|{qbar.ListCount}|{ThemeStamp()}";
			if (!(text == _styleStamp))
			{
				_styleStamp = text;
				Panel.BorderBrush = (Brush)FindResource("Brush.OverlayBorder");
				_panelColor = color;
				ApplyPanelTint();
				RefreshBackdrop();
				Color panelColor = color;
				ApplyForegroundPalette(panelColor);
				Input.FontSize = qbar.TextFontSize;
				Input.MinHeight = TextMetrics.MinBoxHeight(qbar.TextFontSize, "汉字Agqy");
				Results.MaxHeight = qbar.LineHeight * (double)qbar.ListCount + 12.0;
				SizeToContent = SizeToContent.Height;
			}
		}
		catch (Exception ex)
		{
			CoreLog.Warn("Qbar 样式应用失败：" + ex.Message);
		}
	}

	private void ApplyPanelTint()
	{
		bool flag = _host.Settings.Qbar.BlurStrength > 0;
		double num = (double.TryParse(_host.Settings.Qbar.PanelOpacity, out var result) ? Math.Clamp(result, 0.3, 1.0) : 0.96);
		byte a = (flag ? ((byte)Math.Round(Math.Clamp(0.7 * num, 0.15, 1.0) * 255.0)) : byte.MaxValue);
		_panelBrush = new SolidColorBrush(Color.FromArgb(a, _panelColor.R, _panelColor.G, _panelColor.B));
		((Freezable)_panelBrush).Freeze();
		TintFill.Background = _panelBrush;
		Panel.Background = Brushes.Transparent;
		ApplyAccent();
	}

	private uint AccentGradient()
	{
		return (uint)(0x40000000 | (_panelColor.B << 16) | (_panelColor.G << 8) | _panelColor.R);
	}

	private void ApplyAccent()
	{
		try
		{
			nint handle = new WindowInteropHelper(this).Handle;
			if (handle != IntPtr.Zero)
			{
				bool flag = _host.Settings.Qbar.BlurStrength > 0;
				EnableAccent(handle, flag ? 4 : 0, flag ? AccentGradient() : 0u);
			}
		}
		catch (Exception ex)
		{
			CoreLog.Warn("Qbar 亚克力底设置失败：" + ex.Message);
		}
	}

	[DllImport("user32.dll")]
	private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

	private static void EnableAccent(nint hwnd, int state, uint gradient)
	{
		AccentPolicy structure = new AccentPolicy
		{
			AccentState = state,
			AccentFlags = 2,
			GradientColor = (int)gradient,
			AnimationId = 0
		};
		int num = Marshal.SizeOf(structure);
		nint num2 = Marshal.AllocHGlobal(num);
		try
		{
			Marshal.StructureToPtr(structure, num2, fDeleteOld: false);
			WindowCompositionAttributeData data = new WindowCompositionAttributeData
			{
				Attribute = 19,
				Data = num2,
				SizeOfData = num
			};
			SetWindowCompositionAttribute(hwnd, ref data);
		}
		finally
		{
			Marshal.FreeHGlobal(num2);
		}
	}

	private void RefreshBackdrop()
	{
		ApplyAccent();
	}

	protected override void OnSourceInitialized(EventArgs e)
	{
		base.OnSourceInitialized(e);
		if (PresentationSource.FromVisual(this) is HwndSource hwndSource)
		{
			hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
		}
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero)
		{
			int windowLong = NativeMethods.GetWindowLong(handle, -20);
			NativeMethods.SetWindowLong(handle, -20, windowLong | 0x80);
			WindowChrome.EnableRounded(this);
			ApplyPanelTint();
		}
	}

	public void InvalidateStyle()
	{
		_styleStamp = "";
	}

	private void AttachEscInterceptor()
	{
		if (_host == null)
		{
			return;
		}
		if (_escInterceptor == null)
		{
			_escInterceptor = (int vk) =>
			{
				if (vk != 27)
				{
					return false;
				}
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
				{
					HideQbar();
				}), Array.Empty<object>());
				return true;
			};
		}
		_host.Engine.KeyInterceptor = _escInterceptor;
	}

	private void DetachEscInterceptor()
	{
		if (_host != null && _escInterceptor != null && (object)_host.Engine.KeyInterceptor == _escInterceptor)
		{
			_host.Engine.KeyInterceptor = null;
		}
	}

	private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != MouseButton.Left || _closing)
		{
			return;
		}
		object originalSource = e.OriginalSource;
		if (IsInteractiveDragSource((DependencyObject?)((originalSource is DependencyObject) ? originalSource : null)))
		{
			return;
		}
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero && NativeMethods.GetCursorPos(out _dragCursor) && NativeMethods.GetWindowRect(handle, out _dragWindowRect))
		{
			_dragging = true;
			_dragMoved = false;
			Panel.Cursor = GrabClosedCursor;
			e.Handled = true;
			if (_dragPoll == null)
			{
				_dragPoll = CreateDragPoll();
			}
			_dragPoll.Start();
		}
	}

	private DispatcherTimer CreateDragPoll()
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Expected Obj, but got Unknown
		DispatcherTimer val = new DispatcherTimer((DispatcherPriority)5)
		{
			Interval = TimeSpan.FromMilliseconds(15L)
		};
		val.Tick += (object? _, EventArgs _) =>
		{
			PollDrag();
		};
		return val;
	}

	private void PollDrag()
	{
		if (!_dragging)
		{
			DispatcherTimer? dragPoll = _dragPoll;
			if (dragPoll != null)
			{
				dragPoll.Stop();
			}
		}
		else if (_closing || !IsVisible)
		{
			EndPanelDrag(cancel: true);
		}
		else if (IsDown(27))
		{
			EndPanelDrag(cancel: true);
		}
		else if (!IsDown(1))
		{
			EndPanelDrag(cancel: false);
		}
		else
		{
			if (!NativeMethods.GetCursorPos(out var lpPoint))
			{
				return;
			}
			int num = lpPoint.X - _dragCursor.X;
			int num2 = lpPoint.Y - _dragCursor.Y;
			if (_dragMoved || Math.Abs(num) >= 3 || Math.Abs(num2) >= 3)
			{
				nint handle = new WindowInteropHelper(this).Handle;
				if (handle != IntPtr.Zero)
				{
					_dragMoved = true;
					NativeMethods.SetWindowPos(handle, IntPtr.Zero, _dragWindowRect.Left + num, _dragWindowRect.Top + num2, 0, 0, 21u);
				}
			}
		}
	}

	private static Cursor LoadCursorAsset(string relative, Cursor fallback)
	{
		try
		{
			StreamResourceInfo resourceStream = Application.GetResourceStream(new Uri("pack://application:,,,/" + relative));
			if (resourceStream != null)
			{
				return new Cursor(resourceStream.Stream);
			}
			CoreLog.Warn("鼠标指针没打进程序：" + relative);
		}
		catch (Exception ex)
		{
			CoreLog.Warn("读鼠标指针失败（" + relative + "）：" + ex.Message);
		}
		return fallback;
	}

	private void EndPanelDrag(bool cancel)
	{
		if (!_dragging && !_dragMoved)
		{
			return;
		}
		_dragging = false;
		DispatcherTimer? dragPoll = _dragPoll;
		if (dragPoll != null)
		{
			dragPoll.Stop();
		}
		nint handle = new WindowInteropHelper(this).Handle;
		if (cancel)
		{
			if (_dragMoved && handle != IntPtr.Zero)
			{
				NativeMethods.SetWindowPos(handle, IntPtr.Zero, _dragWindowRect.Left, _dragWindowRect.Top, 0, 0, 21u);
			}
		}
		else if (_dragMoved)
		{
			RememberPosition();
			QbarOptions qbar = _host.Settings.Qbar;
			ScreenPlacement.PlaceAt(this, qbar.PositionX, qbar.PositionY);
		}
		_dragMoved = false;
		Panel.Cursor = GrabOpenCursor;
		if (!cancel && IsVisible)
		{
			Input.Focus();
			Keyboard.Focus(Input);
			RefreshBackdrop();
		}
	}

	private void OnPanelMouseMove(object sender, MouseEventArgs e)
	{
		if (!_closing && !_dragging)
		{
			object originalSource = e.OriginalSource;
			bool flag = !IsInteractiveDragSource((DependencyObject?)((originalSource is DependencyObject) ? originalSource : null));
			Panel.Cursor = (flag ? GrabOpenCursor : null);
		}
	}

	private static bool IsInteractiveDragSource(DependencyObject? source)
	{
		for (DependencyObject val = source; val != null; val = ParentOf(val))
		{
			if (val is TextBoxBase)
			{
				return true;
			}
			if (val is ButtonBase)
			{
				return true;
			}
			if (val is ScrollBar)
			{
				return true;
			}
			if (val is Thumb)
			{
				return true;
			}
			if (val is ListBoxItem)
			{
				return true;
			}
			if (val is ListBox)
			{
				return true;
			}
			if (val is ScrollViewer)
			{
				return true;
			}
		}
		return false;
	}

	private static DependencyObject? ParentOf(DependencyObject d)
	{
		try
		{
			if (d is Visual || d is Visual3D)
			{
				return VisualTreeHelper.GetParent(d);
			}
		}
		catch (InvalidOperationException)
		{
		}
		if (d is FrameworkContentElement frameworkContentElement)
		{
			return frameworkContentElement.Parent;
		}
		if (d is FrameworkElement frameworkElement)
		{
			return frameworkElement.Parent;
		}
		return null;
	}

	private void RememberPosition()
	{
		if (_host != null && ScreenPlacement.TryGetFraction(this, out var fx, out var fy))
		{
			QbarOptions qbar = _host.Settings.Qbar;
			qbar.CustomPosition = true;
			qbar.PositionX = Math.Round(fx, 4);
			qbar.PositionY = Math.Round(fy, 4);
			_host.SaveSettings();
		}
	}

	public void ResetPosition()
	{
		if (_host != null)
		{
			QbarOptions qbar = _host.Settings.Qbar;
			qbar.CustomPosition = false;
			qbar.PositionX = 0.5;
			qbar.PositionY = 0.3;
			_host.SaveSettings();
		}
	}

	public void Open(string? prefill = null)
	{
		//IL_0052: Unknown result type (might be due to invalid IL or missing references)
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		if (_closing)
		{
			ReopenFromFade();
		}
		_imeApplied = false;
		_imeKey = "";
		_imeAt = DateTime.MinValue;
		ApplyStyle();
		ClearChip();
		ResetFilterChip();
		QbarIndex.Start();
		WindowService.InvalidateWindowSearchCache();
		if (!IsVisible)
		{
			Rect workArea = SystemParameters.WorkArea;
			Left = workArea.Left + (workArea.Width - Width) / 2.0;
			Top = workArea.Top + workArea.Height * 0.22;
			Show();
			try
			{
				UpdateLayout();
			}
			catch
			{
			}
			nint foregroundWindow = NativeMethods.GetForegroundWindow();
			QbarOptions qbar = _host.Settings.Qbar;
			if (qbar.CustomPosition)
			{
				ScreenPlacement.PlaceAt(this, qbar.PositionX, qbar.PositionY, foregroundWindow);
			}
			else
				ScreenPlacement.Place(this, ScreenPlacement.Anchor.UpperCenter, 12.0, 12.0, foregroundWindow);
		}
		_openedAt = DateTime.Now;
		AttachEscInterceptor();
		StartOutsideWatch();
		string text = prefill ?? "";
		SetInputText(text);
		if (text.Length > 0 && TryMakeChip() && _chip != null)
		{
			SetInputText("");
			RunQuery();
		}
		else
		{
			RunQuery();
		}
		BeginForceFocus();
		Input.Focus();
		Keyboard.Focus(Input);
		Input.CaretIndex = Input.Text.Length;
		_imeApplied = false;
		_imeKey = "";
		if (IsActive)
		{
			_imeApplied = ApplyImePreference();
		}
		QueueImePreference(120);
		RefreshBackdrop();
		PlayShowAnimation();
	}

	public void OpenForRender(string keyword, string? tail = null)
	{
		Open("");
		SetInputText(keyword);
		if (!TryMakeChip())
		{
			RunQuery();
		}
		if (!string.IsNullOrEmpty(tail))
		{
			SetInputText(tail);
		}
		RunQuery();
	}

	public void OpenForRenderFilter(string keyword, string? tail = null)
	{
		Open("");
		SetInputText(keyword);
		if (!TryMakeFilterChip())
		{
			RunQuery();
		}
		if (!string.IsNullOrEmpty(tail))
		{
			SetInputText(tail);
		}
		RunQuery();
	}

	public void OpenFileSearch(string? filterId, string? query = null)
	{
		Open("");
		if (!string.IsNullOrEmpty(filterId))
		{
			QbarFilter qbarFilter = QbarFilterCatalog.Create(_host.Settings.Qbar.Filters).All.FirstOrDefault((QbarFilter f) => string.Equals(f.Id, filterId, StringComparison.Ordinal));
			if (qbarFilter != null)
			{
				SetInputText(qbarFilter.Keyword + ":");
				ApplyFilter(qbarFilter.Id);
			}
		}
		if (!string.IsNullOrEmpty(query))
		{
			SetInputText(query);
			RunQuery();
		}
	}

	public void OpenDictionary(string? word = null)
	{
		Open("");
		QbarCommand qbarCommand = DictionaryLauncher.EffectiveCommand(_host.Settings.Qbar.Commands);
		string inputText = (string.IsNullOrWhiteSpace(qbarCommand?.Keyword) ? "ol" : qbarCommand.Keyword.Trim());
		SetInputText(inputText);
		if (!TryMakeChip())
		{
			RunQuery();
		}
		if (!string.IsNullOrEmpty(word))
		{
			SetInputText(word);
			RunQuery();
		}
	}

	public void OpenForRenderQuery(string text)
	{
		Open("");
		SetInputText(text);
		RunQuery();
	}

	public void ParkOffScreenForRender()
	{
		Left = -32000.0;
		Top = -32000.0;
		nint handle = new WindowInteropHelper(this).Handle;
		if (handle != IntPtr.Zero)
		{
			NativeMethods.SetWindowPos(handle, IntPtr.Zero, -20000, -20000, 0, 0, 21u);
		}
	}

	private void PlayShowAnimation()
	{
		Panel.BeginAnimation(UIElement.OpacityProperty, null);
		Panel.Opacity = 1.0;
		DoubleAnimation animation = new DoubleAnimation(12.0, 0.0, TimeSpan.FromMilliseconds(150L))
		{
			EasingFunction = new CubicEase
			{
				EasingMode = EasingMode.EaseOut
			}
		};
		Slide.BeginAnimation(TranslateTransform.YProperty, animation);
	}

	private void ReopenFromFade()
	{
		Panel.BeginAnimation(UIElement.OpacityProperty, null);
		Panel.Opacity = 1.0;
		_closing = false;
	}

	protected override void OnClosed(EventArgs e)
	{
		_closing = true;
		DetachEscInterceptor();
		StopFocusTimer();
		StopOutsideWatch();
		_suggestTimer.Stop();
		_queryTimer.Stop();
		DispatcherTimer? imeTimer = _imeTimer;
		if (imeTimer != null)
		{
			imeTimer.Stop();
		}
		DispatcherTimer? imeVerify = _imeVerify;
		if (imeVerify != null)
		{
			imeVerify.Stop();
		}
		DispatcherTimer? dragPoll = _dragPoll;
		if (dragPoll != null)
		{
			dragPoll.Stop();
		}
		base.OnClosed(e);
	}

	public void HideQbar()
	{
		if (!IsVisible || _closing)
		{
			return;
		}
		DispatcherTimer? imeTimer = _imeTimer;
		if (imeTimer != null)
		{
			imeTimer.Stop();
		}
		DispatcherTimer? imeVerify = _imeVerify;
		if (imeVerify != null)
		{
			imeVerify.Stop();
		}
		DetachEscInterceptor();
		StopFocusTimer();
		StopOutsideWatch();
		_closing = true;
		DoubleAnimation doubleAnimation = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(90L));
		doubleAnimation.Completed += (object? _, EventArgs _) =>
		{
			if (_closing)
			{
				Hide();
				Panel.BeginAnimation(UIElement.OpacityProperty, null);
				Panel.Opacity = 1.0;
				_closing = false;
			}
		};
		Panel.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
	}

	private bool TryRunQuickCommand()
	{
		QbarService.QuickCommand quickCommand = QbarService.FindQuickCommand(Input.Text);
		if (quickCommand == null)
		{
			return false;
		}
		ExecuteResult(QbarService.ToResult(quickCommand));
		return true;
	}

	private bool TryMakeChip()
	{
		if (_chip != null)
		{
			return false;
		}
		string text = Input.Text.Trim();
		if (text.Length == 0)
		{
			return false;
		}
		QbarChip qbarChip = _host.Qbar.ResolveChip(text);
		if (qbarChip == null)
		{
			return false;
		}
		if (qbarChip.Kind == ChipKind.Web && !string.IsNullOrWhiteSpace(qbarChip.Value))
		{
			ClearChip();
			SetInputText(qbarChip.Value);
			Input.Focus();
			Input.CaretIndex = Input.Text.Length;
			RunQuery();
			return true;
		}
		SetChip(qbarChip);
		SetInputText("");
		RunQuery();
		return true;
	}

	private void SetChip(QbarChip chip)
	{
		_chip = chip;
		string text;
		if (chip.Note.Length > 0)
		{
			text = chip.Note;
		}
		else
		{
			text = (chip.HasIcon ? "" : chip.Label);
		}
		ChipLabel.Text = text;
		ChipLabel.Visibility = ((text.Length == 0) ? Visibility.Collapsed : Visibility.Visible);
		Input.IsReadOnly = chip.Kind == ChipKind.Web;
		Input.IsReadOnlyCaretVisible = chip.Kind != ChipKind.Web;
		if (chip.HasIcon)
		{
			ChipIcon.Source = chip.Icon;
			ChipIconBox.Visibility = Visibility.Visible;
			ChipInitialBox.Visibility = Visibility.Collapsed;
		}
		else
		{
			ChipIcon.Source = null;
			ChipIconBox.Visibility = Visibility.Collapsed;
			ChipInitialBox.Visibility = Visibility.Visible;
			ChipInitial.Text = chip.Initial;
			ChipInitialBox.Background = new SolidColorBrush(chip.Accent);
			ChipInitial.Foreground = Frozen(BestTextOn(chip.Accent));
			ChipKind kind = chip.Kind;
			if ((kind == ChipKind.Run || kind == ChipKind.Command) ? true : false)
			{
				ImageSource imageSource = IconCache.FromExecutable(chip.Value);
				if (imageSource != null)
				{
					SetChipIcon(imageSource);
				}
			}
			else if (chip.Kind == ChipKind.Web)
			{
				IconCache.LoadWebIconAsync(chip.Value, (ImageSource? image) =>
				{
					if (image != null)
					{
						((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
						{
							if (_chip == chip)
							{
								SetChipIcon(image);
							}
						}), Array.Empty<object>());
					}
				});
			}
		}
		ChipBox.Visibility = Visibility.Visible;
		TextBlock modeIcon = ModeIcon;
		modeIcon.Text = chip.Kind switch
		{
			ChipKind.Search => "\ue721", 
			ChipKind.Web => "\ue774", 
			_ => "\ue756", 
		};
		UpdateMode();
		if (IsActive)
		{
			_imeApplied = ApplyImePreference();
		}
		QueueImePreference(100);
	}

	private void SetChipIcon(ImageSource image)
	{
		ChipIcon.Source = image;
		ChipIconBox.Visibility = Visibility.Visible;
		ChipInitialBox.Visibility = Visibility.Collapsed;
	}

	private void ClearChip()
	{
		HideSuggestions();
		_chip = null;
		Input.IsReadOnly = false;
		Input.IsReadOnlyCaretVisible = true;
		ChipBox.Visibility = Visibility.Collapsed;
		ChipIcon.Source = null;
		ModeIcon.Text = "\ue721";
	}

	private void RemoveChip()
	{
		if (_chip != null)
		{
			ClearChip();
			SetInputText("");
			RunQuery();
			Input.Focus();
			Input.CaretIndex = 0;
			if (IsActive)
			{
				_imeApplied = ApplyImePreference();
				QueueImePreference(100);
			}
		}
	}

	private void OnChipClose(object sender, RoutedEventArgs e)
	{
		RemoveChip();
	}

	private void OnExpandClick(object sender, RoutedEventArgs e)
	{
		try
		{
			QbarExpandWindow qbarExpandWindow = new QbarExpandWindow(_host, Input.Text);
			HideQbar();
			qbarExpandWindow.Show();
			qbarExpandWindow.Activate();
		}
		catch (Exception ex)
		{
			CoreLog.Swallow(ex, "展开文件搜索窗口");
		}
	}

	private void SetInputText(string text)
	{
		_suppressTextChanged = true;
		Input.Text = text;
		Input.CaretIndex = text.Length;
		_suppressTextChanged = false;
	}

	private void OnWindowKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_00bb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c1: Invalid comparison between Unknown and I4
		//IL_00ec: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f2: Invalid comparison between Unknown and I4
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0062: Invalid comparison between Unknown and I4
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0067: Invalid comparison between Unknown and I4
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0095: Invalid comparison between Unknown and I4
		//IL_0074: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13)
		{
			if (_appliedFilter != null)
			{
				ClearFilter();
			}
			else if (_menuFile != null)
			{
				CloseFileMenu();
			}
			else if (_suggestVisible)
			{
				HideSuggestions();
			}
			else
			{
				HideQbar();
			}
			e.Handled = true;
			return;
		}
		if (_menuFile == null && _suggestVisible)
		{
			Key key = e.Key;
			if (((int)key == 24 || (int)key == 26) ? true : false)
			{
				MoveSuggestion(((int)e.Key == 26) ? 1 : (-1));
				e.Handled = true;
				return;
			}
			if ((int)e.Key == 6 && SuggestList.SelectedItem is string suggestion)
			{
				AcceptSuggestion(suggestion);
				e.Handled = true;
				return;
			}
		}
		if ((int)e.Key == 2 && _appliedFilter != null && Input.Text.Length == 0)
		{
			ClearFilter();
			e.Handled = true;
		}
		else if ((int)e.Key == 2 && _chip != null && Input.Text.Length == 0)
		{
			RemoveChip();
			e.Handled = true;
		}
	}

	private void OnInputKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Invalid comparison between Unknown and I4
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Invalid comparison between Unknown and I4
		//IL_002a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Invalid comparison between Unknown and I4
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ab: Invalid comparison between Unknown and I4
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Expected I4, but got Unknown
		//IL_00ad: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Invalid comparison between Unknown and I4
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Invalid comparison between Unknown and I4
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b5: Invalid comparison between Unknown and I4
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fb: Unknown result type (might be due to invalid IL or missing references)
		//IL_0125: Expected I4, but got Unknown
		if ((int)e.Key == 59 && (int)Keyboard.Modifiers == 2)
		{
			PinSelected();
			e.Handled = true;
			return;
		}
		if ((int)e.Key == 47 && (int)Keyboard.Modifiers == 2)
		{
			QbarSearchOptions qbarSearchOptions = _host.Settings.Qbar.Search ?? new QbarSearchOptions();
			qbarSearchOptions.DebugPanel = !qbarSearchOptions.DebugPanel;
			_host.Settings.Qbar.Search = qbarSearchOptions;
			_host.SaveSettings();
			UpdateDebugPanel(_lastOutcome);
			e.Handled = true;
			return;
		}
		bool flag = _menuFile != null;
		Key key;
		if (flag)
		{
			key = e.Key;
			bool flag2 = (((int)key == 2 || (int)key == 13 || (int)key == 23) ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			CloseFileMenu();
			e.Handled = true;
			return;
		}
		key = e.Key;
		switch ((int)key - 2)
		{
		case 1:
			if (TryMakeChip())
			{
				e.Handled = true;
				return;
			}
			if (TryMakeFilterChip())
			{
				e.Handled = true;
				return;
			}
			CompleteSelected();
			e.Handled = true;
			return;
		case 0:
			if (_chip != null && Input.Text.Length == 0)
			{
				RemoveChip();
				e.Handled = true;
			}
			return;
		case 4:
			if (_suggestVisible && SuggestList.SelectedItem is string suggestion)
			{
				AcceptSuggestion(suggestion);
				e.Handled = true;
			}
			else if (Results.SelectedItem is QbarItem qbarItem && ShouldCompleteOnEnter(qbarItem.Result))
			{
				CompleteSelected();
				e.Handled = true;
			}
			else
			{
				ExecuteSelected();
				e.Handled = true;
			}
			return;
		case 2:
		case 3:
			return;
		}
		if ((int)key != 13)
		{
			switch ((int)key - 18)
			{
			case 0:
				if (TryRunQuickCommand())
				{
					e.Handled = true;
				}
				else if (TryMakeChip())
				{
					e.Handled = true;
				}
				else if (TryMakeFilterChip())
				{
					e.Handled = true;
				}
				break;
			case 8:
				if (_suggestVisible)
				{
					MoveSuggestion(1);
					e.Handled = true;
				}
				else
				{
					MoveSelection(1);
					e.Handled = true;
				}
				break;
			case 6:
				if (_suggestVisible)
				{
					MoveSuggestion(-1);
					e.Handled = true;
				}
				else
				{
					MoveSelection(-1);
					e.Handled = true;
				}
				break;
			case 2:
				MoveSelection(5);
				e.Handled = true;
				break;
			case 1:
				MoveSelection(-5);
				e.Handled = true;
				break;
			case 3:
			case 4:
			case 5:
			case 7:
				break;
			}
		}
		else if (_suggestVisible)
		{
			HideSuggestions();
			e.Handled = true;
		}
		else
		{
			HideQbar();
			e.Handled = true;
		}
	}

	private static bool ShouldCompleteOnEnter(QbarResult r)
	{
		QbarKind kind = r.Kind;
		bool flag = (uint)(kind - 1) <= 2u;
		if (flag && r.Badge.Length > 0)
		{
			return r.Payload.Contains("{q}", StringComparison.Ordinal);
		}
		return false;
	}

	private void MoveSelection(int delta)
	{
		if (_items.Count != 0)
		{
			int selectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, _items.Count - 1);
			Results.SelectedIndex = selectedIndex;
			Results.ScrollIntoView(Results.SelectedItem);
		}
	}

	private void CompleteSelected()
	{
		HideSuggestions();
		if (Results.SelectedItem is QbarItem qbarItem)
		{
			QbarKind kind = qbarItem.Result.Kind;
			string text = (((uint)(kind - 1) > 2u || qbarItem.Result.Badge.Length <= 0) ? qbarItem.Result.Title : (qbarItem.Result.Badge + " "));
			string inputText = text;
			SetInputText(inputText);
			TryMakeChip();
			RunQuery();
		}
	}

	private bool TryGetSuggestionContext(out string keyword, out string engine)
	{
		keyword = "";
		engine = "auto";
		if (!IsVisible || _closing)
		{
			return false;
		}
		QbarOptions qbar = _host.Settings.Qbar;
		if (!qbar.SuggestEnabled)
		{
			return false;
		}
		QbarChip chip = _chip;
		if (chip == null || chip.Kind != ChipKind.Search)
		{
			return false;
		}
		if (QbarService.LooksLikeProgram(_chip.Value))
		{
			return false;
		}
		string text = (Input.Text ?? "").Trim();
		if (text.Length == 0)
		{
			return false;
		}
		keyword = text;
		engine = SuggestionEngines.Resolve(qbar.SuggestEngine, _chip.Value);
		return true;
	}

	private void CancelSuggestionRequest()
	{
		_suggestTimer.Stop();
		CancellationTokenSource suggestCts = _suggestCts;
		_suggestCts = null;
		if (suggestCts != null)
		{
			try
			{
				suggestCts.Cancel();
			}
			catch
			{
			}
			suggestCts.Dispose();
		}
		_suggestSeq++;
	}

	private void HideSuggestions()
	{
		CancelSuggestionRequest();
		_suggestVisible = false;
		SuggestList.ItemsSource = null;
		SuggestList.SelectedIndex = -1;
		SuggestBox.Visibility = Visibility.Collapsed;
	}

	private void ScheduleSuggestions()
	{
		CancelSuggestionRequest();
		if (!TryGetSuggestionContext(out string _, out string _))
		{
			HideSuggestions();
			return;
		}
		if (_suggestVisible)
		{
			HideSuggestions();
		}
		_suggestTimer.Start();
	}

	private async void OnSuggestTimerTick(object? sender, EventArgs e)
	{
		_suggestTimer.Stop();
		try
		{
			if (!TryGetSuggestionContext(out string keyword, out string engine))
			{
				HideSuggestions();
				return;
			}
			QbarOptions qbar = _host.Settings.Qbar;
			int max = Math.Clamp(qbar.SuggestMax, 1, 10);
			int ttl = Math.Clamp(qbar.SuggestCacheMinutes, 0, 1440);
			int millisecondsDelay = Math.Clamp(qbar.SuggestTimeoutMs, 200, 5000);
			if (SuggestionCache.TryGet(engine, keyword, ttl, max, out IReadOnlyList<string> items))
			{
				QbarWindow qbarWindow = this;
				IReadOnlyList<string> items2;
				if (items.Count <= max)
				{
					items2 = items;
				}
				else
				{
					IReadOnlyList<string> readOnlyList = items.Take(max).ToList();
					items2 = readOnlyList;
				}
				qbarWindow.ShowSuggestions(items2, engine, keyword);
				return;
			}
			int seq = ++_suggestSeq;
			CancellationTokenSource cts = (_suggestCts = new CancellationTokenSource());
			try
			{
				cts.CancelAfter(millisecondsDelay);
			}
			catch (ObjectDisposedException)
			{
				return;
			}
			IReadOnlyList<string> readOnlyList2;
			try
			{
				readOnlyList2 = await SuggestionEngines.Create(engine, max).QueryAsync(keyword, cts.Token);
			}
			catch
			{
				readOnlyList2 = Array.Empty<string>();
			}
			finally
			{
				if (_suggestCts == cts)
				{
					_suggestCts = null;
				}
				cts.Dispose();
			}
			if (seq == _suggestSeq)
			{
				if (readOnlyList2.Count == 0)
				{
					HideSuggestions();
					return;
				}
				SuggestionCache.Put(engine, keyword, ttl, max, readOnlyList2);
				ShowSuggestions(readOnlyList2, engine, keyword);
			}
		}
		catch (Exception ex2)
		{
			CoreLog.Swallow(ex2, "搜索联想");
			HideSuggestions();
		}
	}

	private void ShowSuggestions(IReadOnlyList<string> items, string engine, string keyword)
	{
		if (items.Count == 0 || !TryGetSuggestionContext(out string keyword2, out string engine2) || engine2 != engine || !string.Equals(keyword2, keyword, StringComparison.Ordinal))
		{
			HideSuggestions();
			return;
		}
		SuggestList.ItemsSource = items;
		SuggestList.SelectedIndex = -1;
		_suggestVisible = true;
		SuggestBox.Visibility = Visibility.Visible;
	}

	private void MoveSuggestion(int delta)
	{
		int count = SuggestList.Items.Count;
		if (count != 0)
		{
			int selectedIndex = SuggestList.SelectedIndex;
			int selectedIndex2 = ((selectedIndex >= 0) ? Math.Clamp(selectedIndex + delta, 0, count - 1) : ((delta <= 0) ? (count - 1) : 0));
			SuggestList.SelectedIndex = selectedIndex2;
			if (SuggestList.SelectedItem != null)
			{
				SuggestList.ScrollIntoView(SuggestList.SelectedItem);
			}
		}
	}

	private void AcceptSuggestion(string suggestion)
	{
		QbarChip chip = _chip;
		if (chip != null && chip.Kind == ChipKind.Search && !string.IsNullOrWhiteSpace(suggestion))
		{
			HideSuggestions();
			SetInputText(suggestion);
			RunQuery();
			if (Results.SelectedItem is QbarItem qbarItem)
			{
				ExecuteResult(qbarItem.Result);
			}
		}
	}

	private void OnSuggestionClick(object sender, MouseButtonEventArgs e)
	{
		if (_suggestVisible)
		{
			ListBox suggestList = SuggestList;
			object originalSource = e.OriginalSource;
			if ((ItemsControl.ContainerFromElement(suggestList, (DependencyObject)((originalSource is DependencyObject) ? originalSource : null)) as ListBoxItem)?.DataContext is string suggestion)
			{
				e.Handled = true;
				AcceptSuggestion(suggestion);
			}
		}
	}

	private void OnInputChanged(object sender, TextChangedEventArgs e)
	{
		if (!_suppressTextChanged)
		{
			if (_menuFile != null)
			{
				CloseFileMenu();
			}
			if (_imeComposing)
			{
				_pendingQueryAfterComposition = true;
				return;
			}
			ScheduleQuery();
			ScheduleSuggestions();
		}
	}

	private void OnCompositionStart(object sender, TextCompositionEventArgs e)
	{
		_imeComposing = true;
	}

	private void OnCompositionUpdate(object sender, TextCompositionEventArgs e)
	{
		_imeComposing = e.TextComposition.CompositionText.Length > 0;
		if (_imeComposing)
		{
			_pendingQueryAfterComposition = true;
		}
	}

	private void OnCompositionEnd(object sender, TextCompositionEventArgs e)
	{
		_imeComposing = false;
		if (_pendingQueryAfterComposition)
		{
			_pendingQueryAfterComposition = false;
			ScheduleQuery();
			ScheduleSuggestions();
		}
	}

	private void ScheduleQuery()
	{
		int num = Math.Clamp(_host.Settings.Qbar.Search.DebounceMs, 0, 200);
		if (num == 0)
		{
			_queryTimer.Stop();
			RunQuery();
		}
		else
		{
			_queryTimer.Interval = TimeSpan.FromMilliseconds(num);
			_queryTimer.Stop();
			_queryTimer.Start();
		}
	}

	private void OnQueryTimerTick(object? sender, EventArgs e)
	{
		_queryTimer.Stop();
		RunQuery();
	}

	private string EffectiveInput(string text)
	{
		QbarFilter appliedFilter = _appliedFilter;
		if (appliedFilter == null)
		{
			return text;
		}
		if (!string.IsNullOrEmpty(text))
		{
			return appliedFilter.Keyword + ": " + text;
		}
		return appliedFilter.Keyword + ":";
	}

	private void RunQuery()
	{
		_queryTimer.Stop();
		string text = EffectiveInput(Input.Text);
		int limit = Math.Max(6, _host.Settings.Qbar.ListCount + 2);
		if (!TryStartDictionary(text))
		{
			bool flag = !string.Equals(text, _lastQueryText, StringComparison.Ordinal);
			_lastQueryText = text;
			int keep = ((!flag) ? Math.Max(0, Results.SelectedIndex) : 0);
			List<QbarResult> results = _host.Qbar.QueryPrepared(text, _chip, limit, null, skipFiles: true);
			ApplyResults(results, keep);
			UpdateDebugPanel(null);
			StartFileQuery(text, limit, keep);
		}
	}

	private bool TryStartDictionary(string text)
	{
		if (!TryResolveDictionary(text, out QbarCommand command, out string word))
		{
			return false;
		}
		if (command != null && !string.IsNullOrWhiteSpace(command.Path))
		{
			return false;
		}
		int seq = ++_querySeq;
		_lastOutcome = null;
		if (word.Length == 0)
		{
			ApplyResults(new List<QbarResult>
			{
				new QbarResult(QbarKind.Dictionary, Loc.T("查词"), Loc.T("在 ol 后面打一个词：ol hello / ol 银行"), Loc.T("词典"), "")
				{
					Glyph = "\ue82d"
				}
			}, 0);
			return true;
		}
		ApplyResults(new List<QbarResult>
		{
			new QbarResult(QbarKind.Dictionary, word, Loc.T("正在查…"), Loc.T("词典"), "")
			{
				Glyph = "\ue82d"
			}
		}, 0);
		Task.Run(async () =>
		{
			QbarDictionaryEntry entry;
			try
			{
				entry = await QbarDictionary.LookupAsync(word, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex)
			{
				entry = new QbarDictionaryEntry(word, "", Array.Empty<QbarDictionarySense>(), "", ex.Message);
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
			{
				if (seq == _querySeq)
				{
					ApplyResults(BuildDictionaryResults(entry), 0);
					UpdateDebugPanel(null);
				}
			}), (DispatcherPriority)4, Array.Empty<object>());
		});
		return true;
	}

	private bool TryResolveDictionary(string? text, out QbarCommand? command, out string word)
	{
		word = "";
		command = QbarCommandStore.Effective(_host.Settings.Qbar.Commands).FirstOrDefault((QbarCommand c) => string.Equals(c.Id, "builtin.dict", StringComparison.OrdinalIgnoreCase));
		if (command == null)
		{
			return false;
		}
		QbarChip chip = _chip;
		bool num = chip != null && chip.Kind == ChipKind.Command && string.Equals(chip.CommandId, "builtin.dict", StringComparison.OrdinalIgnoreCase);
		string text2 = (text ?? "").Trim();
		if (!num)
		{
			string text3 = (command.Keyword ?? "").Trim();
			if (text3.Length == 0)
			{
				return false;
			}
			if (!text2.StartsWith(text3, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (text2.Length > text3.Length && !char.IsWhiteSpace(text2[text3.Length]))
			{
				return false;
			}
			text2 = text2.Substring(text3.Length).Trim();
		}
		word = text2;
		return true;
	}

	internal static List<QbarResult> BuildDictionaryResults(QbarDictionaryEntry entry)
	{
		List<QbarResult> list = new List<QbarResult>();
		if (entry == null)
		{
			return list;
		}
		if (!entry.Ok)
		{
			list.Add(new QbarResult(QbarKind.Dictionary, entry.Word, entry.Error ?? Loc.T("没查到这个词，换个拼写试试"), Loc.T("词典"), entry.Word)
			{
				Glyph = "\ue82d"
			});
			return list;
		}
		string subtitle = ((entry.Phonetic.Length > 0) ? entry.Phonetic : entry.Source);
		list.Add(new QbarResult(QbarKind.Dictionary, entry.Word, subtitle, Loc.T("词典"), entry.Word)
		{
			Glyph = "\ue82d",
			Score = -100
		});
		foreach (QbarDictionarySense sense in entry.Senses)
		{
			string title = (string.IsNullOrWhiteSpace(sense.PartOfSpeech) ? Loc.T("释义") : sense.PartOfSpeech);
			list.Add(new QbarResult(QbarKind.Dictionary, title, sense.Text, entry.Word, sense.Text)
			{
				Glyph = "\ue82d"
			});
		}
		return list;
	}

	private void StartFileQuery(string effective, int limit, int keep)
	{
		int seq = ++_querySeq;
		_lastOutcome = null;
		if (_chip != null || effective.Length < 2)
		{
			return;
		}
		QbarIndex.Start();
		QbarQueryContext context = QbarSearchEngine.FromSettings(_host.Settings);
		int fileLimit = Math.Min(6, limit);
		string expected = effective;
		Task.Run(() =>
		{
			QbarSearchOutcome outcome = null;
			try
			{
				outcome = QbarSearchEngine.Run(context, expected, fileLimit);
			}
			catch (Exception ex)
			{
				CoreLog.Swallow(ex, "Qbar 文件检索");
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
			{
				if (seq == _querySeq && _menuFile == null && string.Equals(EffectiveInput(Input.Text), expected, StringComparison.Ordinal))
				{
					_lastOutcome = outcome;
					List<QbarResult> results = _host.Qbar.QueryPrepared(expected, _chip, limit, outcome, skipFiles: false);
					ApplyResults(results, keep);
					UpdateDebugPanel(outcome);
				}
			}), (DispatcherPriority)4, Array.Empty<object>());
		});
	}

	private void ApplyResults(List<QbarResult> results, int keep)
	{
		QbarFilter appliedFilter = _appliedFilter;
		if (appliedFilter != null)
		{
			string action = "qfilter:" + appliedFilter.Id;
			results = results.Where((QbarResult r) => !string.Equals(r.Action, action, StringComparison.Ordinal)).ToList();
		}
		_items = results.Select((QbarResult r) => new QbarItem(r, _host.Settings.Qbar.ShowIcons)).ToList();
		Results.ItemsSource = _items;
		if (_items.Count > 0)
		{
			Results.SelectedIndex = Math.Min(Math.Max(0, keep), _items.Count - 1);
		}
		UpdateMode();
		UpdateHint();
		UpdateFacets();
		UpdateGroupHeader();
		SyncSuggestionSelection();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
		{
			ApplyRowPalette();
		}), (DispatcherPriority)6, Array.Empty<object>());
	}

	private void UpdateGroupHeader()
	{
		string emptyStateGroup = _host.Qbar.EmptyStateGroup;
		bool flag = emptyStateGroup.Length > 0 && _items.Count > 0;
		if (flag)
		{
			GroupHeaderText.Text = emptyStateGroup;
		}
		GroupHeaderBox.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void SyncSuggestionSelection()
	{
		int selectedIndex = Results.SelectedIndex;
		if (selectedIndex < 0 || selectedIndex >= _items.Count)
		{
			SuggestionsSelection.Clear();
			return;
		}
		QbarResult result = _items[selectedIndex].Result;
		if (result.Suggestion.Length == 0)
		{
			SuggestionsSelection.Clear();
			return;
		}
		bool isFolder = Directory.Exists(result.Payload);
		SuggestionsSelection.Set(result.Payload, isFolder, result.Suggestion);
	}

	private void OnSuggestionsChanged(RecSnapshot snapshot)
	{
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
		{
			if (IsVisible && Input.Text.Length == 0 && _chip == null && _menuFile == null)
			{
				RunQuery();
			}
		}), (DispatcherPriority)4, Array.Empty<object>());
	}

	private bool TryMakeFilterChip()
	{
		if (_appliedFilter != null)
		{
			return false;
		}
		QbarFilter qbarFilter = QbarService.ResolveFilterTrigger(Input.Text, _host.Settings.Qbar);
		if (qbarFilter == null)
		{
			return false;
		}
		ApplyFilter(qbarFilter.Id);
		return true;
	}

	private void ApplyFilter(string id)
	{
		QbarFilter qbarFilter = QbarFilterCatalog.Create(_host.Settings.Qbar.Filters).All.FirstOrDefault((QbarFilter f) => string.Equals(f.Id, id, StringComparison.Ordinal));
		if (qbarFilter != null)
		{
			string text = Input.Text.TrimStart();
			int num = text.IndexOf(' ');
			string text2 = ((num < 0) ? text : text.Substring(0, num));
			if (text2.EndsWith(":", StringComparison.Ordinal) || text2.Equals(qbarFilter.Keyword, StringComparison.OrdinalIgnoreCase))
			{
				text = ((num < 0) ? "" : text.Substring(num + 1).TrimStart());
			}
			_appliedFilter = qbarFilter;
			FilterChipLabel.Text = (string.IsNullOrEmpty(qbarFilter.Name) ? qbarFilter.Keyword : qbarFilter.Name);
			FilterChipBox.Visibility = Visibility.Visible;
			SetInputText(text);
			Input.Focus();
			Input.CaretIndex = Input.Text.Length;
			RunQuery();
		}
	}

	private void ClearFilter()
	{
		if (_appliedFilter != null)
		{
			ResetFilterChip();
			RunQuery();
		}
	}

	private void ResetFilterChip()
	{
		_appliedFilter = null;
		FilterChipBox.Visibility = Visibility.Collapsed;
	}

	private void OnFilterChipClose(object sender, RoutedEventArgs e)
	{
		ClearFilter();
	}

	private void UpdateFacets()
	{
		QbarSearchOutcome lastOutcome = _lastOutcome;
		QbarSearchOptions search = _host.Settings.Qbar.Search;
		string text = (((search == null || search.FacetsEnabled) && lastOutcome != null && lastOutcome.Hits.Count > 0) ? lastOutcome.FacetSummary() : "");
		FacetText.Text = text;
		FacetText.Visibility = ((text.Length == 0) ? Visibility.Collapsed : Visibility.Visible);
	}

	private void UpdateDebugPanel(QbarSearchOutcome? outcome)
	{
		QbarSearchOptions search = _host.Settings.Qbar.Search;
		if (search == null || !search.DebugPanel || _chip != null)
		{
			DebugBox.Visibility = Visibility.Collapsed;
			return;
		}
		string input = EffectiveInput(Input.Text);
		QbarTranslateResult qbarTranslateResult = outcome?.Translation ?? QbarQueryTranslator.Translate(input, QbarFilterCatalog.Create(_host.Settings.Qbar.Filters), DateTime.Now);
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("输入: " + ((qbarTranslateResult.Input.Length == 0) ? "(空)" : qbarTranslateResult.Input));
		stringBuilder.AppendLine("归一化: " + qbarTranslateResult.Normalized);
		stringBuilder.AppendLine("索引查询: " + qbarTranslateResult.IndexText);
		stringBuilder.AppendLine("后端: " + ((outcome == null) ? "（尚未查询）" : outcome.BackendText));
		stringBuilder.AppendLine("请求: " + qbarTranslateResult.RequestDetail);
		foreach (QbarDiagEntry stage in qbarTranslateResult.Stages)
		{
			stringBuilder.AppendLine("  [" + stage.Stage + "] " + stage.Detail + (stage.Warn ? "   <-- 需要留意" : ""));
		}
		if (outcome != null)
		{
			stringBuilder.AppendLine("候选: " + outcome.RawCount + " 条 → 展示 " + outcome.Hits.Count + " 条");
			if (outcome.HiddenCount > 0)
			{
				stringBuilder.AppendLine("被规则隐藏: Ignored " + outcome.HiddenIgnored + " / DoNotIndex " + outcome.HiddenDoNotIndex);
			}
			stringBuilder.AppendLine(outcome.TimingText);
			if (outcome.FuzzyUsed)
			{
				stringBuilder.AppendLine("模糊匹配: 已启用");
			}
			if (outcome.PinyinUsed)
			{
				stringBuilder.AppendLine("拼音索引: 命中");
			}
			if (outcome.PinyinPending)
			{
				stringBuilder.AppendLine("拼音索引: 正在后台构建");
			}
			if (outcome.TimedOut)
			{
				stringBuilder.AppendLine("检索超时: 已按当前候选返回");
			}
			string text = outcome.FacetSummary();
			if (text.Length > 0)
			{
				stringBuilder.AppendLine(text);
			}
		}
		DebugText.Text = stringBuilder.ToString().TrimEnd();
		DebugBox.Visibility = Visibility.Visible;
	}

	private void ShowHiddenReasons()
	{
		QbarSearchOutcome lastOutcome = _lastOutcome;
		if (lastOutcome == null || lastOutcome.HiddenCount == 0)
		{
			_host.Notify(Loc.T("这次查询没有被优先级规则隐藏的结果"));
			return;
		}
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine(Loc.F("被优先级规则隐藏 {0} 条（Ignored {1} / DoNotIndex {2}）", lastOutcome.HiddenCount, lastOutcome.HiddenIgnored, lastOutcome.HiddenDoNotIndex));
		foreach (string hiddenSample in lastOutcome.HiddenSamples)
		{
			stringBuilder.AppendLine("  " + hiddenSample);
		}
		_host.Notify(stringBuilder.ToString().TrimEnd());
	}

	private bool TryRunAction(QbarResult result)
	{
		string action = result.Action;
		if (action.StartsWith("qcmd:", StringComparison.Ordinal))
		{
			ExecuteQbarCommand(result);
			return true;
		}
		if (action.StartsWith("qfilter:", StringComparison.Ordinal))
		{
			ApplyFilter(action.Substring("qfilter:".Length));
			return true;
		}
		if (string.Equals(action, "qbar:hidden", StringComparison.Ordinal))
		{
			ShowHiddenReasons();
			return true;
		}
		if (string.Equals(action, "qbar:rebuild-index", StringComparison.Ordinal))
		{
			RebuildFileIndex();
			return true;
		}
		return false;
	}

	private void RebuildFileIndex()
	{
		if (QbarIndex.Building)
		{
			_host.Notify(Loc.T("文件索引正在建立：") + QbarIndex.Status);
			return;
		}
		_host.Notify(Loc.T("正在重建文件索引…"));
		Task.Run(() =>
		{
			QbarIndex.RequestRebuild();
			Stopwatch stopwatch = Stopwatch.StartNew();
			while (!QbarIndex.Ready && stopwatch.ElapsedMilliseconds < 8000)
			{
				Thread.Sleep(200);
			}
			((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
			{
				if (QbarIndex.Ready)
				{
					_host.Notify(Loc.F("文件索引已就绪：{0} 个文件 / {1} 个目录", QbarIndex.FileCount, QbarIndex.DirCount));
					RunQuery();
				}
				else
				{
					_host.Notify((QbarIndex.Problem.Length > 0) ? QbarIndex.Problem : Loc.T("文件索引还在建立，稍后就能搜到"));
				}
			}), Array.Empty<object>());
		});
	}

	private void ExecuteQbarCommand(QbarResult result)
	{
		string id = result.Action.Substring("qcmd:".Length);
		QbarCommand qbarCommand = QbarCommandStore.Effective(_host.Settings.Qbar.Commands).FirstOrDefault((QbarCommand c) => string.Equals(c.Id, id, StringComparison.Ordinal));
		if (qbarCommand == null)
		{
			_host.Notify(Loc.T("这条命令已经不存在，请到设置里重新配置"));
			return;
		}
		string actionPath = SelectedPath();
		string currentFolder = CurrentFolder(actionPath);
		bool flag = QbarCommandStore.IsInternalAction(qbarCommand.Id) && !string.Equals(qbarCommand.Id, "builtin.dict", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(qbarCommand.Path);
		if (QbarCommandStore.IsInternalAction(qbarCommand.Id) && !flag)
		{
			RunInternalCommand(qbarCommand, actionPath, currentFolder, result.Arguments);
			return;
		}
		HideQbar();
		CoreLog.Info("[Qbar] 执行命令：" + qbarCommand.Title + " / " + qbarCommand.Path);
		QbarCommandResult qbarCommandResult = QbarCommandStore.Execute(qbarCommand, result.Arguments, currentFolder, actionPath);
		if (!qbarCommandResult.Ok)
		{
			_host.Notify(qbarCommandResult.Message);
			return;
		}
		RecordHistory("command", qbarCommand.Title, qbarCommand.Path, Results.SelectedIndex);
		if (qbarCommandResult.Message.Length > 0)
		{
			_host.Notify(qbarCommandResult.Message);
		}
	}

	private void RunInternalCommand(QbarCommand command, string? actionPath, string currentFolder, string tail)
	{
		try
		{
			switch (command.Id)
			{
			case "builtin.copypath":
				if (string.IsNullOrEmpty(actionPath))
				{
					_host.Notify(Loc.T("没有选中项，先选一个文件或文件夹"));
					break;
				}
				ClipboardAccess.SetText(actionPath);
				_host.Notify(Loc.T("路径已复制"));
				HideQbar();
				break;
			case "builtin.newfolder":
			{
				string text3 = UniquePath(currentFolder, Loc.T("新建文件夹"), "");
				Directory.CreateDirectory(text3);
				HideQbar();
				RecentTargets.Record(text3);
				RecordHistory("create", Path.GetFileName(text3), text3, Results.SelectedIndex);
				_host.Notify(Loc.T("已创建：") + text3);
				break;
			}
			case "builtin.newfile":
			{
				string text2 = UniquePath(currentFolder, Loc.T("新建文件"), ".txt");
				if (!File.Exists(text2))
				{
					File.WriteAllText(text2, "");
				}
				HideQbar();
				RecordHistory("create", Path.GetFileName(text2), text2, Results.SelectedIndex);
				_host.Notify(Loc.T("已创建：") + text2);
				break;
			}
			case "builtin.parent":
			{
				string text = "";
				if (!string.IsNullOrEmpty(actionPath))
				{
					text = (Directory.Exists(actionPath) ? (Path.GetDirectoryName(actionPath.TrimEnd('\\')) ?? "") : (Path.GetDirectoryName(actionPath) ?? ""));
				}
				if (text.Length == 0)
				{
					_host.Notify(Loc.T("没有可以返回的上一级目录"));
					break;
				}
				SetInputText(text);
				Input.CaretIndex = Input.Text.Length;
				RunQuery();
				break;
			}
			case "builtin.refresh":
				RefreshIndex();
				break;
			case "builtin.dict":
				RunDictionaryCommand(command, tail);
				break;
			}
		}
		catch (Exception ex)
		{
			CoreLog.Error("内置命令执行失败", ex);
			_host.Notify(Loc.T("执行失败：") + ex.Message);
		}
	}

	private void RunDictionaryCommand(QbarCommand command, string tail)
	{
		string text = (tail ?? "").Trim();
		string text2 = (string.IsNullOrWhiteSpace(command.Keyword) ? "ol" : command.Keyword.Trim());
		if (text.Length == 0)
		{
			_host.Notify(Loc.F("在 {0} 后面输入要查的词，例如 {0} hello", text2));
			return;
		}
		HideQbar();
		DictionaryLaunchResult dictionaryLaunchResult = DictionaryLauncher.Launch(text, command, CurrentFolder(SelectedPath()), SelectedPath());
		if (!dictionaryLaunchResult.Ok)
		{
			_host.Notify(dictionaryLaunchResult.Message);
			return;
		}
		RecordHistory("command", dictionaryLaunchResult.External ? command.Title : Loc.T("网页词典"), dictionaryLaunchResult.Target, Results.SelectedIndex);
		if (dictionaryLaunchResult.Message.Length > 0)
		{
			_host.Notify(dictionaryLaunchResult.Message);
		}
	}

	private void RefreshIndex()
	{
		QbarPinyinIndex.Invalidate();
		if (!QbarIndex.Ready && !QbarIndex.Building)
		{
			QbarIndex.Start();
		}
		string message;
		if (QbarIndex.Ready)
		{
			message = Loc.F("文件索引已就绪：{0} 个文件 / {1} 个目录", QbarIndex.FileCount, QbarIndex.DirCount);
		}
		else
		{
			message = (QbarIndex.Building ? Loc.T("文件索引正在建立，稍后可用") : Loc.T("文件索引不可用，请检查索引目录权限"));
		}
		_host.Notify(message);
		QbarPinyinIndex.Start();
		RunQuery();
	}

	private string? SelectedPath()
	{
		QbarItem qbarItem = Results.SelectedItem as QbarItem;
		bool flag = qbarItem != null;
		if (flag)
		{
			QbarKind kind = qbarItem.Result.Kind;
			bool flag2 = ((kind == QbarKind.Run || kind == QbarKind.File) ? true : false);
			flag = flag2;
		}
		if (flag && qbarItem.Result.Payload.Length > 0)
		{
			return qbarItem.Result.Payload;
		}
		for (int i = 0; i < _items.Count; i++)
		{
			QbarResult result = _items[i].Result;
			QbarKind kind = result.Kind;
			flag = ((kind == QbarKind.Run || kind == QbarKind.File) ? true : false);
			if (flag && result.Payload.Length > 0)
			{
				return result.Payload;
			}
		}
		return null;
	}

	private static string CurrentFolder(string? actionPath)
	{
		if (!string.IsNullOrEmpty(actionPath))
		{
			try
			{
				if (Directory.Exists(actionPath))
				{
					return actionPath;
				}
				string directoryName = Path.GetDirectoryName(actionPath);
				if (!string.IsNullOrEmpty(directoryName))
				{
					return directoryName;
				}
			}
			catch (Exception ex)
			{
				CoreLog.Swallow(ex, "推断命令工作目录");
			}
		}
		return Environment.CurrentDirectory;
	}

	private static string UniquePath(string folder, string baseName, string extension)
	{
		string text = Path.Combine(folder, baseName + extension);
		int num = 2;
		while (File.Exists(text) || Directory.Exists(text))
		{
			text = Path.Combine(folder, baseName + " (" + num++ + ")" + extension);
		}
		return text;
	}

	private void RecordHistory(string kind, string name, string path, int rank)
	{
		QbarSearchOptions search = _host.Settings.Qbar.Search;
		if ((search == null || search.HistoryEnabled) && !string.IsNullOrEmpty(path))
		{
			QbarHistory.Record(kind, name, path, Input.Text, rank);
		}
	}

	private void ApplyRowPalette()
	{
		QbarOptions qbar = _host.Settings.Qbar;
		bool flag = IsLightColor((string.IsNullOrWhiteSpace(qbar.PanelColor) || qbar.PanelColor.Equals("auto", StringComparison.OrdinalIgnoreCase)) ? ThemePanelColor() : ParseColor(qbar.PanelColor));
		Color c = (flag ? Color.FromRgb(22, 26, 35) : Colors.White);
		Color c2 = (flag ? Color.FromRgb(91, 100, 120) : Color.FromRgb(201, 214, 236));
		Color c3 = (flag ? Color.FromRgb(138, 147, 166) : Color.FromRgb(127, 141, 168));
		SolidColorBrush solidColorBrush = Frozen(c);
		SolidColorBrush solidColorBrush2 = Frozen(c2);
		SolidColorBrush solidColorBrush3 = Frozen(c3);
		foreach (TextBlock item in FindAll<TextBlock>((DependencyObject)(object)Results))
		{
			TextBlock current;
			double fontSize = (current = item).FontSize;
			SolidColorBrush foreground;
			if (fontSize >= 13.5)
			{
				foreground = solidColorBrush;
			}
			else
			{
				foreground = ((!(fontSize >= 11.0)) ? solidColorBrush3 : solidColorBrush2);
			}
			current.Foreground = foreground;
		}
	}

	private void UpdateMode()
	{
		if (_menuFile != null)
		{
			return;
		}
		if (_chip != null)
		{
			TextBlock hintText = HintText;
			hintText.Text = _chip.Kind switch
			{
				ChipKind.Search => Loc.T("Enter 搜索"), 
				ChipKind.Web => Loc.T("Enter 打开"), 
				_ => Loc.T("Enter 启动"), 
			};
			return;
		}
		string text = Input.Text;
		string text2 = Loc.T("Enter 执行");
		if (text.StartsWith('='))
		{
			text2 = "Enter 复制结果";
		}
		else if (text.StartsWith('/'))
		{
			text2 = "内置命令";
		}
		else if (text.Contains(' ') && _host.Settings.Search.Any((QEntry s) => s.Key.Equals(text.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
		{
			text2 = Loc.T("搜索");
		}
		else if (text.Length > 0 && QbarService.LooksLikeMath(text))
		{
			text2 = Loc.T("计算");
		}
		HintText.Text = text2;
	}

	private void UpdateHint()
	{
		if (_menuFile == null && Results.SelectedItem is QbarItem qbarItem)
		{
			TextBlock hintText = HintText;
			string text;
			switch (qbarItem.Result.Kind)
			{
			case QbarKind.Math:
			case QbarKind.Dictionary:
				text = Loc.T("Enter 复制结果");
				break;
			case QbarKind.Clipboard:
				text = Loc.T("Enter 粘贴");
				break;
			case QbarKind.Window:
				text = Loc.T("Enter 切换窗口");
				break;
			case QbarKind.Command:
				text = Loc.T("Enter 执行");
				break;
			case QbarKind.Search:
				text = ((_chip != null) ? Loc.T("Enter 搜索") : Loc.T("Enter 打开搜索"));
				break;
			case QbarKind.Web:
				text = Loc.T("Enter 打开网址");
				break;
			case QbarKind.Run:
				text = Loc.T("Enter 启动");
				break;
			default:
				text = Loc.T("Enter 执行");
				break;
			}
			hintText.Text = text;
		}
	}

	private void PinSelected()
	{
		if (!(Results.SelectedItem is QbarItem qbarItem))
		{
			return;
		}
		QbarResult r = qbarItem.Result;
		QbarKind kind = r.Kind;
		if (((uint)(kind - 2) > 1u && kind != QbarKind.File) || 1 == 0)
		{
			_host.Notify(Loc.T("这条结果不能固定：请选择文件、文件夹、网址或程序"));
		}
		else
		{
			if (string.IsNullOrWhiteSpace(r.Payload))
			{
				return;
			}
			List<QEntry> pinned = _host.Settings.Qbar.Pinned;
			if (pinned.Any((QEntry x) => x.Value.Equals(r.Payload, StringComparison.OrdinalIgnoreCase)))
			{
				_host.Notify(Loc.T("这个目标已经固定过了"));
				return;
			}
			object obj;
			if (!string.IsNullOrWhiteSpace(r.Title))
			{
				obj = r.Title;
			}
			else
			{
				string fileName = Path.GetFileName(r.Payload.TrimEnd(new char[2] { '\\', '/' }));
				obj = ((fileName != null && fileName.Length > 0) ? fileName : r.Payload);
			}
			string key;
			string text = (key = (string)obj);
			int num = 2;
			while (pinned.Any((QEntry x) => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
			{
				key = $"{text} {num++}";
			}
			pinned.Add(new QEntry
			{
				Key = key,
				Value = r.Payload,
				Comment = text,
				IconPath = (r.IconPath ?? ""),
				Arguments = r.Arguments,
				RunAsAdmin = r.RunAsAdmin
			});
			_host.SaveSettings();
			_host.FlushSettings();
			_host.Notify(Loc.F("已固定到 Qbar：{0}", text));
		}
	}

	private void ExecuteSelected()
	{
		if (_closing || !(Results.SelectedItem is QbarItem qbarItem))
		{
			return;
		}
		if (_menuFile != null)
		{
			if (_menuActions.TryGetValue(qbarItem.Result.Arguments, out Action value))
			{
				value?.Invoke();
			}
		}
		else
		{
			ExecuteResult(qbarItem.Result);
		}
	}

	private void ExecuteResult(QbarResult result)
	{
		if (_closing || (result.Action.Length > 0 && TryRunAction(result)))
		{
			return;
		}
		if (result.Kind == QbarKind.File && result.Payload.Length > 0)
		{
			RecordHistory("select", result.Title, result.Payload, Results.SelectedIndex);
		}
		CoreLog.Info("[Qbar] 执行结果：" + result.Title);
		HideQbar();
		((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
		{
			try
			{
				_host.Qbar.Execute(result, BuildCallbacks());
			}
			catch (Exception ex)
			{
				CoreLog.Error("Qbar 执行失败", ex);
				_host.Notify(Loc.T("执行失败：") + ex.Message);
			}
		}), (DispatcherPriority)4, Array.Empty<object>());
	}

	private void OnResultsRightClick(object sender, MouseButtonEventArgs e)
	{
		e.Handled = true;
		if (_menuFile != null)
		{
			CloseFileMenu();
			return;
		}
		ListBox results = Results;
		object originalSource = e.OriginalSource;
		if (results.ContainerFromElement((DependencyObject)((originalSource is DependencyObject) ? originalSource : null)) is ListBoxItem { DataContext: QbarItem dataContext })
		{
			QbarResult result = dataContext.Result;
			if (result.Kind == QbarKind.File && result.Payload.Length != 0)
			{
				OpenFileMenu(dataContext);
			}
		}
	}

	private void OpenFileMenu(QbarItem owner)
	{
		string path = owner.Result.Payload;
		bool flag = Directory.Exists(path);
		bool num = flag || File.Exists(path);
		bool num2 = !flag && IsRunnableFile(path);
		_menuFile = owner;
		_menuReturnItems = _items;
		_menuReturnIndex = Results.SelectedIndex;
		_menuActions.Clear();
		List<QbarItem> rows = new List<QbarItem>();
		int seq = 0;
		Add("打开", path, "\ue8e5", () =>
		{
			QbarItem qbarItem = owner;
			CloseFileMenu();
			ExecuteResult(qbarItem.Result);
		});
		if (num2)
		{
			Add("以管理员身份运行", path, "\ue7ef", () =>
			{
				CloseFileMenu();
				HideQbar();
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
				{
					RunAsAdmin(path);
				}), Array.Empty<object>());
			});
		}
		if (num)
		{
			AddLocal("打开所在文件夹", flag ? Loc.T("上一级") : Loc.T("在资源管理器中选中"), "\ue8b7", () =>
			{
				OpenContainingFolder(path);
			});
			AddLocal("复制", path, "\ue8c8", () =>
			{
				SetFileClipboard(path, cut: false);
			});
			AddLocal("剪切", path, "\ue8c6", () =>
			{
				SetFileClipboard(path, cut: true);
			});
		}
		AddLocal("复制路径", path, "\ue71b", () =>
		{
			CopyText(path);
		});
		if (num)
		{
			AddLocal("属性", path, "\ue946", () =>
			{
				ShowProperties(path);
			});
		}
		if (owner.Result.Suggestion.Length > 0)
		{
			bool hideFolder = flag;
			AddLocal(hideFolder ? Loc.T("不建议此文件夹") : Loc.T("不建议此文件"), path, "\ue7ba", () =>
			{
				SuggestionsService suggestions = _host.Suggestions;
				if (suggestions != null)
				{
					if (suggestions.Hide(path, hideFolder, out string error))
					{
						ToastWindow.Show(Loc.T("已隐藏，可在设置里还原"), "info");
						if (Input.Text.Length == 0)
						{
							RunQuery();
						}
					}
					else
					{
						ToastWindow.Show(Loc.T("隐藏失败：") + error, "error");
					}
				}
			});
		}
		AddLocal("返回搜索结果", Loc.T("Esc"), "\ue72b", () =>
		{
		});
		MenuHeaderText.Text = path;
		MenuHeaderBox.Visibility = Visibility.Visible;
		ModeIconBox.Visibility = Visibility.Visible;
		ModeIcon.Text = "\ue712";
		HintText.Text = Loc.T("Enter 执行");
		FooterHint.Text = Loc.T("↑↓ 选择    Enter 执行    Esc 或右键返回搜索结果");
		_items = rows;
		Results.ItemsSource = _items;
		if (_items.Count > 0)
		{
			Results.SelectedIndex = 0;
		}
		Results.ScrollIntoView(Results.SelectedItem);
		void Add(string title, string detail, string glyph, Action run)
		{
			int num3 = ++seq;
			string text = num3.ToString(CultureInfo.InvariantCulture);
			_menuActions[text] = run;
			QbarResult result = new QbarResult(QbarKind.Command, title, detail, "", "", null, RunAsAdmin: false, text)
			{
				Glyph = glyph
			};
			rows.Add(new QbarItem(result, _host.Settings.Qbar.ShowIcons));
		}
		void AddLocal(string title, string detail, string glyph, Action run)
		{
			Add(title, detail, glyph, () =>
			{
				CloseFileMenu();
				run();
			});
		}
	}

	private void CloseFileMenu()
	{
		if (_menuFile != null)
		{
			_menuFile = null;
			_menuActions.Clear();
			MenuHeaderBox.Visibility = Visibility.Collapsed;
			ModeIconBox.Visibility = Visibility.Collapsed;
			FooterHint.Text = Loc.T("↑↓ 选择    Enter 执行    Tab 补全    Esc 关闭");
			_items = _menuReturnItems;
			Results.ItemsSource = _items;
			if (_items.Count > 0)
			{
				Results.SelectedIndex = Math.Clamp(_menuReturnIndex, 0, _items.Count - 1);
			}
			UpdateMode();
			UpdateHint();
		}
	}

	private static bool IsRunnableFile(string path)
	{
		string extension = Path.GetExtension(path);
		if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
		{
			return extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void RunAsAdmin(string path)
	{
		try
		{
			// 提权启动也要带工作目录：UAC 只换令牌，不换"子进程的家"
			Process.Start(ExternalLaunch.ForTarget(path, runAs: true));
			RecentTargets.Record(path);
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("以管理员身份运行失败：") + ex.Message);
		}
	}

	private void OpenContainingFolder(string path)
	{
		Task.Run(() =>
		{
			WindowService.RevealOutcome revealOutcome = WindowService.RevealInForeground(path, openFolderOnly: false);
			if (revealOutcome.Launched)
			{
				if (revealOutcome.WentUp)
				{
					_host.Notify(Loc.T("文件已不在了，已打开上级目录"));
				}
			}
			else
			{
				_host.Notify(Loc.T(revealOutcome.Reachable ? "打开文件夹失败：" : "所在位置无法访问") + ((revealOutcome.Why.Length > 0) ? revealOutcome.Why : ""));
			}
		});
	}

	private void SetFileClipboard(string path, bool cut)
	{
		try
		{
			DataObject dataObject = new DataObject();
			dataObject.SetFileDropList(new StringCollection { path });
			dataObject.SetData("Preferred DropEffect", new MemoryStream((!cut) ? new byte[4] { 1, 0, 0, 0 } : new byte[4] { 2, 0, 0, 0 }));
			ClipboardAccess.SetDataObject(dataObject, copy: true);
			_host.Notify(cut ? Loc.T("已剪切到剪贴板") : Loc.T("已复制到剪贴板"));
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("写入剪贴板失败：") + ex.Message);
		}
	}

	private void CopyText(string text)
	{
		try
		{
			ClipboardAccess.SetText(text);
			_host.Notify(Loc.T("路径已复制"));
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("复制失败：") + ex.Message);
		}
	}

	private void ShowProperties(string path)
	{
		Thread thread = new Thread(() =>
		{
			try
			{
				NativeMethods.SHELLEXECUTEINFO info = new NativeMethods.SHELLEXECUTEINFO
				{
					cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFO>(),
					fMask = 12u,
					hwnd = IntPtr.Zero,
					lpVerb = "properties",
					lpFile = path,
					nShow = 1
				};
				if (!NativeMethods.ShellExecuteExW(ref info))
				{
					throw new Win32Exception(Marshal.GetLastWin32Error());
				}
			}
			catch (Exception ex)
			{
				string msg = ex.Message;
				((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
				{
					_host.Notify(Loc.T("打开属性失败：") + msg);
				}), Array.Empty<object>());
			}
		});
		thread.IsBackground = true;
		thread.Name = "InputPlus.Properties";
		thread.SetApartmentState(ApartmentState.STA);
		thread.Start();
	}

	private QbarService.QbarCallbacks BuildCallbacks()
	{
		return new QbarService.QbarCallbacks(_host.Notify, (string text) =>
		{
			try
			{
				ClipboardAccess.SetText(text);
			}
			catch
			{
			}
		}, (string text) =>
		{
			_host.Clipboard.PasteText(text);
		}, OpenTarget, RunTarget, RunCommand, (string action) =>
		{
			_host.InvokeAction(action, "");
		});
	}

	private void OpenTarget(string target)
	{
		if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase) || target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
		{
			_host.Context.OpenUrl(target);
			return;
		}
		try
		{
			// 打开文件/文件夹都走统一入口：本机路径自动带上它自己的目录
			Process.Start(ExternalLaunch.ForTarget(target));
			RecentTargets.Record(target);
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("打开失败：") + ex.Message);
		}
	}

	private void RunTarget(string target, bool asAdmin, string args)
	{
		try
		{
			/* 目标是自己目录里的程序时，"工作目录"决定它读哪份配置 ——
			 * 交给统一入口算，别在这里另写一份判断。 */
			Process.Start(ExternalLaunch.ForTarget(target, args, asAdmin));
			RecentTargets.Record(target);
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("启动失败：") + ex.Message);
		}
	}

	private void RunCommand(string command)
	{
		if (command == null)
		{
			return;
		}
		switch (command.Length)
		{
		case 8:
			switch (command[4])
			{
			case 'H':
				if (command == "openHelp")
				{
					_host.ShowMainWindow("help");
				}
				break;
			case 'M':
				if (command == "openMath")
				{
					_host.ShowMathBoard();
				}
				break;
			}
			break;
		case 13:
			switch (command[0])
			{
			case 'o':
				if (command == "openClipboard")
				{
					_host.ShowClipboardHistory();
				}
				break;
			case 't':
				if (command == "toggleEnabled")
				{
					_host.ToggleEnabled();
				}
				break;
			}
			break;
		case 12:
			if (command == "openSettings")
			{
				_host.ShowMainWindow("settings");
			}
			break;
		case 5:
			if (command == "touch")
			{
				CreateDesktopFile();
			}
			break;
		case 6:
			if (command == "reload")
			{
				_host.ReloadAll();
			}
			break;
		case 4:
			if (command == "exit")
			{
				_host.ExitApp();
			}
			break;
		case 7:
		case 9:
		case 10:
		case 11:
			break;
		}
	}

	private void CreateDesktopFile()
	{
		string text = TextPromptWindow.Ask(this, Loc.T("创建文件"), Loc.T("文件名"), "新建文件.txt");
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		text = Path.GetFileName(text.Trim());
		if (text.Length == 0)
		{
			return;
		}
		string path = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		try
		{
			if (Registry.GetValue("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\User Shell Folders", "Desktop", null) is string { Length: >0 } text2)
			{
				string text3 = Environment.ExpandEnvironmentVariables(text2);
				if (Directory.Exists(text3))
				{
					path = text3;
				}
			}
		}
		catch
		{
		}
		string text4 = Path.Combine(path, text);
		if (File.Exists(text4))
		{
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			string extension = Path.GetExtension(text);
			for (int i = 1; i < 1000; i++)
			{
				if (!File.Exists(text4))
				{
					break;
				}
				text4 = Path.Combine(path, $"{fileNameWithoutExtension} ({i}){extension}");
			}
		}
		try
		{
			File.WriteAllText(text4, "");
			_host.Notify(Loc.T("已创建：") + text4);
		}
		catch (Exception ex)
		{
			_host.Notify(Loc.T("创建失败：") + ex.Message);
		}
	}

	private void OnResultClick(object sender, MouseButtonEventArgs e)
	{
		object originalSource = e.OriginalSource;
		DependencyObject val = (DependencyObject)((originalSource is DependencyObject) ? originalSource : null);
		if (val != null && FindAncestor<ListBoxItem>(val) != null)
		{
			ExecuteSelected();
		}
	}

	private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
	{
		while (d != null)
		{
			T val = (T)(object)((d is T) ? d : null);
			if (val != null)
			{
				return val;
			}
			d = VisualTreeHelper.GetParent(d);
		}
		return default;
	}
}
