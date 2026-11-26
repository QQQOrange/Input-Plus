// 显式声明全局 using，避免 WPF 与 WinForms 的隐式 using 互相打架（Clipboard / Point 等）。
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// 界面文案：几乎每个界面文件都要 Loc.T("…")，逐个加 using 是纯噪音，
// 而且以后新加页面容易漏（要到编译报错才发现）。
global using InputPlus.Core.Localization;

// 剪贴板后端：取词、替换选区、输入文本、宏、脚本、UI 复制按钮、自检都要用。
// 逐个文件补 using 只会让下一个人加调用点时先撞一次编译错误。
// 注意它叫 ClipboardAccess 而不是 Clipboard —— 就是为了不和 System.Windows.Clipboard 撞名。
global using InputPlus.Core.Text;
