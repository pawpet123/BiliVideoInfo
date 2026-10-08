using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BilibiliInfo
{
    public sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = new List<string>();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri.AbsoluteUri);
            return Respond(request, token);
        }
    }

    public static class Tests
    {
        private const string Bv = "BV17x411w7KC";
        private const string VideoJson = "{\"code\":0,\"data\":{\"title\":\"测试视频：这是离线样例\",\"owner\":{\"name\":\"测试 UP 主\",\"mid\":123456},\"stat\":{\"view\":120000,\"like\":3200,\"reply\":156}}}";
        private const string UserJson = "{\"code\":0,\"data\":{\"follower\":48200,\"following\":36}}";
        private static int passed;
        private static void Check(bool condition, string name)
        { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
        private static void Reject(Action action, string name)
        { try { action(); } catch (QueryException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
        private static HttpResponseMessage Response(string body, HttpStatusCode status)
        { return new HttpResponseMessage(status) { Content = new StringContent(body) }; }
        private static FakeHandler NormalHandler()
        {
            return new FakeHandler { Respond = delegate(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                string json = request.RequestUri.AbsolutePath.Contains("relation") ? UserJson : VideoJson;
                return Task.FromResult(Response(json, HttpStatusCode.OK));
            } };
        }

        private static FakeHandler CompatibilityHandler()
        {
            return new FakeHandler { Respond = delegate(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (request.RequestUri.AbsolutePath == "/x/web-interface/view")
                    return Task.FromResult(Response("<html>blocked</html>", (HttpStatusCode)412));
                return Task.FromResult(Response(request.RequestUri.AbsolutePath.Contains("relation") ? UserJson : VideoJson, HttpStatusCode.OK));
            } };
        }

        public static async Task Run()
        {
            Check(InputParser.Parse(Bv, QueryMode.Video).Bvid == Bv, "raw BV");
            Check(InputParser.Parse(" https://www.bilibili.com/video/" + Bv + "/?spm_id_from=333#part ", QueryMode.Video).Bvid == Bv, "video URL with trailing slash, query, fragment");
            Check(InputParser.Parse("bilibili.com/video/" + Bv, QueryMode.Video).Bvid == Bv, "URL without scheme");
            Check(InputParser.Parse("https://space.bilibili.com/123456/video?tid=0", QueryMode.Uploader).Uid == "123456", "UP page tabs");
            Check(InputParser.Parse("123456", QueryMode.Uploader).Uid == "123456", "raw UID");
            Check(InputParser.Parse("https://b23.tv/abc", QueryMode.Video).ShortUrl != null, "short URL parsing");
            Check(InputParser.Split(Bv + ", 123456，https://b23.tv/abc\n" + Bv).Length == 4, "mixed separators");
            Reject(delegate { InputParser.Parse("https://evil.example/video/" + Bv, QueryMode.Video); }, "reject non-Bilibili host");
            Reject(delegate { InputParser.Parse("https://www.bilibili.com.evil.example/video/" + Bv, QueryMode.Video); }, "reject misleading host");
            Reject(delegate { InputParser.Parse("https://space.bilibili.com/123456", QueryMode.Video); }, "UP page in video mode");
            Reject(delegate { InputParser.Parse("BVwrong", QueryMode.Video); }, "invalid BV");
            Reject(delegate { InputParser.Parse("https://space.bilibili.com/9999999999999999999", QueryMode.Uploader); }, "overflow UID");
            Reject(delegate { InputParser.Parse("https://user@www.bilibili.com/video/" + Bv, QueryMode.Video); }, "reject URL user info");
            Reject(delegate { InputParser.Parse("https://www.bilibili.com:8080/video/" + Bv, QueryMode.Video); }, "reject unusual URL ports");
            Reject(delegate { BiliClient.Decode("<html>validation</html>"); }, "HTML validation response");
            Reject(delegate { BiliClient.Decode("{\"code\":-404,\"data\":null}"); }, "API error with null data");
            Reject(delegate { BiliClient.Decode("{\"code\":-412,\"data\":null}"); }, "API rate-limit code");
            Reject(delegate { BiliClient.Decode("{\"code\":0,\"data\":null}"); }, "missing data");
            Reject(delegate { BiliClient.Decode("{}"); }, "missing API code");
            FakeHandler normal = NormalHandler();
            using (BiliClient api = new BiliClient(normal))
            {
                QueryResult video = await api.Query(Bv, QueryMode.Video, CancellationToken.None);
                Check(video.Title.Contains("离线样例") && video.Views == 120000 && video.Likes == 3200 && video.Comments == 156 && video.Uploader == "测试 UP 主", "video fields");
                Check(normal.Requests.Count == 1 && normal.Requests[0].Contains("bvid=" + Bv), "video endpoint");
                QueryResult user = await api.Query(Bv, QueryMode.Uploader, CancellationToken.None);
                Check(user.Uid == "123456" && user.Uploader == "测试 UP 主" && user.Followers == 48200 && user.Following == 36, "UP from video");
                QueryResult direct = await api.Query("https://space.bilibili.com/123456", QueryMode.Uploader, CancellationToken.None);
                Check(direct.Uploader == "UID 123456" && normal.Requests[normal.Requests.Count - 1].Contains("vmid=123456"), "UID-only name and relation endpoint");
                int previous = normal.Requests.Count;
                try { await api.Query("bad", QueryMode.Video, CancellationToken.None); } catch (QueryException) { }
                Check(normal.Requests.Count == previous, "invalid input performs no HTTP request");
            }
            FakeHandler fallback = CompatibilityHandler();
            using (BiliClient api = new BiliClient(fallback))
            {
                QueryResult first = await api.Query(Bv, QueryMode.Video, CancellationToken.None);
                Check(first.Views == 120000 && first.Title.Contains("离线样例") && fallback.Requests.Count == 2,
                    "HTTP 412 falls back and preserves video fields");
                Check(fallback.Requests[0].Contains("/web-interface/view?")
                    && fallback.Requests[1].Contains("/web-interface/wbi/view?bvid=" + Bv), "fallback endpoint and BV parameter");
                await api.Query(Bv, QueryMode.Video, CancellationToken.None);
                Check(fallback.Requests.Count == 3 && fallback.Requests[2].Contains("/wbi/view?"), "subsequent videos use successful compatible endpoint");
                QueryResult uploader = await api.Query(Bv, QueryMode.Uploader, CancellationToken.None);
                Check(uploader.Followers == 48200 && uploader.Uid == "123456"
                    && fallback.Requests.Count == 5 && fallback.Requests[4].Contains("/relation/stat?"), "UP from video works with compatible endpoint");
            }
            FakeHandler apiBlocked = new FakeHandler { Respond = delegate(HttpRequestMessage request, CancellationToken token)
            {
                string json = request.RequestUri.AbsolutePath == "/x/web-interface/view"
                    ? "{\"code\":-412,\"message\":\"request was banned\",\"data\":null}" : VideoJson;
                return Task.FromResult(Response(json, HttpStatusCode.OK));
            } };
            using (BiliClient api = new BiliClient(apiBlocked))
            {
                QueryResult result = await api.Query(Bv, QueryMode.Video, CancellationToken.None);
                Check(result.Views == 120000 && apiBlocked.Requests.Count == 2, "JSON API -412 fallback");
            }
            foreach (int errorStatus in new int[] { 403, 404, 429 })
            {
                FakeHandler noFallback = new FakeHandler { Respond = delegate
                    { return Task.FromResult(Response("blocked", (HttpStatusCode)errorStatus)); } };
                using (BiliClient api = new BiliClient(noFallback))
                {
                    bool handled = false;
                    try { await api.Query(Bv, QueryMode.Video, CancellationToken.None); }
                    catch (QueryException ex) { handled = ex.HttpStatus == errorStatus; }
                    Check(handled && noFallback.Requests.Count == 1, "HTTP " + errorStatus + " does not trigger alternate API");
                }
            }
            FakeHandler deleted = new FakeHandler { Respond = delegate
                { return Task.FromResult(Response("{\"code\":-404,\"data\":null}", HttpStatusCode.OK)); } };
            using (BiliClient api = new BiliClient(deleted))
            {
                bool handled = false;
                try { await api.Query(Bv, QueryMode.Video, CancellationToken.None); }
                catch (QueryException ex) { handled = ex.ApiCode == -404; }
                Check(handled && deleted.Requests.Count == 1, "deleted video API error does not retry");
            }
            FakeHandler bothBlocked = new FakeHandler { Respond = delegate
                { return Task.FromResult(Response("<html>blocked</html>", (HttpStatusCode)412)); } };
            using (BiliClient api = new BiliClient(bothBlocked))
            {
                bool handled = false;
                try { await api.Query(Bv, QueryMode.Video, CancellationToken.None); }
                catch (QueryException ex) { handled = ex.HttpStatus == 412 && ex.Message.Contains("兼容视频接口也失败"); }
                Check(handled && bothBlocked.Requests.Count == 2, "both video endpoints fail clearly with bounded requests");
            }
            FakeHandler relationBlocked = new FakeHandler { Respond = delegate
                { return Task.FromResult(Response("blocked", (HttpStatusCode)412)); } };
            using (BiliClient api = new BiliClient(relationBlocked))
            {
                bool handled = false;
                try { await api.Query("123456", QueryMode.Uploader, CancellationToken.None); }
                catch (QueryException ex) { handled = ex.HttpStatus == 412; }
                Check(handled && relationBlocked.Requests.Count == 1 && relationBlocked.Requests[0].Contains("/relation/stat?"),
                    "UP relation 412 never invokes video compatibility route");
            }
            FakeHandler cancelFallback = new FakeHandler { Respond = delegate
                { return Task.FromResult(Response("blocked", (HttpStatusCode)412)); } };
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (BiliClient api = new BiliClient(cancelFallback))
            {
                stop.CancelAfter(50); bool handled = false;
                try { await api.Query(Bv, QueryMode.Video, stop.Token); }
                catch (OperationCanceledException) { handled = true; }
                Check(handled && cancelFallback.Requests.Count == 1, "cancel during compatibility delay prevents extra request");
            }
            FakeHandler shortHandler = NormalHandler();
            shortHandler.Respond = delegate(HttpRequestMessage request, CancellationToken token)
            {
                if (request.RequestUri.Host == "b23.tv")
                {
                    HttpResponseMessage redirect = Response("", HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://www.bilibili.com/video/" + Bv + "/?share_source=copy_link");
                    return Task.FromResult(redirect);
                }
                return Task.FromResult(Response(VideoJson, HttpStatusCode.OK));
            };
            using (BiliClient api = new BiliClient(shortHandler))
            {
                QueryResult result = await api.Query("https://b23.tv/abc", QueryMode.Video, CancellationToken.None);
                Check(result.Bvid == Bv && result.Input == "https://b23.tv/abc" && shortHandler.Requests.Count == 2, "short-link redirect resolution");
            }
            FakeHandler forbidden = new FakeHandler { Respond = delegate { return Task.FromResult(Response("blocked", HttpStatusCode.Forbidden)); } };
            using (BiliClient api = new BiliClient(forbidden))
            {
                bool handled = false;
                try { await api.Query(Bv, QueryMode.Video, CancellationToken.None); }
                catch (QueryException ex) { handled = ex.Message.Contains("403"); }
                Check(handled, "HTTP failure without parsing HTML");
            }
            FakeHandler foreign = new FakeHandler { Respond = delegate
            {
                HttpResponseMessage response = Response("", HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://evil.example/collect");
                return Task.FromResult(response);
            } };
            using (BiliClient api = new BiliClient(foreign))
            {
                bool handled = false;
                try { await api.Query("https://b23.tv/abc", QueryMode.Video, CancellationToken.None); }
                catch (QueryException) { handled = true; }
                Check(handled && foreign.Requests.Count == 1, "reject foreign short-link redirects before following");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (BiliClient api = new BiliClient(NormalHandler()))
            {
                stop.Cancel(); bool handled = false;
                try { await api.Query(Bv, QueryMode.Video, stop.Token); }
                catch (OperationCanceledException) { handled = true; }
                Check(handled, "query cancellation");
            }
            QueryResult sample = new QueryResult { Title = "=SUM(1,2)\n\"中文\"", Input = "https://example.com", Uploader = "测试", Views = 1234 };
            string csv = ResultExport.Csv(new QueryResult[] { sample });
            Check(csv.Contains("\"'=SUM(1,2)\n\"\"中文\"\"\""), "CSV multiline, quote escaping, formula protection");
            Check(ResultExport.Values(sample)[5] == "1234" && ResultExport.Values(sample)[6] == "", "CSV numeric precision and missing values");
            Console.WriteLine("TOTAL: " + passed + " passed");
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && args[0] == "--ui")
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    FakeHandler uiHandler = CompatibilityHandler();
                    MainWindow form = new MainWindow(new BiliClient(uiHandler));
                    TextBox box = (TextBox)typeof(MainWindow).GetField("input", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    Button button = (Button)typeof(MainWindow).GetField("query", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    DataGridView table = (DataGridView)typeof(MainWindow).GetField("grid", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    box.Text = Bv + "\nhttps://www.bilibili.com/video/" + Bv + "/?from=sample\ninvalid-link";
                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 150 };
                    int ticks = 0;
                    timer.Tick += delegate
                    {
                        ticks++;
                        if (table.Rows.Count == 3 && button.Enabled)
                        {
                            timer.Stop();
                            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                            using (Graphics graphics = Graphics.FromImage(bitmap))
                            { graphics.CopyFromScreen(form.Location, Point.Empty, form.Size); bitmap.Save(args[1]); }
                            Check(table.Rows[0].Cells[4].Value.ToString() == "120,000", "UI renders count");
                            Check(table.Rows[2].Cells[9].Value.ToString().Contains("请输入"), "UI preserves failed result alongside successes");
                            Check(uiHandler.Requests.Count == 3 && uiHandler.Requests[1].Contains("/wbi/view?")
                                && form.Text.Contains("v1.1"), "UI recovers HTTP 412 and shows correct version");
                            form.Close();
                        }
                        if (ticks > 150) throw new Exception("UI test timed out");
                    };
                    form.Shown += delegate { button.PerformClick(); timer.Start(); };
                    Application.Run(form);
                    timer.Dispose();
                }
                else Run().GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
