using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using InputPlus.Core;
using InputPlus.UI.Windows;

namespace InputPlus.UI;

/// <summary>
/// 离屏渲染工具（诊断 / 文档用）。
///
/// 用法：InputPlus.exe --render &lt;输出目录&gt;
/// 会把每个页面和 Qbar 面板渲染成 PNG。
/// 相比截屏，它不受桌面遮挡、DPI 缩放和焦点动画的影响，结果稳定可复现，
/// 也方便以后做界面回归对比。
/// </summary>
public static class RenderTools
{
    /// <summary>是否输出布局诊断文件（由 --render-diag 打开，避免污染正常截图目录）。</summary>
    public static bool DiagnosticsRequested { get; set; }

    private static readonly string[] Pages =
    {
        // 「bindings」页已移除：窗口绑定并入按键映射，作为动作处理
        "guide", "keys", "hotstrings", "layers", "gestures", "scroll", "holddrag", "macros", "qbar",
        "clipboard", "backup", "settings", "test", "help", "about",
    };

    public static int Run(AppHost host, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int count = 0;

        /* 出图要能指定深浅色：深色界面的问题（标题栏一条白杠、字看不清）
           只有真切成深色才看得见。INPUTPLUS_RENDER_THEME=dark|light|system。
           只改这一轮进程里的主题，不写盘。 */
        try
        {
            string? theme = Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_THEME");
            if (!string.IsNullOrWhiteSpace(theme)) Theme.ThemeManager.Apply(Theme.ThemeManager.Parse(theme!));
        }
        catch (Exception ex) { CoreLog.Warn("渲染：切换主题失败：" + ex.Message); }

        /* 截图是给人看"正常长什么样"的：老配置里可能还留着 `/`、`、` 这类
         * 不在候选里的旧符号，截出来胶囊旁边会写「旧符号」，容易被当成 bug。
         * 所以只在渲染这一轮里临时换成冒号，**不写盘**。 */
        try
        {
            var hs = host.Settings.Hotstring;
            if (hs != null && Core.Settings.HotstringOptions.LabelOf(hs.EffectivePrefix).Length == 0)
            {
                hs.Prefixes = new List<string> { ":" };
                hs.NormalizePrefixes();
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("渲染：整理触发符号失败：" + ex.Message);
        }

        var main = host.CreateMainWindow();
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = -32000;
        main.Top = -32000;
        main.ShowInTaskbar = false;

        /* 支持指定出图尺寸：INPUTPLUS_RENDER_SIZE=1240x800
         *
         * 默认尺寸只能验证"最大化好看"这一种情况。用户报的键盘被压扁
         * 恰恰只在**缩小窗口**时出现，所以出图必须能换尺寸，
         * 否则这类回归永远看不出来。 */
        string? size = Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_SIZE");
        if (!string.IsNullOrWhiteSpace(size))
        {
            string[] parts = size.Split('x', 'X');
            if (parts.Length == 2
                && double.TryParse(parts[0], out double w) && w > 200
                && double.TryParse(parts[1], out double h) && h > 200)
            {
                main.Width = w;
                main.Height = h;
            }
            else
            {
                CoreLog.Warn("INPUTPLUS_RENDER_SIZE 格式不对（要 宽x高，如 1240x800）：" + size);
            }
        }

        main.Show();
        Pump();

        /* 只出指定页面：INPUTPLUS_RENDER_PAGES=qbar,holddrag
           全套 15 页出一遍要十几秒，改一页就只想看那一页 ——
           不能因为「想快一点」就得去改 Pages 数组再改回来。 */
        string? only = Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_PAGES");
        string[] wanted = string.IsNullOrWhiteSpace(only)
            ? Pages
            : only.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var page in Pages)
        {
            if (Array.IndexOf(wanted, page) < 0) continue;
            main.Navigate(page);
            /* 手势页最重（左中右三栏 + 61 条触发方式 + 动作下拉），
               220ms 常常还没画完就按快门，拍出来是一张白页。多等一拍。 */
            Pump(page == "gestures" ? 700 : 220);
            Save(main, Path.Combine(outDir, $"{page}.png"));

            /* 条目表四个标签（搜索指令 / 启动程序 / 常用网址 / 固定项）共用一张表，
               平时只拍到第一屏。选中态是不是还铺着系统蓝的直角横条、列头挤没挤、
               「附加菜单」列够不够宽，全在另外三个标签里 —— 逐个切过去拍一张。 */
            if (page == "qbar" && main.GetPage("qbar") is InputPlus.UI.Views.QbarSettingsView qtab)
            {
                try
                {
                    foreach (string tag in new[] { "search", "run", "web", "pinned" })
                    {
                        qtab.SelectTabForRender(tag);
                        Pump(260);
                        /* 选第二行：搜索指令页第一行自带「默认」徽标，看选中态不如第二行干净。 */
                        qtab.SelectEntryForRender(1);
                        Pump(120);
                        Save(main, Path.Combine(outDir, $"qbar-entries-{tag}.png"));
                    }
                    qtab.SelectTabForRender("search");
                    Pump(160);
                }
                catch (Exception ex) { CoreLog.Warn("渲染 Qbar 条目表四个标签失败：" + ex.Message); }
            }

            /* 文件对话框浮层：只有真的弹出「打开 / 保存」对话框时才看得见，平时连截图都没有。
               宽度是固定的（对话框 3/5），这里手工塞几行长短差别很大的数据离屏拍一张 ——
               专看「图标 | 文件名 | 地址 | 快捷键」四列的对齐和地址的中间省略。 */
            if (page == "qbar")
            {
                try
                {
                    var bar = new FileDialogBarWindow
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        Width = 720,
                        Height = 380,
                    };
                    bar.SetRowsForRender();
                    bar.SetFolderChip(@"D:\video");     // 推荐文件夹 chip（带 × ）
                    bar.Show();
                    Pump(500);
                    Save(bar, Path.Combine(outDir, "filedialog-bar.png"));
                    /* 脚注面包屑只在鼠标悬停时出现，单独拍一张才看得到 */
                    bar.ShowBreadcrumbForRender(3);
                    Pump(160);
                    Save(bar, Path.Combine(outDir, "filedialog-bar-hover.png"));
                    bar.Close();
                    Pump(120);
                }
                catch (Exception ex) { CoreLog.Warn("渲染文件对话框浮层失败：" + ex.Message); }
            }

            /* 附加菜单（中键唤出）：宽度、行距、圆角都是照着眼睛调出来的，
               不留下离屏截图，下次改坏了没人看得见。 */
            if (page == "qbar")
            {
                try
                {
                    var attachDef = InputPlus.Core.Gestures.MiddleMenuPolicy.Build(host.Settings);
                    var attach = new GestureMenuWindow(attachDef, InputPlus.Core.Actions.ActionCatalog.TitleOf)
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                    };
                    attach.Show();
                    Pump(300);
                    Save(attach, Path.Combine(outDir, "attach-menu.png"));

                    /* 反例专用：四个汉字 + 二级菜单箭头（"历史记录 ▸"）。
                       这正是用户说"连历史记录四个字都显示不全"的那一行，
                       菜单宽度改过之后必须有一张图能一眼看出来它到底是全的还是被截的。 */
                    var wideDef = new InputPlus.Core.Scripting.GestureMenuDef
                    {
                        Id = "render.wide",
                        Name = "",
                        Hint = "",
                        HideTitle = true,
                    };
                    wideDef.Items.Add(new InputPlus.Core.Scripting.GestureMenuItem
                    {
                        Title = "历史记录",
                        Glyph = "\uE81C",
                        Action = "",
                        Children = new List<InputPlus.Core.Scripting.GestureMenuItem>
                        {
                            new() { Title = "最近打开的文件", Action = "" },
                        },
                    });
                    wideDef.Items.Add(new InputPlus.Core.Scripting.GestureMenuItem
                    {
                        Title = "打开设置",
                        Glyph = "\uE713",
                        Action = "openSettings",
                    });
                    var wide = new GestureMenuWindow(wideDef, InputPlus.Core.Actions.ActionCatalog.TitleOf)
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                    };
                    wide.Show();
                    Pump(260);
                    Save(wide, Path.Combine(outDir, "attach-menu-wide.png"));
                    wide.Close();

                    attach.Close();
                    Pump(120);
                }
                catch (Exception ex) { CoreLog.Warn("渲染附加菜单失败：" + ex.Message); }
            }

            /* 平滑滚动的「程序例外」空表时只有一行提示，模板里的名字、状态、
               禁用/恢复按钮一个都看不到 —— 塞两条假数据专门拍一张。 */
            if (page == "scroll" && main.GetPage("scroll") is InputPlus.UI.Views.ScrollView scrollPage)
            {
                try
                {
                    scrollPage.SetSampleAppsForRender();
                    Pump(240);
                    Save(main, Path.Combine(outDir, "scroll-apps.png"));
                }
                catch (Exception ex) { CoreLog.Warn("渲染平滑滚动例外列表失败：" + ex.Message); }
                scrollPage.ClearSampleAppsForRender();
                Pump(120);
            }

            /* 「按住拖拽」页默认是关着的，直接拍出来是一屏灰字。
               塞一份示例配置，拍出真实参数填满的样子。 */
            if (page == "holddrag" && main.GetPage("holddrag") is InputPlus.UI.Views.HoldDragView hdv)
            {
                try
                {
                    hdv.SetSampleForRender();
                    Pump(260);
                    Save(main, Path.Combine(outDir, "holddrag-sample.png"));

                    /* 吸附档位：AltDrag 的 AutoSnap 四档 + 阈值 + Aero 开关，
                       在首屏之外，单独拍一张，回归时才看得见它们到底排成什么样。 */
                    try
                    {
                        hdv.ScrollSnapForRender();
                        Pump(160);
                        Save(main, Path.Combine(outDir, "holddrag-snap.png"));
                    }
                    catch (Exception ex) { CoreLog.Warn("渲染吸附档位失败：" + ex.Message); }

                    /* 左栏最底下是「窗口例外」，右边「当前状态」里是程序例外清单 ——
                       左栏滚到底、右栏停在卡片顶上，一张图里两边都看得见。 */
                    hdv.ScrollListsForRender();
                    Pump(160);
                    Save(main, Path.Combine(outDir, "holddrag-lists.png"));
                }
                catch (Exception ex) { CoreLog.Warn("渲染按住拖拽示例失败：" + ex.Message); }
            }

            /* Qbar 页右栏滚到底才看得到「文件搜索」那张卡（内置索引的状态与范围），
               平时没人会截图给它，回归时它的排版坏了也发现不了。 */
            if (page == "qbar" && main.GetPage("qbar") is InputPlus.UI.Views.QbarSettingsView qv)
            {
                /* 先把建议采集跑起来：这样右栏底部那两张卡的统计行是真实数字，
                   而不是"还没采集完"。采集在后台线程，最多等 3 秒。 */
                try
                {
                    host.Suggestions?.Start();
                    var warmDeadline = DateTime.UtcNow.AddSeconds(3);
                    while (host.Suggestions is { IsWarm: false } && DateTime.UtcNow < warmDeadline)
                        System.Threading.Thread.Sleep(40);
                }
                catch (Exception ex) { CoreLog.Warn("渲染前启动建议失败：" + ex.Message); }

                try
                {
                    Pump(200);
                    qv.ScrollRightToEndForRender();
                    Pump(220);
                    Save(main, Path.Combine(outDir, "qbar-files.png"));

                    /* 「文件搜」标签是折叠的，不主动切过去就永远拍不到：
                       过滤器/优先级两张编辑卡有没有挤坏、内置项有没有列出来，
                       只能靠这张图看。拍完切回「搜索指令」，别把页面留在别的标签上。 */
                    qv.SelectTabForRender("files");
                    Pump(280);
                    Save(main, Path.Combine(outDir, "qbar-files-tab.png"));

                    /* 下半截（优先级 / 语法与调试）在折叠面板的滚动区里，
                       一张图看不全 —— 滚到底再拍一张，别让它们永远没人看。 */
                    qv.ScrollFilesToForRender(0.5);
                    Pump(200);
                    Save(main, Path.Combine(outDir, "qbar-files-tab-mid.png"));

                    /* 命令卡夹在优先级和语法之间，两个端点都拍不到它，
                        单独给它一个位置 —— 命令列表挤坏过一次，得能看图查。 */
                    qv.ScrollFilesToForRender(0.75);
                    Pump(200);
                    Save(main, Path.Combine(outDir, "qbar-files-tab-cmd.png"));
                    qv.ScrollFilesToForRender(1.0);
                    Pump(220);
                    Save(main, Path.Combine(outDir, "qbar-files-tab-bottom.png"));

                    /* 命令卡换成了软件自带的「选择程序… / 点选窗口」按钮（跟手势页同一套），
                       按钮挤掉输入框这种事故只有看图才发现，所以按卡片锚点单独拍一张。 */
                    qv.ScrollFilesToCardForRender(3);
                    Pump(220);
                    Save(main, Path.Combine(outDir, "qbar-files-tab-picker.png"));
                    qv.ScrollFilesToForRender(0.0);
                    qv.SelectTabForRender("search");
                    Pump(160);
                }
                catch (Exception ex) { CoreLog.Warn("渲染 Qbar 文件搜索卡失败：" + ex.Message); }

                /* 「文件对话框浮层」现在是「文件搜索」页里的一张独立卡（v1.3.13 从右栏搬过来）：
                   切到该标签并滚到卡片再拍，排版挤坏（复选框换行、滑块标签重叠）只有靠图能看出来。 */
                try
                {
                    qv.ScrollToFileDialogBarForRender();
                    Pump(260);
                    Save(main, Path.Combine(outDir, "qbar-filedialog-settings.png"));
                    qv.SelectTabForRender("search");
                    Pump(120);
                }
                catch (Exception ex) { CoreLog.Warn("渲染文件对话框设置卡失败：" + ex.Message); }

                try
                {
                    qv.SelectTabForRender("files");
                    Pump(220);
                    qv.ScrollFilesToCardForRender(5);
                    Pump(240);
                    Save(main, Path.Combine(outDir, "qbar-suggestions-settings.png"));
                    qv.SelectTabForRender("search");
                    Pump(160);
                }
                catch (Exception ex) { CoreLog.Warn("渲染建议设置卡失败：" + ex.Message); }

                /* 展开后的文件搜索窗口：它只在按了展开按钮之后才存在，
                   平时连编译期都看不出来长啥样。这里用一个固定查询拍一张，
                   分面侧栏挤没挤坏、两列表对齐没有，全靠这张图。 */
                try
                {
                    var expand = new QbarExpandWindow(host, "report");
                    expand.WindowStartupLocation = WindowStartupLocation.Manual;
                    expand.Left = -32000;
                    expand.Top = -32000;
                    expand.ShowInTaskbar = false;
                    if (TryRenderSize(expand))
                    {
                        expand.Show();
                        Pump(700);          // 等一次防抖 + 查询回来
                        Save(expand, Path.Combine(outDir, "qbar-expand.png"));

                        /* 圆角、跟随系统明暗是 DWM 画的，RenderTargetBitmap 拍不到。
                           把窗口真摆到屏幕上再拷一张（几百毫秒），拍完挪回离屏。 */
                        expand.Left = 80;
                        expand.Top = 80;
                        Pump(500);
                        SaveOnScreen(expand, Path.Combine(outDir, "qbar-expand-onscreen.png"));
                        expand.Left = -32000;
                        expand.Top = -32000;
                        Pump(120);

                        /* 点表头排序是纯交互，截图看不出来 —— 切到"大小降序"再拍一张，
                           顺带验证表头箭头和数值序（不是字符串序）。 */
                        expand.SortForRender("size", true);
                        Pump(120);
                        Save(expand, Path.Combine(outDir, "qbar-expand-sorted.png"));
                        expand.Close();
                    }
                }
                catch (Exception ex) { CoreLog.Warn("渲染展开搜索窗口失败：" + ex.Message); }
            }

            /* 手势录制窗口：不单独拍一张的话，回归时根本看不到它长什么样。
             * 它是模态窗，这里用 Show() 而不是 ShowDialog()，拍完就关。 */
            if (page == "gestures")
            {
                try
                {
                    var dlg = new GestureRecordWindow
                    {
                        Owner = main,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 80,
                        Top = 60,
                    };
                    dlg.Show();
                    Pump(240);
                    Save(dlg, Path.Combine(outDir, "gesture-record.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染手势录制窗口失败：" + ex.Message);
                }
            }

            /* 「允许的触发方式」默认收起：不展开就没人验过这 61 条胶囊长什么样、
             * 三态颜色对不对，所以单独拍一张。 */
            if (page == "gestures" &&
                main.GetPage("gestures") is InputPlus.UI.Views.GesturesView gv)
            {
                try
                {
                    gv.SetTriggersExpandedForRender(true);
                    Pump(320);
                    Save(main, Path.Combine(outDir, "gestures-triggers.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染触发方式列表失败：" + ex.Message);
                }
                gv.SetTriggersExpandedForRender(false);
                Pump(120);
            }

            /* 外观 / 参数 / 开关快捷键都进了右上角齿轮开的「手势设置」窗：
             * 不单拍一张，回归时谁也看不到它长什么样。模态窗，用 Show() 拍完就关。 */
            if (page == "gestures")
            {
                try
                {
                    var dlg = new GestureSettingsWindow(host)
                    {
                        Owner = main,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 80,
                        Top = 40,
                    };
                    dlg.ExpandAllForRender();
                    dlg.Show();
                    Pump(320);
                    Save(dlg, Path.Combine(outDir, "gestures-prefs.png"));
                    /* 参数卡在最底下：滚到底再拍一张，免得只有外观那半截。 */
                    dlg.ScrollOptionsToEndForRender();
                    Pump(220);
                    Save(dlg, Path.Combine(outDir, "gestures-prefs-params.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染手势设置窗失败：" + ex.Message);
                }
            }

            /* 多步手势（v1.5.0）：步骤清单长什么样只能靠眼睛验，单独拍一张。 */
            if (page == "gestures" &&
                main.GetPage("gestures") is InputPlus.UI.Views.GesturesView gst)
            {
                try
                {
                    var lines = gst.AddStepsSampleForRender();
                    Pump(300);
                    Save(main, Path.Combine(outDir, "gestures-steps.png"));
                    foreach (string line in lines) CoreLog.Info("多步手势出图：" + line);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染多步手势失败：" + ex.Message);
                }
            }

            /* Lua 脚本窗口（v1.5.0）：左边脚本列表、右边代码与 ip.* 速查表。 */
            if (page == "gestures")
            {
                try
                {
                    if (host.Settings.Scripts.Count == 0)
                    {
                        host.Settings.Scripts.Add(new InputPlus.Core.Scripting.LuaScript
                        {
                            Id = "renderlua",
                            Name = "整理剪贴板",
                            Comment = "去掉首尾空白再写回去",
                            Code = InputPlus.Core.Scripting.LuaRunner.SampleCode,
                        });
                    }
                    var dlg = new LuaScriptsWindow(host)
                    {
                        Owner = main,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 70,
                        Top = 30,
                    };
                    dlg.Show();
                    Pump(260);
                    dlg.SelectFirstForRender();
                    Pump(200);
                    Save(dlg, Path.Combine(outDir, "lua-scripts.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染 Lua 脚本窗口失败：" + ex.Message);
                }
            }

            /* 手势菜单管理窗（v1.5.0）：三栏 —— 菜单 / 条目 / 条目详情。 */
            if (page == "gestures")
            {
                try
                {
                    if (host.Settings.GestureMenus.Count == 0)
                    {
                        var sample = new InputPlus.Core.Scripting.GestureMenuDef
                        {
                            Id = "rendermenu",
                            Name = "窗口菜单",
                            Hint = "选一个窗口操作",
                        };
                        sample.Items.Add(new InputPlus.Core.Scripting.GestureMenuItem { Title = "最小化", Action = "winMinimize" });
                        sample.Items.Add(new InputPlus.Core.Scripting.GestureMenuItem { Title = "最大化", Action = "winMaximize" });
                        sample.Items.Add(new InputPlus.Core.Scripting.GestureMenuItem { Title = "关闭窗口", Action = "winClose" });
                        host.Settings.GestureMenus.Add(sample);
                    }
                    var dlg = new GestureMenusWindow(host)
                    {
                        Owner = main,
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 70,
                        Top = 30,
                    };
                    dlg.Show();
                    Pump(260);
                    dlg.SelectFirstForRender();
                    Pump(200);
                    Save(dlg, Path.Combine(outDir, "gesture-menus.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染手势菜单管理窗失败：" + ex.Message);
                }
            }

            /* 手势菜单弹层（v1.5.0）：鼠标旁边那一列，1..9 快选。 */
            if (page == "gestures" && host.Settings.GestureMenus.Count > 0)
            {
                try
                {
                    var dlg = new GestureMenuWindow(host.Settings.GestureMenus[0], InputPlus.Core.Actions.ActionCatalog.TitleOf)
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 120,
                        Top = 90,
                    };
                    dlg.Show();
                    Pump(240);
                    Save(dlg, Path.Combine(outDir, "gesture-menu-popup.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染手势菜单弹层失败：" + ex.Message);
                }
            }

            /* 「已禁用」到底看不看得出来，只能靠眼睛：拍一张第一个应用程序被禁用后的样子。 */
            if (page == "gestures" &&
                main.GetPage("gestures") is InputPlus.UI.Views.GesturesView gd)
            {
                try
                {
                    if (gd.SetFirstAppDisabledForRender(true))
                    {
                        Pump(240);
                        Save(main, Path.Combine(outDir, "gestures-disabled.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染禁用态失败：" + ex.Message);
                }
                try { gd.SetFirstAppDisabledForRender(false); } catch { /* 预期内：收尾 */ }
                Pump(120);
            }

            /* 列表排序：四档不只是"下拉里有字"，还得看行序真的变了、手柄只在自定义顺序里出现。
             * 下拉本身弹窗截不到，所以同时写一份 txt 记录行数/手柄数/前四行。 */
            if (page == "gestures" &&
                Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_SORT") == "1" &&
                main.GetPage("gestures") is InputPlus.UI.Views.GesturesView gs)
            {
                try
                {
                    var lines = new List<string>();
                    lines.AddRange(gs.SetSortForRender(InputPlus.Core.Gestures.GestureListOrder.Custom));
                    Pump(200);
                    Save(main, Path.Combine(outDir, "gestures-sort-custom.png"));
                    lines.Add("");
                    lines.AddRange(gs.SetSortForRender(InputPlus.Core.Gestures.GestureListOrder.ByName));
                    Pump(200);
                    Save(main, Path.Combine(outDir, "gestures-sort-name.png"));
                    lines.Add("");
                    lines.AddRange(gs.SetSortForRender(InputPlus.Core.Gestures.GestureListOrder.ByTrigger));
                    lines.Add("");
                    lines.AddRange(gs.SetSortForRender(InputPlus.Core.Gestures.GestureListOrder.ByAction));
                    File.WriteAllLines(Path.Combine(outDir, "gestures-sort.txt"), lines);
                    gs.SetSortForRender(InputPlus.Core.Gestures.GestureListOrder.Custom);
                    Pump(120);
                }
                catch (Exception ex) { CoreLog.Warn("渲染手势排序失败：" + ex.Message); }
            }

            /* 「（旧功能，建议换掉）」这类补偿文案已经删掉了，改由启动时的迁移
             * 换成正式动作。这里拍一条原本会撞上它的手势（runProgram），
             * 确认分类自己跳到「系统功能」、功能显示「运行命令」、
             * 参数框出现，并且下拉里一个"旧功能"字样都没有。
             * 下拉列表本身弹窗截不到，所以同时写一份 txt 记录列表内容。 */
            if (page == "gestures" &&
                Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_LEGACY") == "1" &&
                main.GetPage("gestures") is InputPlus.UI.Views.GesturesView gl)
            {
                try
                {
                    var lines = new List<string>();
                    lines.AddRange(gl.SelectActionForRender("runProgram"));
                    Pump(320);
                    Save(main, Path.Combine(outDir, "gestures-action-runprogram.png"));

                    /* 迁移过没有：老配置里的「打开网址」用的是 openUrl，
                     * 启动迁移后应当变成 webPage。找不到就说明这一步没跑。 */
                    lines.Add("");
                    lines.Add("—— 老动作迁移 ——");
                    lines.AddRange(gl.SelectActionForRender("webPage"));
                    var legacyLeft = gl.SelectActionForRender("openUrl");
                    lines.Add("openUrl 还有没有：" + (legacyLeft.Length == 1 ? "没有（已迁移）" : "还有（没迁移）"));
                    Pump(160);
                    Save(main, Path.Combine(outDir, "gestures-action-migrated.png"));
                    File.WriteAllLines(Path.Combine(outDir, "gestures-action-runprogram.txt"), lines);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染动作下拉失败：" + ex.Message);
                }
            }

            /* 使用指南默认折叠，展开后「⑪ 鼠标手势」一节也在几百像素以外。
             * 不单独滚过去截一张，那 16 条参数说明（起始超时、匹配容差、
             * 起手距离、换向系数…）就永远没人用眼睛验过排版。 */
            if (page == "help" &&
                main.GetPage("help") is InputPlus.UI.Views.HelpView hv)
            {
                try
                {
                    hv.ShowGestureGuideForRender();
                    Pump(260);
                    Save(main, Path.Combine(outDir, "help-gesture-guide.png"));
                    /* 一屏只放得下九条：作用目标四类、61 条触发方式、特殊目标这些
                     * 新写的都在后半段，滚到尾巴再拍一张。 */
                    hv.ShowGestureGuideTailForRender();
                    Pump(200);
                    Save(main, Path.Combine(outDir, "help-gesture-guide-tail.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染使用指南手势章节失败：" + ex.Message);
                }
                hv.ResetGuideForRender();
                Pump(120);
            }

            /* 新写的 ⑫ 设备方案 / ⑰ 命令行两节也各截一张：
             * 指南越长越容易在某处排版崩掉（尤其命令行那段等宽块）。 */
            if (page == "help" &&
                main.GetPage("help") is InputPlus.UI.Views.HelpView hv3)
            {
                foreach (var (key, file) in new[]
                         {
                             ("devices", "help-guide-devices.png"),
                             ("cli", "help-guide-cli.png"),
                         })
                {
                    try
                    {
                        hv3.ShowGuideSectionForRender(key);
                        Pump(220);
                        Save(main, Path.Combine(outDir, file));
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("渲染帮助页小节失败（" + key + "）：" + ex.Message);
                    }
                }
                hv3.ResetGuideForRender();
                Pump(120);
            }

            /* 全部功能一览：帮助页承诺"所有功能"，得有一张图证明它真列全了。 */
            if (page == "help" &&
                main.GetPage("help") is InputPlus.UI.Views.HelpView hv2)
            {
                try
                {
                    hv2.ShowActionCatalogForRender();
                    Pump(260);
                    Save(main, Path.Combine(outDir, "help-action-catalog.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染全部功能一览失败：" + ex.Message);
                }
                hv2.ResetActionCatalogForRender();
                Pump(120);
            }

            /* 额外截一张"窗口绑定面板"的样子。
             *
             * 这个面板只有选中「窗口绑定」动作时才出现，普通 keys.png 里看不到，
             * 所以单独渲染一张，用于确认它真的画出来了、布局没崩。 */
            if (page == "keys" &&
                main.GetPage("keys") is InputPlus.UI.Views.KeysView kv)
            {
                /* 键盘档位先出图：这时页面的分类/选中键都还是初始状态，
                 * 图里不会残留上一个面板（比如「自定义按键」）的内容。
                 * 每张图都把窗口设成**该档位的最小尺寸**，所以看到的就是
                 * 用户打开这个页面时的第一眼。 */
                RenderKeyboardSizes(main, kv, outDir);

                try
                {
                    if (kv.ShowWinBindPanelForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-winbind.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染窗口绑定面板失败：" + ex.Message);
                }
                kv.ResetRenderState();
                Pump(120);

                // 同理：直达链接面板（目标 + 自动识别 + 备注）也只有选那个分类才出现。
                // 一次性把三种识别结果都拍下来：网址 / 文件夹 / 文件。
                try
                {
                    if (kv.ShowWebPagePanelForRender())
                    {
                        /* 编辑区在键盘下方，800 高的窗口里它在折叠线以下。
                         * 不滚过去的话，这三张图拍到的全是键盘 —— 徽标和提示
                         * 一个像素都看不见，等于没验。 */
                        kv.ScrollToEditorForRender();
                        Pump(200);

                        /* 网址：填完立刻拍（不等 700ms 的标题抓取防抖），
                         * 免得截图里那行状态是"正在获取网页标题…"。 */
                        kv.FillLinkTargetForRender("https://github.com/QQQOrange/Input-Plus");
                        Pump(300);
                        Save(main, Path.Combine(outDir, "keys-webpage.png"));

                        /* 文件夹 / 文件：用 C:\Windows 这类任何机器上都存在的路径，
                         * 徽标才会是"文件夹存在"而不是"路径不存在"，
                         * 截图不依赖谁在哪台机器上渲染。 */
                        kv.FillLinkTargetForRender(@"C:\Windows");
                        Pump(260);
                        Save(main, Path.Combine(outDir, "keys-link-folder.png"));

                        kv.FillLinkTargetForRender(@"C:\Windows\notepad.exe");
                        Pump(260);
                        Save(main, Path.Combine(outDir, "keys-link-file.png"));

                        /* 命令行（v1.4.2）：徽标该是「命令」、说明该说参数原样传、
                         * 「浏览…」按钮该消失 —— 这张图就是这三件事的证据。 */
                        kv.FillLinkTargetForRender(@"notepad.exe D:\笔记.txt");
                        Pump(260);
                        Save(main, Path.Combine(outDir, "keys-command.png"));

                        /* 回归：真的按一次「确定绑定」（环境变量打开才跑）。
                         *
                         * 这一步会真的改掉"渲染进程"那份数据目录里的绑定，
                         * 所以默认不跑 —— 截图是观察，不该顺手改配置。
                         * 要跑：INPUTPLUS_RENDER_LINK_CONFIRM=1。 */
                        if (Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_LINK_CONFIRM") == "1")
                        {
                            kv.FillLinkTargetForRender("https://github.com/QQQOrange/Input-Plus");
                            Pump(200);
                            var (status, combo, action, arg) = kv.ConfirmForTest();
                            File.WriteAllLines(Path.Combine(outDir, "keys-link-confirm.txt"), new[]
                            {
                                "combo=" + combo,
                                "action=" + action,
                                "arg=" + arg,
                                "status=" + status,
                            });
                            Pump(240);
                            Save(main, Path.Combine(outDir, "keys-link-confirm.png"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染直达网页面板失败：" + ex.Message);
                }
                kv.ResetRenderState();
                Pump(120);

                // 智能直达（系统 → 直达链接）也要能截到，确认它真在功能下拉里
                try
                {
                    if (kv.ShowSmartOpenForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-smartopen.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染智能直达失败：" + ex.Message);
                }
                // 自定义按键面板：确认"键 / 应用 / 两个开关 / 提示"都画出来了
                try
                {
                    if (kv.ShowCustomKeyForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "keys-customkey.png"));

                        /* 编辑区在键盘**下方**，窗口只有 800 高时它在折叠线以下。
                         * 滚过去再拍一张，才能确认面板里的键 / 应用 / 开关
                         * 都真的画出来了，而不是被折叠线挡住、看起来"没做"。 */
                        kv.ScrollToEditorForRender();
                        Pump(220);
                        Save(main, Path.Combine(outDir, "keys-editor.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染自定义按键失败：" + ex.Message);
                }

                kv.ResetRenderState();
                Pump(120);

                /* 设备胶囊的弹层。Popup 不进主窗口截图，只能单独截；
                 * 这张图同时也是"设备清单真的填进去了"的回归 —— 一台设备都没有时，
                 * 弹层里只剩那行「通用设置」，图上一眼就能看出来。 */
                if (kv.OpenDeviceScopeForRender() is { } scope)
                {
                    Pump(250);
                    try
                    {
                        SaveElement(scope, Path.Combine(outDir, "keys-device-scope.png"));
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("渲染设备弹层失败：" + ex.Message);
                    }
                    finally
                    {
                        kv.CloseDeviceScopeForRender();
                        Pump(80);
                    }
                }

                /* 「设备备注」：系统里五条同名设备，只能靠备注分清。
                 *
                 * 备注按钮只在选中某台设备后出现，所以先切到第一台真设备再开弹层。 */
                try
                {
                    var first = InputPlus.Core.Input.InputDevices.All().FirstOrDefault(d => d.Key.Length > 0);
                    if (first != null && kv.OpenAliasForRender(first.Key) is { } aliasCard)
                    {
                        Pump(250);
                        SaveElement(aliasCard, Path.Combine(outDir, "keys-device-alias.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染备注弹层失败：" + ex.Message);
                }
                finally
                {
                    try { kv.CloseAliasForRender(); Pump(80); } catch { /* 预期内：收尾 */ }
                }

                /* 「编辑绑定」搜索框的结果弹层。
                 *
                 * 它是个 Popup：独立的顶层视觉，**不进主窗口截图**，所以它到底
                 * 弹在哪儿从来没被任何一张图验证过 —— 用户报过"结果跑到窗口
                 * 左上角"，而截图里一切正常，找不到证据。
                 * 这里把它单独拍一张（看内容有没有画出来），同时把相对搜索框的
                 * 偏移量写成 txt（看位置对不对）。位置对不上时，双击那个 txt
                 * 就能看出来，不用再靠用户描述。 */
                try
                {
                    /* 量位置必须在**屏幕上**的窗口里量：整个渲染窗口默认放在
                     * (-32000,-32000)，Popup 在那里没有"下面还有多少空间"可算，
                     * 量出来的偏移是假的。所以量之前把窗口挪到屏幕里，
                     * 量完立刻挪回去，正常出图不受影响。 */
                    main.Left = 120;
                    main.Top = 120;
                    Pump(160);
                    if (kv.OpenBindingSearchForRender("a") is { } hits)
                    {
                        Pump(220);
                        SaveElement(hits, Path.Combine(outDir, "keys-binding-search.png"));
                        File.WriteAllText(Path.Combine(outDir, "keys-binding-search-placement.txt"),
                            kv.DescribeBindingSearchPlacement() + "\n" + kv.DescribeBindingSearchField()
                            + "\n" + kv.DescribeDegeneratePlacement());
                    }
                    else
                    {
                        File.WriteAllText(Path.Combine(outDir, "keys-binding-search-placement.txt"),
                            "没有命中：当前层没有可搜的键，也没有可搜的功能");
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染搜索弹层失败：" + ex.Message);
                }
                finally
                {
                    try { kv.CloseBindingSearchForRender(); Pump(60); } catch { /* 预期内：收尾 */ }
                    try { main.Left = -32000; main.Top = -32000; Pump(80); } catch { /* 预期内：收尾 */ }
                }

                /* 「找回丢失的别名」：只有真丢过条目才弹出来的窗口，
                 * 平时谁也没见过它长什么样。用假数据渲染一张，保证列表、来源、按钮都对齐。 */
                try
                {
                    var fake = new InputPlus.Core.Settings.SnippetRecovery.ScanResult(
                        new List<InputPlus.Core.Settings.SnippetRecovery.Candidate>
                        {
                            new("、mail", "someone@example.com", "工作", "settings.json.bak", DateTime.Now.AddDays(-2)),
                            new("、tel", "13800000000", "工作", "settings-20261112.json", DateTime.Now.AddDays(-4)),
                            new(";addr", "广东省潮州市湘桥区…", "地址", "settings.json.2026-11-14", DateTime.Now.AddDays(-6)),
                        }, 7, 1);
                    var recoverDlg = InputPlus.UI.Windows.SnippetRecoverWindow.CreateForRender(fake);
                    if (recoverDlg.Content is FrameworkElement recoverCard)
                    {
                        recoverCard.Measure(new Size(516, double.PositiveInfinity));
                        recoverCard.Arrange(new Rect(0, 0, 516, recoverCard.DesiredSize.Height));
                        recoverCard.UpdateLayout();
                        Pump(120);
                        SaveElement(recoverCard, Path.Combine(outDir, "hotstrings-recover.png"));
                    }
                    recoverDlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染找回别名窗口失败：" + ex.Message);
                }

                /* 「原键替换」层：不需要触发键的一层。
                 *
                 * 它是伪层（Id=0，不在 Settings.Layers 里），最容易坏的地方是
                 * 下拉项、层名、提示语、计数这四处各说各话 —— 全都在这张图里。 */
                try
                {
                    kv.SelectLayerForRender(InputPlus.UI.Views.KeysView.ReplaceLayerId);
                    Pump(160);
                    Save(main, Path.Combine(outDir, "keys-replace-layer.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染原键替换层失败：" + ex.Message);
                }
                finally
                {
                    try { kv.SelectLayerForRender(1); Pump(100); } catch { /* 预期内：恢复默认层 */ }
                }

                /* 「恢复默认」的二次确认框。
                 *
                 * 这是整页唯一的破坏性按钮，必须能被截图回归：警告图标、
                 * 红色确定键、逐条写清"丢什么 / 留什么 / 能不能撤销"。
                 * 平时只有点按钮才会出现，而且 ShowDialog 是模态阻塞的 ——
                 * 这里用 CreateForRender 造实例、摆到屏幕外再截，绝不真弹出来。 */
                try
                {
                    var dlg = AppDialog.CreateForRender(main,
                        "把基础层的按键全部恢复成默认值？\n"
                        + "· 你在这一层改过的绑定会被覆盖（功能、参数、网址、文本、程序、备注）；\n"
                        + "· 你自己新增的组合键会保留，其它 9 层完全不动；\n"
                        + "· 这一步不能撤销。",
                        "恢复默认按键", DialogKind.Warn,
                        showCancel: true, okText: "恢复默认", cancelText: null);
                    dlg.WindowStartupLocation = WindowStartupLocation.Manual;
                    dlg.Left = -32000;
                    dlg.Top = -32000;
                    dlg.ShowInTaskbar = false;
                    dlg.Show();
                    Pump(220);
                    Save(dlg, Path.Combine(outDir, "keys-reset-confirm.png"));
                    dlg.Close();
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染恢复默认确认框失败：" + ex.Message);
                }
            }

            /* 宏页额外截一张"正在录制"的样子。
             *
             * 实时列表只在录制时出现，平时截不到 —— 而它恰恰是这次改动的重点，
             * 必须能被截图回归确认（按钮变红、状态点呼吸、按键一行行往下滚）。 */
            if (page == "macros" &&
                main.GetPage("macros") is InputPlus.UI.Views.MacrosView mv)
            {
                try
                {
                    if (mv.ShowRecordingForRender())
                    {
                        Pump(200);
                        Save(main, Path.Combine(outDir, "macros-recording.png"));
                    }
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染录制状态失败：" + ex.Message);
                }
                mv.ResetRenderState();
                Pump(120);
            }

            /* 备份页额外截一张恢复对话框。
             *
             * 它是本次新增的交互（先拉清单、再选覆盖/合并），
             * 平时只有点「从备份恢复…」才出现，必须能被截图回归。 */
            if (page == "backup")
            {
                try
                {
                    var rw = new RestoreWindow(host)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -32000,
                        Top = -32000,
                        ShowInTaskbar = false,
                        /* 截图时去掉标题栏：带标题栏的窗口里，
                         * 内容相对窗口有一个"标题栏高度"的视觉偏移，
                         * 直接对内容做 RenderTargetBitmap 会整体下移、
                         * 右下角被切掉一截（截出来顶部还有一条黑边）。 */
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                    };
                    rw.Show();
                    Pump(320);
                    CoreLog.Info($"[渲染] 恢复对话框 Window={rw.Width}x{rw.Height} "
                                 + $"Actual={rw.ActualWidth}x{rw.ActualHeight} "
                                 + $"Content={((FrameworkElement)rw.Content).ActualWidth}"
                                 + $"x{((FrameworkElement)rw.Content).ActualHeight}");
                    Save(rw, Path.Combine(outDir, "restore.png"));
                    rw.Close();
                    Pump(80);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染恢复对话框失败：" + ex.Message);
                }
            }

            /* 设置页的「外观」区：主题、键盘尺寸、界面语言都在这一区，
             * 而它默认正好在折叠线以下 —— 不滚过去截一张，
             * "语言下拉里到底显示了什么"就没人看得见
             * （这个下拉曾经渲染成 `LocLanguage { … }`，全靠这一张才发现）。 */
            if (page == "settings" &&
                main.GetPage("settings") is InputPlus.UI.Views.SettingsView sv)
            {
                try
                {
                    sv.ScrollToAppearanceForRender();
                    Pump(240);
                    Save(main, Path.Combine(outDir, "settings-appearance.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染设置页外观区失败：" + ex.Message);
                }
                /* 「提示条」（v1.6.3，原名「通知样式」）：紧跟在外观卡后面的那张卡，
                 * 默认落在折叠线以下 —— 它是「所有提示共用一个外观」的唯一入口，
                 * 排版必须有人看见过。 */
                try
                {
                    sv.ScrollToNotificationsForRender();
                    Pump(240);
                    Save(main, Path.Combine(outDir, "settings-notifications.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染设置页提示条失败：" + ex.Message);
                }
                Pump(120);

                // 「使用统计 → 查看明细」：默认收起，不展开就没人看见过它的排版
                try
                {
                    sv.ShowUsageDetailForRender();
                    Pump(240);
                    Save(main, Path.Combine(outDir, "settings-usage.png"));
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("渲染使用统计明细失败：" + ex.Message);
                }
                Pump(120);

                sv.ResetRenderStateForRender();
                Pump(120);
            }

            // 诊断文件只在显式要求时输出（--render-diag <目录>），
            // 免得把一堆中间产物混进 docs/screenshots。
            if (DiagnosticsRequested)
            {
                try
                {
                    DumpTextBoxMetrics(main, Path.Combine(outDir, $"metrics-{page}.txt"), page);
                    DumpUntranslatedAudit(main, Path.Combine(outDir, $"untranslated-{page}.txt"), page);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn($"输出 {page} 输入框诊断失败：" + ex.Message);
                }
            }
            count++;
        }

        /* 全局搜索面板（Ctrl+K）。
         *
         * 模态浮层，页面截图里永远没有它；但它现在的角色是「整个软件都从这里找」，
         * 「Kind 徽章 + 标题 + 明细」这套三栏排版值得一张固定的图。 */
        try
        {
            var paletteHits = Core.Search.GlobalSearch.Build(host.Settings, Loc.T, host.Actions.Items);
            var palette = SearchPaletteWindow.ForRender(paletteHits, "手势");
            palette.Show();
            Pump(260);
            palette.UpdateLayout();
            Save(palette, Path.Combine(outDir, "search-palette.png"));
            palette.Close();
            count++;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("渲染全局搜索面板失败：" + ex.Message);
        }

        /* 右键菜单样式（`CLP_RENDER_MENU=1` 时额外出一张）。
         * 菜单是 Popup，主窗口截图里没有它，所以单独渲染。 */
        if (Environment.GetEnvironmentVariable("CLP_RENDER_MENU") == "1")
        {
            main.Navigate("hotstrings");
            Pump(200);
            RenderMenuShot(outDir);
        }

        /* 手势轨迹出图（`INPUTPLUS_RENDER_TRAIL=1`）。
         *
         * 轨迹由覆盖层窗口自己画，页面截图里永远没有它：切角、描边、
         * 阴影这些只有眼睛能验。这里直接把 GestureOverlayWindow 当画布，
         * 按真实采样节奏（20px 一个点）喂一段折角，再渲染成 PNG。 */
        if (Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_TRAIL") == "1")
        {
            try
            {
                RenderTrailShots(host, outDir);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("渲染手势轨迹失败：" + ex.Message);
            }
        }

        main.Close();

        /* 亮度横条。
         *
         * 只出亮度这一张：音量已经交给系统音量键，Windows 自己弹它的横条，
         * 我们不再画第二条（v4.3.0 删掉了自绘音量横条）。
         * 这张图是自绘浮层，"长什么样、出现在哪"必须能一眼看到。 */
        foreach (var (file, pct) in new[]
                 {
                     ("osd-brightness.png", 40),
                     ("osd-p00.png", 0),
                     ("osd-p100.png", 100),
                 })
        {
            var osd = OsdWindow.CreateDetached();
            osd.ShowInTaskbar = false;
            osd.WindowStartupLocation = WindowStartupLocation.Manual;
            osd.Left = -32000;
            osd.Top = -32000;
            osd.Show();
            osd.PrepareForRender(pct);
            Pump(160);
            osd.UpdateLayout();
            Save(osd, Path.Combine(outDir, file));
            osd.Close();
            count++;
        }

        // Qbar 面板（深色浮层，单独渲染）
        var qbar = new QbarWindow(host) { ShowInTaskbar = false, Left = -32000, Top = -32000 };
        qbar.Open("bd 天气");
        qbar.ParkOffScreenForRender();
        Pump(300);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-panel.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(qbar, Path.Combine(outDir, "metrics-win-qbar-panel.txt"), "Qbar 面板");
            DumpUntranslatedAudit(qbar, Path.Combine(outDir, "untranslated-win-qbar-panel.txt"), "Qbar 面板");
        }
        count++;

        /* 空态建议：这是「搜索前建议」唯一的视觉证据 —— 分组标题、徽标、
           行高、虚拟化列表的空隙都在这张图里。
           采集走后台线程，这里最多等 3 秒；等不到就渲染空列表，
           图仍然有效（只是不能当验收证据），不会把 --render 卡死。 */
        if (host.Suggestions != null)
        {
            try
            {
                host.Suggestions.Start();
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!host.Suggestions.IsWarm && DateTime.UtcNow < deadline)
                    System.Threading.Thread.Sleep(40);

                qbar.Open("");
                qbar.ParkOffScreenForRender();
                Pump(320);
                qbar.Height = qbar.ActualHeight;
                qbar.UpdateLayout();
                Pump(120);
                Save(qbar, Path.Combine(outDir, "qbar-empty-suggestions.png"));
                count++;
            }
            catch (Exception ex) { CoreLog.Warn("渲染空态建议失败：" + ex.Message); }
        }

        // 标签形态：输入 "bd " 之后 bd 变成标签，输入框只剩搜索词
        qbar.OpenForRender("bd");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip.png"));
        count++;

        /* 引擎预填直达：走**生产路径** Open("zh ")，
         * 验证 qbar:<key> 动作绑到按键后，弹出的面板真的已经带上引擎标签
         * （而不是只把 "zh " 当普通文字塞进输入框）。 */
        qbar.Open("zh ");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-engine-prefill.png"));
        count++;

        // 标签 + 已输入搜索词
        qbar.OpenForRender("bd", "天气 预报");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-typing.png"));
        count++;

        /* 查词：ol 标签 + 词，走的是和用户完全一样的路径（TryStartDictionary）。
           联网结果不固定，但"有没有释义行 / 排版会不会挤爆"这张图能看出来；
           渲染时网络不通就只会显示一行错误文案，同样是有效信息。 */
        qbar.OpenForRender("ol", "report");
        qbar.ParkOffScreenForRender();
        Pump(2600);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-dict.png"));
        count++;

        /* 中文词另出一张：中文释义比英文长，最容易把行高/静默截断的问题暴露出来；
           顺带验证「银行」这种简体词走的是有道那条路（萌典只有繁体）。 */
        /* 不带标签的查词：直接把整句打进去（用户不打空格就不会生成 chip）。
           这条路径以前是死的，现在应该同样出释义；出一张图钉住它。 */
        qbar.OpenForRenderQuery("ol hello");
        qbar.ParkOffScreenForRender();
        Pump(2400);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-dict-nolabel.png"));
        count++;

        /* 动作触发的那条路（「Qbar 搜索」分类的 qdict.lookup → OpenDictionary）：
           与上面 OpenForRender("ol", …) 不是同一段代码 —— 前者要自己把
           "命令关键字"变成 chip，关键字还可能被用户改过名。出一张图钉住它。 */
        qbar.OpenDictionary("hello");
        qbar.ParkOffScreenForRender();
        Pump(2600);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-dict-action.png"));
        count++;

        /* 命令行：首词命中命令关键字时，动作行与普通搜索结果必须并列出现（不许互相吃掉）。
           这里用 cmd 而不是 ol：cmd 走的是「外部程序 + {current_folder}」那条模板，
           参数展开对不对一眼能看出来；ol 是内部动作，验证不到模板那一层。 */
        qbar.OpenForRenderQuery("cmd dir");
        qbar.ParkOffScreenForRender();
        Pump(700);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-command.png"));
        count++;

        /* 过滤器 chip：输入 doc 直接按空格就该变成左侧的 chip 并把结果筛成文档。
           以前只有 "doc:" 加点的写法，这条新路没有图就没人能确认它真的生效。 */
        qbar.OpenForRenderFilter("doc", "报表");
        qbar.ParkOffScreenForRender();
        Pump(600);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-filter-chip.png"));
        count++;

        /* 绑定按键那条路：动作 qfilter:<Id> 直接把面板打开并挂好 chip。
           和上一张的区别只看一件事 —— 不用用户打关键字，chip 就已经在框外了。 */
        qbar.OpenFileSearch("builtin.doc", "报表");
        qbar.ParkOffScreenForRender();
        Pump(600);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-filter-action.png"));
        count++;

        qbar.OpenForRender("ol", "银行");
        qbar.ParkOffScreenForRender();
        Pump(2600);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-dict-cn.png"));
        count++;

        // 网址关键字：不建标签，整条 URL 直接进输入框
        qbar.OpenForRender("bili");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-web-url.png"));
        count++;

        // 程序标签（自动取 exe 图标）
        qbar.OpenForRender("calc");
        qbar.ParkOffScreenForRender();
        Pump(260);
        qbar.Height = qbar.ActualHeight;
        qbar.UpdateLayout();
        Pump(120);
        Save(qbar, Path.Combine(outDir, "qbar-chip-run.png"));
        count++;

        /* 这两个窗口的 XAML 写的是 WindowStartupLocation=CenterScreen，
         * 它会**盖过**手动设的 Left/Top —— 不显式改成 Manual 的话，
         * 后台渲染时它们会正正地弹在用户屏幕中央。 */
        /* 首次运行引导：全程序唯一"第一次打开就会看到"的窗口，
         * 却一直没进过出图流程（改坏了也没人发现）。 */
        var onboarding = new OnboardingWindow(host)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        onboarding.Show();
        Pump(200);
        Save(onboarding, Path.Combine(outDir, "onboarding.png"));
        onboarding.Close();

        var math = new MathBoardWindow
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        math.Show();
        Pump(200);
        Save(math, Path.Combine(outDir, "mathboard.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(math, Path.Combine(outDir, "metrics-win-mathboard.txt"), "计算板");
            DumpUntranslatedAudit(math, Path.Combine(outDir, "untranslated-win-mathboard.txt"), "计算板");
        }
        count++;
        math.Close();

        // 剪贴板历史
        // 剪贴板历史（同样有 CenterScreen，必须改 Manual 才不会弹在屏幕中央）
        var clip = new ClipboardHistoryWindow(host)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        clip.Show();
        Pump(240);
        /* ⚠ 不能叫 clipboard.png：上面那个循环已经用页面名生成过 clipboard.png（剪贴板设置页），
         * 同名会把它**覆盖**成这个历史窗口 —— 于是 docs 里的"剪贴板"截图看到的
         * 其实是这张浮窗，页面本身从来没进过文档。 */
        Save(clip, Path.Combine(outDir, "clipboard-history.png"));
        if (DiagnosticsRequested)
        {
            DumpTextBoxMetrics(clip, Path.Combine(outDir, "metrics-win-clipboard.txt"), "剪贴板历史");
            DumpUntranslatedAudit(clip, Path.Combine(outDir, "untranslated-win-clipboard.txt"), "剪贴板历史");
        }
        count++;
        clip.Close();

        // 十字准星点选器：展示“点选窗口”时的遮罩界面。
        // ⚠ 这个窗口是 Topmost + 铺满虚拟屏的，正常显示会盖住用户整个桌面；
        //   出图必须走 renderOffScreen，只截内容、不打扰用户。
        var picker = new WindowPickerWindow(renderOffScreen: true) { ShowInTaskbar = false };
        picker.Show();
        Pump(260);
        Save(picker, Path.Combine(outDir, "window-picker.png"));
        count++;
        picker.Close();

        CoreLog.Info($"离屏渲染完成：{count} 张 → {outDir}");
        return 0;
    }

    /// <summary>
    /// 「英文界面中文残留」审计：把视觉树里**当前真正显示出来**的文案全抓出来，
    /// 挑出还含汉字的，连同它来自哪个控件一起写成清单。
    ///
    /// 为什么要这个：语言包齐不齐自检能查（键都在、占位符都对），
    /// 但"某处文案到底有没有走翻译出口"自检查不出来 —— 只有把界面搭起来、
    /// 把每个 TextBlock / Content / ToolTip 的实际文本抓一遍才看得见。
    /// 逐张看截图也行，但 30 多张图靠眼睛扫一定会漏。
    /// </summary>
    public static void DumpUntranslatedAudit(DependencyObject root, string outPath, string page = "")
    {
        var hits = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        WalkAll(root, node =>
        {
            /* 顺序有讲究：HeaderedContentControl 本身就是 ContentControl，
             * 先匹配 ContentControl 的话 Header 永远取不到。
             * 占位文字不用单独取 —— 它是模板里的 TextBlock，走 TextBlock 那条就抓到了。 */
            string? text = node switch
            {
                System.Windows.Controls.TextBlock tb => tb.Text,
                System.Windows.Controls.TextBox box => box.Text,
                System.Windows.Controls.HeaderedContentControl hc => (hc.Content as string) ?? (hc.Header as string),
                System.Windows.Controls.HeaderedItemsControl hi => hi.Header as string,   // MenuItem / TabItem
                System.Windows.Controls.ContentControl cc => cc.Content as string,
                _ => null,
            };
            Collect(text, node);

            if (node is FrameworkElement fe) Collect(fe.ToolTip as string, node);
            if (node is Window win) Collect(win.Title, node);
        });

        WriteAudit(page);

        void Collect(string? s, DependencyObject node)
        {
            if (string.IsNullOrWhiteSpace(s) || !HasCjk(s)) return;
            string key = s!;
            if (!seen.Add(key)) return;   // 同一句只报一次，避免长列表刷屏
            hits.Add($"{node.GetType().Name,-22} {(node as FrameworkElement)?.Name ?? "",-20} {key.Replace("\n", " ⏎ ")}");
        }

        void WriteAudit(string pg)
        {
            var all = new List<string> { $"未翻译文案审计（页面：{pg}，语言：{Core.Localization.Loc.Language}）" };
            all.Add(hits.Count == 0 ? "  没有发现中文残留 ✓" : $"  发现 {hits.Count} 条：");
            all.AddRange(hits.Select(r => "  " + r));
            File.WriteAllLines(outPath, all);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex CjkText =
        new(@"[\u4e00-\u9fff]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool HasCjk(string s) => CjkText.IsMatch(s);

    private static void WalkAll(DependencyObject node, Action<DependencyObject> visit)
    {
        visit(node);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) WalkAll(VisualTreeHelper.GetChild(node, i), visit);
    }

    /// <summary>
    /// 诊断：dump 每个 TextBox 的真实布局，包括文字实际画在哪里。
    ///
    /// 只比较"控件高度"和"字号"不够——真正决定会不会被裁的，
    /// 是文字块相对内容区的位置。这里直接算出来，才能判断是顶到上边还是超出下边。
    /// </summary>
    public static void DumpTextBoxMetrics(System.Windows.Media.Visual root, string outPath, string page = "")
    {
        var lines = new List<string>();
        lines.Add($"TextBox 布局诊断（页面：{page}）");
        lines.Add($"DPI scale = {VisualTreeHelper.GetDpi(root).DpiScaleX}");
        lines.Add("");
        lines.Add("名称            控件高  内容顶  内容底  文字底  文字高   上余   下余  判定");

        Walk(root, tb =>
        {
            if (tb.ActualHeight <= 0) return;

            double contentTop = tb.BorderThickness.Top + tb.Padding.Top;
            double contentBottom = tb.ActualHeight - tb.BorderThickness.Bottom - tb.Padding.Bottom;

            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var ft = new FormattedText(
                string.IsNullOrEmpty(tb.Text) ? "Ag中qg" : tb.Text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);
            double textH = ft.Height;

            // 内容区垂直居中时文字块的位置
            double avail = contentBottom - contentTop;
            double textTop = contentTop + Math.Max(0, (avail - textH) / 2);
            double textBottom = textTop + textH;
            double topGap = textTop - contentTop;
            double bottomGap = contentBottom - textBottom;

            bool clipped = textBottom > contentBottom + 0.5 || textH > avail + 0.5;
            string verdict = clipped ? "裁切 <--" : (bottomGap < 1.5 ? "贴边" : "正常");

            lines.Add($"{tb.Name,-14} {tb.ActualHeight,6:0.0} {contentTop,7:0.0} {contentBottom,7:0.0} " +
                      $"{textBottom,7:0.0} {textH,7:0.0} {topGap,6:0.0} {bottomGap,6:0.0}  {verdict}");
        });

        File.WriteAllLines(outPath, lines);
    }

    private static void Walk(DependencyObject node, Action<System.Windows.Controls.TextBox> visit)
    {
        if (node is System.Windows.Controls.TextBox tb) visit(tb);
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), visit);
    }

    private static T? FindChild<T>(DependencyObject node) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T t) return t;
            var deeper = FindChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 按四个键盘档位各出一张图，外加一张"高窗口"图。
    ///
    /// 每张图都把窗口设成**该档位的最小尺寸**，这样看到的就是用户打开这个页面
    /// 时的第一眼：键盘刚好铺满，没有多余横向空白。最后那张高窗口图用来看
    /// "键盘下方原本空着的地方现在装了什么"。
    /// </summary>
    private static void RenderKeyboardSizes(MainWindow main, InputPlus.UI.Views.KeysView kv, string outDir)
    {
        double savedW = main.Width, savedH = main.Height;
        double savedMinW = main.MinWidth, savedMinH = main.MinHeight;

        foreach (var preset in InputPlus.Core.Settings.KeyboardSizePreset.All)
        {
            kv.SetSizePresetForRender(preset.Id);
            var (w, h) = kv.RequiredWindowSize();

            /* 先解除最小限制再设尺寸：MinWidth 已经按档位抬上去了，
             * 直接设更小的 Width 会被它顶回来。 */
            main.MinWidth = 0;
            main.MinHeight = 0;
            main.Width = w;
            main.Height = h;
            Pump(300);

            Save(main, Path.Combine(outDir, $"keys-size-{preset.Id}.png"));
        }

        /* 整页：键盘大小**不变**，多出来的高度全部给下方编辑区。
         *
         * 尺寸取「大」档的最小宽度 + 刚好装下编辑卡片的高度（860）：
         * 编辑区压缩成多列之后内容只要 800 出头，再高就是一片空白，
         * 图放到 README 里不好看；再矮编辑卡会被折掉。
         * 这张用来看"编辑区在键盘正下方"之后的整页观感。 */
        kv.SetSizePresetForRender("large");
        main.Width = kv.RequiredWindowSize().Width;
        main.Height = 860;
        Pump(300);
        Save(main, Path.Combine(outDir, "keys-full.png"));

        kv.ClearSizePresetForRender();
        main.MinWidth = savedMinW;
        main.MinHeight = savedMinH;
        main.Width = savedW;
        main.Height = savedH;
        Pump(200);
    }

    private static void Pump(int ms = 120)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ContextIdle)
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 出图：右键菜单的样式（诊断用，`CLP_RENDER_MENU=1 --render` 才会跑）。
    ///
    /// 菜单是 Popup，不在主窗口的视觉树里，所以主窗口截图拍不到它 ——
    /// 这里单独对一个 ContextMenu 做 RenderTargetBitmap。
    /// </summary>
    private static void RenderMenuShot(string outDir)
    {
        try
        {
            var menu = new System.Windows.Controls.ContextMenu();

            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "重命名分组…" });
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "删除分组" });
            menu.Items.Add(new System.Windows.Controls.Separator());

            var move = new System.Windows.Controls.MenuItem { Header = "移到分组" };
            move.Items.Add(new System.Windows.Controls.MenuItem { Header = "常用", IsCheckable = true, IsChecked = true });
            move.Items.Add(new System.Windows.Controls.MenuItem { Header = "账号", IsCheckable = true });
            move.Items.Add(new System.Windows.Controls.MenuItem { Header = "未分组", IsCheckable = true });
            move.Items.Add(new System.Windows.Controls.Separator());
            move.Items.Add(new System.Windows.Controls.MenuItem { Header = "新建分组…" });
            menu.Items.Add(move);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "删除这个别名" });

            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
            menu.HorizontalOffset = 200;
            menu.VerticalOffset = 200;
            menu.IsOpen = true;
            Pump(400);

            menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            Pump(150);

            int w = Math.Max(1, (int)Math.Ceiling(menu.ActualWidth));
            int h = Math.Max(1, (int)Math.Ceiling(menu.ActualHeight));

            /* 留一圈边距给阴影，否则会被裁掉。 */
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w + 28, h + 28, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(menu);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = System.IO.File.Create(Path.Combine(outDir, "menu.png"));
            encoder.Save(fs);

            menu.IsOpen = false;
            CoreLog.Info($"菜单截图已保存：menu.png（{w}x{h}）");
        }
        catch (Exception ex)
        {
            CoreLog.Warn("渲染菜单截图失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 手势轨迹出图：常态色（浅底）与已识别色（深底）各一张。
    /// 走的是覆盖层窗口自己的绘制路径（GestureTrail + 三层 Path），
    /// 所以图里看到的就是真机上画出来的样子。
    /// </summary>
    private static void RenderTrailShots(AppHost host, string outDir)
    {
        const double shotW = 660;
        const double shotH = 520;

        /* 先横后竖的一段折角：直线段看有没有抖，拐角看 Chaikin 切角，
         * 收笔看圆头。点距 20px 正好是 GestureTrail.MinDistance，
         * 即"能被收下"的临界值（小于 20 会被过滤掉）。 */
        double ox = SystemParameters.VirtualScreenLeft + 90;
        double oy = SystemParameters.VirtualScreenTop + 120;
        var stroke = new List<(double X, double Y)>();
        for (int i = 0; i <= 22; i++) stroke.Add((ox + i * 20, oy));
        for (int i = 1; i <= 14; i++) stroke.Add((ox + 440, oy + i * 20));

        void Snapshot(GestureOverlayWindow win, Brush backdrop, string file)
        {
            var root = (FrameworkElement)win.Content;
            root.Width = shotW;
            root.Height = shotH;
            root.Measure(new Size(shotW, shotH));
            root.Arrange(new Rect(0, 0, shotW, shotH));
            root.UpdateLayout();

            int w = (int)shotW;
            int h = (int)shotH;
            var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);

            /* 覆盖层本身是透明的，没有桌面可叠；铺一层中性底才看得出
             * 白描边和黑阴影到底有没有画出来。 */
            var backdropVisual = new DrawingVisual();
            using (var dc = backdropVisual.RenderOpen())
            {
                dc.DrawRectangle(backdrop, null, new Rect(0, 0, w, h));
            }
            bmp.Render(backdropVisual);
            bmp.Render(root);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs2 = File.Create(Path.Combine(outDir, file));
            encoder.Save(fs2);
            win.Clear();
        }

        void Draw(bool recognized, Brush backdrop, string file)
        {
            var win = new GestureOverlayWindow { Width = shotW, Height = shotH };
            win.Begin(host.Settings.GestureOptions);
            if (recognized) win.MarkRecognized();

            foreach (var (x, y) in stroke)
            {
                win.AddPoint(x, y);
                /* 16ms 节流是给 8ms 轮询用的：一次性喂完点，
                 * 用 ReplaceLast 强制刷最后一笔，否则除首点外全是空几何。 */
                win.ReplaceLast(x, y);
            }

            Snapshot(win, backdrop, file);
        }

        /* 起始超时红点单独拍一张：白描边压在灰底上才看得出边框，
         * 圆的位置也顺便验一下是不是以光标为圆心（WG2 的 B/X.cs:21-28）。 */
        void DrawDot(Brush backdrop, string file)
        {
            var win = new GestureOverlayWindow { Width = shotW, Height = shotH };
            win.Begin(host.Settings.GestureOptions);
            win.ShowTimeoutDot(ox + 220, oy + 140);
            Snapshot(win, backdrop, file);
        }

        Draw(false, new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)), "gesture-trail.png");
        Draw(true, new SolidColorBrush(Color.FromRgb(0x1F, 0x22, 0x27)), "gesture-trail-dark.png");
        DrawDot(new SolidColorBrush(Color.FromRgb(0x53, 0x59, 0x63)), "gesture-dot.png");
    }

    /// <summary>
    /// 把 INPUTPLUS_RENDER_SIZE 套到任意窗口上。主窗口那边是内联解析的，
    /// 这里单独抽一个给"临时弹出来的窗口"用（展开窗口、手势录制窗口之类），
    /// 免得每加一个窗口就复制一遍解析逻辑。
    /// 返回 false 表示没设尺寸或格式不对 —— 调用方自己决定用默认大小。
    /// </summary>
    private static bool TryRenderSize(Window window)
    {
        string? size = Environment.GetEnvironmentVariable("INPUTPLUS_RENDER_SIZE");
        if (string.IsNullOrWhiteSpace(size)) return false;
        string[] parts = size!.Split('x', 'X');
        if (parts.Length != 2
            || !double.TryParse(parts[0], out double w) || w <= 200
            || !double.TryParse(parts[1], out double h) || h <= 200) return false;
        window.Width = w;
        window.Height = h;
        return true;
    }

    /// <summary>
    /// 拍一张"屏幕上真的长什么样"的图。RenderTargetBitmap 只画 WPF 视觉树，
    /// DWM 画的圆角、系统阴影、Mica 背景、深浅色标题栏统统拍不到 —— 而"窗口没有圆角"、
    /// "不跟系统明暗"这类反馈恰好只存在于合成器那一层。这里把窗口真摆到屏幕上，
    /// 用屏幕拷贝取窗口的物理矩形（连圆角外的桌面像素一起取进来），拍完由调用方挪回去。
    /// </summary>
    private static void SaveOnScreen(Window window, string path)
    {
        try
        {
            window.UpdateLayout();
            Point topLeft = window.PointToScreen(new Point(0, 0));
            Point bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));
            /* 四周多留 12 像素：圆角外面是桌面，留白才能看出"角被切掉了"。 */
            const int pad = 12;
            int x = (int)Math.Round(topLeft.X) - pad;
            int y = (int)Math.Round(topLeft.Y) - pad;
            int w = (int)Math.Round(bottomRight.X - topLeft.X) + pad * 2;
            int h = (int)Math.Round(bottomRight.Y - topLeft.Y) + pad * 2;
            if (w <= 0 || h <= 0) return;
            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { CoreLog.Warn("屏幕截图失败：" + ex.Message); }
    }

    private static void Save(Window window, string path)
    {
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        double dpiScale = VisualTreeHelper.GetDpi(root).DpiScaleX;
        double dpi = 96 * dpiScale;
        double scale = dpi / 96.0;

        double logicalW = root.ActualWidth;
        double logicalH = root.ActualHeight;

        // 只有“随内容自动定高”的窗口（如 Qbar）才需要按内容的期望高度补足，
        // 主窗口是固定尺寸，不能这么做，否则会把滚动区的内容全展开成一张长图。
        if (window.SizeToContent != SizeToContent.Manual)
        {
            root.Measure(new Size(logicalW, double.PositiveInfinity));
            double desiredH = root.DesiredSize.Height;
            if (desiredH > logicalH + 1)
            {
                window.Height = window.ActualHeight + (desiredH - logicalH);
                window.UpdateLayout();
                Pump(120);
                root.Measure(new Size(logicalW, double.PositiveInfinity));
                desiredH = root.DesiredSize.Height;
            }
            logicalH = Math.Max(logicalH, desiredH);
        }

        /* 根元素带 Margin 时，RenderTargetBitmap.Render(root) 会把它在窗口里的位置
         * 一起算进去（相当于从窗口左上角开始画）。以前位图只按 ActualWidth/Height 开，
         * 带 Margin 的窗口（对话框都是 22,18）右下角就被裁掉一条 ——
         * 「手势设置」窗的底栏按钮就这么少过半截。把外边距算进位图尺寸即可。 */
        var rootMargin = root.Margin;
        logicalW += rootMargin.Left + rootMargin.Right;
        logicalH += rootMargin.Top + rootMargin.Bottom;

        int w = (int)Math.Ceiling(logicalW * scale);
        int ht = (int)Math.Ceiling(logicalH * scale);
        if (w <= 0 || ht <= 0) return;

        var bmp = new RenderTargetBitmap(w, ht, dpi, dpi, PixelFormats.Pbgra32);

        /* 先铺一层窗口自己的 Background，再把内容画上去。
         *
         * RenderTargetBitmap 只渲染**传进去的那个 Visual 的子树**，
         * 而 Window.Background 是窗口自己（而不是 Content）的属性 ——
         * 内容里所有透明的地方（标题栏那一行、页面外边距、侧栏与页面之间
         * 的缝）在 PNG 里就成了纯黑。
         *
         * 深色主题下"黑"与底色本来就接近，看不出来；浅色主题下整条标题栏
         * 都是黑块、标题文字（深色字）完全不可读，看起来像渲染器坏了。
         * 发版截图（docs/screenshots）就是这条路径产出的，所以必须铺底。 */
        var background = window.Background;

        /* 覆盖层（Qbar 面板、取键窗口）的 Background 是 Transparent ——
         * 它们本来就要"浮在桌面之上"，透明是对的。
         * 但截图没有"桌面"可叠，透明像素在 PNG 里就是空白：
         * 深色主题下那块投影会在白底上显成一圈亮边，看着像渲染器坏了。
         * 所以透明时改用主题的窗口底色当画布，让截图看起来像"面板浮在应用上"。 */
        if (background == null ||
            (background is System.Windows.Media.SolidColorBrush scb && scb.Color.A == 0))
        {
            background = window.TryFindResource("Brush.WindowGradient") as Brush
                         ?? window.TryFindResource("Brush.Surface") as Brush
                         ?? background;
        }

        if (background != null)
        {
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, logicalW, logicalH));
            }
            bmp.Render(backdrop);
        }

        bmp.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    /// <summary>
    /// 把**一个元素**（而不是整个窗口）渲染成 PNG —— 给 Popup 用：
    /// 弹层挂在独立顶层窗口上，主窗口的截图里永远没有它。
    /// </summary>
    private static void SaveElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();

        if (element.ActualWidth < 1 || element.ActualHeight < 1)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            element.Arrange(new Rect(element.DesiredSize));
            element.UpdateLayout();
        }

        double dpiScale = VisualTreeHelper.GetDpi(element).DpiScaleX;
        double dpi = 96 * dpiScale;

        int w = (int)Math.Ceiling(element.ActualWidth * dpiScale);
        int h = (int)Math.Ceiling(element.ActualHeight * dpiScale);
        if (w <= 0 || h <= 0) return;

        /* 弹层内容自带底（Card 样式），但仍先把"页面底色"铺一层：
         * AllowsTransparency 的 Popup 在离屏渲染里没有桌面可叠，
         * 圆角以外会是纯透明，PNG 看起来像缺了一块。 */
        var background = element.TryFindResource("Brush.Surface") as Brush;
        var bmp = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);

        if (background != null)
        {
            var backdrop = new DrawingVisual();
            using (var dc = backdrop.RenderOpen())
                dc.DrawRectangle(background, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bmp.Render(backdrop);
        }

        bmp.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
