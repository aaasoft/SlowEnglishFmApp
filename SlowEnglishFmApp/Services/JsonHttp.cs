using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 带 gzip 解压的 JSON 读取 + 线上流量计数。
///
/// 背景：/api/live 的明文响应约 808KB（含 300+ 条 history），服务端支持 gzip 后约 98KB（8.3 倍）。
/// .NET 的 HttpClient 默认既不发 Accept-Encoding、也不解压响应 —— 每个轮询周期都白传 700KB，
/// 这正是「听 128kbps 的流、每秒却吃掉 200KB 流量」的主因。
///
/// 这里显式声明可接受 gzip/deflate/br，并在响应仍带 Content-Encoding 时手动解压：
/// 部分平台的 handler（如 Android 原生栈）会自动解压并去掉该头，那种情况下直接读原始流。
///
/// 计数：在解压**之前**包一层 CountingStream，统计的是压缩后的真实线上流量；
/// 每次读取结束把字节数累加到 TrafficMonitor，并输出一条 [traffic] 日志
/// （VS 输出窗口 / adb logcat 可见，用于定位哪个接口在刷流量）。
/// </summary>
internal static class JsonHttp
{
    /// <summary>让该 HttpClient 的所有请求都声明支持压缩。</summary>
    public static void EnableCompression(HttpClient http)
    {
        var headers = http.DefaultRequestHeaders;
        if (headers.AcceptEncoding.Count > 0) return;
        headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
        headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("deflate"));
    }

    /// <summary>
    /// 读取（必要时先解压）响应体并解析成 JsonDocument。调用方负责 Dispose 结果与 response。
    /// <paramref name="label"/> 仅用于流量日志（通常是请求 URL）。
    /// </summary>
    public static async Task<JsonDocument> ReadAsync(
        HttpResponseMessage response,
        CancellationToken ct = default,
        string? label = null)
    {
        Stream raw = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var counter = new CountingStream(raw);
        Stream stream = counter;

        foreach (string encoding in response.Content.Headers.ContentEncoding)
        {
            stream = encoding.ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress),
                "deflate" => new ZLibStream(stream, CompressionMode.Decompress),
                "br" => new BrotliStream(stream, CompressionMode.Decompress),
                _ => stream,
            };
        }

        try
        {
            return await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        }
        finally
        {
            // 释放解压流（会级联释放计数流与原始流）；重复释放安全。
            await stream.DisposeAsync().ConfigureAwait(false);

            long bytes = counter.BytesRead;
            TrafficMonitor.Add(bytes);
            if (label is not null)
                Debug.WriteLine($"[traffic] {label}: {bytes} bytes");
        }
    }

    /// <summary>统计读取字节数的透传流（在解压之前包装，计的是压缩后的线上流量）。</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
