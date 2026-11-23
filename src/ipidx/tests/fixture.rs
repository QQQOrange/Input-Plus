//! 20 条假路径 fixture：覆盖中文子串、拼音、ext、size、date、path、取反、目录 vs 文件、正则、glob。
//!
//! 为什么走 C ABI 而不是内部函数：FFI 边界本身就是最容易出错的地方（编码、所有权、
//! panic 越界），用真入口测才发现得了。测试串行跑 —— 索引是进程级单例。

use std::sync::Mutex;

use ipidx::*;

static LOCK: Mutex<()> = Mutex::new(());

const FIXTURE: &str = r#"[
 {"p":"C:\\Users\\me\\Documents\\年度总结.docx","d":0,"s":2097152,"t":8000,"dp":4,"py":"nianduzongjie","pi":"ndzj"},
 {"p":"C:\\Users\\me\\Documents\\年度报表.xlsx","d":0,"s":512000,"t":8100,"dp":4,"py":"niandubaobiao","pi":"ndbb"},
 {"p":"C:\\Users\\me\\Documents\\报表-2026.docx","d":0,"s":3145728,"t":8200,"dp":4,"py":"baobiao","pi":"bb"},
 {"p":"C:\\Users\\me\\Desktop\\银行流水.pdf","d":0,"s":1048576,"t":9000,"dp":3,"py":"yinhangliushui","pi":"yhls"},
 {"p":"C:\\Users\\me\\Desktop\\银行对账单.txt","d":0,"s":10240,"t":100,"dp":3,"py":"yinhangduizhangdan","pi":"yhdzd"},
 {"p":"D:\\work\\Annual Report.docx","d":0,"s":4194304,"t":8500,"dp":2,"py":"","pi":""},
 {"p":"D:\\work\\report_out.rs","d":0,"s":18432,"t":8600,"dp":2,"py":"","pi":""},
 {"p":"D:\\work\\reporter.py","d":0,"s":1024,"t":8700,"dp":2,"py":"","pi":""},
 {"p":"D:\\tmp\\scratch.tmp","d":0,"s":2048,"t":8300,"dp":2,"py":"","pi":""},
 {"p":"D:\\tmp\\cache.dat","d":0,"s":0,"t":-1,"dp":2,"py":"","pi":""},
 {"p":"C:\\Users\\me\\AppData\\Local\\x.dll","d":0,"s":102400,"t":7000,"dp":5,"py":"","pi":""},
 {"p":"C:\\Windows\\System32\\kernel32.dll","d":0,"s":716800,"t":6000,"dp":3,"py":"","pi":""},
 {"p":"C:\\Users\\me\\Documents\\设计稿","d":1,"s":0,"t":8100,"dp":4,"py":"shejigao","pi":"sjg"},
 {"p":"C:\\Users\\me\\Documents\\报表","d":1,"s":0,"t":8100,"dp":4,"py":"baobiao","pi":"bb"},
 {"p":"D:\\work\\build","d":1,"s":0,"t":8200,"dp":2,"py":"","pi":""},
 {"p":"D:\\work\\chrome_setup.exe","d":0,"s":94371840,"t":8800,"dp":2,"py":"","pi":""},
 {"p":"D:\\work\\Reporter Setup.exe","d":0,"s":83886080,"t":8900,"dp":2,"py":"","pi":""},
 {"p":"C:\\Users\\me\\Videos\\视频教程.mp4","d":0,"s":524288000,"t":5000,"dp":3,"py":"shipinjiaocheng","pi":"spjc"},
 {"p":"D:\\notes\\说明.md","d":0,"s":5120,"t":8400,"dp":2,"py":"shuoming","pi":"sm"},
 {"p":"C:\\Users\\me\\Pictures\\截图 2026-01-01.png","d":0,"s":2097152,"t":8000,"dp":3,"py":"jietu","pi":"jt"}
]"#;

/// 准备一个**确定是空**的临时索引目录。
///
/// 不能"删不掉就凑合用"：Windows 上杀软/同步盘会短暂捏住删除的句柄，此时目录里
/// 还留着上一轮的索引，`open_or_create` 会把它当成现有索引打开，于是本次 20 条
/// 变成 40 条、排序断言随机失败。所以这里逐个换号直到找到一个真的不存在的目录。
fn tmp_dir(tag: &str) -> String {
    let base = std::env::temp_dir();
    for n in 0..1000 {
        let p = base.join(format!("ipidx-test-{tag}-{}-{n}", std::process::id()));
        if !p.exists() {
            std::fs::create_dir_all(&p).unwrap();
            return p.to_string_lossy().to_string();
        }
        let _ = std::fs::remove_dir_all(&p);
        if !p.exists() {
            std::fs::create_dir_all(&p).unwrap();
            return p.to_string_lossy().to_string();
        }
    }
    panic!("无法准备临时索引目录");
}

unsafe fn call_buf(buf: IpIdxBuf) -> String {
    if buf.ptr.is_null() || buf.len == 0 {
        return String::new();
    }
    let slice = std::slice::from_raw_parts(buf.ptr, buf.len);
    let text = String::from_utf8_lossy(slice).to_string();
    ipidx_buf_free(buf);
    text
}

fn open_index(tag: &str) -> String {
    // 杀软/同步盘会在 commit 那一刻短暂捏住新写的 .store，表现为 PermissionDenied
    // 且 commit 之后 writer 里已经没有待提交文档。这时唯一正确的做法是**整批重来**：
    // 换一个全新的目录，重新 open + add + commit。断言一条都不放宽。
    let mut last = String::new();
    for attempt in 0..6 {
        let dir = tmp_dir(&format!("{tag}{attempt}"));
        let rc = unsafe { ipidx_open(dir.as_ptr(), dir.len()) };
        assert_eq!(rc, 0, "open 失败: {}", unsafe { call_buf(ipidx_last_error()) });
        let n = unsafe { ipidx_add_docs(FIXTURE.as_ptr(), FIXTURE.len()) };
        assert_eq!(n, 20, "应当写入 20 条");
        let rc = unsafe { ipidx_commit() };
        if rc != 0 {
            last = unsafe { call_buf(ipidx_last_error()) };
            continue;
        }
        // 提交之后条数必须是 20：commit 半途失败会留下「少了几个文档」的索引，
        // 而那种索引在后面的查询里只会表现为「莫名其妙搜不到」，很难反推。
        let stats = unsafe { call_buf(ipidx_stats()) };
        let v: serde_json::Value = serde_json::from_str(&stats).unwrap();
        if v["docs"].as_u64().unwrap() != 20 {
            last = format!("提交后文档数不是 20：{stats}");
            continue;
        }
        return dir;
    }
    panic!("连续 6 次建索引失败：{last}");
}

/// 跑一次查询，返回命中路径列表（保持引擎给的顺序）。
fn search(spec: &str, limit: u32) -> Vec<(String, u8, u64, i64)> {
    let json = unsafe { call_buf(ipidx_search(spec.as_ptr(), spec.len(), limit)) };
    let v: serde_json::Value = serde_json::from_str(&json).expect("返回必须是合法 JSON");
    assert_eq!(v["ok"], serde_json::Value::Bool(true), "查询失败: {json}");
    v["hits"]
        .as_array()
        .unwrap()
        .iter()
        .map(|h| {
            (
                h["p"].as_str().unwrap().to_string(),
                h["d"].as_u64().unwrap() as u8,
                h["s"].as_u64().unwrap(),
                h["t"].as_i64().unwrap(),
            )
        })
        .collect()
}

fn paths_of(hits: &[(String, u8, u64, i64)]) -> Vec<String> {
    hits.iter().map(|h| h.0.clone()).collect()
}

fn has(hits: &[(String, u8, u64, i64)], needle: &str) -> bool {
    hits.iter().any(|h| h.0.ends_with(needle))
}

#[test]
fn fixture_chinese_substring_two_chars() {
    let _g = LOCK.lock().unwrap();
    open_index("cjk2");
    let hits = search(r#"{"terms":["报表"]}"#, 200);
    assert!(has(&hits, "年度报表.xlsx"), "2 字中文子串必须命中：{:?}", paths_of(&hits));
    assert!(has(&hits, "报表-2026.docx"));
    assert!(has(&hits, r"Documents\报表"), "目录也要命中");
    assert!(!has(&hits, "年度总结.docx"));
}

#[test]
fn fixture_chinese_single_char() {
    let _g = LOCK.lock().unwrap();
    open_index("cjk1");
    let hits = search(r#"{"terms":["银"]}"#, 200);
    assert_eq!(hits.len(), 2, "单字「银」应当只命中两条银行文件：{:?}", paths_of(&hits));
    assert!(has(&hits, "银行流水.pdf"));
    assert!(has(&hits, "银行对账单.txt"));
}

#[test]
fn fixture_chinese_four_chars_needs_all_bigrams() {
    let _g = LOCK.lock().unwrap();
    open_index("cjk4");
    let hits = search(r#"{"terms":["年度报表"]}"#, 200);
    assert!(has(&hits, "年度报表.xlsx"));
    // 「年度总结」不含「报表」的 2-gram 组合，不该被 4 字串召回
    assert!(!has(&hits, "年度总结.docx"));
}

#[test]
fn fixture_ascii_substring_is_case_insensitive() {
    let _g = LOCK.lock().unwrap();
    open_index("ascii");
    let hits = search(r#"{"terms":["REPORT"]}"#, 200);
    assert!(has(&hits, "Annual Report.docx"), "{:?}", paths_of(&hits));
    assert!(has(&hits, "report_out.rs"));
    assert!(has(&hits, "reporter.py"));
    assert!(has(&hits, "Reporter Setup.exe"));
}

#[test]
fn fixture_pinyin_initials() {
    let _g = LOCK.lock().unwrap();
    open_index("py1");
    let json = unsafe {
        let spec = br#"{"terms":["sjg"]}"#;
        call_buf(ipidx_pinyin_search(spec.as_ptr(), spec.len(), 50))
    };
    assert!(json.contains("设计稿"), "首字母 sjg 应当命中设计稿：{json}");
    assert!(json.contains("\"py\":1"), "拼音命中要带标记");
}

#[test]
fn fixture_pinyin_full() {
    let _g = LOCK.lock().unwrap();
    open_index("py2");
    let json = unsafe {
        let spec = br#"{"terms":["shejigao"]}"#;
        call_buf(ipidx_pinyin_search(spec.as_ptr(), spec.len(), 50))
    };
    assert!(json.contains("设计稿"), "全拼 shejigao 应当命中设计稿：{json}");
}

#[test]
fn fixture_pinyin_two_letters() {
    let _g = LOCK.lock().unwrap();
    open_index("py3");
    let json = unsafe {
        let spec = br#"{"terms":["bb"]}"#;
        call_buf(ipidx_pinyin_search(spec.as_ptr(), spec.len(), 50))
    };
    assert!(json.contains("年度报表.xlsx"), "首字母 bb 应当命中报表：{json}");
}

#[test]
fn fixture_ext_filter() {
    let _g = LOCK.lock().unwrap();
    open_index("ext");
    let hits = search(r#"{"ext":["docx"],"files":true}"#, 200);
    let names = paths_of(&hits);
    assert_eq!(names.len(), 3, "docx 应当 3 条：{names:?}");
    assert!(has(&hits, "年度总结.docx"));
    assert!(has(&hits, "报表-2026.docx"));
    assert!(has(&hits, "Annual Report.docx"));
}

#[test]
fn fixture_neg_ext_filter() {
    let _g = LOCK.lock().unwrap();
    open_index("negext");
    let hits = search(r#"{"negext":["tmp"]}"#, 200);
    assert!(!has(&hits, "scratch.tmp"), "排除 tmp 之后不该出现");
    assert!(has(&hits, "年度总结.docx"), "其它文件要留着");
}

#[test]
fn fixture_ext_and_size() {
    let _g = LOCK.lock().unwrap();
    open_index("extsize");
    let hits = search(r#"{"ext":["docx"],"minsize":1048576}"#, 200);
    let names = paths_of(&hits);
    assert_eq!(names.len(), 3, "三个 docx 都 > 1MB：{names:?}");
    assert!(!has(&hits, "reporter.py"), "py 不该进来");
}

#[test]
fn fixture_size_range_excludes() {
    let _g = LOCK.lock().unwrap();
    open_index("size");
    let hits = search(r#"{"minsize":1048576,"maxsize":4194304}"#, 200);
    for h in &hits {
        assert!(h.2 >= 1048576 && h.2 <= 4194304, "越界: {} = {}", h.0, h.2);
    }
    assert!(has(&hits, "年度总结.docx"));
    assert!(!has(&hits, "chrome_setup.exe"), "90MB 不该在区间里");
}

#[test]
fn fixture_date_range() {
    let _g = LOCK.lock().unwrap();
    open_index("date");
    let hits = search(r#"{"minday":8000,"maxday":8200,"files":true}"#, 200);
    assert!(has(&hits, "年度总结.docx"));
    assert!(has(&hits, "报表-2026.docx"));
    assert!(!has(&hits, "银行流水.pdf"), "day=9000 在区间外");
    assert!(!has(&hits, "cache.dat"), "未知日期不参与日期区间");
}

#[test]
fn fixture_path_pin() {
    let _g = LOCK.lock().unwrap();
    open_index("path");
    let hits = search(r#"{"path":["Documents"]}"#, 200);
    assert!(has(&hits, "年度总结.docx"));
    assert!(has(&hits, "报表-2026.docx"));
    assert!(!has(&hits, "report_out.rs"), "D:\\work 不在 Documents 下");
}

#[test]
fn fixture_neg_path_pin() {
    let _g = LOCK.lock().unwrap();
    open_index("negpath");
    let hits = search(r#"{"negpath":["AppData"]}"#, 200);
    assert!(!has(&hits, "x.dll"), "AppData 下的要排除");
    assert!(has(&hits, "年度总结.docx"), "其它文件要留着");
}

#[test]
fn fixture_neg_term() {
    let _g = LOCK.lock().unwrap();
    open_index("negterm");
    let hits = search(r#"{"terms":["report"],"neg":["setup"]}"#, 200);
    assert!(has(&hits, "report_out.rs"));
    assert!(!has(&hits, "Reporter Setup.exe"), "取反词要生效");
}

#[test]
fn fixture_dirs_only() {
    let _g = LOCK.lock().unwrap();
    open_index("dirs");
    let hits = search(r#"{"dirs":true,"files":false}"#, 200);
    assert!(!hits.is_empty());
    for h in &hits {
        assert_eq!(h.1, 1, "只允许目录: {}", h.0);
    }
    assert!(has(&hits, r"Documents\报表"));
}

#[test]
fn fixture_files_only() {
    let _g = LOCK.lock().unwrap();
    open_index("files");
    let hits = search(r#"{"files":true,"dirs":false}"#, 200);
    for h in &hits {
        assert_eq!(h.1, 0, "只允许文件: {}", h.0);
    }
}

#[test]
fn fixture_glob() {
    let _g = LOCK.lock().unwrap();
    open_index("glob");
    let hits = search(r#"{"glob":"*.pdf"}"#, 200);
    assert!(has(&hits, "银行流水.pdf"));
    assert!(!has(&hits, "年度总结.docx"));
}

#[test]
fn fixture_glob_question_mark() {
    let _g = LOCK.lock().unwrap();
    open_index("globq");
    let hits = search(r#"{"glob":"x.d?l"}"#, 200);
    assert!(has(&hits, "x.dll"));
    assert!(!has(&hits, "kernel32.dll"), "x.d?l 只该匹配 x.dll");
}

#[test]
fn fixture_regex_anchored() {
    let _g = LOCK.lock().unwrap();
    open_index("regex");
    // RegexQuery 不接受 ^ $：索引层退成超集预筛，锚定语义由 C# 侧真正则补。
    let hits = search(r#"{"regex":"^annual.*\\.docx$"}"#, 200);
    assert!(has(&hits, "Annual Report.docx"), "{:?}", paths_of(&hits));
}

#[test]
fn fixture_sort_by_size_desc() {
    let _g = LOCK.lock().unwrap();
    open_index("sortsize");
    let hits = search(r#"{"ext":["exe"],"sort":"size","desc":true}"#, 200);
    let sizes: Vec<u64> = hits.iter().map(|h| h.2).collect();
    let mut sorted = sizes.clone();
    sorted.sort_by(|a, b| b.cmp(a));
    assert_eq!(sizes, sorted, "size 降序：{sizes:?}");
    assert_eq!(hits[0].0, r"D:\work\chrome_setup.exe");
}

#[test]
fn fixture_sort_by_mtime_asc() {
    let _g = LOCK.lock().unwrap();
    open_index("sortdate");
    let hits = search(r#"{"ext":["dll"],"sort":"mtime","desc":false}"#, 200);
    let days: Vec<i64> = hits.iter().map(|h| h.3).collect();
    let mut sorted = days.clone();
    sorted.sort();
    assert_eq!(days, sorted, "mtime 升序：{days:?}");
}

#[test]
fn fixture_empty_query_returns_all() {
    let _g = LOCK.lock().unwrap();
    open_index("all");
    let hits = search("{}", 200);
    assert_eq!(hits.len(), 20, "空查询等于全量");
}

#[test]
fn fixture_stats_counts() {
    let _g = LOCK.lock().unwrap();
    open_index("stats");
    let json = unsafe { call_buf(ipidx_stats()) };
    let v: serde_json::Value = serde_json::from_str(&json).unwrap();
    assert_eq!(v["ok"], serde_json::Value::Bool(true), "{json}");
    assert_eq!(v["docs"].as_u64().unwrap(), 20);
    assert_eq!(v["files"].as_u64().unwrap(), 17);
    assert_eq!(v["dirs"].as_u64().unwrap(), 3);
    assert_eq!(v["schema_version"].as_u64().unwrap(), SCHEMA_VERSION as u64);
}

#[test]
fn fixture_delete_path() {
    let _g = LOCK.lock().unwrap();
    open_index("delete");
    let del = r#"["c:\\users\\me\\documents\\年度总结.docx"]"#;
    let n = unsafe { ipidx_delete_paths(del.as_ptr(), del.len()) };
    assert_eq!(n, 1);
    assert_eq!(unsafe { ipidx_commit() }, 0);
    let hits = search(r#"{"terms":["年度总结"]}"#, 200);
    assert!(!has(&hits, "年度总结.docx"), "删掉之后不该再出现");
    let all = search("{}", 200);
    assert_eq!(all.len(), 19, "总数应当少一条");
}

#[test]
fn fixture_ping_reports_schema() {
    let json = unsafe { call_buf(ipidx_ping()) };
    assert!(json.starts_with("ipidx/"), "{json}");
    assert!(json.contains(&format!("schema={SCHEMA_VERSION}")), "{json}");
}

#[test]
fn fixture_bad_utf8_returns_error_not_panic() {
    let _g = LOCK.lock().unwrap();
    let bytes: [u8; 2] = [0xff, 0xfe];
    let rc = unsafe { ipidx_open(bytes.as_ptr(), bytes.len()) };
    assert_eq!(rc, -2, "非法 UTF-8 应当被拒绝");
    let err = unsafe { call_buf(ipidx_last_error()) };
    assert!(err.contains("UTF-8"), "错误说明要可读：{err}");
}

/* ────────────────── 校准的幂等性（F1/F3：先删后加必须真的幂等） ────────────────── */

/// 同一条路径"删掉再写回"跑三轮，每次提交后条数都必须回到原值。
///
/// 这条用例抓的是"校准把每个文件重复写一遍"：tantivy 是 append-only，同一路径 add 两次
/// 就是两条文档。C# 侧的校准每 6 小时跑一次，一旦不幂等，用户看到的是每个文件都出现两遍。
#[test]
fn fixture_delete_then_readd_is_idempotent() {
    let _g = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    const MINI: &str = r#"[
     {"p":"C:\\q\\alpha.txt","d":0,"s":10,"t":8000,"dp":2,"py":"","pi":""},
     {"p":"C:\\q\\beta.txt","d":0,"s":20,"t":8001,"dp":2,"py":"","pi":""},
     {"p":"C:\\q\\子目录","d":1,"s":0,"t":8000,"dp":2,"py":"","pi":""}
    ]"#;
    let _dir = open_index("readd");
    // open_index 写的是 20 条的大夹具，这里换成小夹具：先把大的删干净
    let big: Vec<String> = serde_json::from_str::<Vec<serde_json::Value>>(FIXTURE)
        .unwrap()
        .iter()
        .map(|v| v["p"].as_str().unwrap().to_string())
        .collect();
    let big_json = serde_json::to_string(&big).unwrap();
    unsafe { ipidx_delete_paths(big_json.as_ptr(), big_json.len()) };
    assert_eq!(unsafe { ipidx_commit() }, 0);
    let n = unsafe { ipidx_add_docs(MINI.as_ptr(), MINI.len()) };
    assert_eq!(n, 3);
    assert_eq!(unsafe { ipidx_commit() }, 0);
    assert_eq!(docs_now(), 3, "小夹具应当有 3 条");

    let paths = r#"["C:\\q\\alpha.txt","C:\\q\\beta.txt","C:\\q\\子目录"]"#;
    for round in 0..3 {
        // 校准做的事：按路径删旧条目，再把同样这批写回来
        assert_eq!(unsafe { ipidx_delete_paths(paths.as_ptr(), paths.len()) }, 3);
        assert_eq!(unsafe { ipidx_add_docs(MINI.as_ptr(), MINI.len()) }, 3);
        assert_eq!(unsafe { ipidx_commit() }, 0, "第 {round} 轮提交失败");
        assert_eq!(docs_now(), 3, "第 {round} 轮提交后条数应当仍是 3");
        let hits = search(r#"{"terms":["alpha"]}"#, 10);
        assert_eq!(paths_of(&hits), vec!["C:\\q\\alpha.txt".to_string()], "第 {round} 轮后查不到 alpha");
        let all = search(r#"{"terms":[],"files":false,"dirs":false}"#, 10);
        assert_eq!(all.len(), 3, "第 {round} 轮后全量命中应当仍是 3 条");
    }
}

fn docs_now() -> u64 {
    let stats = unsafe { call_buf(ipidx_stats()) };
    let v: serde_json::Value = serde_json::from_str(&stats).unwrap();
    v["docs"].as_u64().unwrap()
}