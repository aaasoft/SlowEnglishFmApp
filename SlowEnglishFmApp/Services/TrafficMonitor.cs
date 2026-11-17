using System.Threading;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// App 自身 HTTP 请求（/api/live、/api/live-clock、word-timings）的流量统计。
///
/// 用途：区分「流量大户到底是 API 还是音频流」。系统流量监控把两者加在一起，
/// 这里只统计 App 发出的 HTTP —— 若界面上的 API 速率远小于系统显示的速率，
/// 差额就是音频流（播放器反复重开连接/重缓冲，每次重开服务端都会突发下发）。
/// 注意：音频流走 MediaElement/ExoPlayer，不经 HttpClient，不在此统计内。
/// </summary>
internal static class TrafficMonitor
{
    private static long _totalBytes;

    /// <summary>本次进程运行以来所有 HTTP 响应的累计字节数（压缩后的线上传输量）。</summary>
    public static long TotalBytes => Interlocked.Read(ref _totalBytes);

    public static void Add(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _totalBytes, bytes);
    }
}
