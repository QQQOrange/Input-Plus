using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using InputPlus.Core.Scripting;

namespace InputPlus.UI.Windows;

/// <summary>
/// Lua 脚本管理窗口：左边挑脚本，右边改名字/说明/代码，顺手「运行测试」看输出。
///
/// 脚本本身活在 <c>AppSettings.Scripts</c> 里 —— 动作目录里「Lua 脚本」分类下的每条动作就是这里的每条脚本，
/// 所以这个窗口只负责"增删改查 + 试跑"，绑按键/绑手势还是走 GestureSettingsWindow。
///
/// 编辑期间只改内存对象，只有「保存」才落盘：每敲一个字就写一次设置文件既慢又容易抖。
/// </summary>
public partial class LuaScriptsWindow : Window
{
    private readonly AppHost _host;

    /// <summary>列表现有的行（把 Scripts 包一层，见下面的 ScriptRow）。</summary>
    private List<ScriptRow> _rows = new();

    /// <summary>当前选中的脚本；没选中时是 null。</summary>
    private LuaScript? _current;

    /// <summary>正在往控件里灌值时置 true，用来抑制 TextChanged 回写（否则切换脚本会互相污染）。</summary>
    private bool _loading;

    public LuaScriptsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Load();
        // 正文从头铺到底（Brush.Background），标题栏也跟着用同一个色 —— 否则顶上会多出一条亮/暗一档的横杠。
        Theme.WindowChrome.AttachTo(this, "Brush.Background");
    }

    // ================= 渲染工具用的钩子 =================

    /// <summary>渲染用：选中第一条，不弹框。</summary>
    internal void SelectFirstForRender()
    {
        if (ScriptList.Items.Count > 0)
        {
            ScriptList.SelectedIndex = 0;
        }
        else
        {
            ShowScript(null);
        }
    }

    /// <summary>渲染用：选中第一条并把示例代码填进编辑器（只改内存，不落盘）。</summary>
    internal void UseSampleForRender()
    {
        SelectFirstForRender();

        // 设置里一条脚本都没有时（全新配置），拿一个临时对象把编辑器填满 —— 只为了截图好看，不进 Settings。
        if (_current is null)
        {
            ShowScript(new LuaScript { Name = Loc.T("示例脚本"), Comment = Loc.T("展示 ip.* 怎么用") });
        }

        if (_current is null) { return; }

        _loading = true;
        try
        {
            CodeBox.Text = LuaRunner.SampleCode;
            _current.Code = LuaRunner.SampleCode;
        }
        finally
        {
            _loading = false;
        }

        CodeBox.CaretIndex = 0;
        ShowResult(Loc.T("示例已插入，点「运行测试」看看效果。"), "Brush.TextTertiary");
        SetStatus(Loc.T("示例已插入，点「运行测试」看看效果。"));
    }

    // ================= 初始化 / 列表 =================

    private void Load()
    {
        // 右上「可以用什么」直接吃 LuaRunner 里的速查表：脚本能调什么，只有一处需要维护。
        ApiText.Text = LuaRunner.ApiDoc;

        ReloadList(null);

        if (_host.Settings.Scripts.Count > 0)
        {
            ScriptList.SelectedIndex = 0;
        }
        else
        {
            ShowScript(null);
            SetStatus(Loc.T("还没有脚本，点「新建」加一个。"));
        }
    }

    /// <summary>把 Settings.Scripts 重新包成列表行；select 指定要选中哪一条（null = 不选）。</summary>
    private void ReloadList(LuaScript? select)
    {
        _rows = new List<ScriptRow>(_host.Settings.Scripts.Count);
        foreach (LuaScript script in _host.Settings.Scripts)
        {
            _rows.Add(new ScriptRow(script));
        }

        ScriptList.ItemsSource = _rows;

        if (select is not null)
        {
            ScriptList.SelectedItem = FindRow(select);
        }

        UpdateCount();
    }

    private ScriptRow? FindRow(LuaScript script)
        => _rows.FirstOrDefault(row => ReferenceEquals(row.Script, script));

    /// <summary>名字/说明改了以后刷一下列表里的那一行（只通知，不重建容器，光标不乱跳）。</summary>
    private void RefreshRow(LuaScript script) => FindRow(script)?.Refresh();

    private void UpdateCount()
    {
        int count = _host.Settings.Scripts.Count;
        CountText.Text = count == 0 ? Loc.T("共 0 个脚本") : Loc.F("共 {0} 个脚本", count);
    }

    /// <summary>把脚本灌进右边编辑器；script 为 null 时清空并禁用编辑区。</summary>
    private void ShowScript(LuaScript? script)
    {
        _current = script;
        bool has = script is not null;

        _loading = true;
        try
        {
            NameBox.Text = script?.Name ?? "";
            CommentBox.Text = script?.Comment ?? "";
            CodeBox.Text = script?.Code ?? "";
            EnabledBox.IsChecked = script is { Enabled: true };

            NameBox.IsEnabled = has;
            CommentBox.IsEnabled = has;
            CodeBox.IsEnabled = has;
            EnabledBox.IsEnabled = has;

            ShowResult(script?.LastResult ?? "", "Brush.TextSecondary");
        }
        finally
        {
            _loading = false;
        }

        if (!has) { SetStatus(""); }
    }

    /// <summary>把编辑器里的内容写回选中对象（运行测试 / 保存 / 切走之前都要先冲一次）。</summary>
    private void FlushEditor()
    {
        if (_current is null) { return; }

        _current.Name = NameBox.Text;
        _current.Comment = CommentBox.Text;
        _current.Code = CodeBox.Text;
        RefreshRow(_current);
    }

    // ================= 状态行 / 结果区 =================

    /// <summary>右下角那行小字：说一句刚才干了什么。</summary>
    private void SetStatus(string text, string brushKey = "Brush.TextTertiary")
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>运行结果区：成功用正文色，失败用警示色（配色都走主题，别写死颜色）。</summary>
    private void ShowResult(string text, string brushKey)
    {
        ResultText.Text = text;
        ResultText.SetResourceReference(Control.ForegroundProperty, brushKey);
    }

    private void MarkDirty() => SetStatus(Loc.T("有改动，点「保存」写入设置。"), "Brush.TextSecondary");

    // ================= 选择 / 编辑 =================

    private void OnScriptSelected(object sender, SelectionChangedEventArgs e)
        => ShowScript((ScriptList.SelectedItem as ScriptRow)?.Script);

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null) { return; }

        _current.Name = NameBox.Text;
        RefreshRow(_current);
        MarkDirty();
    }

    private void OnCommentChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null) { return; }

        _current.Comment = CommentBox.Text;
        RefreshRow(_current);
        MarkDirty();
    }

    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _current is null) { return; }

        _current.Code = CodeBox.Text;
        MarkDirty();
    }

    private void OnEnabledClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _current is null) { return; }

        _current.Enabled = EnabledBox.IsChecked == true;
        RefreshRow(_current);
        SetStatus(_current.Enabled ? Loc.T("已启用，点「保存」生效。") : Loc.T("已停用，点「保存」生效。"));
    }

    // ================= 增 / 删 / 复制 =================

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        LuaScript script = new LuaScript { Name = UniqueName(Loc.T("新脚本")) };
        _host.Settings.Scripts.Add(script);

        ReloadList(script);
        FocusName();
        SetStatus(Loc.T("新脚本建好了，写完点「保存」。"));
    }

    /// <summary>
    /// 从内置模板新建一条脚本：模板本身不动，复制出来的是用户自己的副本，
    /// 改名字 / 改代码 / 保存都跟手写脚本一样。
    /// </summary>
    private void OnTemplateClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = TemplateButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        /* 一百多条模板平铺成一个竖列根本没法挑 —— 按分类折成子菜单：
         * 窗口 / 文本 / 打开 / 文件夹 / 搜索 / 时间 / 键鼠 / 网络 / 开发者 / 示例。
         * 分类顺序跟着 LuaTemplates.Categories 走（按用途排，不按字母排）。 */
        foreach (var category in LuaTemplates.Categories)
        {
            var group = new MenuItem { Header = Loc.T(category) };
            /* Visible：跟动作目录一致，与原生动作重复的模板不在这里再出现一次。 */
            foreach (var tpl in LuaTemplates.Visible)
            {
                if (tpl.Category != category) continue;
                var snap = tpl;
                var item = new MenuItem
                {
                    Header = Loc.T(snap.Name),
                    ToolTip = Loc.T(snap.Description),
                };
                item.Click += (_, _) => NewFromTemplate(snap);
                group.Items.Add(item);
            }

            if (group.Items.Count > 0) menu.Items.Add(group);
        }

        menu.IsOpen = true;
    }

    private void NewFromTemplate(LuaTemplate tpl)
    {
        var script = new LuaScript
        {
            Name = UniqueName(Loc.T(tpl.Name)),
            Comment = Loc.T(tpl.Description),
            Code = tpl.Code,
        };

        _host.Settings.Scripts.Add(script);

        ReloadList(script);
        FocusName();

        /* 模板新建的有完整代码，落盘之后动作目录里立刻就有这一条 ——
         * 用户不用再去点「保存」才敢用（其它的新建仍然等他写完再保存）。 */
        _host.SaveSettings();
        SetStatus(Loc.F("已从模板新建「{0}」，改成你要的再点「保存」。", script.Name));
    }

    private void OnDuplicateClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus(Loc.T("先在左边选一个脚本。")); return; }

        LuaScript copy = _current.Clone();

        // Clone() 连 Id 一起复制，这里必须换一个新的：两条脚本撞 Id，"按键绑的是哪一条"就说不清了。
        copy.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        copy.Name = UniqueName(Loc.F("{0} 副本", _current.DisplayName));
        copy.LastResult = "";

        _host.Settings.Scripts.Add(copy);

        ReloadList(copy);
        FocusName();
        SetStatus(Loc.T("复制好了，改个名字再点「保存」。"));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus(Loc.T("先在左边选一个脚本。")); return; }

        if (!AppDialog.ConfirmDanger(this,
                Loc.F("删除脚本「{0}」？删掉就找不回来了。", _current.DisplayName),
                Loc.T("删除脚本"),
                Loc.T("删除")))
        {
            return;
        }

        LuaScript removed = _current;
        _current = null;
        _host.Settings.Scripts.Remove(removed);

        ReloadList(null);
        if (ScriptList.Items.Count > 0)
        {
            ScriptList.SelectedIndex = 0;
        }
        else
        {
            ShowScript(null);
        }

        _host.SaveSettings();   // 删除是不可撤销的，当场落盘，别等用户想起来点保存
        SetStatus(Loc.T("已删除"), "Brush.Success");
    }

    /// <summary>挑一个不重名的名字：新脚本 / 新脚本 2 / 新脚本 3……（列表里两条同名，用户根本分不清）。</summary>
    private string UniqueName(string baseName)
    {
        string name = baseName;
        int index = 2;

        while (_host.Settings.Scripts.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = Loc.F("{0} {1}", baseName, index);
            index++;
        }

        return name;
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    // ================= 运行测试 / 保存 / 示例 =================

    private void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus(Loc.T("先在左边选一个脚本。")); return; }

        FlushEditor();

        if (string.IsNullOrWhiteSpace(_current.Code))
        {
            ShowResult(Loc.T("这脚本还空着：写两行，或者展开下面「可以用什么」点「插入示例」。"), "Brush.TextTertiary");
            return;
        }

        // 这里会阻塞到脚本结束（最多 TimeoutMs），期间界面不刷新属于正常 —— 脚本超时不会拖住窗口。
        LuaRunResult result = LuaRunner.Run(_current, _host.Context, _host.InvokeAction);

        string summary = result.Ok
            ? Loc.F("✓ 用时 {0} 毫秒", result.ElapsedMs.ToString("0", Loc.Culture))
            : Loc.T("✗ 错误");

        string body = result.Ok ? result.Output : result.Error;
        if (!result.Ok && !string.IsNullOrEmpty(result.Output))
        {
            body = body + "\n" + Loc.T("（下面是出错前已经打印的内容）") + "\n" + result.Output;
        }

        if (string.IsNullOrEmpty(body))
        {
            body = result.Ok ? Loc.T("（脚本跑完了，什么都没打印）") : Loc.T("（没有更多信息）");
        }

        _current.LastResult = summary + "\n" + body;
        ShowResult(_current.LastResult, result.Ok ? "Brush.TextPrimary" : "Brush.Warning");
        SetStatus(result.Ok ? Loc.T("跑通了。") : Loc.T("有错误，看看上面的提示。"),
                  result.Ok ? "Brush.Success" : "Brush.Warning");
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        FlushEditor();
        _host.SaveSettings();
        SetStatus(Loc.T("已保存"), "Brush.Success");
    }

    private void OnInsertSampleClick(object sender, RoutedEventArgs e)
    {
        if (_current is null) { SetStatus(Loc.T("先在左边选一个脚本。")); return; }

        if (!string.IsNullOrWhiteSpace(CodeBox.Text)
            && !AppDialog.Confirm(this,
                Loc.T("编辑器里已经有内容，用示例覆盖吗？"),
                Loc.T("插入示例"),
                DialogKind.Question,
                Loc.T("覆盖")))
        {
            return;
        }

        CodeBox.Text = LuaRunner.SampleCode;   // TextChanged 会顺手写回对象
        CodeBox.CaretIndex = 0;
        CodeBox.Focus();
        SetStatus(Loc.T("示例已插入，点「运行测试」看看效果。"));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ================= 列表行视图模型 =================

    /// <summary>
    /// 列表行的视图模型：把 LuaScript 包一层，好在名字/说明/启用状态改动后通知列表刷新。
    /// 用 Items.Refresh() 也行，但那会重建全部容器，正在输入的焦点和光标位置会被搞乱。
    /// </summary>
    private sealed class ScriptRow : INotifyPropertyChanged
    {
        public ScriptRow(LuaScript script) => Script = script;

        public LuaScript Script { get; }

        public string DisplayName => Script.DisplayName;

        public string Comment => Script.Comment;

        public bool Enabled => Script.Enabled;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>三个成员一起通知：名字、说明、启用状态都可能刚改过。</summary>
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }
}
