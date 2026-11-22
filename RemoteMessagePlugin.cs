using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace RemoteMessagePlugin
{
    [PluginEntrance]
    public class RemoteMessagePlugin : PluginBase
    {
        private const string PluginVersion = "1.1.0";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private SoundPlayer? _soundPlayer;
        private readonly object _audioLock = new();
        private readonly ConcurrentDictionary<long, Task> _inFlight = new();
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _rateBuckets = new();
        private long _requestSeq;
        private DateTimeOffset _startedAt;
        private RemoteMessageConfig _config = new();
        private byte[]? _tokenHash;

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            _startedAt = DateTimeOffset.UtcNow;

            string configFolder = !string.IsNullOrWhiteSpace(PluginConfigFolder) ? PluginConfigFolder : PluginFolder;
            _config = RemoteMessageConfig.Load(configFolder, msg => Log(msg), (msg, ex) => LogError(msg, ex));
            _tokenHash = string.IsNullOrEmpty(_config.Token)
                ? null
                : SHA256.HashData(Encoding.UTF8.GetBytes(_config.Token));

            Log($"RemoteMessagePlugin {PluginVersion} 正在初始化，端口 {_config.Port}，鉴权 {(_tokenHash == null ? "关闭" : "开启")}。");
            InitAudioPlayer();
            StartHttpServer();
        }

        public override void Shutdown()
        {
            base.Shutdown();
            StopHttpServer();
            DisposeAudioPlayer();
            Log("RemoteMessagePlugin 已停止。");
        }

        // ---------- 音频 ----------

        private void InitAudioPlayer()
        {
            try
            {
                string wavPath = FindAudioFile();
                if (!File.Exists(wavPath))
                {
                    Log($"未找到提示音文件: {wavPath}");
                    return;
                }

                _soundPlayer = new SoundPlayer(wavPath);
                _soundPlayer.Load(); // 同步预载入，避免首次通知的播放延迟
                Log($"提示音预载入完成: {wavPath}");
            }
            catch (Exception ex)
            {
                LogError("初始化音频播放器失败", ex);
            }
        }

        private string FindAudioFile()
        {
            string wavPath = Path.Combine(PluginFolder, "notify-normal-new.wav");
            return File.Exists(wavPath) ? wavPath : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notify-normal-new.wav");
        }

        private void PlayAudio()
        {
            lock (_audioLock)
            {
                try
                {
                    if (_soundPlayer != null)
                    {
                        _soundPlayer.Play();
                        return;
                    }

                    string wavPath = FindAudioFile();
                    if (!File.Exists(wavPath))
                    {
                        Log($"未找到提示音文件: {wavPath}");
                        return;
                    }

                    _soundPlayer = new SoundPlayer(wavPath);
                    _soundPlayer.Load();
                    _soundPlayer.Play();
                }
                catch (Exception ex)
                {
                    LogError("播放音频失败", ex);
                }
            }
        }

        private void DisposeAudioPlayer()
        {
            try
            {
                lock (_audioLock)
                {
                    _soundPlayer?.Stop();
                    _soundPlayer?.Dispose();
                    _soundPlayer = null;
                }
            }
            catch { }
        }

        // ---------- HTTP 服务 ----------

        private void StartHttpServer()
        {
            try
            {
                int port = _config.Port;
                string bind = _config.BindAddress.Trim();
                string[] candidates = bind switch
                {
                    "*" or "+" or "any" or "0.0.0.0" => new[] { $"http://*:{port}/", $"http://+:{port}/" },
                    "localhost" or "127.0.0.1" or "local" => new[] { $"http://localhost:{port}/", $"http://127.0.0.1:{port}/" },
                    _ => new[] { $"http://{bind}:{port}/" }
                };

                bool started = false;
                foreach (string prefix in candidates)
                {
                    var candidate = new HttpListener();
                    try
                    {
                        candidate.Prefixes.Add(prefix);
                        candidate.Start();
                        _listener = candidate;
                        started = true;
                        Log($"HTTP 服务已启动: {prefix}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"绑定 {prefix} 失败，尝试其他地址: {ex.Message}");
                        try { candidate.Close(); } catch { }
                    }
                }

                if (!started)
                {
                    var fallback = new HttpListener();
                    fallback.Prefixes.Add($"http://localhost:{port}/");
                    fallback.Prefixes.Add($"http://127.0.0.1:{port}/");
                    fallback.Start();
                    _listener = fallback;
                    Log($"已降级为仅本机监听: http://localhost:{port}/");
                }

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                LogError($"启动 {_config.Port} 端口 HTTP 服务失败", ex);
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || _listener == null || !_listener.IsListening) break;
                    LogError("HttpListener 接收客户端连接失败", ex);
                    try { await Task.Delay(200, token); } catch (OperationCanceledException) { }
                    continue;
                }

                long id = Interlocked.Increment(ref _requestSeq);
                Task task = ProcessRequestAsync(context);
                _inFlight[id] = task;
                _ = task.ContinueWith(
                    completed => _inFlight.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            try
            {
                SetCommonHeaders(response);

                string method = request.HttpMethod.ToUpperInvariant();
                string path = (request.Url?.AbsolutePath ?? "/").TrimEnd('/');
                if (path.Length == 0) path = "/";
                string decodedPath = WebUtility.UrlDecode(path);

                if (method == "OPTIONS")
                {
                    response.StatusCode = (int)HttpStatusCode.NoContent;
                    return;
                }

                if (path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase))
                {
                    response.Headers.Set("X-Content-Type-Options", "nosniff");
                    response.Headers.Set("Referrer-Policy", "no-referrer");
                    response.Headers.Set("Content-Security-Policy",
                        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; font-src 'self' data:");
                    await WriteTextAsync(response, HttpStatusCode.OK, "text/html; charset=utf-8", WebUi.Render(_config.MaxContentLength));
                    return;
                }

                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/api/health", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(response, HttpStatusCode.OK, new
                    {
                        ok = true,
                        version = PluginVersion,
                        port = _config.Port,
                        tokenRequired = _tokenHash != null,
                        uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startedAt).TotalSeconds
                    });
                    return;
                }

                if (decodedPath.Equals("/notice", StringComparison.OrdinalIgnoreCase) ||
                    decodedPath.StartsWith("/notice/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!IsTokenValid(request))
                    {
                        await WriteJsonAsync(response, HttpStatusCode.Unauthorized, new
                        {
                            ok = false,
                            error = "访问令牌无效，请通过 X-API-Key 请求头或 token 查询参数提供。"
                        });
                        return;
                    }

                    if (method != "GET" && method != "POST")
                    {
                        response.Headers.Set("Allow", "GET, POST");
                        await WriteJsonAsync(response, HttpStatusCode.MethodNotAllowed, new { ok = false, error = "仅支持 GET 或 POST。" });
                        return;
                    }

                    string clientKey = request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                    if (!IsRateAllowed(clientKey))
                    {
                        response.Headers.Set("Retry-After", "60");
                        await WriteJsonAsync(response, (HttpStatusCode)429, new
                        {
                            ok = false,
                            error = $"请求过于频繁，每分钟最多 {_config.RateLimitPerMinute} 条。"
                        });
                        return;
                    }

                    NoticeRequest? payload = null;
                    string? content;
                    if (method == "POST")
                    {
                        int maxBodyBytes = _config.MaxContentLength * 4 + 4096;
                        if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                        {
                            await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { ok = false, error = "请求体必须是 JSON（Content-Type: application/json）。" });
                            return;
                        }

                        if (request.ContentLength64 > maxBodyBytes)
                        {
                            await WriteJsonAsync(response, (HttpStatusCode)413, new { ok = false, error = "请求体过大。" });
                            return;
                        }

                        try
                        {
                            string body = await ReadBodyLimitedAsync(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, maxBodyBytes);
                            payload = JsonSerializer.Deserialize<NoticeRequest>(body, JsonOptions);
                        }
                        catch (JsonException ex)
                        {
                            await WriteJsonAsync(response, HttpStatusCode.BadRequest, new { ok = false, error = "JSON 格式错误: " + ex.Message });
                            return;
                        }
                        catch (InvalidDataException ex)
                        {
                            await WriteJsonAsync(response, (HttpStatusCode)413, new { ok = false, error = ex.Message });
                            return;
                        }

                        content = payload?.Content;
                    }
                    else
                    {
                        content = decodedPath.StartsWith("/notice/", StringComparison.OrdinalIgnoreCase)
                            ? decodedPath.Substring("/notice/".Length)
                            : request.QueryString["content"] ?? request.QueryString["text"];
                    }

                    content = (content ?? string.Empty).Trim();
                    if (content.Length == 0) content = "收到一条远程通知消息";
                    if (content.Length > _config.MaxContentLength)
                    {
                        await WriteJsonAsync(response, HttpStatusCode.BadRequest, new
                        {
                            ok = false,
                            error = $"消息长度超过限制（最多 {_config.MaxContentLength} 个字符）。"
                        });
                        return;
                    }

                    string title = string.IsNullOrWhiteSpace(payload?.Title) ? content : payload!.Title!.Trim();
                    string message = string.IsNullOrWhiteSpace(payload?.Message) ? "Important" : payload!.Message!.Trim();
                    NotificationLevel level = ParseLevel(payload?.Level);
                    bool playSound = payload?.PlaySound ?? _config.PlaySound;

                    TriggerNotice(content, title, message, level, playSound);

                    await WriteJsonAsync(response, HttpStatusCode.OK, new
                    {
                        ok = true,
                        content,
                        level = level.ToString()
                    });
                    return;
                }

                await WriteJsonAsync(response, HttpStatusCode.NotFound, new
                {
                    ok = false,
                    error = "Not found",
                    usage = "GET /  |  GET /notice/{内容}  |  POST /notice  |  GET /health"
                });
            }
            catch (Exception ex)
            {
                LogError("处理 HTTP 请求失败", ex);
                try
                {
                    await WriteJsonAsync(response, HttpStatusCode.InternalServerError, new { ok = false, error = "服务器内部错误。" });
                }
                catch { }
            }
            finally
            {
                try { response.Close(); } catch { }
            }
        }

        private static void SetCommonHeaders(HttpListenerResponse response)
        {
            response.Headers.Set("Access-Control-Allow-Origin", "*");
            response.Headers.Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Set("Access-Control-Allow-Headers", "Content-Type, X-API-Key, Authorization");
            response.Headers.Set("Access-Control-Max-Age", "86400");
        }

        private bool IsTokenValid(HttpListenerRequest request)
        {
            if (_tokenHash == null) return true;

            string? provided = request.Headers["X-API-Key"];
            if (string.IsNullOrEmpty(provided))
            {
                string auth = request.Headers["Authorization"] ?? string.Empty;
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    provided = auth.Substring(7).Trim();
            }
            if (string.IsNullOrEmpty(provided)) provided = request.QueryString["token"];
            if (string.IsNullOrEmpty(provided)) return false;

            byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
            return CryptographicOperations.FixedTimeEquals(_tokenHash, actual);
        }

        private bool IsRateAllowed(string clientKey)
        {
            if (_config.RateLimitPerMinute <= 0) return true;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Queue<DateTimeOffset> bucket = _rateBuckets.GetOrAdd(clientKey, _ => new Queue<DateTimeOffset>());
            lock (bucket)
            {
                while (bucket.Count > 0 && now - bucket.Peek() > TimeSpan.FromMinutes(1))
                    bucket.Dequeue();

                if (bucket.Count >= _config.RateLimitPerMinute) return false;
                bucket.Enqueue(now);
            }

            if (_rateBuckets.Count > 1000)
            {
                foreach (var pair in _rateBuckets)
                {
                    lock (pair.Value)
                    {
                        if (pair.Value.Count == 0 || DateTimeOffset.UtcNow - pair.Value.Last() > TimeSpan.FromMinutes(5))
                            _rateBuckets.TryRemove(pair.Key, out _);
                    }
                }
            }

            return true;
        }

        private static async Task<string> ReadBodyLimitedAsync(Stream stream, Encoding encoding, int maxBytes)
        {
            using var bufferStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException("请求体过大。");
                bufferStream.Write(buffer, 0, read);
            }
            return encoding.GetString(bufferStream.ToArray());
        }

        private static async Task WriteTextAsync(HttpListenerResponse response, HttpStatusCode status, string contentType, string body)
        {
            byte[] buffer = Encoding.UTF8.GetBytes(body);
            response.StatusCode = (int)status;
            response.ContentType = contentType;
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private static Task WriteJsonAsync<T>(HttpListenerResponse response, HttpStatusCode status, T payload)
        {
            return WriteTextAsync(response, status, "application/json; charset=utf-8", JsonSerializer.Serialize(payload, JsonOptions));
        }

        private static NotificationLevel ParseLevel(string? level)
        {
            return level?.Trim().ToLowerInvariant() switch
            {
                "info" or "information" or "low" => NotificationLevel.Info,
                "error" or "critical" => NotificationLevel.Error,
                "success" or "ok" => NotificationLevel.Success,
                _ => NotificationLevel.Warning
            };
        }

        private void TriggerNotice(string content, string title, string message, NotificationLevel level, bool playSound)
        {
            try
            {
                var notificationService = GetService<INotificationService>();
                if (notificationService != null)
                {
                    notificationService.Show(
                        title: title,
                        message: message,
                        level: level,
                        onClicked: () => Log($"用户点击了通知: {content}"));
                }
                else
                {
                    Log("INotificationService 服务未就绪，仅记录消息: " + content);
                }
            }
            catch (Exception ex)
            {
                LogError("发送通知失败", ex);
            }

            if (playSound) PlayAudio();
        }

        private void StopHttpServer()
        {
            try
            {
                _cts?.Cancel();
                HttpListener? listener = _listener;
                if (listener != null)
                {
                    if (listener.IsListening) listener.Stop();
                    listener.Close();
                }
                _listener = null;

                Task[] tasks = _inFlight.Values.ToArray();
                if (tasks.Length > 0)
                {
                    try { Task.WhenAll(tasks).Wait(TimeSpan.FromSeconds(3)); }
                    catch (Exception ex) { LogError("等待进行中的请求结束失败", ex); }
                }

                _cts?.Dispose();
                _cts = null;
                _rateBuckets.Clear();
            }
            catch (Exception ex)
            {
                LogError("关闭 HTTP 服务失败", ex);
            }
        }

        private sealed class NoticeRequest
        {
            public string? Content { get; set; }
            public string? Title { get; set; }
            public string? Message { get; set; }
            public string? Level { get; set; }
            public bool? PlaySound { get; set; }
        }
    }
}
