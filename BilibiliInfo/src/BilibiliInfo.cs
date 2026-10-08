using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace BilibiliInfo
{
    public enum QueryMode { Video, Uploader }

    public sealed class InputItem
    {
        public string Original;
        public string Bvid;
        public string Uid;
        public Uri ShortUrl;
    }

    public static class InputParser
    {
        private static readonly Regex Bv = new Regex("^BV[0-9A-Za-z]{10}$");
        private static readonly Regex Uid = new Regex("^[1-9][0-9]{0,18}$");

        public static string[] Split(string text)
        {
            return Regex.Split(text.Trim(), @"[\s,，;；]+", RegexOptions.None);
        }

        public static bool IsAllowedHost(string host)
        {
            host = host.ToLowerInvariant();
            return host == "bilibili.com" || host == "www.bilibili.com"
                || host == "m.bilibili.com" || host == "space.bilibili.com"
                || host == "b23.tv";
        }

        public static InputItem Parse(string input, QueryMode mode)
        {
            string raw = input.Trim();
            InputItem item = new InputItem { Original = raw };
            if (Bv.IsMatch(raw)) { item.Bvid = raw; return item; }
            long numericUid;
            if (mode == QueryMode.Uploader && Uid.IsMatch(raw)
                && Int64.TryParse(raw, out numericUid))
            { item.Uid = raw; return item; }
            if (raw.StartsWith("www.bilibili.com/", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("bilibili.com/", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("space.bilibili.com/", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("m.bilibili.com/", StringComparison.OrdinalIgnoreCase)
                || raw.StartsWith("b23.tv/", StringComparison.OrdinalIgnoreCase))
                raw = "https://" + raw;

            Uri uri;
            if (!Uri.TryCreate(raw, UriKind.Absolute, out uri)
                || (uri.Scheme != "https" && uri.Scheme != "http")
                || !IsAllowedHost(uri.Host) || !String.IsNullOrEmpty(uri.UserInfo)
                || !uri.IsDefaultPort)
                throw new QueryException("请输入 B 站视频链接、主页链接或 BV 号。UP 主模式也支持 UID。");
            if (uri.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase))
            { item.ShortUrl = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri; return item; }

            string path = Uri.UnescapeDataString(uri.AbsolutePath);
            Match video = Regex.Match(path, @"^/video/(BV[0-9A-Za-z]{10})/?$", RegexOptions.None);
            if (video.Success) { item.Bvid = video.Groups[1].Value; return item; }
            if (uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
            {
                Match user = Regex.Match(path, @"^/([1-9][0-9]{0,18})(?:/.*)?$");
                if (user.Success && Int64.TryParse(user.Groups[1].Value, out numericUid))
                {
                    if (mode == QueryMode.Video)
                        throw new QueryException("这是 UP 主主页。请切换到“UP 主信息”，或输入视频链接。");
                    item.Uid = user.Groups[1].Value; return item;
                }
            }
            throw new QueryException("链接格式不支持。请使用 /video/BV… 视频链接或 space.bilibili.com/UID 主页链接。");
        }
    }

    public sealed class QueryException : Exception
    {
        public readonly int? HttpStatus;
        public readonly long? ApiCode;
        public QueryException(string message, int? httpStatus = null, long? apiCode = null) : base(message)
        { HttpStatus = httpStatus; ApiCode = apiCode; }
    }

    public sealed class QueryResult
    {
        public string Kind = "";
        public string Title = "";
        public string Bvid = "";
        public string Uploader = "";
        public string Uid = "";
        public long? Views, Likes, Comments, Followers, Following;
        public string Status = "成功";
        public string Input = "";
        public DateTime QueriedAt = DateTime.Now;
    }

    public sealed class BiliClient : IDisposable
    {
        private readonly HttpClient http;
        private bool useCompatibleVideoEndpoint;
        public BiliClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
        public BiliClient(HttpMessageHandler handler)
        {
            http = new HttpClient(handler);
            http.Timeout = TimeSpan.FromSeconds(20);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
            http.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        }

        public static Dictionary<string, object> ObjectAt(Dictionary<string, object> data, string key)
        {
            object value;
            if (data == null || !data.TryGetValue(key, out value)
                || !(value is Dictionary<string, object>))
                throw new QueryException("接口返回的数据不完整（缺少 " + key + "）。");
            return (Dictionary<string, object>)value;
        }

        public static string TextAt(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }

        public static long? NumberAt(Dictionary<string, object> data, string key)
        {
            long number;
            return Int64.TryParse(TextAt(data, key), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out number) ? (long?)number : null;
        }

        public static Dictionary<string, object> Decode(string json)
        {
            Dictionary<string, object> root;
            try
            {
                root = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }
                    .DeserializeObject(json) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                if (!(ex is ArgumentException) && !(ex is InvalidOperationException)) throw;
                throw new QueryException("返回内容不是有效的 JSON。可能是网络限制或 B 站验证页面。");
            }
            if (root == null) throw new QueryException("接口返回内容格式不正确。");
            long? code = NumberAt(root, "code");
            if (!code.HasValue) throw new QueryException("接口响应缺少状态码。");
            if (code.Value != 0)
            {
                string hint;
                switch (code.Value)
                {
                    case -404: case 62002: hint = "内容不存在、已删除或无权查看。"; break;
                    case -412: case -352: hint = "B 站触发了访问验证。请稍后再试，并减少查询频率。"; break;
                    case -101: hint = "这个接口要求登录，当前版本只查询公开数据。"; break;
                    case -400: hint = "参数不正确，请检查链接或 ID。"; break;
                    default: hint = TextAt(root, "message"); break;
                }
                throw new QueryException("B 站接口错误 " + code.Value + "：" + hint, null, code.Value);
            }
            return ObjectAt(root, "data");
        }

        private async Task<Dictionary<string, object>> GetData(string path, CancellationToken token)
        {
            using (HttpResponseMessage response = await http.GetAsync(
                "https://api.bilibili.com" + path, token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    int status = (int)response.StatusCode;
                    string hint = status == 412 || status == 403 || status == 429
                        ? "访问受到限制，请稍后再试。" : "请检查网络或稍后再试。";
                    throw new QueryException("HTTP " + status + "：" + hint, status);
                }
                return Decode(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private async Task<Dictionary<string, object>> GetVideoData(string bvid, CancellationToken token)
        {
            string query = "?bvid=" + Uri.EscapeDataString(bvid);
            if (useCompatibleVideoEndpoint)
                return await GetData("/x/web-interface/wbi/view" + query, token).ConfigureAwait(false);
            try
            {
                return await GetData("/x/web-interface/view" + query, token).ConfigureAwait(false);
            }
            catch (QueryException originalError)
            {
                // Bounded compatibility fallback: one additional request on HTTP/API 412.
                // Deleted videos, login failures, 403/429 and cancellation keep their original meaning.
                if (originalError.HttpStatus != 412 && originalError.ApiCode != -412) throw;
                await Task.Delay(1000, token).ConfigureAwait(false);
                try
                {
                    Dictionary<string, object> data = await GetData(
                        "/x/web-interface/wbi/view" + query, token).ConfigureAwait(false);
                    useCompatibleVideoEndpoint = true;
                    return data;
                }
                catch (QueryException compatibleError)
                {
                    throw new QueryException("旧视频接口返回 412；兼容视频接口也失败（"
                        + compatibleError.Message + "）。请稍后再试。",
                        compatibleError.HttpStatus, compatibleError.ApiCode);
                }
            }
        }

        private async Task<InputItem> Resolve(InputItem item, QueryMode mode, CancellationToken token)
        {
            Uri current = item.ShortUrl;
            for (int i = 0; i < 5; i++)
            {
                token.ThrowIfCancellationRequested();
                using (HttpResponseMessage response = await http.GetAsync(current, token).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    Uri location = response.Headers.Location;
                    if (status < 300 || status >= 400 || location == null)
                        throw new QueryException("短链接未跳转到视频或主页，请从浏览器复制完整链接。");
                    Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    InputItem resolved = InputParser.Parse(next.AbsoluteUri, mode);
                    if (resolved.ShortUrl == null)
                    { resolved.Original = item.Original; return resolved; }
                    current = resolved.ShortUrl;
                }
            }
            throw new QueryException("短链接跳转次数过多，请使用完整链接。");
        }

        public async Task<QueryResult> Query(string input, QueryMode mode, CancellationToken token)
        {
            InputItem item = InputParser.Parse(input, mode);
            if (item.ShortUrl != null) item = await Resolve(item, mode, token).ConfigureAwait(false);
            QueryResult result = new QueryResult { Kind = mode == QueryMode.Video ? "视频" : "UP 主", Input = input };
            if (item.Bvid != null)
            {
                Dictionary<string, object> data = await GetVideoData(item.Bvid, token).ConfigureAwait(false);
                Dictionary<string, object> owner = ObjectAt(data, "owner");
                result.Bvid = item.Bvid;
                result.Title = TextAt(data, "title");
                result.Uploader = TextAt(owner, "name");
                result.Uid = TextAt(owner, "mid");
                if (mode == QueryMode.Video)
                {
                    Dictionary<string, object> stat = ObjectAt(data, "stat");
                    result.Views = NumberAt(stat, "view");
                    result.Likes = NumberAt(stat, "like");
                    result.Comments = NumberAt(stat, "reply");
                    return result;
                }
            }
            else result.Uid = item.Uid;
            long uid;
            if (!Int64.TryParse(result.Uid, out uid) || uid <= 0)
                throw new QueryException("视频信息中未返回有效的 UP 主 UID。");
            Dictionary<string, object> relation = await GetData(
                "/x/relation/stat?vmid=" + Uri.EscapeDataString(result.Uid), token).ConfigureAwait(false);
            result.Followers = NumberAt(relation, "follower");
            result.Following = NumberAt(relation, "following");
            // The relation endpoint does not include a name. Keep UID-only queries honest.
            if (String.IsNullOrEmpty(result.Uploader)) result.Uploader = "UID " + result.Uid;
            return result;
        }

        public void Dispose() { http.Dispose(); }
    }

    public static class ResultExport
    {
        public static readonly string[] Headers = { "类型", "视频标题", "BV 号", "UP 主", "UID", "播放量", "点赞", "评论", "粉丝", "关注", "状态", "输入链接或 ID", "查询时间" };
        private static string Number(long? value) { return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : ""; }
        public static string[] Values(QueryResult r)
        {
            return new string[] { r.Kind, r.Title, r.Bvid, r.Uploader, r.Uid,
                Number(r.Views), Number(r.Likes), Number(r.Comments), Number(r.Followers),
                Number(r.Following), r.Status, r.Input, r.QueriedAt.ToString("yyyy-MM-dd HH:mm:ss") };
        }

        public static string CsvCell(string text)
        {
            // Neutralize text that spreadsheet programs could interpret as a formula.
            if (Regex.IsMatch(text, @"^\s*[=+\-@]")) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        public static string Csv(IList<QueryResult> results)
        {
            StringBuilder output = new StringBuilder();
            output.AppendLine(String.Join(",", Array.ConvertAll(Headers, CsvCell)));
            foreach (QueryResult result in results)
                output.AppendLine(String.Join(",", Array.ConvertAll(Values(result), CsvCell)));
            return output.ToString();
        }

        public static string PlainText(IList<QueryResult> results)
        {
            StringBuilder output = new StringBuilder();
            foreach (QueryResult result in results)
            {
                string[] values = Values(result);
                for (int i = 0; i < Headers.Length; i++)
                    if (!String.IsNullOrEmpty(values[i])) output.AppendLine(Headers[i] + "：" + values[i]);
                output.AppendLine();
            }
            return output.ToString();
        }
    }

    public sealed class MainWindow : Form
    {
        private readonly TextBox input = new TextBox();
        private readonly RadioButton videoMode = new RadioButton();
        private readonly RadioButton uploaderMode = new RadioButton();
        private readonly Button query = new Button();
        private readonly Button cancel = new Button();
        private readonly Button copy = new Button();
        private readonly Button export = new Button();
        private readonly Button clear = new Button();
        private readonly DataGridView grid = new DataGridView();
        private readonly Label status = new Label();
        private readonly List<QueryResult> results = new List<QueryResult>();
        private readonly BiliClient client;
        private CancellationTokenSource cancellation;
        private bool closing;

        public MainWindow() : this(new BiliClient()) { }
        public MainWindow(BiliClient api)
        {
            client = api;
            Text = "B 站信息查询 v1.1";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 740);
            MinimumSize = new Size(900, 660);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(247, 248, 250);

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill,
                ColumnCount = 1, RowCount = 8, Padding = new Padding(24) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new int[] { 42, 32, 40, 114, 52 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "B 站信息查询", AutoSize = true,
                Font = new Font(Font.FontFamily, 20, FontStyle.Bold), ForeColor = Color.FromArgb(24, 30, 39) }, 0, 0);
            layout.Controls.Add(new Label { Text = "批量查看视频数据与 UP 主关注数据", AutoSize = true,
                ForeColor = Color.FromArgb(90, 99, 112) }, 0, 1);

            FlowLayoutPanel modes = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            videoMode.Text = "视频信息"; videoMode.Checked = true; videoMode.AutoSize = true;
            uploaderMode.Text = "UP 主信息"; uploaderMode.AutoSize = true;
            videoMode.Margin = new Padding(0, 6, 24, 0); uploaderMode.Margin = new Padding(0, 6, 0, 0);
            modes.Controls.Add(videoMode); modes.Controls.Add(uploaderMode);
            layout.Controls.Add(modes, 0, 2);

            input.Multiline = true; input.ScrollBars = ScrollBars.Vertical;
            input.Dock = DockStyle.Fill; input.AcceptsReturn = true; input.WordWrap = false;
            input.Font = new Font("Microsoft YaHei UI", 10F); input.MaxLength = 60000;
            input.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(input, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
                Padding = new Padding(0, 2, 0, 0) };
            ConfigureButton(query, "开始查询", true); ConfigureButton(cancel, "停止", false);
            ConfigureButton(copy, "复制结果", false); ConfigureButton(export, "导出 CSV", false);
            ConfigureButton(clear, "清空", false);
            cancel.Enabled = false; copy.Enabled = false; export.Enabled = false;
            actions.Controls.AddRange(new Control[] { query, cancel, copy, export, clear });
            layout.Controls.Add(actions, 0, 4);

            grid.Dock = DockStyle.Fill; grid.ReadOnly = true; grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Color.FromArgb(229, 232, 237); grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(236, 239, 244);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(45, 54, 67);
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font.FontFamily, 9, FontStyle.Bold);
            grid.ColumnHeadersHeight = 38;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(221, 233, 251);
            grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 30, 39);
            grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 253);
            grid.RowTemplate.Height = 36; grid.Margin = new Padding(0);
            AddColumn("类型", 64); AddColumn("视频标题 / UP 主", 250);
            AddColumn("BV 号 / UID", 140); AddColumn("UP 主", 132);
            foreach (string name in new string[] { "播放量", "点赞", "评论", "粉丝", "关注" }) AddColumn(name, 85);
            AddColumn("状态", 240);
            layout.Controls.Add(grid, 0, 5);

            status.Text = "就绪 · 输入链接后点击“开始查询”";
            status.AutoSize = true; status.Margin = new Padding(0, 10, 0, 0);
            status.ForeColor = Color.FromArgb(66, 78, 96); layout.Controls.Add(status, 0, 6);
            Label hint = new Label { AutoSize = true, ForeColor = Color.FromArgb(100, 109, 120),
                Text = "用换行或逗号分隔 · 视频模式支持 BV 号 · UP 主模式支持主页、UID 或视频链接" };
            layout.Controls.Add(hint, 0, 7);

            query.Click += async delegate { await RunQuery(); };
            cancel.Click += delegate { if (cancellation != null) cancellation.Cancel(); };
            clear.Click += delegate { input.Clear(); results.Clear(); grid.Rows.Clear(); UpdateExport(); status.Text = "已清空"; input.Focus(); };
            copy.Click += delegate
            {
                try { Clipboard.SetText(ResultExport.PlainText(results)); status.Text = "已复制全部结果"; }
                catch (Exception ex) { MessageBox.Show(this, "无法访问剪贴板：" + ex.Message, "复制失败"); }
            };
            export.Click += delegate { ExportCsv(); };
            grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0)
                    MessageBox.Show(this, ResultExport.PlainText(new QueryResult[] { results[e.RowIndex] }), "查询详情");
            };
            FormClosing += delegate
            {
                closing = true;
                if (cancellation != null) cancellation.Cancel();
                client.Dispose();
            };
        }

        private void AddColumn(string name, int width)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        private static string FormatNumber(long? value)
        { return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "—"; }
        private static void ConfigureButton(Button button, string text, bool primary)
        {
            button.Text = text; button.Size = new Size(108, 36); button.Margin = new Padding(0, 0, 10, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(211, 217, 225);
            button.BackColor = primary ? Color.FromArgb(45, 102, 196) : Color.White;
            button.ForeColor = primary ? Color.White : Color.FromArgb(42, 52, 65);
            if (primary) button.FlatAppearance.BorderSize = 0;
        }
        private void SetBusy(bool busy)
        {
            query.Enabled = !busy; cancel.Enabled = busy; clear.Enabled = !busy;
            input.ReadOnly = busy; videoMode.Enabled = !busy; uploaderMode.Enabled = !busy;
            UpdateExport();
        }
        private void UpdateExport() { copy.Enabled = export.Enabled = results.Count > 0; }
        private void AddResult(QueryResult result)
        {
            results.Add(result);
            int row = grid.Rows.Add(result.Kind, result.Kind == "视频" ? result.Title : result.Uploader,
                result.Kind == "视频" ? result.Bvid : result.Uid, result.Uploader,
                FormatNumber(result.Views), FormatNumber(result.Likes), FormatNumber(result.Comments),
                FormatNumber(result.Followers), FormatNumber(result.Following), result.Status);
            if (result.Status != "成功") grid.Rows[row].DefaultCellStyle.ForeColor = Color.FromArgb(175, 56, 56);
            grid.Rows[row].Cells[1].ToolTipText = result.Title;
            grid.Rows[row].Cells[9].ToolTipText = result.Status + "\n" + result.Input;
            grid.FirstDisplayedScrollingRowIndex = row;
            UpdateExport();
        }

        private async Task RunQuery()
        {
            if (cancellation != null) return;
            List<string> items = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string inputToken in InputParser.Split(input.Text))
                if (inputToken.Length > 0 && seen.Add(inputToken)) items.Add(inputToken);
            if (items.Count == 0) { MessageBox.Show(this, "请先输入视频链接、BV 号、主页链接或 UID。", "没有输入"); return; }
            if (items.Count > 100) { MessageBox.Show(this, "每批最多查询 100 个链接，请分批查询。", "链接过多"); return; }
            QueryMode mode = videoMode.Checked ? QueryMode.Video : QueryMode.Uploader;
            results.Clear(); grid.Rows.Clear();
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            SetBusy(true);
            int succeeded = 0, failed = 0;
            bool stopped = false;
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    status.Text = String.Format("正在查询 {0} / {1} · 成功 {2} · 失败 {3}", i + 1, items.Count, succeeded, failed);
                    QueryResult result;
                    try
                    {
                        result = await client.Query(items[i], mode, token);
                        token.ThrowIfCancellationRequested();
                        succeeded++;
                    }
                    catch (OperationCanceledException)
                    {
                        if (token.IsCancellationRequested) throw;
                        result = Failure(items[i], mode, "请求超时（20 秒）。请检查网络或稍后再试。"); failed++;
                    }
                    catch (QueryException ex) { result = Failure(items[i], mode, ex.Message); failed++; }
                    catch (HttpRequestException) { result = Failure(items[i], mode, "网络请求失败，请检查网络、代理或 HTTPS 连接。"); failed++; }
                    catch (Exception ex) { result = Failure(items[i], mode, "查询失败：" + ex.Message); failed++; }
                    if (closing) return;
                    AddResult(result);
                    if (i + 1 < items.Count) await Task.Delay(500, token);
                }
            }
            catch (OperationCanceledException) { stopped = true; }
            finally
            {
                cancellation.Dispose(); cancellation = null;
                if (!closing)
                {
                    SetBusy(false);
                    status.Text = String.Format("{0} · 成功 {1} · 失败 {2} · 共 {3} 条结果 · 双击行查看详情",
                        stopped ? "已停止" : "查询完成", succeeded, failed, results.Count);
                }
            }
        }

        private static QueryResult Failure(string input, QueryMode mode, string error)
        { return new QueryResult { Kind = mode == QueryMode.Video ? "视频" : "UP 主", Input = input, Status = error }; }

        private void ExportCsv()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = "bilibili_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv", AddExtension = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, ResultExport.Csv(results), new UTF8Encoding(true)); status.Text = "CSV 已保存，可用 Excel 打开"; }
                catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, "无法保存"); }
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }
}
