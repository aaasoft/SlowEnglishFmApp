namespace SlowEnglishFmApp.Models;

/// <summary>
/// 正在播出（或即将播出）的一个条目，对应 SlowEnglish.FM 的 /api/live 中的 current / next / history 项。
/// 字段命名与网站返回保持一致（camelCase，由 JsonSerializer 的 case-insensitive 解析）。
/// </summary>
public class LiveEntry
{
    public string? Id { get; set; }

    /// <summary>条目类型：episode（有字幕）、transition、programme-jingle、music 等。</summary>
    public string? Type { get; set; }

    /// <summary>内容类别：show、flash、word_flash、news、weather……</summary>
    public string? Kind { get; set; }

    public string? Title { get; set; }

    /// <summary>节目（可回看）的 id；音乐/过场可能为 null。</summary>
    public string? EpisodeId { get; set; }

    /// <summary>一次播出的运行 id，用于拼接字幕计时文件地址。</summary>
    public string? RunId { get; set; }

    /// <summary>该条目在服务器端的开始时间（UTC，ISO-8601）。</summary>
    public DateTime StartsAt { get; set; }

    /// <summary>该条目在服务器端的结束时间（UTC，ISO-8601）。</summary>
    public DateTime EndsAt { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>音频相对条目的偏移（秒），网站字幕同步时加上它。</summary>
    public double AudioOffsetSeconds { get; set; }
}

/// <summary>
/// GET https://slowenglish.fm/api/live 的返回结构（仅保留播放器需要的部分）。
/// </summary>
public class LiveResponse
{
    public bool Available { get; set; }
    public string? Mode { get; set; }
    public int Listeners { get; set; }

    /// <summary>当前正在播出的条目。</summary>
    public LiveEntry? Current { get; set; }

    /// <summary>接下来将要播出的条目（API 实际上返回的是数组）。</summary>
    public List<LiveEntry>? Next { get; set; }

    /// <summary>节目目录版本号，用于判断是否需要刷新。</summary>
    public double CatalogRevision { get; set; }

    public List<LiveEntry>? History { get; set; }
}
