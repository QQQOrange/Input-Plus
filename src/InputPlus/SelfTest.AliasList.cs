using System.Collections.Generic;
using InputPlus.Core.Settings;
using InputPlus.UI.Controls;

namespace InputPlus;

/// <summary>
/// 「全部别名」搜索框的分类过滤 + 统一搜索框控件本身的自检。
///
/// 这两块都是**纯逻辑**：给定一条别名和选中的列，命中就是命中，不依赖窗口、
/// 不依赖 DataGrid —— 所以能在自检里跑，也就必须在这里锁住。
/// 用户报过"选了备注、结果正文里有同一个词的也全冒出来"，这条断言就是防它回来。
/// </summary>
public static partial class SelfTest
{
    private static void TestAliasListSearch()
    {
        var snippet = new Snippet
        {
            Trigger = ";mail",
            Text = "zhang@example.com",
            Comment = "工作邮箱",
            Group = "工作",
        };

        var prefixes = new List<string> { ";" };

        // ---- 按列过滤：选哪一列只搜哪一列 ----
        Check("别名搜索：关键字列命中触发词",
            UI.Views.HotstringView.MatchesColumn(snippet, "key", "mail", prefixes));
        Check("别名搜索：关键字列也认带前缀的原文",
            UI.Views.HotstringView.MatchesColumn(snippet, "key", ";mail", prefixes));
        Check("别名搜索：正文列命中正文",
            UI.Views.HotstringView.MatchesColumn(snippet, "text", "zhang", prefixes));
        Check("别名搜索：备注列命中备注",
            UI.Views.HotstringView.MatchesColumn(snippet, "comment", "工作", prefixes));

        /* 关键回归：选了「备注」就不能因为正文里有这个词而命中。 */
        Check("别名搜索：备注列不命中正文里的词",
            !UI.Views.HotstringView.MatchesColumn(snippet, "comment", "zhang", prefixes));
        Check("别名搜索：正文列不命中备注里的词",
            !UI.Views.HotstringView.MatchesColumn(snippet, "text", "工作", prefixes));
        Check("别名搜索：关键字列不命中备注里的词",
            !UI.Views.HotstringView.MatchesColumn(snippet, "key", "工作", prefixes));

        Check("别名搜索：空关键词等于不过滤",
            UI.Views.HotstringView.MatchesColumn(snippet, "comment", "  ", prefixes));

        /* 列表里显示的是剥掉前缀后的样子，照着屏幕打必须能搜到。 */
        var noPrefixHint = new List<string>();
        Check("别名搜索：没有前缀配置时也能按原文搜",
            UI.Views.HotstringView.MatchesColumn(snippet, "key", ";mail", noPrefixHint));

        // ---- 统一搜索框控件：分类下拉的显隐与默认选中 ----
        var field = new SearchField();
        field.SetCategories(
            new SearchCategory("别名关键字", "key"),
            new SearchCategory("别名正文", "text"),
            new SearchCategory("备注", "comment"));

        Check("搜索框：默认选中第一项分类", field.SelectedTag == "key", field.SelectedTag);
        field.SelectCategory("comment");
        Check("搜索框：能按标识切分类", field.SelectedTag == "comment", field.SelectedTag);

        /* 只有一个分类时下拉必须收掉：选项和"没得选"是一回事，
         * 留着它只会占掉半条框的宽度。 */
        var single = new SearchField();
        single.SetCategories(new SearchCategory("只有一项", "only"));
        Check("搜索框：单分类不显示下拉", single.SelectedTag == "only", single.SelectedTag);

        var none = new SearchField();
        none.SetCategories();
        Check("搜索框：没有分类时没有选中项", none.SelectedTag == null, none.SelectedTag ?? "null");

        // ---- 文字与事件同步 ----
        var typed = new SearchField();
        typed.SearchText = "abc";
        Check("搜索框：代码写 SearchText 会同步到框里", typed.InputBox.Text == "abc", typed.InputBox.Text);

        typed.ClearSearch();
        Check("搜索框：清空后 SearchText 也是空", typed.SearchText == "", typed.SearchText);
        Check("搜索框：清空后框里也是空", typed.InputBox.Text == "", typed.InputBox.Text);
    }
}
