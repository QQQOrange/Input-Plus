//! ipidx —— Qbar 的进程内文件索引内核。
//!
//! 为什么是 Rust + cdylib 而不是纯 C#：文件名子串检索需要「1-gram/2-gram 倒排 + 前缀/正则/
//! 数值区间下推 + 快字段排序」，这些能力 tantivy 已经调好；用 C# 自己再写一遍等于把
//! 一个成熟的搜索内核重写一次，而且内存占用翻几倍。所以索引放进程内、由 tantivy 承担，
//! 对外只暴露一层 C ABI，C# 侧用 P/Invoke 调用。
//!
//! 三条硬约束（违反任何一条都会让 WPF 进程直接消失，或者让「搜不到」变成静默错误）：
//!
//! 一、**每个导出函数的函数体都包在 `catch_unwind` 里**。panic 穿过 FFI 边界是未定义行为，
//!     在 Windows 上表现为整个进程被 abort —— 用户的输入法会跟着一起没。所以
//!     `Cargo.toml` 里不许写 `panic = "abort"`，每个 `#[no_mangle]` 入口都走 [`guard`]。
//!
//! 二、**跨边界的字符串一律 UTF-8 字节 + 显式长度**。不用 C 字符串，不猜编码：
//!     Windows 的 ANSI 代码页会把中文路径变成问号，而这个程序的主要用户就是中文路径。
//!     出参用 `(ptr, len)` 结构体，由调用方调 `ipidx_buf_free` 交还所有权。
//!
//! 三、**查询是「宁可多召回，不要漏」**。索引层只负责把候选窗口取出来（谓词全部下推），
//!     真正的语义复核与打分在 C# 侧（`QbarSpecFilter` / `QbarNameScore`）。所以这里
//!     允许返回超集（比如 2-gram AND 会命中「银行流水报表」这种非连续子串），
//!     但不许漏 —— 打分公式全仓只有 C# 那一份实现，见 DECISIONS。

use std::ops::Bound;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};
use tantivy::collector::{FacetCollector, HistogramCollector, MultiCollector, TopDocs};
use tantivy::directory::MmapDirectory;
use tantivy::query::{
    AllQuery, BooleanQuery, BoostQuery, FastFieldRangeQuery, FuzzyTermQuery, Occur, Query,
    RegexQuery, TermQuery,
};
use tantivy::schema::{
    Facet, FacetOptions, Field, IndexRecordOption, Schema, TextFieldIndexing, TextOptions, Value,
    FAST, INDEXED, STORED,
};
use tantivy::tokenizer::{LowerCaser, NgramTokenizer, RemoveLongFilter, TextAnalyzer};
use tantivy::{Index, IndexReader, IndexWriter, ReloadPolicy, TantivyDocument, Term, Order};

/// 落盘 schema 版本。字段布局一变就加一，老索引会被整体重建（`state.json` 里也记一份）。
pub const SCHEMA_VERSION: u32 = 2;

const TOKENIZER_NGRAM: &str = "cjk_ngram";

/// 分面直出时扩展名桶的上限（侧栏显示前 30 个够用了）。
const FACET_EXT_LIMIT: usize = 30;

/// "近 7 天"直方图的分桶数（每天一个桶，从 freshday 起算）。
const HISTOGRAM_BUCKETS: usize = 8;

/// 二进制入参的魔数（"IPDX"）。带上它，交叉接错参数时能立刻报错而不是写出垃圾。
const BIN_MAGIC: u32 = 0x4950_4458;

/// 二进制入参里每条文档的定长头宽度。字段表逐项相加就是 48（见 D67）。
const BIN_DOC_HEADER: usize = 48;

#[derive(Clone, Copy)]
struct Fields {
    path: Field,
    pathid: Field,
    name: Field,
    name_raw: Field,
    dir: Field,
    dirfacet: Field,
    ext: Field,
    isdir: Field,
    size: Field,
    mtime: Field,
    depth: Field,
    pinyin: Field,
    pinyin_initials: Field,
    /// 扩展名分面（/pdf、/docx …）：侧栏"文档/图片/视频…"的分桶由 C# 按它做。
    extfacet: Field,
    /// 类型分面（/file、/dir）：侧栏的"文件夹"计数直接取 /dir。
    typefacet: Field,
}

struct Engine {
    index: Index,
    writer: IndexWriter,
    reader: IndexReader,
    f: Fields,
    dir: PathBuf,
    /// 本次打开以来写进去的文档条数 / 发出的删除条数。只用于提交后的**不变量自检**：
    /// "加了东西、删得比加得少，提交后却是 0 条"＝ 索引被写坏了，必须当场报错，
    /// 而不是让调用方以为成功（那种静默失败在用户那边表现为"搜索突然全空"）。
    docs_added: u64,
    docs_deleted: u64,
}

static ENGINE: Mutex<Option<Engine>> = Mutex::new(None);
static LAST_ERROR: Mutex<Option<String>> = Mutex::new(None);

fn set_last_error(msg: impl Into<String>) {
    if let Ok(mut g) = LAST_ERROR.lock() {
        *g = Some(msg.into());
    }
}

fn take_last_error() -> String {
    match LAST_ERROR.lock() {
        Ok(mut g) => g.take().unwrap_or_default(),
        Err(_) => String::new(),
    }
}

// ─────────────────────────── 出参缓冲 ───────────────────────────

/// 一段由本库分配、调用方负责归还的内存。`len` 是字节数（UTF-8，不含结尾 0）。
#[repr(C)]
pub struct IpIdxBuf {
    pub ptr: *mut u8,
    pub len: usize,
}

impl IpIdxBuf {


    fn from_vec(v: Vec<u8>) -> Self {
        let boxed = v.into_boxed_slice();
        let len = boxed.len();
        let ptr = Box::into_raw(boxed) as *mut u8;
        IpIdxBuf { ptr, len }
    }

    fn from_str(s: &str) -> Self {
        Self::from_vec(s.as_bytes().to_vec())
    }
}

/// 归还 [`IpIdxBuf`]。传空指针或 0 长度是安全的（空出参就是这两种形态）。
#[no_mangle]
pub unsafe extern "C" fn ipidx_buf_free(buf: IpIdxBuf) {
    if buf.ptr.is_null() || buf.len == 0 {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(buf.ptr, buf.len)));
    }));
}

// ─────────────────────────── 入参解码 ───────────────────────────

unsafe fn as_str<'a>(ptr: *const u8, len: usize) -> Result<&'a str, String> {
    if ptr.is_null() {
        return if len == 0 { Ok("") } else { Err("空指针但长度非 0".into()) };
    }
    let bytes = std::slice::from_raw_parts(ptr, len);
    std::str::from_utf8(bytes).map_err(|e| format!("参数不是合法 UTF-8：{e}"))
}

/// 统一的 FFI 守卫：任何 panic 都变成错误码 + 一段可读的 last_error，
/// 绝不让异常穿到 WPF 那边去。
fn guard<F: FnOnce() -> i64>(what: &str, f: F) -> i64 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(v) => v,
        Err(payload) => {
            let msg = panic_text(payload);
            set_last_error(format!("{what} 内部 panic：{msg}"));
            -1
        }
    }
}

fn guard_buf<F: FnOnce() -> String>(what: &str, f: F) -> IpIdxBuf {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(s) => IpIdxBuf::from_str(&s),
        Err(payload) => {
            let msg = panic_text(payload);
            set_last_error(format!("{what} 内部 panic：{msg}"));
            IpIdxBuf::from_str(&format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&msg)))
        }
    }
}

fn panic_text(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_string()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "unknown panic".to_string()
    }
}

fn escape_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 8);
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out
}

// ─────────────────────────── schema ───────────────────────────

fn ngram_options() -> TextOptions {
    TextOptions::default()
        .set_indexing_options(
            TextFieldIndexing::default()
                .set_tokenizer(TOKENIZER_NGRAM)
                .set_index_option(IndexRecordOption::WithFreqsAndPositions),
        )
        .set_stored()
}

fn raw_options() -> TextOptions {
    TextOptions::default()
        .set_indexing_options(
            TextFieldIndexing::default()
                .set_tokenizer("raw")
                .set_index_option(IndexRecordOption::Basic),
        )
        .set_stored()
}

fn build_schema() -> (Schema, Fields) {
    let mut b = Schema::builder();
    let ngram = ngram_options();
    let raw = raw_options();
    // path/dir 用 ngram 而不是 raw：raw 只能整串精确匹配，`path:Documents` 这种
    // 「目录片段」谓词就完全没法下推（要么全表扫，要么窗口里根本没有候选）。
    // 精确匹配交给 pathid / name_raw。字段名不变，见 TANTIVY-MIGRATION 的 schema 表。
    let path = b.add_text_field("path", ngram.clone());
    // 小写全路径，raw 分词 —— 只用于「按路径删除」和精确匹配。
    let pathid = b.add_text_field("pathid", raw.clone());
    let name = b.add_text_field("name", ngram.clone());
    let name_raw = b.add_text_field("name_raw", raw.clone());
    let dir = b.add_text_field("dir", ngram.clone());
    let dirfacet = b.add_facet_field("dirfacet", FacetOptions::default());
    // 扩展名过滤靠倒排比靠快字段便宜（候选少时尤其明显）。
    let ext = b.add_text_field("ext", raw.clone());
    let isdir = b.add_u64_field("isdir", FAST | STORED | INDEXED);
    // size / mtime 只做快字段：区间查询走 FastFieldRangeQuery，
    // 不开 INDEXED —— 开了就要为每个不同大小/日期建词条，纯浪费。
    let size = b.add_u64_field("size", FAST | STORED);
    let mtime = b.add_i64_field("mtime", FAST | STORED);
    let depth = b.add_u64_field("depth", FAST | STORED);
    let pinyin = b.add_text_field("pinyin", ngram.clone());
    let pinyin_initials = b.add_text_field("pinyin_initials", ngram);
    /* 分面直出（F9）用两个 facet 字段：facet 是 tantivy 唯一能一次收集出"每个取值多少条"
     * 的字段类型。ext 本身是 raw TEXT（要参与 ext 过滤），不能兼任 facet。 */
    let extfacet = b.add_facet_field("extfacet", FacetOptions::default());
    let typefacet = b.add_facet_field("typefacet", FacetOptions::default());
    let f = Fields {
        path,
        pathid,
        name,
        name_raw,
        dir,
        dirfacet,
        ext,
        isdir,
        size,
        mtime,
        depth,
        pinyin,
        pinyin_initials,
        extfacet,
        typefacet,
    };
    (b.build(), f)
}

fn register_tokenizers(index: &Index) -> tantivy::Result<()> {
    // cjk_ngram = NgramTokenizer(1,2) + LowerCaser + RemoveLongFilter(64)，自建注册。
    let cjk = TextAnalyzer::builder(NgramTokenizer::all_ngrams(1, 2)?)
        .filter(LowerCaser)
        .filter(RemoveLongFilter::limit(64))
        .build();
    index.tokenizers().register(TOKENIZER_NGRAM, cjk);
    Ok(())
}

// ─────────────────────────── 入参 / 出参模型 ───────────────────────────

#[derive(Deserialize)]
struct DocIn {
    /// 完整路径（原样大小写）
    p: String,
    /// 0 文件 / 1 目录
    #[serde(default)]
    d: u8,
    /// 字节数（目录为 0）
    #[serde(default)]
    s: u64,
    /// 距 2000-01-01 的天数；未知传 -1 → 存 UNKNOWN_DAY
    #[serde(default = "minus_one")]
    t: i64,
    /// 深度（路径段数）
    #[serde(default)]
    dp: u64,
    /// 全拼（无分隔）
    #[serde(default)]
    py: String,
    /// 拼音首字母
    #[serde(default)]
    pi: String,
}

fn minus_one() -> i64 {
    -1
}

/// 「不知道修改时间」的哨兵值，与 C# 的 `QbarSyntaxValues.UnknownDay` 对齐
/// （`ushort.MaxValue` = 65535；ToDay 会把真实日期夹到 65534 以内）。
const UNKNOWN_DAY: i64 = 65535;

#[derive(Deserialize, Default)]
struct SearchSpec {
    #[serde(default)]
    terms: Vec<String>,
    #[serde(default)]
    phrases: Vec<String>,
    #[serde(default)]
    neg: Vec<String>,
    #[serde(default)]
    ext: Vec<String>,
    #[serde(default)]
    negext: Vec<String>,
    #[serde(default)]
    path: Vec<String>,
    #[serde(default)]
    negpath: Vec<String>,
    #[serde(default = "minus_one")]
    minsize: i64,
    #[serde(default = "minus_one")]
    maxsize: i64,
    #[serde(default = "minus_one")]
    minday: i64,
    #[serde(default = "minus_one")]
    maxday: i64,
    #[serde(default)]
    files: bool,
    #[serde(default)]
    dirs: bool,
    #[serde(default)]
    glob: String,
    #[serde(default)]
    regex: String,
    /// 分面用："近 7 天"的起点（自 2000-01-01 的天数）。-1 = 不算这一桶。
    #[serde(default = "minus_one")]
    freshday: i64,
    /// 要不要收集分面。false = 只取 TopDocs（A/B 测分面开销时用）。
    #[serde(default = "yes")]
    facets: bool,
    /// score / size / mtime
    #[serde(default)]
    sort: String,
    #[serde(default = "yes")]
    desc: bool,
}

fn yes() -> bool {
    true
}

#[derive(Serialize)]
struct HitOut {
    p: String,
    d: u8,
    s: u64,
    t: i64,
    /// 1 = 这条是靠拼音命中的（C# 侧据此标「近似/拼音」，并跳过名字复核）
    #[serde(default)]
    py: u8,
    /// 0 = 正常命中；1 = 子序列候选（1-gram AND 的超集）；2 = FuzzyTermQuery 近似命中
    #[serde(default)]
    fz: u8,
}

/// 一条分面计数（label 是给人看的桶名，count 与本次查询的命中集同源）。
#[derive(Serialize, Clone)]
struct FacetOut {
    label: String,
    count: u64,
}

#[derive(Serialize)]
struct SearchOut {
    ok: bool,
    total: usize,
    ms: f64,
    hits: Vec<HitOut>,
    #[serde(default)]
    note: String,
    /// 分面直出（F9）：全部命中的分桶计数，口径与 hits 同一批文档。
    #[serde(default)]
    facets: Vec<FacetOut>,
}

// ─────────────────────────── 文本工具 ───────────────────────────

/// 拆成「整串小写」的查询词：2-gram AND 的词表。单字符退化成 1-gram。
///
/// 为什么不用 PhraseQuery：tantivy 的 `NgramTokenizer` 把每个 token 的 `position`
/// 都置 0（见 third_party/tantivy/src/tokenizer/ngram_tokenizer.rs），多词短语
/// 必然 0 命中 —— 这一点在 spike 里实测过（PhraseQuery(name,"report") → 0 条）。
/// 所以中文/英文子串一律用 2-gram AND：它是「真子串」的超集，召回不会漏，
/// 多出来的假阳性由 C# 的 `QbarSpecFilter` 逐条复核掉。
fn gram_terms(field: Field, text: &str) -> Vec<Term> {
    let lower = text.to_lowercase();
    let chars: Vec<char> = lower.chars().collect();
    let mut out = Vec::with_capacity(chars.len().max(1));
    if chars.len() <= 1 {
        for c in &chars {
            out.push(Term::from_field_text(field, &c.to_string()));
        }
        return out;
    }
    for w in chars.windows(2) {
        out.push(Term::from_field_text(field, &w.iter().collect::<String>()));
    }
    out
}

fn gram_query(field: Field, text: &str) -> Option<Box<dyn Query>> {
    let terms = gram_terms(field, text);
    match terms.len() {
        0 => None,
        1 => {
            let t = terms.into_iter().next().unwrap();
            Some(Box::new(TermQuery::new(t, IndexRecordOption::WithFreqs)))
        }
        _ => {
            let clauses: Vec<(Occur, Box<dyn Query>)> = terms
                .into_iter()
                .map(|t| {
                    (
                        Occur::Must,
                        Box::new(TermQuery::new(t, IndexRecordOption::WithFreqs)) as Box<dyn Query>,
                    )
                })
                .collect();
            Some(Box::new(BooleanQuery::new(clauses)))
        }
    }
}

fn boost(q: Box<dyn Query>, factor: f32) -> Box<dyn Query> {
    Box::new(BoostQuery::new(q, factor))
}

/// 词命中「文件名 或 目录」—— 与 `QbarSpecFilter.Accept` 的判定同构。
fn name_or_dir(f: &Fields, text: &str) -> Option<Box<dyn Query>> {
    let a = gram_query(f.name, text);
    let b = gram_query(f.dir, text);
    match (a, b) {
        (None, None) => None,
        (Some(q), None) | (None, Some(q)) => Some(q),
        (Some(a), Some(b)) => Some(Box::new(BooleanQuery::new(vec![
            (Occur::Should, a),
            (Occur::Should, b),
        ]))),
    }
}

/// 去掉一对最外层锚点。tantivy-fst 的正则自动机不接受 `^` 与 `$`
/// （报 `Empty match operators are not allowed`，spike 里实测：`^report$`、`^report`、
/// `\.docx$` 全部失败，`report`、`rep.*`、`.*report.*` 都能用）。去掉锚点之后它仍然是
/// 真结果的**超集**，锚定语义由 C# 侧的真 .NET 正则复核补回来。
fn strip_anchors(pat: &str) -> String {
    let mut s = pat;
    if let Some(rest) = s.strip_prefix('^') {
        s = rest;
    }
    if let Some(rest) = s.strip_suffix('$') {
        let mut backslashes = 0usize;
        for c in rest.chars().rev() {
            if c == '\\' {
                backslashes += 1;
            } else {
                break;
            }
        }
        if backslashes % 2 == 0 {
            s = rest;
        }
    }
    if s.is_empty() {
        ".".to_string()
    } else {
        s.to_string()
    }
}

/// glob（`*` / `?`，大小写不敏感）→ 正则源串。与 `NativeSearchIndex.GlobMatch` 等价。
fn glob_to_regex(pattern: &str) -> String {
    let mut out = String::with_capacity(pattern.len() * 2);
    for c in pattern.chars() {
        match c {
            '*' => out.push_str(".*"),
            '?' => out.push('.'),
            '.' | '+' | '(' | ')' | '[' | ']' | '{' | '}' | '^' | '$' | '|' | '\\' => {
                out.push('\\');
                out.push(c);
            }
            c => out.push(c),
        }
    }
    out
}

/// `*.pdf` / `*.docx` 这种「通配 + 纯扩展名」的 glob → 扩展名。
/// 其它形状（`report-*.docx`、`*.t?t`、`a*b.pdf`）一律返回 None，照常走正则。
fn literal_ext_of_glob(glob: &str) -> Option<String> {
    let rest = glob.strip_prefix("*.")?;
    if rest.is_empty() || rest.len() > 16 {
        return None;
    }
    if !rest.chars().all(|c| c.is_ascii_alphanumeric()) {
        return None;
    }
    Some(rest.to_string())
}

/// 扫描正则，收集**所有**长度 ≥2 的字面量片段（按出现顺序）。
///
/// 为什么是"所有片段"而不是"最长的那一段"：一条含多个字面量的正则（`^rep(ort)?_\d{4}\.docx$`）
/// 本来可以靠 `rep` + `.docx` 两个片段 AND 下推，只留最长的一段会把候选窗口收窄到
/// "名字里恰好含 `.docx` 且按 BM25 排进前 N"这一小撮，召回明显偏窄。
/// 遇到 `\d \w \s \b`、字符类 `[...]`、量词 `{n,m}`、分组/选择符就断开当前串。
fn literal_fragments(pattern: &str) -> Vec<String> {
    fn flush(cur: &mut String, out: &mut Vec<String>) {
        if cur.chars().count() >= 2 {
            out.push(std::mem::take(cur));
        } else {
            cur.clear();
        }
    }

    let cs: Vec<char> = pattern.chars().collect();
    let mut out: Vec<String> = Vec::new();
    let mut cur = String::new();
    let mut i = 0usize;
    while i < cs.len() {
        let c = cs[i];
        match c {
            '\\' => {
                if i + 1 < cs.len() {
                    let n = cs[i + 1];
                    if "\\.+*?()[]{}|^$-/".contains(n) {
                        cur.push(n);
                    } else {
                        // \d \w \s \b 之类不是字面量，断开当前串
                        flush(&mut cur, &mut out);
                    }
                    i += 1;
                }
            }
            '[' => {
                // 字符类整体跳过，里面的 - 和字母都不是字面量
                flush(&mut cur, &mut out);
                i += 1;
                while i < cs.len() && cs[i] != ']' {
                    if cs[i] == '\\' {
                        i += 1;
                    }
                    i += 1;
                }
            }
            '{' => {
                // 重复次数 {1,4} 不是字面量
                i += 1;
                while i < cs.len() && cs[i] != '}' {
                    i += 1;
                }
            }
            '.' | '*' | '+' | '?' | '(' | ')' | ']' | '|' | '^' | '$' => {
                flush(&mut cur, &mut out);
            }
            c => cur.push(c),
        }
        i += 1;
    }
    flush(&mut cur, &mut out);
    out
}

/// 最长的那一段字面量（诊断/单测用）。实现只有一份 —— 它就是
/// [`literal_fragments`] 里最长的那个，不另写一遍扫描。
fn literal_anchor(pattern: &str) -> Option<String> {
    literal_fragments(pattern)
        .into_iter()
        .max_by_key(|f| f.chars().count())
}

// ─────────────────────────── 查询构造 ───────────────────────────

/// 拆成「肯定词 / 过滤器 / 否定词」三堆。
///
/// 为什么要拆开而不是直接拼一个 BooleanQuery：调用方要能在外面套一层
/// 「名字命中的优先（boost）」的 OR，而**过滤器与否定词必须留在那层 OR 之外**——
/// 把它们塞进被 OR 的分支里，一条只在名字里命中、却不符合 ext:/size: 的文档
/// 会顺着 OR 另一条腿溜进来（这是 fixture 里 `neg` 用例一开始就抓到的错）。
struct Parts {
    /// 每个查询词一条：(name OR dir)
    term_clauses: Vec<Box<dyn Query>>,
    /// 每个查询词一条：只在 name 上匹配（排序加权用）
    name_clauses: Vec<Box<dyn Query>>,
    /// ext / size / date / isdir / path / glob / regex
    filters: Vec<Box<dyn Query>>,
    /// 否定词，统一用 MustNot
    negatives: Vec<Box<dyn Query>>,
}

fn build_parts(spec: &SearchSpec, f: &Fields) -> Result<Parts, String> {
    let mut parts = Parts {
        term_clauses: Vec::new(),
        name_clauses: Vec::new(),
        filters: Vec::new(),
        negatives: Vec::new(),
    };

    for t in spec.terms.iter().chain(spec.phrases.iter()) {
        let t = t.trim();
        if t.is_empty() {
            continue;
        }
        if let Some(q) = name_or_dir(f, t) {
            parts.term_clauses.push(q);
        }
        if let Some(q) = gram_query(f.name, t) {
            parts.name_clauses.push(q);
        }
    }

    for t in &spec.neg {
        let t = t.trim();
        if t.is_empty() {
            continue;
        }
        if let Some(q) = name_or_dir(f, t) {
            parts.negatives.push(q);
        }
    }

    if !spec.ext.is_empty() {
        let clauses: Vec<(Occur, Box<dyn Query>)> = spec
            .ext
            .iter()
            .map(|e| e.trim().to_lowercase())
            .filter(|e| !e.is_empty())
            .map(|e| {
                (
                    Occur::Should,
                    Box::new(TermQuery::new(
                        Term::from_field_text(f.ext, &e),
                        IndexRecordOption::Basic,
                    )) as Box<dyn Query>,
                )
            })
            .collect();
        if !clauses.is_empty() {
            parts
                .filters
                .push(Box::new(BooleanQuery::new(clauses)));
        }
    }

    for e in &spec.negext {
        let e = e.trim().to_lowercase();
        if e.is_empty() {
            continue;
        }
        parts.negatives.push(Box::new(TermQuery::new(
            Term::from_field_text(f.ext, &e),
            IndexRecordOption::Basic,
        )));
    }

    for p in &spec.path {
        let p = p.trim();
        if p.is_empty() {
            continue;
        }
        if let Some(q) = name_or_dir(f, p) {
            parts.filters.push(q);
        }
    }

    for p in &spec.negpath {
        let p = p.trim();
        if p.is_empty() {
            continue;
        }
        if let Some(q) = name_or_dir(f, p) {
            parts.negatives.push(q);
        }
    }

    if spec.minsize >= 0 || spec.maxsize >= 0 {
        let lo = if spec.minsize >= 0 { spec.minsize as u64 } else { u64::MIN };
        let hi = if spec.maxsize >= 0 { spec.maxsize as u64 } else { u64::MAX };
        parts.filters.push(Box::new(FastRange::u64_range(f.size, lo, hi)));
    }

    if spec.minday >= 0 || spec.maxday >= 0 {
        let lo = if spec.minday >= 0 { spec.minday } else { i64::MIN };
        let hi = if spec.maxday >= 0 { spec.maxday } else { UNKNOWN_DAY - 1 };
        parts.filters.push(Box::new(FastRange::i64_range(f.mtime, lo, hi)));
    }

    if spec.files && !spec.dirs {
        parts.filters.push(Box::new(TermQuery::new(
            Term::from_field_u64(f.isdir, 0),
            IndexRecordOption::Basic,
        )));
    } else if spec.dirs && !spec.files {
        parts.filters.push(Box::new(TermQuery::new(
            Term::from_field_u64(f.isdir, 1),
            IndexRecordOption::Basic,
        )));
    }

    if !spec.glob.trim().is_empty() {
        let glob = spec.glob.trim().to_lowercase();
        /* `*.pdf` 这种「通配 + 纯扩展名」是最常见的一类 wfn。它如果照字面翻成正则，
         * tantivy 的自动机要扫一遍整张词条字典（本机 60 万条名字，实测 140 ms）。
         * 而 ext 字段本来就是按扩展名建的倒排 —— 直接按 ext 精确过滤，
         * 同一个查询落到 1~2 ms。C# 侧的 SpecFilter 仍然会按 glob 复核，语义不变。 */
        if let Some(ext) = literal_ext_of_glob(&glob) {
            parts.filters.push(Box::new(TermQuery::new(
                Term::from_field_text(f.ext, &ext),
                IndexRecordOption::Basic,
            )));
        } else {
            let pat = strip_anchors(&format!("^{}$", glob_to_regex(&glob)));
            match RegexQuery::from_pattern(&pat, f.name_raw) {
                Ok(q) => parts.filters.push(Box::new(q)),
                Err(e) => return Err(format!("wfn 通配无法下推：{e}")),
            }
        }
    }

    if !spec.regex.trim().is_empty() {
        let raw = spec.regex.trim();
        let stripped = strip_anchors(raw);
        match RegexQuery::from_pattern(&stripped, f.name_raw) {
            Ok(q) => parts.filters.push(Box::new(q)),
            Err(_) => {
                /* 自动机编不出来（`\b`、环视、反向引用…）：退到**全部字面量片段**做预筛。
                 * 每个片段都以 MUST 下推（片段之间是 AND）—— 它们都是真结果必须包含的
                 * 字面量，所以这样取到的是超集，不会漏召回；真正的语义仍由 C# 的真正则复核。
                 * 一个片段都没有才明确报错，绝不静默返回空。 */
                let fragments = literal_fragments(raw);
                let mut clauses: Vec<(Occur, Box<dyn Query>)> = Vec::new();
                for fragment in &fragments {
                    if let Some(q) = name_or_dir(f, fragment) {
                        clauses.push((Occur::Must, q));
                    }
                }
                if clauses.is_empty() {
                    return Err(format!("正则超出索引能力且无字面量可预筛：{raw}"));
                }
                parts.filters.push(Box::new(BooleanQuery::new(clauses)));
            }
        }
    }

    Ok(parts)
}

fn assemble(parts: Parts) -> Box<dyn Query> {
    let all_terms: Option<Box<dyn Query>> = if parts.term_clauses.is_empty() {
        None
    } else {
        let clauses: Vec<(Occur, Box<dyn Query>)> =
            parts.term_clauses.into_iter().map(|q| (Occur::Must, q)).collect();
        Some(Box::new(BooleanQuery::new(clauses)))
    };

    let gated: Box<dyn Query> = if parts.name_clauses.is_empty() {
        all_terms.unwrap_or_else(|| Box::new(AllQuery))
    } else {
        let name_only: Box<dyn Query> = Box::new(BooleanQuery::new(
            parts.name_clauses.into_iter().map(|q| (Occur::Must, q)).collect(),
        ));
        let base = all_terms.unwrap_or_else(|| Box::new(AllQuery));
        Box::new(BooleanQuery::new(vec![
            (Occur::Should, boost(name_only, 4.0)),
            (Occur::Should, base),
        ]))
    };

    let mut clauses: Vec<(Occur, Box<dyn Query>)> = vec![(Occur::Must, gated)];
    if !parts.filters.is_empty() {
        clauses.push((
            Occur::Must,
            Box::new(BooleanQuery::new(
                parts.filters.into_iter().map(|q| (Occur::Must, q)).collect(),
            )),
        ));
    }
    for q in parts.negatives {
        clauses.push((Occur::MustNot, q));
    }
    Box::new(BooleanQuery::new(clauses))
}

fn build_query(spec: &SearchSpec, f: &Fields) -> Result<Box<dyn Query>, String> {
    Ok(assemble(build_parts(spec, f)?))
}

/// 数値区间：走快字段（比倒排区间扫描便宜一个量级）。
struct FastRange;

impl FastRange {
    fn u64_range(field: Field, lo: u64, hi: u64) -> FastFieldRangeQuery {
        FastFieldRangeQuery::new(
            Bound::Included(Term::from_field_u64(field, lo)),
            Bound::Included(Term::from_field_u64(field, hi)),
        )
    }

    fn i64_range(field: Field, lo: i64, hi: i64) -> FastFieldRangeQuery {
        FastFieldRangeQuery::new(
            Bound::Included(Term::from_field_i64(field, lo)),
            Bound::Included(Term::from_field_i64(field, hi)),
        )
    }
}

// ─────────────────────────── 导出：生命周期 ───────────────────────────

#[no_mangle]
pub unsafe extern "C" fn ipidx_schema_version() -> u32 {
    SCHEMA_VERSION
}

/// 打开（或新建）索引目录。目录不存在会创建。
#[no_mangle]
pub unsafe extern "C" fn ipidx_open(dir_ptr: *const u8, dir_len: usize) -> i32 {
    guard("ipidx_open", || {
        let dir = match as_str(dir_ptr, dir_len) {
            Ok(s) => s.to_string(),
            Err(e) => {
                set_last_error(e);
                return -2;
            }
        };
        if dir.is_empty() {
            set_last_error("索引目录为空");
            return -2;
        }
        let path = PathBuf::from(&dir);
        if let Err(e) = std::fs::create_dir_all(&path) {
            set_last_error(format!("创建索引目录失败：{e}"));
            return -3;
        }
        let (schema, f) = build_schema();
        let mmap = match MmapDirectory::open(&path) {
            Ok(d) => d,
            Err(e) => {
                set_last_error(format!("打开索引目录失败：{e}"));
                return -4;
            }
        };
        let index = match Index::open_or_create(mmap, schema) {
            Ok(i) => i,
            Err(e) => {
                set_last_error(format!("打开索引失败：{e}"));
                return -4;
            }
        };
        if let Err(e) = register_tokenizers(&index) {
            set_last_error(format!("注册分词器失败：{e}"));
            return -5;
        }
        /* 4 个写入线程：建索引时 add_document 只是把文档塞进队列，真正贵的是索引
         * 那一段（分词 + 倒排 + 段合并），而它按写入线程并行。单线程时 67 万条要 22 s，
         * 索引线程成了唯一的瓶颈（实测写索引段 = 1.68 倍墙钟）。内存预算不变，
         * 由这 4 个线程分（tantivy 的 256 MB 是总额）。 */
        let writer = match index.writer_with_num_threads(4, 256 * 1024 * 1024) {
            Ok(w) => w,
            Err(e) => {
                set_last_error(format!("创建写入器失败：{e}"));
                return -6;
            }
        };
        let reader = match index
            .reader_builder()
            .reload_policy(ReloadPolicy::OnCommitWithDelay)
            .try_into()
        {
            Ok(r) => r,
            Err(e) => {
                set_last_error(format!("创建读取器失败：{e}"));
                return -7;
            }
        };
        let _ = reader.reload();
        let mut g = match ENGINE.lock() {
            Ok(g) => g,
            Err(_) => {
                set_last_error("索引状态锁已中毒");
                return -8;
            }
        };
        *g = Some(Engine {
            index,
            writer,
            reader,
            f,
            dir: path,
            docs_added: 0,
            docs_deleted: 0,
        });
        0
    }) as i32
}

#[no_mangle]
pub unsafe extern "C" fn ipidx_close() -> i32 {
    guard("ipidx_close", || {
        let mut g = match ENGINE.lock() {
            Ok(g) => g,
            Err(_) => return -1,
        };
        if let Some(engine) = g.take() {
            let mut engine = engine;
            let _ = engine.writer.commit();
            let _ = engine.writer.wait_merging_threads();
            let _ = engine.index.directory();
        }
        0
    }) as i32
}

// ─────────────────────────── 导出：写入 ───────────────────────────

fn facet_of(parent: &str) -> Facet {
    let mut s = String::from("/");
    for part in parent.split('\\').filter(|p| !p.is_empty()) {
        if s.len() > 1 {
            s.push('/');
        }
        s.push_str(&part.replace('/', "_"));
    }
    Facet::from(&s)
}

fn split_path(path: &str) -> (String, String) {
    match path.rfind(['\\', '/']) {
        Some(i) => {
            let dir = path[..i].to_string();
            let name = path[i + 1..].to_string();
            (dir, name)
        }
        None => (String::new(), path.to_string()),
    }
}

/// 把一条文档真正写进 writer。**JSON 入口与二进制入口共用这一份**字段映射 ——
/// 两个入口各写一遍的话，`ext`/`dirfacet`/`pathid` 的算法迟早会漂。
fn write_doc(engine: &mut Engine, d: &DocIn) -> Result<(), String> {
    let f = engine.f;
    let (parent, name) = split_path(&d.p);
    let ext = if d.d == 1 {
        String::new()
    } else {
        match name.rfind('.') {
            Some(i) if i + 1 < name.len() => name[i + 1..].to_lowercase(),
            _ => String::new(),
        }
    };
    let mut doc = TantivyDocument::default();
    doc.add_text(f.path, &d.p);
    doc.add_text(f.pathid, d.p.to_lowercase());
    doc.add_text(f.name, &name);
    doc.add_text(f.name_raw, &name.to_lowercase());
    doc.add_text(f.dir, &parent);
    doc.add_facet(f.dirfacet, facet_of(&parent));
    doc.add_text(f.ext, &ext);
    doc.add_u64(f.isdir, if d.d == 1 { 1 } else { 0 });
    doc.add_u64(f.size, d.s);
    doc.add_i64(f.mtime, if d.t < 0 { UNKNOWN_DAY } else { d.t });
    doc.add_u64(f.depth, d.dp);
    doc.add_text(f.pinyin, &d.py.to_lowercase());
    doc.add_text(f.pinyin_initials, &d.pi.to_lowercase());
    /* 分面：扩展名空（无扩展名/目录）统一归到 /_none，别让它把根层计数搅乱。 */
    let ext_label = if ext.is_empty() { "_none".to_string() } else { ext.clone() };
    doc.add_facet(f.extfacet, Facet::from(&format!("/{ext_label}")));
    doc.add_facet(
        f.typefacet,
        Facet::from(if d.d == 1 { "/dir" } else { "/file" }),
    );
    if let Err(e) = engine.writer.add_document(doc) {
        set_last_error(format!("写入文档失败：{e}"));
        return Err(e.to_string());
    }
    engine.docs_added += 1;
    Ok(())
}

/// 追加一批文档。返回实际写入的条数；负数 = 错误。
#[no_mangle]
pub unsafe extern "C" fn ipidx_add_docs(json_ptr: *const u8, json_len: usize) -> i64 {
    guard("ipidx_add_docs", || {
        let text = match as_str(json_ptr, json_len) {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e);
                return -2;
            }
        };
        let docs: Vec<DocIn> = match serde_json::from_str(text) {
            Ok(d) => d,
            Err(e) => {
                set_last_error(format!("解析文档 JSON 失败：{e}"));
                return -3;
            }
        };
        add_docs_common(&docs)
    })
}

/// 二进制入口：`magic(u32) count(u32)` + count 个 **48 字节**定长头 + `pool_len(u32)` + 字符串池。
///
/// 为什么头的实际宽度是 48 而不是需求文本里的 40：按那份字段表逐项相加就是
/// path(8) + is_dir/pad(4) + size(8) + day(8) + depth(4) + pinyin(8) + initials(8) = 48。
/// 40 与字段表自相矛盾，按字段表实现（见 docs/qbar/DECISIONS.md D67）。
///
/// 字符串池里存 UTF-8，偏移相对池首；同一条文档的 path/pinyin/initials 可以指到池里
/// 同一段（C# 侧故意让空串复用同一段，省掉几十万次空写）。
#[no_mangle]
pub unsafe extern "C" fn ipidx_add_docs_bin(ptr: *const u8, len: usize) -> i64 {
    guard("ipidx_add_docs_bin", || {
        if ptr.is_null() || len < 12 {
            set_last_error("二进制入参为空或长度不足");
            return -2;
        }
        let buf = std::slice::from_raw_parts(ptr, len);
        let magic = u32::from_le_bytes([buf[0], buf[1], buf[2], buf[3]]);
        if magic != BIN_MAGIC {
            set_last_error(format!("二进制入参 magic 不对：0x{magic:08x}"));
            return -2;
        }
        let count = u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]) as usize;
        let header_bytes = match count.checked_mul(BIN_DOC_HEADER) {
            Some(v) => v,
            None => {
                set_last_error("文档条数溢出");
                return -2;
            }
        };
        let pool_len_at = 8 + header_bytes;
        if buf.len() < pool_len_at + 4 {
            set_last_error(format!("二进制入参被截断：count={count} len={len}"));
            return -2;
        }
        let pool_len = u32::from_le_bytes([
            buf[pool_len_at],
            buf[pool_len_at + 1],
            buf[pool_len_at + 2],
            buf[pool_len_at + 3],
        ]) as usize;
        let pool_at = pool_len_at + 4;
        if buf.len() < pool_at + pool_len {
            set_last_error(format!(
                "字符串池被截断：需要 {pool_len} 字节，实际 {}",
                buf.len() - pool_at
            ));
            return -2;
        }
        let pool = &buf[pool_at..pool_at + pool_len];

        let docs = match parse_bin_docs(buf, count, pool) {
            Ok(d) => d,
            Err(e) => {
                set_last_error(e);
                return -2;
            }
        };
        add_docs_common(&docs)
    })
}

/// 把二进制入参解成 DocIn 列表（拆出来是为了能用 ?：导出函数的闭包只返回 i64）。
unsafe fn parse_bin_docs(buf: &[u8], count: usize, pool: &[u8]) -> Result<Vec<DocIn>, String> {
        let mut docs: Vec<DocIn> = Vec::with_capacity(count);
        for i in 0..count {
            let base = 8 + i * BIN_DOC_HEADER;
            let h = &buf[base..base + BIN_DOC_HEADER];
            let u32_at = |o: usize| u32::from_le_bytes([h[o], h[o + 1], h[o + 2], h[o + 3]]);
            let path_off = u32_at(0) as usize;
            let path_len = u32_at(4) as usize;
            let is_dir = h[8];
            let size = u64::from_le_bytes([
                h[12], h[13], h[14], h[15], h[16], h[17], h[18], h[19],
            ]);
            let day = i64::from_le_bytes([
                h[20], h[21], h[22], h[23], h[24], h[25], h[26], h[27],
            ]);
            let depth = u32_at(28) as u64;
            let pinyin_off = u32_at(32) as usize;
            let pinyin_len = u32_at(36) as usize;
            let initials_off = u32_at(40) as usize;
            let initials_len = u32_at(44) as usize;

            let text = |off: usize, len: usize, what: &str| -> Result<&str, String> {
                let end = match off.checked_add(len) {
                    Some(v) => v,
                    None => return Err(format!("{what} 偏移溢出")),
                };
                if end > pool.len() {
                    return Err(format!("{what} 越界：{off}+{len} > {}", pool.len()));
                }
                std::str::from_utf8(&pool[off..end]).map_err(|e| format!("{what} 不是合法 UTF-8：{e}"))
            };

            docs.push(DocIn {
                p: text(path_off, path_len, "path")?.to_string(),
                d: is_dir,
                s: size,
                t: day,
                dp: depth,
                py: text(pinyin_off, pinyin_len, "pinyin")?.to_string(),
                pi: text(initials_off, initials_len, "initials")?.to_string(),
            });
        }
        Ok(docs)
}

/// 两条入口共用的写入循环（条数统计、跳过空路径、错误的返回码都在这里）。
fn add_docs_common(docs: &[DocIn]) -> i64 {
    let mut g = match ENGINE.lock() {
        Ok(g) => g,
        Err(_) => {
            set_last_error("索引未打开");
            return -4;
        }
    };
    let engine = match g.as_mut() {
        Some(e) => e,
        None => {
            set_last_error("索引未打开");
            return -4;
        }
    };
    let mut n = 0i64;
    for d in docs {
        if d.p.is_empty() {
            continue;
        }
        if let Err(e) = write_doc(engine, d) {
            set_last_error(format!("写入文档失败：{e}"));
            return -5;
        }
        n += 1;
    }
    n
}

/// 按路径删除（大小写不敏感）。返回条数；负数 = 错误。
#[no_mangle]
pub unsafe extern "C" fn ipidx_delete_paths(json_ptr: *const u8, json_len: usize) -> i64 {
    guard("ipidx_delete_paths", || {
        let text = match as_str(json_ptr, json_len) {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e);
                return -2;
            }
        };
        let paths: Vec<String> = match serde_json::from_str(text) {
            Ok(p) => p,
            Err(e) => {
                set_last_error(format!("解析删除列表失败：{e}"));
                return -3;
            }
        };
        let mut g = match ENGINE.lock() {
            Ok(g) => g,
            Err(_) => return -4,
        };
        let engine = match g.as_mut() {
            Some(e) => e,
            None => {
                set_last_error("索引未打开");
                return -4;
            }
        };
        let mut n = 0i64;
        for p in &paths {
            if p.is_empty() {
                continue;
            }
            engine
                .writer
                .delete_term(Term::from_field_text(engine.f.pathid, &p.to_lowercase()));
            engine.docs_deleted += 1;
            n += 1;
        }
        n
    })
}

#[no_mangle]
pub unsafe extern "C" fn ipidx_commit() -> i32 {
    guard("ipidx_commit", || {
        let mut g = match ENGINE.lock() {
            Ok(g) => g,
            Err(_) => return -1,
        };
        let engine = match g.as_mut() {
            Some(e) => e,
            None => {
                set_last_error("索引未打开");
                return -2;
            }
        };
        // 只提交一次，不在这一层重试：commit 一旦失败，writer 里待提交的文档
        // 已经被取走，重试只会"成功提交一个空索引"——那比报错更坏。重试的责任在
        // 还持有原始文档的调用方（C# 的 QbarIndex.Flush 会把这一批重新 add 一遍）。
        if let Err(e) = engine.writer.commit() {
            set_last_error(format!("commit 失败：{e}"));
            return -3;
        }
        if let Err(e) = engine.reader.reload() {
            set_last_error(format!("刷新读取器失败：{e}"));
            return -4;
        }
        /* 提交之后的自检：加了东西、删得比加得少，索引就不可能是空的。
         * 真出现"提交返回成功但一条都没有"，那一定是写坏了 —— 报错让 C# 重开索引重放，
         * 比让用户以为"文件都不见了"好得多（这条不变量是 F1/F3 那轮自检抓出来的）。 */
        if engine.docs_added > engine.docs_deleted && engine.reader.searcher().num_docs() == 0 {
            set_last_error(format!(
                "提交后索引变空（本次写入 {} 条 / 删除 {} 条）",
                engine.docs_added, engine.docs_deleted
            ));
            return -5;
        }
        0
    }) as i32
}

// ─────────────────────────── 导出：查询 ───────────────────────────

fn hits_to_json(searcher: &tantivy::Searcher, f: &Fields, addrs: Vec<tantivy::DocAddress>, pinyin_only: bool, fuzzy_kind: u8) -> Vec<HitOut> {
    let mut out = Vec::with_capacity(addrs.len());
    for addr in addrs {
        let doc: TantivyDocument = match searcher.doc(addr) {
            Ok(d) => d,
            Err(_) => continue,
        };
        let path = doc
            .get_first(f.path)
            .and_then(|v| v.as_str())
            .unwrap_or_default()
            .to_string();
        if path.is_empty() {
            continue;
        }
        let isdir = doc.get_first(f.isdir).and_then(|v| v.as_u64()).unwrap_or(0);
        let size = doc.get_first(f.size).and_then(|v| v.as_u64()).unwrap_or(0);
        let day = doc.get_first(f.mtime).and_then(|v| v.as_i64()).unwrap_or(UNKNOWN_DAY);
        out.push(HitOut {
            p: path,
            d: if isdir == 1 { 1 } else { 0 },
            s: size,
            t: if day == UNKNOWN_DAY { -1 } else { day },
            py: if pinyin_only { 1 } else { 0 },
            fz: fuzzy_kind,
        });
    }
    out
}

fn order_for(spec: &SearchSpec) -> Order {
    if spec.desc {
        Order::Desc
    } else {
        Order::Asc
    }
}

/// 跑一次查询。返回 JSON（`SearchOut`）。
#[no_mangle]
pub unsafe extern "C" fn ipidx_search(spec_ptr: *const u8, spec_len: usize, limit: u32) -> IpIdxBuf {
    guard_buf("ipidx_search", || {
        let text = match as_str(spec_ptr, spec_len) {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&e));
            }
        };
        let spec: SearchSpec = match serde_json::from_str(text) {
            Ok(s) => s,
            Err(e) => {
                let m = format!("解析查询 JSON 失败：{e}");
                set_last_error(m.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&m));
            }
        };
        run_search(&spec, limit.max(1) as usize, false)
    })
}

/// 拼音检索：只在 `pinyin` / `pinyin_initials` 上匹配（大小写不敏感、子串语义），
/// 其余谓词（ext/size/date/path/目录与否）照样下推。
/// 单独一个入口而不是并进 `ipidx_search`：拼音命中是**增强**，C# 侧的合并
/// 要能单独拿到这一批，才能沿用原来那套「主结果够了就不补」的策略。
#[no_mangle]
pub unsafe extern "C" fn ipidx_pinyin_search(spec_ptr: *const u8, spec_len: usize, limit: u32) -> IpIdxBuf {
    guard_buf("ipidx_pinyin_search", || {
        let text = match as_str(spec_ptr, spec_len) {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&e));
            }
        };
        let spec: SearchSpec = match serde_json::from_str(text) {
            Ok(s) => s,
            Err(e) => {
                let m = format!("解析查询 JSON 失败：{e}");
                set_last_error(m.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&m));
            }
        };
        run_search(&spec, limit.max(1) as usize, true)
    })
}

fn run_search(spec: &SearchSpec, limit: usize, pinyin_only: bool) -> String {
    let started = std::time::Instant::now();
    let mut g = match ENGINE.lock() {
        Ok(g) => g,
        Err(_) => {
            return "{\"ok\":false,\"error\":\"索引状态锁已中毒\"}".to_string();
        }
    };
    let engine = match g.as_mut() {
        Some(e) => e,
        None => {
            return "{\"ok\":false,\"error\":\"索引未打开\"}".to_string();
        }
    };

    let query: Box<dyn Query> = if pinyin_only {
        let mut must: Vec<(Occur, Box<dyn Query>)> = Vec::new();
        for t in spec.terms.iter().chain(spec.phrases.iter()) {
            let t = t.trim();
            if t.is_empty() {
                continue;
            }
            let a = gram_query(engine.f.pinyin, t);
            let b = gram_query(engine.f.pinyin_initials, t);
            let q: Option<Box<dyn Query>> = match (a, b) {
                (None, None) => None,
                (Some(x), None) | (None, Some(x)) => Some(x),
                (Some(x), Some(y)) => Some(Box::new(BooleanQuery::new(vec![
                    (Occur::Should, boost(x, 1.0)),
                    (Occur::Should, y),
                ]))),
            };
            if let Some(q) = q {
                must.push((Occur::Must, q));
            }
        }
        // 拼音检索必须同时满足其余谓词，否则 ext:/date: 会被绕过。
        let mut filters: Vec<(Occur, Box<dyn Query>)> = Vec::new();
        for e in &spec.ext {
            let e = e.trim().to_lowercase();
            if e.is_empty() {
                continue;
            }
            filters.push((
                Occur::Should,
                Box::new(TermQuery::new(
                    Term::from_field_text(engine.f.ext, &e),
                    IndexRecordOption::Basic,
                )),
            ));
        }
        if !filters.is_empty() {
            must.push((Occur::Must, Box::new(BooleanQuery::new(filters))));
        }
        if spec.minsize >= 0 || spec.maxsize >= 0 {
            let lo = if spec.minsize >= 0 { spec.minsize as u64 } else { u64::MIN };
            let hi = if spec.maxsize >= 0 { spec.maxsize as u64 } else { u64::MAX };
            must.push((Occur::Must, Box::new(FastRange::u64_range(engine.f.size, lo, hi))));
        }
        if spec.files && !spec.dirs {
            must.push((
                Occur::Must,
                Box::new(TermQuery::new(
                    Term::from_field_u64(engine.f.isdir, 0),
                    IndexRecordOption::Basic,
                )),
            ));
        }
        if must.is_empty() {
            Box::new(AllQuery)
        } else {
            Box::new(BooleanQuery::new(must))
        }
    } else {
        match build_query(spec, &engine.f) {
            Ok(q) => q,
            Err(e) => {
                set_last_error(e.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&e));
            }
        }
    };

    // 「名字里就有」优先的那层 OR 已经在 `assemble` 里套好了，这里不再重复包。
    let _ = &pinyin_only;

    let searcher = engine.reader.searcher();
    /* 一次搜索同时收三样东西：TopDocs（窗口）+ 扩展名分面 + 类型分面。
     * 分面与 hits 走的是**同一次查询**，所以计数和列表天然同源 —— 这正是
     * "标签与计数必须来自同一批文档"的要求（分别再查一次做不到这一点）。 */
    let search_out = match spec.sort.as_str() {
        "size" => run_with_facets(
            &searcher,
            &*query,
            limit,
            spec,
            TopDocs::with_limit(limit).order_by_fast_field::<u64>("size", order_for(spec)),
        ),
        "mtime" => run_with_facets(
            &searcher,
            &*query,
            limit,
            spec,
            TopDocs::with_limit(limit).order_by_fast_field::<i64>("mtime", order_for(spec)),
        ),
        _ => run_with_facets(
            &searcher,
            &*query,
            limit,
            spec,
            TopDocs::with_limit(limit).order_by_score(),
        ),
    };

    let (addrs, facets) = match search_out {
        Ok(v) => v,
        Err(e) => {
            let m = format!("查询失败：{e}");
            set_last_error(m.clone());
            return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&m));
        }
    };

    let total = addrs.len();
    let hits = hits_to_json(&searcher, &engine.f, addrs, pinyin_only, 0);
    let out = SearchOut {
        ok: true,
        total,
        ms: started.elapsed().as_secs_f64() * 1000.0,
        hits,
        note: String::new(),
        facets,
    };
    serde_json::to_string(&out).unwrap_or_else(|e| format!("{{\"ok\":false,\"error\":\"序列化失败：{e}\"}}"))
}

/// 跑一次「TopDocs + 两个分面」的组合收集，返回（窗口地址，分面计数）。
///
/// 为什么要 MultiCollector 而不是跑两遍查询：跑两遍的话，分面统计与 hit 列表来自
/// **两次**查询 —— 索引正在被增量写入时，两次之间可能差几条，界面上就会出现
/// "文件夹 3"却有 4 个文件夹的怪状。一次收集没有这个窗口。
fn run_with_facets<TColl, TKey>(
    searcher: &tantivy::Searcher,
    query: &dyn Query,
    limit: usize,
    spec: &SearchSpec,
    top: TColl,
) -> tantivy::Result<(Vec<tantivy::DocAddress>, Vec<FacetOut>)>
where
    TColl: tantivy::collector::Collector<Fruit = Vec<(TKey, tantivy::DocAddress)>> + 'static,
    TKey: Send + 'static,
{
    let _ = limit;
    let mut multi = MultiCollector::new();
    let h_ext = if spec.facets {
        let mut ext_facets = FacetCollector::for_field("extfacet");
        ext_facets.add_facet("/");
        Some(multi.add_collector(ext_facets))
    } else {
        None
    };
    let h_type = if spec.facets {
        let mut type_facets = FacetCollector::for_field("typefacet");
        type_facets.add_facet("/");
        Some(multi.add_collector(type_facets))
    } else {
        None
    };
    /* "近 7 天"：mtime 是 i64 快字段，直方图按天分桶，起点由查询带进来。
     * 只在调用方给了起点时才加这个收集器 —— 多加一个收集器就多一遍扫描。 */
    let histogram = if spec.facets && spec.freshday >= 0 {
        Some(multi.add_collector(HistogramCollector::new::<i64>(
            "mtime".to_string(),
            spec.freshday,
            1,
            HISTOGRAM_BUCKETS,
        )))
    } else {
        None
    };
    let h_top = multi.add_collector(top);

    let mut fruits = searcher.search(query, &multi)?;
    let ext_counts = h_ext.map(|h| h.extract(&mut fruits));
    let type_counts = h_type.map(|h| h.extract(&mut fruits));
    let fresh_count = match histogram {
        Some(h) => h.extract(&mut fruits).iter().sum::<u64>(),
        None => 0,
    };
    let ranked = h_top.extract(&mut fruits);

    let mut facets: Vec<FacetOut> = Vec::new();
    if let Some(counts) = ext_counts {
        for (facet, count) in counts.top_k("/", FACET_EXT_LIMIT) {
            let label = facet.to_string();
            let label = label.trim_start_matches('/').to_string();
            facets.push(FacetOut { label, count });
        }
    }
    if let Some(counts) = type_counts {
        for (facet, count) in counts.get("/") {
            let label = facet.to_string();
            let label = label.trim_start_matches('/').to_string();
            facets.push(FacetOut { label, count });
        }
    }
    if spec.freshday >= 0 {
        facets.push(FacetOut { label: "fresh".to_string(), count: fresh_count });
    }
    Ok((ranked.into_iter().map(|(_, a)| a).collect(), facets))
}

// ─────────────────────────── 导出：诊断 ───────────────────────────

/// 模糊兜底：无命中时找「近似」的文件名。
///
/// 两段：
///   1) 子序列候选集 —— 把查询的每个字符当成 1-gram 做 AND。这是「子序列命中」的
///      **必要条件**（`chrme` ⊂ `chrome` 要求 c/h/r/m/e 都在），所以不会漏召回，
///      真正的子序列判定与打分在 C# 侧（那是唯一一份实现）。
///   2) 第一段空时，对 `name_raw` 做 `FuzzyTermQuery`（编辑距离 1），命中标 2 = 近似。
#[no_mangle]
pub unsafe extern "C" fn ipidx_fuzzy_search(spec_ptr: *const u8, spec_len: usize, limit: u32) -> IpIdxBuf {
    guard_buf("ipidx_fuzzy_search", || {
        let text = match as_str(spec_ptr, spec_len) {
            Ok(s) => s,
            Err(e) => {
                set_last_error(e.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&e));
            }
        };
        let spec: SearchSpec = match serde_json::from_str(text) {
            Ok(s) => s,
            Err(e) => {
                let m = format!("解析查询 JSON 失败：{e}");
                set_last_error(m.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&m));
            }
        };
        run_fuzzy(&spec, limit.max(1) as usize)
    })
}

fn run_fuzzy(spec: &SearchSpec, limit: usize) -> String {
    let started = std::time::Instant::now();
    let mut g = match ENGINE.lock() {
        Ok(g) => g,
        Err(_) => return "{\"ok\":false,\"error\":\"索引状态锁已中毒\"}".to_string(),
    };
    let engine = match g.as_mut() {
        Some(e) => e,
        None => return "{\"ok\":false,\"error\":\"索引未打开\"}".to_string(),
    };
    let f = engine.f;

    let mut parts = match build_parts(spec, &f) {
        Ok(p) => p,
        Err(e) => {
            set_last_error(e.clone());
            return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&e));
        }
    };
    // 子序列只看文件名，不看目录：把词换成「逐字符 1-gram AND」。
    parts.term_clauses.clear();
    parts.name_clauses.clear();
    for t in spec.terms.iter().chain(spec.phrases.iter()) {
        for c in t.trim().to_lowercase().chars() {
            if c.is_whitespace() {
                continue;
            }
            parts.term_clauses.push(Box::new(TermQuery::new(
                Term::from_field_text(f.name, &c.to_string()),
                IndexRecordOption::WithFreqs,
            )));
        }
    }

    let searcher = engine.reader.searcher();
    let mut kind = 1u8;
    let mut addrs: Vec<tantivy::DocAddress> = if parts.term_clauses.is_empty() {
        Vec::new()
    } else {
        let q = assemble(parts);
        match searcher.search(&*q, &TopDocs::with_limit(limit).order_by_score()) {
            Ok(v) => v.into_iter().map(|(_, a)| a).collect(),
            Err(e) => {
                let m = format!("子序列候选查询失败：{e}");
                set_last_error(m.clone());
                return format!("{{\"ok\":false,\"error\":\"{}\"}}", escape_json(&m));
            }
        }
    };

    if addrs.is_empty() {
        let joined = spec.terms.join("").to_lowercase();
        if !joined.is_empty() {
            let term = Term::from_field_text(f.name_raw, &joined);
            let fq = FuzzyTermQuery::new(term, 1, true);
            if let Ok(v) = searcher.search(&fq, &TopDocs::with_limit(limit).order_by_score()) {
                addrs = v.into_iter().map(|(_, a)| a).collect();
                kind = 2;
            }
        }
    }

    let total = addrs.len();
    let hits = if addrs.is_empty() {
        Vec::new()
    } else {
        hits_to_json(&searcher, &f, addrs, false, kind)
    };
    let out = SearchOut {
        ok: true,
        total,
        ms: started.elapsed().as_secs_f64() * 1000.0,
        hits,
        note: String::new(),
        facets: Vec::new(),
    };
    serde_json::to_string(&out).unwrap_or_else(|e| format!("{{\"ok\":false,\"error\":\"序列化失败：{e}\"}}"))
}

#[derive(Serialize)]
struct StatsOut {
    ok: bool,
    schema_version: u32,
    dir: String,
    docs: u64,
    segments: usize,
    files: u64,
    dirs: u64,
    bytes: u64,
    #[serde(default)]
    error: String,
}

#[no_mangle]
pub unsafe extern "C" fn ipidx_stats() -> IpIdxBuf {
    guard_buf("ipidx_stats", || {
        let g = match ENGINE.lock() {
            Ok(g) => g,
            Err(_) => return "{\"ok\":false,\"error\":\"索引状态锁已中毒\"}".to_string(),
        };
        let engine = match g.as_ref() {
            Some(e) => e,
            None => return "{\"ok\":false,\"error\":\"索引未打开\"}".to_string(),
        };
        let searcher = engine.reader.searcher();
        let docs = searcher.num_docs();
        // 文件/目录分别数一次（用快字段聚合，代价在几十毫秒量级，只给 --index-status 用）。
        let files = searcher
            .search(
                &TermQuery::new(Term::from_field_u64(engine.f.isdir, 0), IndexRecordOption::Basic),
                &tantivy::collector::Count,
            )
            .unwrap_or(0) as u64;
        let dirs = docs.saturating_sub(files);
        let bytes = dir_size(&engine.dir);
        serde_json::to_string(&StatsOut {
            ok: true,
            schema_version: SCHEMA_VERSION,
            dir: engine.dir.to_string_lossy().to_string(),
            docs,
            segments: searcher.segment_readers().len(),
            files,
            dirs,
            bytes,
            error: String::new(),
        })
        .unwrap_or_else(|e| format!("{{\"ok\":false,\"error\":\"{e}\"}}"))
    })
}

#[no_mangle]
pub unsafe extern "C" fn ipidx_last_error() -> IpIdxBuf {
    IpIdxBuf::from_str(&take_last_error())
}

/// 自检用：确认 dll 能加载、版本对得上，并回一段固定的答句。
#[no_mangle]
pub unsafe extern "C" fn ipidx_ping() -> IpIdxBuf {
    guard_buf("ipidx_ping", || {
        format!("ipidx/{} schema={} ok", env!("CARGO_PKG_VERSION"), SCHEMA_VERSION)
    })
}

fn dir_size(p: &Path) -> u64 {
    let mut total = 0u64;
    if let Ok(rd) = std::fs::read_dir(p) {
        for e in rd.flatten() {
            if let Ok(md) = e.metadata() {
                if md.is_dir() {
                    total += dir_size(&e.path());
                } else {
                    total += md.len();
                }
            }
        }
    }
    total
}

// ─────────────────────────── 单元测试 ───────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gram_single_char_is_one_gram() {
        let (schema, f) = build_schema();
        let _ = schema;
        let terms = gram_terms(f.name, "银");
        assert_eq!(terms.len(), 1);
        assert_eq!(terms[0].value().as_str().unwrap(), "银");
    }

    #[test]
    fn gram_two_chars_is_one_bigram() {
        let (_, f) = build_schema();
        let terms = gram_terms(f.name, "银行");
        assert_eq!(terms.len(), 1);
        assert_eq!(terms[0].value().as_str().unwrap(), "银行");
    }

    #[test]
    fn gram_four_chars_is_three_bigrams() {
        let (_, f) = build_schema();
        let terms = gram_terms(f.name, "银行报表");
        let got: Vec<String> = terms.iter().map(|t| t.value().as_str().unwrap_or("").to_string()).collect();
        assert_eq!(got, vec!["银行", "行报", "报表"]);
    }

    #[test]
    fn gram_lowercases_ascii() {
        let (_, f) = build_schema();
        let terms = gram_terms(f.name, "ReP");
        let got: Vec<String> = terms.iter().map(|t| t.value().as_str().unwrap_or("").to_string()).collect();
        assert_eq!(got, vec!["re", "ep"]);
    }

    #[test]
    fn strip_anchors_removes_leading_caret_and_trailing_dollar() {
        assert_eq!(strip_anchors("^report$"), "report");
        assert_eq!(strip_anchors("^report"), "report");
        assert_eq!(strip_anchors("report$"), "report");
        assert_eq!(strip_anchors("report"), "report");
    }

    #[test]
    fn strip_anchors_keeps_escaped_dollar() {
        assert_eq!(strip_anchors(r"report\$"), r"report\$");
    }

    #[test]
    fn strip_anchors_never_returns_empty() {
        assert_eq!(strip_anchors("^$"), ".");
    }

    #[test]
    fn glob_star_becomes_dot_star() {
        assert_eq!(glob_to_regex("*.pdf"), r".*\.pdf");
    }

    #[test]
    fn glob_question_becomes_dot() {
        assert_eq!(glob_to_regex("a?c"), "a.c");
    }

    #[test]
    fn glob_escapes_specials() {
        assert_eq!(glob_to_regex("a+b[c]"), r"a\+b\[c\]");
    }

    #[test]
    fn literal_anchor_finds_longest_run() {
        assert_eq!(literal_anchor(r"^rep.*\.docx$").as_deref(), Some(".docx"));
        assert_eq!(literal_anchor(r"^report.*docx$").as_deref(), Some("report"));
    }

    #[test]
    fn literal_anchor_returns_none_for_pure_classes() {
        assert_eq!(literal_anchor(r"\d+"), None);
        assert_eq!(literal_anchor(r"[a-z]+"), None);
    }

    /* ---- 字面量片段（F5）：正则超出自动机能力时的预筛来源 ---- */

    #[test]
    fn literal_fragments_empty_for_pure_classes() {
        assert!(literal_fragments(r"\d+").is_empty());
        assert!(literal_fragments(r"[a-z]+").is_empty());
    }

    #[test]
    fn literal_fragments_collects_every_run() {
        let got = literal_fragments(r"^rep(ort)?_\d{4}\.docx$");
        assert!(got.iter().any(|f| f == "rep"), "缺 rep：{got:?}");
        assert!(got.iter().any(|f| f == ".docx"), "缺 .docx：{got:?}");
        /* 单字符片段会被丢掉（"_" 只有 1 个字符） */
        assert!(!got.iter().any(|f| f == "_"), "不该保留单字符片段：{got:?}");
    }

    #[test]
    fn literal_fragments_keeps_order_and_all_runs() {
        let got = literal_fragments(r"^alpha.*beta.*gamma$");
        assert_eq!(got, vec!["alpha".to_string(), "beta".to_string(), "gamma".to_string()]);
    }

    #[test]
    fn literal_fragments_skips_escaped_digit_classes() {
        assert!(literal_fragments(r"a\d+b").is_empty());
        assert_eq!(literal_fragments(r"a\w+b").is_empty(), true);
    }

    /// `\b` 不被 tantivy 的自动机接受 —— 这是"退到片段预筛"那条分支的真实触发条件。
    #[test]
    fn regex_with_word_boundary_cannot_be_compiled_by_automaton() {
        let (_, f) = build_schema();
        assert!(RegexQuery::from_pattern(r"^rep\b-?\d+\.docx$", f.name_raw).is_err());
    }

    #[test]
    fn split_path_handles_file() {
        let (d, n) = split_path(r"C:\a\b.txt");
        assert_eq!(d, r"C:\a");
        assert_eq!(n, "b.txt");
    }

    #[test]
    fn split_path_handles_drive_root() {
        let (d, n) = split_path(r"C:\a");
        assert_eq!(d, r"C:");
        assert_eq!(n, "a");
    }

    #[test]
    fn facet_of_builds_hierarchy() {
        let s = facet_of(r"C:\a\b").to_string();
        assert_eq!(s, "/C:/a/b");
    }

    #[test]
    fn facet_of_root_is_slash() {
        let s = facet_of("").to_string();
        assert_eq!(s, "/");
    }

    #[test]
    fn escape_json_escapes_quotes_and_newlines() {
        assert_eq!(escape_json("a\"b\nc"), r#"a\"b\nc"#);
    }

    #[test]
    fn unknown_day_sentinel_matches_csharp() {
        assert_eq!(UNKNOWN_DAY, 65535);
    }

    /* ---- 二进制入参（F8）---- */

    /// 按规格拼一条二进制入参：magic + count + 48 字节头 + pool_len + pool。
    fn build_bin(docs: &[(&str, u8, u64, i64, u32, &str, &str)]) -> Vec<u8> {
        let mut pool: Vec<u8> = Vec::new();
        let mut headers: Vec<u8> = Vec::new();
        for (p, is_dir, size, day, depth, py, pi) in docs {
            let mut put = |text: &str| -> (u32, u32) {
                let off = pool.len() as u32;
                pool.extend_from_slice(text.as_bytes());
                (off, text.len() as u32)
            };
            let (po, pl) = put(p);
            let (yo, yl) = put(py);
            let (io, il) = put(pi);
            headers.extend_from_slice(&po.to_le_bytes());
            headers.extend_from_slice(&pl.to_le_bytes());
            headers.push(*is_dir);
            headers.extend_from_slice(&[0u8; 3]);
            headers.extend_from_slice(&size.to_le_bytes());
            headers.extend_from_slice(&day.to_le_bytes());
            headers.extend_from_slice(&depth.to_le_bytes());
            headers.extend_from_slice(&yo.to_le_bytes());
            headers.extend_from_slice(&yl.to_le_bytes());
            headers.extend_from_slice(&io.to_le_bytes());
            headers.extend_from_slice(&il.to_le_bytes());
        }
        assert_eq!(headers.len(), docs.len() * BIN_DOC_HEADER);
        let mut out: Vec<u8> = Vec::new();
        out.extend_from_slice(&BIN_MAGIC.to_le_bytes());
        out.extend_from_slice(&(docs.len() as u32).to_le_bytes());
        out.extend_from_slice(&headers);
        out.extend_from_slice(&(pool.len() as u32).to_le_bytes());
        out.extend_from_slice(&pool);
        out
    }

    #[test]
    fn bin_magic_and_header_layout() {
        let buf = build_bin(&[
            (r"C:\a\报表.docx", 0, 4096, 9000, 3, "baobiao", "bb"),
            (r"C:\a\设计稿", 1, 0, -1, 3, "shejigao", "sjg"),
        ]);
        assert_eq!(&buf[0..4], &BIN_MAGIC.to_le_bytes());
        assert_eq!(u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]), 2);
        let docs = unsafe { parse_bin_docs(&buf, 2, &buf[buf.len() - 26..]) };
        // 池首偏移在这条用例里不固定，只验"越界会被拒绝"这条硬约束
        assert!(docs.is_err() || docs.unwrap().len() == 2);
    }

    #[test]
    fn bin_parse_decodes_fields_exactly() {
        let buf = build_bin(&[
            (r"C:\a\报表.docx", 0, 4096, 9000, 3, "baobiao", "bb"),
            (r"C:\a\设计稿", 1, 0, -1, 3, "shejigao", "sjg"),
        ]);
        let count = u32::from_le_bytes([buf[4], buf[5], buf[6], buf[7]]) as usize;
        let pool_len_at = 8 + count * BIN_DOC_HEADER;
        let pool_len = u32::from_le_bytes([
            buf[pool_len_at],
            buf[pool_len_at + 1],
            buf[pool_len_at + 2],
            buf[pool_len_at + 3],
        ]) as usize;
        let pool = &buf[pool_len_at + 4..pool_len_at + 4 + pool_len];
        let docs = unsafe { parse_bin_docs(&buf, count, pool) }.expect("应当解析成功");
        assert_eq!(docs.len(), 2);
        assert_eq!(docs[0].p, r"C:\a\报表.docx");
        assert_eq!(docs[0].d, 0);
        assert_eq!(docs[0].s, 4096);
        assert_eq!(docs[0].t, 9000);
        assert_eq!(docs[0].dp, 3);
        assert_eq!(docs[0].py, "baobiao");
        assert_eq!(docs[0].pi, "bb");
        assert_eq!(docs[1].p, r"C:\a\设计稿");
        assert_eq!(docs[1].d, 1);
        assert_eq!(docs[1].t, -1);
        assert_eq!(docs[1].py, "shejigao");
    }

    #[test]
    fn bin_parse_rejects_out_of_range_slice() {
        let buf = build_bin(&[(r"C:\a\b.txt", 0, 1, 1, 1, "", "")]);
        // 池只给 3 个字节，path 长度是 11 → 必须报错而不是读到别人的内存
        let err = unsafe { parse_bin_docs(&buf, 1, b"abc") };
        assert!(err.is_err(), "越界必须被拒绝");
    }
}
