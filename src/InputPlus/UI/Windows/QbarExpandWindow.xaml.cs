using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.Core.Icons;
using InputPlus.Core.Localization;
using InputPlus.Core.Qbar.Search;
using InputPlus.UI.Theme;

namespace InputPlus.UI.Windows;

public partial class QbarExpandWindow : Window, IComponentConnector
{
	public sealed class Row : INotifyPropertyChanged
	{
		internal bool IconRequested;

		public string Path { get; set; } = "";

		public string Name { get; set; } = "";

		public string Folder { get; set; } = "";

		public string Glyph { get; set; } = "\ue8a5";

		public string DateText { get; set; } = "";

		public string SizeText { get; set; } = "";

		public string TypeKey { get; set; } = "other";

		public string TimeKey { get; set; } = "older";

		public long SizeBytes { get; set; }

		public int DayValue { get; set; } = -1;

		public int Rank { get; set; }

		public IReadOnlyList<QbarTitleSegment> Segments { get; set; } = new List<QbarTitleSegment>();

		public ImageSource? IconSource { get; private set; }

		public bool HasIcon => IconSource != null;

		public event PropertyChangedEventHandler? PropertyChanged;

		internal void SetIcon(ImageSource image)
		{
			IconSource = image;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IconSource"));
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("HasIcon"));
		}
	}

	public sealed class FacetVm
	{
		public string Key { get; set; } = "all";

		public string Glyph { get; set; } = "";

		public string Label { get; set; } = "";

		public int Count { get; set; }

		public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
	}

	private readonly AppHost _host;

	private readonly DispatcherTimer _debounce;

	private List<Row> _all = new List<Row>();

	private List<Row> _view = new List<Row>();

	private int _seq;

	private string _typeKey = "all";

	private string _timeKey = "all";

	private string _sortKey = "rank";

	private bool _sortDesc;

	private int _total;

	private int _hidden;

	public QbarExpandWindow(AppHost host, string? query)
	{
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Expected Obj, but got Unknown
		_host = host;
		InitializeComponent();
		WindowChrome.AttachTo(this, "Brush.Background");
		_debounce = new DispatcherTimer
		{
			Interval = TimeSpan.FromMilliseconds(160L)
		};
		_debounce.Tick += (object? _, EventArgs _) =>
		{
			_debounce.Stop();
			RunQuery();
		};
		QueryBox.Text = query ?? "";
		Loaded += (object _, RoutedEventArgs _) =>
		{
			QueryBox.Focus();
			QueryBox.CaretIndex = QueryBox.Text.Length;
			RunQuery();
		};
		SizeChanged += (object _, SizeChangedEventArgs _) =>
		{
			FitPathColumn();
		};
		ResultList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));
		ResultList.ItemContainerGenerator.StatusChanged += (object? _, EventArgs _) =>
		{
			if (ResultList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
			{
				RequestVisibleIcons();
			}
		};
		ResultList.AddHandler(ScrollViewer.ScrollChangedEvent, (ScrollChangedEventHandler)((object _, ScrollChangedEventArgs _) =>
		{
			RequestVisibleIcons();
		}));
	}

	private void OnQueryChanged(object sender, TextChangedEventArgs e)
	{
		_debounce.Stop();
		_debounce.Start();
	}

	private void OnRefreshClick(object sender, RoutedEventArgs e)
	{
		_debounce.Stop();
		RunQuery();
	}

	private void RunQuery()
	{
		string text = (QueryBox.Text ?? "").Trim();
		int seq = ++_seq;
		if (text.Length == 0)
		{
			_all = new List<Row>();
			_view = new List<Row>();
			_total = 0;
			_hidden = 0;
			BuildFacets();
			ApplyFilter();
			StatusText.Text = Loc.T("输入关键词开始搜索");
			return;
		}
		QbarSearchOptions qbarSearchOptions = _host.Settings.Qbar.Search ?? new QbarSearchOptions();
		int limit = Math.Clamp(qbarSearchOptions.MaxCandidates, 200, 20000);
		Task.Run(() =>
		{
			QbarQueryContext context = QbarSearchEngine.FromSettings(_host.Settings);
			QbarSearchOutcome outcome = QbarSearchEngine.Run(context, text, limit);
			List<Row> rows = new List<Row>(outcome.Hits.Count);
			long today = QbarSyntaxValues.ToDay(DateTime.Now);
			for (int i = 0; i < outcome.Hits.Count; i++)
			{
				QbarSearchHit qbarSearchHit = outcome.Hits[i];
				rows.Add(new Row
				{
					Path = qbarSearchHit.Path,
					Name = qbarSearchHit.Name,
					Segments = QbarTitleHighlight.Segments(qbarSearchHit.Name, qbarSearchHit.Highlights),
					Folder = FolderOf(qbarSearchHit),
					Glyph = (qbarSearchHit.IsDir ? "\ue8b7" : "\ue8a5"),
					DateText = FormatDay(qbarSearchHit.Day),
					SizeText = (qbarSearchHit.IsDir ? "" : FormatSize(qbarSearchHit.SizeBytes)),
					SizeBytes = (qbarSearchHit.IsDir ? (-1) : qbarSearchHit.SizeBytes),
					DayValue = qbarSearchHit.Day,
					Rank = i,
					TypeKey = TypeOf(qbarSearchHit),
					TimeKey = TimeOf(qbarSearchHit.Day, today)
				});
			}
			((DispatcherObject)this).Dispatcher.Invoke((Action)(() =>
			{
				if (seq == _seq)
				{
					_all = rows;
					_total = outcome.RawCount;
					_hidden = outcome.HiddenCount;
					BuildFacets();
					ApplyFilter();
					if (outcome.TimedOut)
					{
						StatusText.Text = Loc.T("查询超时，结果可能不完整");
					}
				}
			}));
		});
	}

	private static string FolderOf(QbarSearchHit hit)
	{
		try
		{
			return Path.GetDirectoryName(hit.Path) ?? "";
		}
		catch (ArgumentException)
		{
			return "";
		}
	}

	private static string FormatDay(int day)
	{
		if (day < 0 || day == 65535)
		{
			return "";
		}
		try
		{
			return QbarSyntaxValues.FromDay(day).ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
		}
		catch (ArgumentOutOfRangeException)
		{
			return "";
		}
	}

	private static string FormatSize(long bytes)
	{
		if (bytes <= 0)
		{
			return "<1 KB";
		}
		string[] array = new string[5] { "B", "KB", "MB", "GB", "TB" };
		double num = bytes;
		int num2 = 0;
		while (num >= 1024.0 && num2 < array.Length - 1)
		{
			num /= 1024.0;
			num2++;
		}
		if (num2 != 0)
		{
			return num.ToString((num >= 100.0) ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + array[num2];
		}
		return ((long)num).ToString(CultureInfo.CurrentCulture) + " B";
	}

	private static string TypeOf(QbarSearchHit hit)
	{
		if (hit.IsDir)
		{
			return "folder";
		}
		string text = Path.GetExtension(hit.Path);
		if (text.StartsWith("."))
		{
			text = text.Substring(1);
		}
		if (Has("c;chm;cpp;cxx;doc;docm;docx;dot;dotm;dotx;h;hpp;htm;html;hxx;ini;java;js;lua;mdb;mht;mhtml;pdf;potm;potx;ppam;pps;ppsm;ppsx;ppt;pptm;pptx;rtf;sldm;sldx;thmx;txt;vsd;wpd;wps;wri;xlam;xls;xlsb;xlsm;xlsx;xltm;xltx;xml", text))
		{
			return "doc";
		}
		if (Has("ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;svg;tga;tif;tiff;webp;wmf", text))
		{
			return "pic";
		}
		if (Has("3g2;3gp;3gp2;3gpp;asf;avi;bdmv;bik;divx;drc;f4v;flc;fli;flv;ifo;ivf;m1v;m2ts;m2v;m4v;mkv;mov;mp4;mpe;mpeg;mpg;mpv2;mts;ogm;pss;pva;qt;rm;rmvb;roq;rpm;smk;swf;vob;vp6;webm;wm;wmp;wmv", text))
		{
			return "video";
		}
		if (Has("aac;ac3;aif;aifc;aiff;amr;ape;au;cda;dts;flac;it;m3u;m4a;mid;midi;mka;mod;mp2;mp3;mpa;ogg;ogm;ra;rmi;snd;spc;umx;vgm;vgz;voc;wav;wma", text))
		{
			return "audio";
		}
		return "other";
	}

	private static bool Has(string list, string ext)
	{
		if (ext.Length == 0)
		{
			return false;
		}
		return (";" + list + ";").IndexOf(";" + ext + ";", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	private static string TimeOf(int day, long today)
	{
		if (day < 0 || day == 65535)
		{
			return "older";
		}
		long num = today - day;
		if (num <= 1)
		{
			return "d1";
		}
		if (num <= 7)
		{
			return "d7";
		}
		if (num <= 30)
		{
			return "d30";
		}
		if (num <= 365)
		{
			return "d365";
		}
		return "older";
	}

	private void BuildFacets()
	{
		Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
		for (int i = 0; i < _all.Count; i++)
		{
			Row row = _all[i];
			Bump(counts, row.TypeKey);
			Bump(counts, row.TimeKey);
			Bump(counts, "all");
		}
		List<FacetVm> list = new List<FacetVm>
		{
			new FacetVm
			{
				Key = "all",
				Glyph = "\ue8b7",
				Label = Loc.T("全部"),
				Count = Get("all")
			},
			new FacetVm
			{
				Key = "folder",
				Glyph = "\ue8b7",
				Label = Loc.T("文件夹"),
				Count = Get("folder")
			},
			new FacetVm
			{
				Key = "doc",
				Glyph = "\ue8a5",
				Label = Loc.T("文档"),
				Count = Get("doc")
			},
			new FacetVm
			{
				Key = "pic",
				Glyph = "\ueb9f",
				Label = Loc.T("图片"),
				Count = Get("pic")
			},
			new FacetVm
			{
				Key = "video",
				Glyph = "\ue714",
				Label = Loc.T("视频"),
				Count = Get("video")
			},
			new FacetVm
			{
				Key = "audio",
				Glyph = "\ue8d6",
				Label = Loc.T("音频"),
				Count = Get("audio")
			},
			new FacetVm
			{
				Key = "other",
				Glyph = "\ue7c3",
				Label = Loc.T("其他"),
				Count = Get("other")
			}
		};
		List<FacetVm> list2 = new List<FacetVm>
		{
			new FacetVm
			{
				Key = "all",
				Label = Loc.T("全部"),
				Count = Get("all")
			},
			new FacetVm
			{
				Key = "d1",
				Label = Loc.T("最近 1 天"),
				Count = Get("d1")
			},
			new FacetVm
			{
				Key = "d7",
				Label = Loc.T("最近 7 天"),
				Count = Get("d7")
			},
			new FacetVm
			{
				Key = "d30",
				Label = Loc.T("最近 30 天"),
				Count = Get("d30")
			},
			new FacetVm
			{
				Key = "d365",
				Label = Loc.T("最近 365 天"),
				Count = Get("d365")
			},
			new FacetVm
			{
				Key = "older",
				Label = Loc.T("更早"),
				Count = Get("older")
			}
		};
		int num = list.FindIndex((FacetVm x) => x.Key == _typeKey);
		int num2 = list2.FindIndex((FacetVm x) => x.Key == _timeKey);
		TypeList.ItemsSource = list;
		TimeList.ItemsSource = list2;
		TypeList.SelectedIndex = ((num >= 0) ? num : 0);
		TimeList.SelectedIndex = ((num2 >= 0) ? num2 : 0);
		int Get(string key)
		{
			if (!counts.TryGetValue(key, out var value))
			{
				return 0;
			}
			return value;
		}
	}

	private static void Bump(Dictionary<string, int> counts, string key)
	{
		counts.TryGetValue(key, out var value);
		counts[key] = value + 1;
	}

	private void ApplyFilter()
	{
		List<Row> list = new List<Row>(_all.Count);
		for (int i = 0; i < _all.Count; i++)
		{
			Row row = _all[i];
			if ((!(_typeKey != "all") || !(row.TypeKey != _typeKey)) && (!(_timeKey != "all") || !(row.TimeKey != _timeKey)))
			{
				list.Add(row);
			}
		}
		SortRows(list);
		_view = list;
		ResultList.ItemsSource = list;
		if (list.Count > 0)
		{
			ResultList.SelectedIndex = 0;
		}
		RequestVisibleIcons();
		bool flag = _typeKey != "all" || _timeKey != "all";
		ClearFilterButton.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		ClearFilterText.Text = Loc.T("清除筛选") + " (1)";
		FitPathColumn();
		string text = ((_total <= list.Count) ? Loc.F("{0} 个项目", list.Count.ToString("N0", CultureInfo.CurrentCulture)) : Loc.F("{0} 个项目（已列出 {1} 条）", _total.ToString("N0", CultureInfo.CurrentCulture), list.Count.ToString("N0", CultureInfo.CurrentCulture)));
		if (_hidden > 0)
		{
			text = text + " · " + Loc.F("已按优先级规则隐藏 {0} 条", _hidden.ToString(CultureInfo.CurrentCulture));
		}
		StatusText.Text = text;
	}

	private void SortRows(List<Row> rows)
	{
		SortRows(rows, _sortKey, _sortDesc);
	}

	internal static void SortRows(List<Row> rows, string key, bool desc)
	{
		// 反编译修复：rank 表示「恢复原始相关度序」，不排序（由 --selftest 用例校验）
		if (key == "rank")
		{
			return;
		}
		Comparison<Row> cmp = key switch
		{
			"name" => (Comparison<Row>)((Row a, Row b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase)), 
			"folder" => (Row a, Row b) => string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase), 
			"date" => (Row a, Row b) => a.DayValue.CompareTo(b.DayValue), 
			"size" => (Row a, Row b) => a.SizeBytes.CompareTo(b.SizeBytes), 
			_ => (Row a, Row b) => a.Rank.CompareTo(b.Rank), 
		};
		rows.Sort((Row a, Row b) =>
		{
			int num = (desc ? cmp(b, a) : cmp(a, b));
			return (num == 0) ? a.Rank.CompareTo(b.Rank) : num;
		});
	}

	private void OnHeaderClick(object sender, RoutedEventArgs e)
	{
		if (!(e.OriginalSource is GridViewColumnHeader { Role: not GridViewColumnHeaderRole.Padding } gridViewColumnHeader))
		{
			return;
		}
		string text;
		if (gridViewColumnHeader.Column == null)
		{
			text = "";
		}
		else if (gridViewColumnHeader.Column == NameColumn)
		{
			text = "name";
		}
		else if (gridViewColumnHeader.Column == PathColumn)
		{
			text = "folder";
		}
		else if (gridViewColumnHeader.Column == DateColumn)
		{
			text = "date";
		}
		else
		{
			text = ((gridViewColumnHeader.Column == SizeColumn) ? "size" : "");
		}
		if (text.Length != 0)
		{
			if (_sortKey != text)
			{
				_sortKey = text;
				bool sortDesc = ((text == "date" || text == "size") ? true : false);
				_sortDesc = sortDesc;
			}
			else if (!_sortDesc)
			{
				_sortDesc = true;
			}
			else
			{
				_sortKey = "rank";
				_sortDesc = false;
			}
			UpdateHeaderTitles();
			ApplyFilter();
		}
	}

	internal void SortForRender(string key, bool desc)
	{
		_sortKey = key;
		_sortDesc = desc;
		UpdateHeaderTitles();
		ApplyFilter();
	}

	private void UpdateHeaderTitles()
	{
		NameColumn.Header = TitleWithArrow(Loc.T("名称"), "name");
		PathColumn.Header = TitleWithArrow(Loc.T("路径"), "folder");
		DateColumn.Header = TitleWithArrow(Loc.T("修改日期"), "date");
		SizeColumn.Header = TitleWithArrow(Loc.T("大小"), "size");
	}

	private string TitleWithArrow(string title, string key)
	{
		if (!(_sortKey == key))
		{
			return title;
		}
		return title + (_sortDesc ? " ▼" : " ▲");
	}

	private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
	{
		if (TypeList.SelectedItem is FacetVm facetVm)
		{
			_typeKey = facetVm.Key;
			ApplyFilter();
		}
	}

	private void OnTimeChanged(object sender, SelectionChangedEventArgs e)
	{
		if (TimeList.SelectedItem is FacetVm facetVm)
		{
			_timeKey = facetVm.Key;
			ApplyFilter();
		}
	}

	private void OnClearFilter(object sender, RoutedEventArgs e)
	{
		_typeKey = "all";
		_timeKey = "all";
		TypeList.SelectedIndex = 0;
		TimeList.SelectedIndex = 0;
		ApplyFilter();
	}

	private void RequestVisibleIcons()
	{
		if (ResultList.ItemsSource == null)
		{
			return;
		}
		double dpiScaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
		int pixelSize = ShellIcons.PickPixels(18, dpiScaleX);
		for (int i = 0; i < ResultList.Items.Count; i++)
		{
			if (!(ResultList.ItemContainerGenerator.ContainerFromIndex(i) is ListViewItem { DataContext: Row { IconRequested: false } dataContext }))
			{
				continue;
			}
			dataContext.IconRequested = true;
			ImageSource imageSource = ShellIcons.TryGetCached(dataContext.Path, pixelSize, dpiScaleX);
			if (imageSource != null)
			{
				dataContext.SetIcon(imageSource);
				continue;
			}
			Row target = dataContext;
			ShellIcons.Request(dataContext.Path, pixelSize, dpiScaleX, (ImageSource? image) =>
			{
				if (image != null)
				{
					((DispatcherObject)this).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
					{
						target.SetIcon(image);
					}), Array.Empty<object>());
				}
			});
		}
	}

	private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
	}

	private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
	{
		OpenSelected();
	}

	private void OnWindowKeyDown(object sender, KeyEventArgs e)
	{
		//IL_0001: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Invalid comparison between Unknown and I4
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004d: Invalid comparison between Unknown and I4
		//IL_005e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Invalid comparison between Unknown and I4
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Invalid comparison between Unknown and I4
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Invalid comparison between Unknown and I4
		if ((int)e.Key == 13)
		{
			if (_typeKey != "all" || _timeKey != "all")
			{
				OnClearFilter(sender, e);
			}
			else
			{
				Close();
			}
			e.Handled = true;
		}
		else if ((int)e.Key == 6)
		{
			OpenSelected();
			e.Handled = true;
		}
		else if ((int)e.Key == 94)
		{
			RunQuery();
			e.Handled = true;
		}
		else if ((int)e.Key == 46 && (int)Keyboard.Modifiers == 2)
		{
			if (ResultList.SelectedItem is Row row)
			{
				ClipboardAccess.SetText(row.Path);
			}
			e.Handled = true;
		}
	}

	private void OpenSelected()
	{
		if (!(ResultList.SelectedItem is Row row))
		{
			return;
		}
		try
		{
			// 统一入口：文件自己的目录当工作目录（不设就会继承 InputPlus 的目录）
			Process.Start(InputPlus.Core.Windows.ExternalLaunch.ForTarget(row.Path));
		}
		catch (Exception ex)
		{
			CoreLog.Swallow(ex, "打开文件");
			StatusText.Text = Loc.F("打不开：{0}", row.Path);
		}
	}

	private void FitPathColumn()
	{
		if (PathColumn != null)
		{
			double val = ResultList.ActualWidth - 280.0 - 118.0 - 84.0 - 34.0;
			PathColumn.Width = Math.Max(160.0, val);
		}
	}

	private void OnClose(object sender, RoutedEventArgs e)
	{
		Close();
	}
}
