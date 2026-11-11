using SlowEnglishFmApp.Models;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 字幕计时服务：按 runId + kind 获取逐词计时 JSON 并做内存缓存。
/// 地址规则：https://media.slowenglish.fm/radio/{runId}/{kind}/timings.json
/// </summary>
public interface ITimingsService
{
    /// <summary>
    /// 获取某次播出（runId + kind）的逐词计时。失败或暂不可用时返回 null。
    /// 同一节目会缓存，避免重复下载。
    /// </summary>
    Task<IReadOnlyList<WordTiming>?> GetTimingsAsync(string runId, string kind, CancellationToken cancellationToken = default);
}
