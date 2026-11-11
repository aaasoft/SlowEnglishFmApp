using SlowEnglishFmApp.Models;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 实时广播状态服务：周期性轮询 /api/live，对外暴露当前/下一个节目，并在“正在播出的节目”发生变化时抛出事件。
/// </summary>
public interface ILiveRadioService
{
    /// <summary>电台是否可用（正在播出）。</summary>
    bool IsAvailable { get; }

    /// <summary>当前正在播出的条目。</summary>
    LiveEntry? Current { get; }

    /// <summary>接下来将要播出的若干条目（API 实际返回的是数组）。</summary>
    IReadOnlyList<LiveEntry>? Next { get; }

    /// <summary>当前在线听众数。</summary>
    int Listeners { get; }

    /// <summary>
    /// 节目时间轴（最近的历史条目 + 当前条目 + 即将播出的条目，按 StartsAt 升序）。
    /// 播放器用“当前听到的节目时刻”在这条时间轴上定位节目，从而让标题与音频保持同步。
    /// </summary>
    IReadOnlyList<LiveEntry> Timeline { get; }

    /// <summary>当前条目（用于字幕）发生变化时触发，参数为新的条目（可能为 null）。</summary>
    event EventHandler<LiveEntry?>? CurrentChanged;

    /// <summary>发生可恢复的错误时触发（用于状态提示）。</summary>
    event EventHandler<string>? Error;

    /// <summary>开始轮询。</summary>
    void Start();

    /// <summary>停止轮询并释放资源。</summary>
    void Stop();

    /// <summary>
    /// 查询 /api/live-clock：返回指定连接对应的“真实节目时刻”（Unix 毫秒，已含直播流的服务端缓冲延迟）。
    /// 用于把字幕进度对齐到用户实际听到的音频，而非本地墙钟。连接尚未就绪时返回 null。
    /// </summary>
    Task<DateTimeOffset?> GetProgrammeAtAsync(string connection);
}
