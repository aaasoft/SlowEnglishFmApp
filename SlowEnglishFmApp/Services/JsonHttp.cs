using System.IO.Compression;
using System.Text.Json;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 带 gzip 解压的 JSON 读取。
///
/// 背景：/api/live 的明文响应约 808KB（含 300+ 条 history），服务端支持 gzip 后约 98KB（8.3 倍）。
/// .NET 的 HttpClient 默认既不发 Accept-Encoding、也不解压响应 —— 每个轮询周期都白传 700KB，
/// 这正是「听 128kbps 的流、每秒却吃掉 200KB 流量」的主因。
///
/// 这里显式声明可接受 gzip/deflate，并在响应仍带 Content-Encoding 时手动解压：
/// 部分平台的 handler（如 Android 原生栈）会自动解压并去掉该头，那种情况下直接读原始流。
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
    /// </summary>
    public static async Task<JsonDocument> ReadAsync(
        HttpResponseMessage response,
        CancellationToken ct = default)
    {
        Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

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
            // 释放解压流（没有解压层时就是网络流本身，由 response 兜底，重复释放安全）。
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
