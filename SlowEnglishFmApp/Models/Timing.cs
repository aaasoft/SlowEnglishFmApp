namespace SlowEnglishFmApp.Models;

/// <summary>
/// 单句（turn）中的单个词，带起止时间，用于卡拉OK式逐词高亮。
/// 对应 https://slowenglish.fm/timings/runs/{runId}/{kind}/word-timings.json 中的 words[] 元素。
/// </summary>
public class WordTiming
{
    /// <summary>所属句子序号（0 起）。</summary>
    public int Turn { get; set; }

    /// <summary>
    /// 服务端给出的词序号。注意：它并不可靠——多数文件里是**全文件累计序号**
    /// （turn 0 为 0..13、turn 1 为 14..65），个别文件又会按句重置为 0。
    /// 因此不要用它做"句中第几个词"的比对，请用词在所属 turn 内的先后顺序。
    /// </summary>
    public int Index { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>相对节目开始的发音起始时间（秒）。</summary>
    public double Start { get; set; }

    /// <summary>相对节目开始的发音结束时间（秒）。</summary>
    public double End { get; set; }
}

/// <summary>
/// 字幕计时文件根对象。quality 仅作透传保留，卡拉OK同步不使用其细节。
/// </summary>
public class TimingFile
{
    public List<WordTiming> Words { get; set; } = new();

    public object? Quality { get; set; }
}
