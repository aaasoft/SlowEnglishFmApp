using System.Net.Http.Json;
using System.Text.Json;
using SlowEnglishFmApp.Models;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 字幕计时服务实现。使用独立 HttpClient 指向 media.slowenglish.fm，
/// 并对 runId+kind 做简单的内存缓存 + 失败冷却，避免对失败节目高频重试。
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
            var url = $"radio/{runId}/{kind}/timings.json";
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
