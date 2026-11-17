using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SlowEnglishFmApp.Models;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 轮询 SlowEnglish.FM 的 /api/live 端点，跟踪“当前正在播出的节目”。
/// 设计要点：
/// - 轮询节奏按播放状态自适应（见 PollIntervalPlaying / PollIntervalIdle）：
///   该响应含 300+ 条 history，明文约 808KB，是流量消耗的最大项。播放中**不做周期轮询**
///   （仅 5 分钟安全网），时间轴由播放器按需调 RequestRefresh 刷新（临近节目切换时）。
/// - 请求声明 Accept-Encoding: gzip 并解压（服务端支持，808KB → 98KB）。
/// - 仅在 current 的身份（Id）变化时触发 CurrentChanged，避免高频刷新字幕计时。
/// - 解析使用 JsonDocument 仅抽取需要的字段（available / listeners / current / next），
///   这样即使响应里其它大字段（如 history）结构变化，也不会拖垮整个解析；
///   其中 next 在 API 中实际是一个数组，需逐个元素反序列化为 LiveEntry。
/// - 解析失败不会中断轮询，仅通过 Error 事件上报。
/// </summary>
public sealed class LiveRadioService : ILiveRadioService
{
    /// <summary>
    /// 播放中的周期轮询间隔 —— 仅作**安全网**：正常情况下播放中不靠周期轮询，
    /// 而是由播放器在「当前条目临近结束 / 时间轴过期」时调 RequestRefresh 按需刷新
    /// （见 RadioViewModel.TickKaraoke）。安全网只是防止按需逻辑失效后时间轴永久卡死。
    /// </summary>
    private static readonly TimeSpan PollIntervalPlaying = TimeSpan.FromMinutes(5);

    /// <summary>空闲时的轮询间隔：只用于展示「已就绪 / 即将播出」，无需 3 秒级刷新。</summary>
    private static readonly TimeSpan PollIntervalIdle = TimeSpan.FromSeconds(20);

    /// <summary>RequestRefresh 的最小间隔：按需刷新由 tick 驱动（16Hz），必须节流。</summary>
    private static readonly long RefreshThrottleMs = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private LiveEntry? _current;
    private IReadOnlyList<LiveEntry>? _next;
    private IReadOnlyList<LiveEntry> _timeline = Array.Empty<LiveEntry>();
    private int _listeners;
    private bool _available;
    private bool _running;
    private bool _playing;
    /// <summary>同一时刻只允许一个轮询在飞（1 = 在飞），避免慢请求叠加。</summary>
    private int _polling;
    /// <summary>上次 RequestRefresh 被接受的时刻（TickCount64），用于节流。</summary>
    private long _lastRefreshTick;

    public LiveRadioService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
        JsonHttp.EnableCompression(_http);
        _timer = new Timer(_ => _ = PollAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsAvailable
    {
        get { lock (_gate) return _available; }
        private set { lock (_gate) _available = value; }
    }

    public LiveEntry? Current
    {
        get { lock (_gate) return _current; }
        private set { lock (_gate) _current = value; }
    }

    public IReadOnlyList<LiveEntry>? Next
    {
        get { lock (_gate) return _next; }
        private set { lock (_gate) _next = value; }
    }

    public int Listeners
    {
        get { lock (_gate) return _listeners; }
        private set { lock (_gate) _listeners = value; }
    }

    public IReadOnlyList<LiveEntry> Timeline
    {
        get { lock (_gate) return _timeline; }
    }

    public event EventHandler<LiveEntry?>? CurrentChanged;
    public event EventHandler<string>? Error;

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }

        // 立即拉取一次；后续节奏由 ScheduleNext 按播放状态决定。
        _ = PollAsync();
    }

    public void Stop()
    {
        lock (_gate) _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 页面在开始/停止播放时调用：播放中停掉周期轮询（数据大头是这条接口），
    /// 时间轴改由 RequestRefresh 按需刷新；由空闲切到播放时会立即刷新一次。
    /// </summary>
    public void SetPlaybackActive(bool playing)
    {
        bool changed;
        lock (_gate)
        {
            changed = _playing != playing;
            _playing = playing;
        }

        if (!changed) return;

        if (playing)
            RequestRefresh();     // 开播立刻刷新一次时间轴（内部结束时会自行按新节奏排下一次）
        else
            ScheduleNext();
    }

    /// <summary>
    /// 请求立即刷新一次 /api/live（播放中的时间轴按需刷新入口）。
    /// 带 30 秒节流：调用方是 16Hz 的 tick，不节流会在边界窗口内重复拉取大响应。
    /// </summary>
    public void RequestRefresh()
    {
        if (Environment.TickCount64 - Volatile.Read(ref _lastRefreshTick) < RefreshThrottleMs)
            return;

        Volatile.Write(ref _lastRefreshTick, Environment.TickCount64);

        bool shouldRun;
        lock (_gate) shouldRun = _running;
        if (shouldRun) _ = PollAsync();
    }

    private void ScheduleNext()
    {
        TimeSpan interval;
        lock (_gate)
        {
            if (!_running) return;
            interval = _playing ? PollIntervalPlaying : PollIntervalIdle;
        }
        _timer.Change(interval, Timeout.InfiniteTimeSpan);
    }

    private async Task PollAsync()
    {
        bool shouldRun;
        lock (_gate) shouldRun = _running;
        if (!shouldRun) return;

        // 慢请求期间又排到一次轮询：跳过，避免多个大响应并发。
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/live");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            // 仅抽取需要的字段，避免对 700KB+ 的完整响应（含 history）整体反序列化。
            using var doc = await JsonHttp.ReadAsync(response, default, "api/live").ConfigureAwait(false);
            var root = doc.RootElement;

            bool available = root.TryGetProperty("available", out var avEl) && avEl.ValueKind == JsonValueKind.True;
            int listeners = root.TryGetProperty("listeners", out var lsEl) && lsEl.ValueKind == JsonValueKind.Number
                ? lsEl.GetInt32()
                : 0;

            LiveEntry? current = null;
            if (root.TryGetProperty("current", out var curEl) && curEl.ValueKind == JsonValueKind.Object)
            {
                try { current = curEl.Deserialize<LiveEntry>(JsonOptions); }
                catch { /* 单个字段异常不影响整体；current 留空 */ }
            }

            var next = ExtractEntryList(root, "next");
            // 只取历史里最近的一小段（history 有 300+ 条，全解析没必要），用于按播放时刻回溯/定位节目。
            var recentHistory = ExtractRecentHistory(root, 40);
            var timeline = BuildTimeline(recentHistory, current, next);

            bool changed = Current?.Id != current?.Id;

            lock (_gate)
            {
                _available = available;
                _listeners = listeners;
                _current = current;
                _next = next;
                _timeline = timeline;
            }

            if (changed)
            {
                CurrentChanged?.Invoke(this, current);
            }
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
            ScheduleNext();
        }
    }

    /// <summary>
    /// 从 root 的指定属性中抽取 LiveEntry 列表。该属性可能是数组（正常情况）、单个对象，或缺失/为空。
    /// </summary>
    private static IReadOnlyList<LiveEntry> ExtractEntryList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el))
            return Array.Empty<LiveEntry>();

        var result = new List<LiveEntry>();
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    var entry = item.Deserialize<LiveEntry>(JsonOptions);
                    if (entry is not null) result.Add(entry);
                }
                catch { /* 跳过无法解析的单条 */ }
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var entry = el.Deserialize<LiveEntry>(JsonOptions);
                if (entry is not null) result.Add(entry);
            }
            catch { /* 跳过 */ }
        }

        return result;
    }

    /// <summary>
    /// 从 history 数组中取最后 <paramref name="max"/> 条（history 按时间升序，越靠后越新），
    /// 避免为 300+ 条历史记录做无谓的反序列化。
    /// </summary>
    private static IReadOnlyList<LiveEntry> ExtractRecentHistory(JsonElement root, int max)
    {
        if (!root.TryGetProperty("history", out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<LiveEntry>();

        int total = el.GetArrayLength();
        int skip = Math.Max(0, total - max);
        var result = new List<LiveEntry>(Math.Min(total, max));

        int i = 0;
        foreach (var item in el.EnumerateArray())
        {
            if (i++ < skip) continue;
            if (item.ValueKind != JsonValueKind.Object) continue;
            try
            {
                var entry = item.Deserialize<LiveEntry>(JsonOptions);
                if (entry is not null) result.Add(entry);
            }
            catch { /* 跳过无法解析的单条 */ }
        }

        return result;
    }

    /// <summary>把历史 + 当前 + 接下来的条目合成一条按开始时间升序的时间轴（按 Id 去重）。</summary>
    private static IReadOnlyList<LiveEntry> BuildTimeline(
        IReadOnlyList<LiveEntry> history,
        LiveEntry? current,
        IReadOnlyList<LiveEntry> next)
    {
        var byId = new Dictionary<string, LiveEntry>(StringComparer.Ordinal);

        void Add(LiveEntry? e)
        {
            if (e is null || string.IsNullOrEmpty(e.Id)) return;
            byId[e.Id] = e; // 后加入的覆盖先加入的（current/next 比 history 更新）
        }

        foreach (var e in history) Add(e);
        Add(current);
        foreach (var e in next) Add(e);

        return byId.Values.OrderBy(e => e.StartsAt).ToList();
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// GET /api/live-clock?connection=... 查询该连接对应的真实节目时刻（Unix 毫秒）。
    /// 该时刻已包含直播流的服务端缓冲延迟，是字幕与音频对齐的基准。
    /// 连接尚未就绪（{"error":"Connection not ready"}）或请求失败时返回 null。
    /// </summary>
    public async Task<DateTimeOffset?> GetProgrammeAtAsync(string connection)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/live-clock?connection={Uri.EscapeDataString(connection)}");
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };

            using var response = await _http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            // 注意：本次改动的 HttpClient 已声明 Accept-Encoding，响应可能是压缩体，
            // 必须走 JsonHttp 解压读取（直接 Parse 压缩流会抛 JsonException）。
            using var doc = await JsonHttp.ReadAsync(response, default, "api/live-clock").ConfigureAwait(false);
            var root = doc.RootElement;

            if (root.TryGetProperty("programmeAt", out var pEl))
            {
                if (pEl.ValueKind == JsonValueKind.Number && pEl.TryGetInt64(out var ms))
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);
                if (pEl.ValueKind == JsonValueKind.Number)
                    return DateTimeOffset.FromUnixTimeMilliseconds((long)pEl.GetDouble());
                if (pEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(pEl.GetString(), out var dto))
                    return dto;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
