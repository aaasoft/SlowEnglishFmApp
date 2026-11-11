using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Timers;
using SlowEnglishFmApp.Models;
using SlowEnglishFmApp.Services;

namespace SlowEnglishFmApp.ViewModels;

/// <summary>单个被显示的词，用于在界面上做“已读/正在读/未读”三种状态的可视化。</summary>
public class KaraokeWord : INotifyPropertyChanged
{
    public string Text { get; init; } = string.Empty;

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    private bool _isPast;
    public bool IsPast
    {
        get => _isPast;
        set => Set(ref _isPast, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>
/// 播放器主视图模型。
/// 职责：
/// 1. 维护播放状态（IsPlaying）与音量。
/// 2. 订阅 LiveRadioService，跟踪当前节目。
/// 3. 在播放时用“本地播放计时 + 节目起始时间”推算节目内已播秒数，
///    据此从字幕计时中定位当前正在发音的词，并高亮整句。
/// 说明：字幕同步使用本地播放经过时间（Stopwatch），与网站一致，
/// 可避免设备时钟偏差；网络缓冲带来的固定延迟可通过 SubtitleOffsetSeconds 微调。
/// </summary>
public class RadioViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILiveRadioService _live;
    private readonly ITimingsService _timings;
    private readonly System.Timers.Timer _karaokeTimer;

    private readonly Stopwatch _sw = new();
    private List<WordTiming>? _words;
    private int _currentTurn = -1;
    private string? _anchoredEntryId;

    // 字幕与音频对齐：用服务器 /api/live-clock 返回的真实节目时刻锚定，而非本地墙钟。
    // 因为直播流存在服务端缓冲延迟（约 10~15 秒），直接按墙钟算会让字幕比实际听到的音频快这么多。
    private DateTimeOffset _programmeAtAnchor = DateTimeOffset.UtcNow;
    private static readonly string _connectionId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _calCts;
    private bool _recalibrateNow;

    private bool _isPlaying;
    private string _programmeTitle = "Slow English FM";
    private string _nextTitle = string.Empty;
    private int _listeners;
    private bool _showKaraoke;
    private string _statusMessage = "点击播放按钮，开始收听实时广播";
    private double _volume = 1.0;
    private double _subtitleOffsetSeconds;
    private bool _isLiveAvailable = true;
    private int _activeWordIndex = -1;
    private int _lastActiveIndex = -1;

    public RadioViewModel(ILiveRadioService live, ITimingsService timings)
    {
        _live = live;
        _timings = timings;

        TogglePlayCommand = new Command(TogglePlay);
        NudgeOffsetCommand = new Command<double>(NudgeOffset);

        CurrentTurnWords = new ObservableCollection<KaraokeWord>();

        _live.CurrentChanged += OnCurrentChanged;
        _live.Error += OnLiveError;

        _karaokeTimer = new System.Timers.Timer(200);
        _karaokeTimer.AutoReset = true;
        _karaokeTimer.Elapsed += (_, _) => MainThread.BeginInvokeOnMainThread(TickKaraoke);

        // 立即同步一次当前节目信息（服务可能已先轮询到数据）。
        OnCurrentChanged(_live.Current);

        // 开始轮询实时状态。
        _live.Start();
    }

    public ObservableCollection<KaraokeWord> CurrentTurnWords { get; }

    public Command TogglePlayCommand { get; }
    public Command<double> NudgeOffsetCommand { get; }

    /// <summary>实时音频流地址（带连接 id）。页面首次播放时把它设为 MediaElement 的源；
    /// 同一 connection id 也会用于 /api/live-clock 校准字幕，必须保持一致。</summary>
    public string StreamUrl { get; } =
        $"https://slowenglish.fm/live.mp3?connection={_connectionId}";

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public string ProgrammeTitle
    {
        get => _programmeTitle;
        set => Set(ref _programmeTitle, value);
    }

    public string NextTitle
    {
        get => _nextTitle;
        set => Set(ref _nextTitle, value);
    }

    public int Listeners
    {
        get => _listeners;
        set => Set(ref _listeners, value);
    }

    public bool ShowKaraoke
    {
        get => _showKaraoke;
        set => Set(ref _showKaraoke, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public double Volume
    {
        get => _volume;
        set => Set(ref _volume, Math.Clamp(value, 0, 1));
    }

    /// <summary>字幕人工同步偏移（秒）。网络缓冲导致的固定延迟可用它补偿（负值让字幕提前）。</summary>
    public double SubtitleOffsetSeconds
    {
        get => _subtitleOffsetSeconds;
        set => Set(ref _subtitleOffsetSeconds, Math.Clamp(value, -30, 30));
    }

    public bool IsLiveAvailable
    {
        get => _isLiveAvailable;
        set => Set(ref _isLiveAvailable, value);
    }

    /// <summary>当前正在发音的词在当前句中的序号（-1 表示无）。页面据此自动滚动字幕。</summary>
    public int ActiveWordIndex
    {
        get => _activeWordIndex;
        private set => Set(ref _activeWordIndex, value);
    }

    // ---- 播放控制（由页面在 MediaElement 实际开始/暂停时回调） ----

    /// <summary>页面在 MediaElement 首次真正开始播放后调用：重置并锚定计时、启动字幕。</summary>
    public void NotifyPlaybackStarted()
    {
        if (!IsPlaying) IsPlaying = true;
        IsLiveAvailable = _live.IsAvailable;
        Reanchor(_live.Current);
        if (!_karaokeTimer.Enabled) _karaokeTimer.Start();
        StartClockCalibration();
    }

    /// <summary>页面在 MediaElement 从暂停恢复播放后调用：直播流暂停后恢复会跳到“新的直播边缘”，
    /// 本地计时已失准，故重置计时并以墙钟兜底，立即触发一次时钟校准对齐到真实节目时刻。</summary>
    public void ResumePlayback()
    {
        if (!IsPlaying) IsPlaying = true;
        _programmeAtAnchor = DateTimeOffset.UtcNow;
        _sw.Reset();
        _sw.Start();
        _recalibrateNow = true;
    }

    /// <summary>页面在 MediaElement 暂停后调用，暂停本地计时。</summary>
    public void NotifyPlaybackPaused()
    {
        _sw.Stop();
    }

    /// <summary>页面在 MediaElement 播放失败时调用。</summary>
    public void NotifyPlaybackFailed(string message)
    {
        IsPlaying = false;
        _sw.Stop();
        StatusMessage = $"播放失败：{message}";
    }

    private void TogglePlay()
    {
        IsPlaying = !IsPlaying;
        // 页面侦听 IsPlaying 变化来调用 MediaElement.Play()/Pause()。
    }

    private void NudgeOffset(double delta)
    {
        SubtitleOffsetSeconds += delta;
    }

    // ---- 当前节目变化 ----

    private void OnCurrentChanged(object? sender, LiveEntry? entry) => OnCurrentChanged(entry);
    private void OnCurrentChanged(LiveEntry? entry)
    {
        ProgrammeTitle = entry?.Title ?? "Slow English FM";
        NextTitle = _live.Next is { Count: > 0 } ? _live.Next[0].Title ?? string.Empty : string.Empty;
        Listeners = _live.Listeners;
        IsLiveAvailable = _live.IsAvailable;

        bool isEpisode = entry is { Type: "episode" }
                         && !string.IsNullOrEmpty(entry.RunId)
                         && !string.IsNullOrEmpty(entry.Kind);
        ShowKaraoke = isEpisode && _words is { Count: > 0 };

        if (IsPlaying)
        {
            Reanchor(entry);
        }
        else
        {
            // 尚未播放：清空字幕，等待播放时再锚定并加载。
            _words = null;
            _currentTurn = -1;
            _anchoredEntryId = entry?.Id;
            ClearTurnWords();
            StatusMessage = isEpisode
                ? "已就绪，点击播放开始收听并同步字幕"
                : (string.IsNullOrEmpty(entry?.Title) ? "电台暂未播出，请稍候…" : $"♪ {entry!.Title}");
        }
    }

    private void OnLiveError(object? sender, string message)
    {
        if (!IsPlaying)
            StatusMessage = $"连接电台出错：{message}";
    }

    /// <summary>重新锚定到某个节目：重置本地计时、加载字幕计时。
    /// 锚定前先捕获“当前真实节目时刻”，使跨节目切换连续、字幕不跳变。</summary>
    private void Reanchor(LiveEntry? entry)
    {
        if (entry is null) return;

        _anchoredEntryId = entry.Id;

        // 在重置计时前捕获当前真实节目时刻，随后把锚点设回它，保证连续。
        var trueNow = _programmeAtAnchor + _sw.Elapsed;
        _sw.Reset();
        _sw.Start();
        _programmeAtAnchor = trueNow;

        bool isEpisode = entry.Type == "episode"
                         && !string.IsNullOrEmpty(entry.RunId)
                         && !string.IsNullOrEmpty(entry.Kind);
        ShowKaraoke = isEpisode;
        _currentTurn = -1;
        _words = null;
        ClearTurnWords();

        if (!isEpisode)
        {
            StatusMessage = $"♪ {entry.Title}";
            return;
        }

        StatusMessage = "正在加载字幕…";
        _ = LoadTimingsAsync(entry.RunId!, entry.Kind!);
    }

    /// <summary>
    /// 启动后台时钟校准：轮询 /api/live-clock，把内部节目时间轴对齐到服务器真实节目时刻
    /// （该值已包含直播流的服务端缓冲延迟）。校准成功后每 30 秒复校一次，纠正时钟漂移/重缓冲。
    /// </summary>
    private void StartClockCalibration()
    {
        if (_calCts is not null) return;
        _calCts = new CancellationTokenSource();
        _ = CalibrateClockAsync(_calCts.Token);
    }

    private async Task CalibrateClockAsync(CancellationToken ct)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(500);
        while (!ct.IsCancellationRequested)
        {
            if (!IsPlaying || !_sw.IsRunning)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // 恢复播放后立刻复校，并复位退避。
            if (_recalibrateNow)
                backoff = TimeSpan.FromMilliseconds(500);

            var programmeAt = await _live.GetProgrammeAtAsync(_connectionId).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;

            if (programmeAt.HasValue)
            {
                // 无缝对齐：把服务器节目时刻减去“已播放时长”，得到计时起点对应的节目时刻。
                // 锚点写入调度到主线程，避免与 TickKaraoke（主线程）读 _programmeAtAnchor 发生撕裂读。
                var anchor = programmeAt.Value - _sw.Elapsed;
                MainThread.BeginInvokeOnMainThread(() => _programmeAtAnchor = anchor);
                _recalibrateNow = false;
                backoff = TimeSpan.FromMilliseconds(500);
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            else
            {
                try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
            }
        }
    }

    private async Task LoadTimingsAsync(string runId, string kind)
    {
        var words = await _timings.GetTimingsAsync(runId, kind).ConfigureAwait(false);
        if (words is null)
        {
            // 仍播放中且仍是同一节目才提示；否则忽略。
            if (_anchoredEntryId is not null)
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (IsPlaying) StatusMessage = "暂无字幕计时，继续收听中…";
                });
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _words = words.ToList();
            ShowKaraoke = true;
            StatusMessage = string.Empty;
            _currentTurn = -1; // 强制下一帧重建当前句
        }).ConfigureAwait(false);
    }

    // ---- 字幕逐帧更新 ----

    private void TickKaraoke()
    {
        if (!IsPlaying || _words is null or { Count: 0 })
            return;

        var entry = _live.Current;
        if (entry is null)
            return;

        // 字幕进度 = “当前真实节目时刻(经服务器时钟校准) − 节目起始时间 + 音频偏移 − 手动微调”。
        double elapsed = (_programmeAtAnchor.UtcDateTime - entry.StartsAt).TotalSeconds
                         + entry.AudioOffsetSeconds
                         - SubtitleOffsetSeconds
                         + _sw.Elapsed.TotalSeconds;
        var active = _words.LastOrDefault(w => w.Start <= elapsed + 0.08);
        int turn = active?.Turn ?? 0;

        if (turn != _currentTurn)
        {
            _currentTurn = turn;
            RebuildTurn(turn);
        }

        int idx = active?.Index ?? -1;
        for (int i = 0; i < CurrentTurnWords.Count; i++)
        {
            CurrentTurnWords[i].IsActive = i == idx;
            CurrentTurnWords[i].IsPast = i < idx;
        }

        if (idx != _lastActiveIndex)
        {
            _lastActiveIndex = idx;
            ActiveWordIndex = idx;
        }
    }

    private void RebuildTurn(int turn)
    {
        ClearTurnWords();
        foreach (var w in _words!.Where(w => w.Turn == turn))
            CurrentTurnWords.Add(new KaraokeWord { Text = w.Text });
    }

    private void ClearTurnWords()
    {
        if (CurrentTurnWords.Count > 0)
            CurrentTurnWords.Clear();
    }

    // ---- INotifyPropertyChanged ----

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public void Dispose()
    {
        _live.CurrentChanged -= OnCurrentChanged;
        _live.Error -= OnLiveError;
        _calCts?.Cancel();
        _calCts?.Dispose();
        _karaokeTimer.Stop();
        _karaokeTimer.Dispose();
        GC.SuppressFinalize(this);
    }
}
