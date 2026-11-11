using System.Net.Http.Json;
using System.Text.Json;
using SlowEnglishFmApp.Models;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 字幕计时服务实现。使用独立 HttpClient 指向 slowenglish.fm，
/// 并对 runId+kind 做简单的内存缓存 + 失败冷却，避免对失败节目高频重试。
/// 说明：网站真正的逐词计时地址是
/// <c>/timings/runs/{runId}/{kind}/word-timings.json</c>（由 episode 的 audio/wordTimings 推导）。
/// 旧的 <c>media.slowenglish.fm/radio/{runId}/{kind}/timings.json</c> 只对部分节目存在，已弃用。
/// </summary>
public sealed class TimingsService : ITimingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Dictionary<string, List<WordTiming>> _cache = new();
    private readonly Dictionary<string, DateTime> _retryAt = new();
    private readonly object _gate = new();

    public TimingsService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(15);
        // 站点 CDN 可能按 UA 过滤（同域 media 主机曾对非浏览器 UA 返回 403），这里带一个常规 UA。
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Linux; Android 13) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Mobile Safari/537.36");
    }

    public async Task<IReadOnlyList<WordTiming>?> GetTimingsAsync(string runId, string kind, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(kind))
            return null;

        string key = $"{runId}/{kind}";

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
            if (_retryAt.TryGetValue(key, out var at) && DateTime.UtcNow < at)
                return null;
        }

        try
        {
            var url = $"timings/runs/{runId}/{kind}/word-timings.json";
            var file = await _http.GetFromJsonAsync<TimingFile>(url, JsonOptions, cancellationToken).ConfigureAwait(false);

            if (file?.Words is { Count: > 0 } words &&
                words.TrueForAll(w => double.IsFinite(w.Start) && double.IsFinite(w.End)))
            {
                lock (_gate)
                {
                    _cache[key] = words;
                    _retryAt.Remove(key);
                }

                return words;
            }

            // 数据不完整，冷却 30 秒后重试。
            lock (_gate) _retryAt[key] = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
        catch
        {
            lock (_gate) _retryAt[key] = DateTime.UtcNow.AddSeconds(30);
            return null;
        }
    }
}
