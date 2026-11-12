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
    private string _text = string.Empty;

    /// <summary>
    /// 词文本。可写是为了换句时能复用已有的词控件（只改文字、不重建视图），
    /// 见 RadioViewModel.RebuildTurn —— 换句重建整句 Label 是卡顿的主要来源之一。
    /// </summary>
    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

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
/// 2. 订阅 LiveRadioService，跟踪节目时间轴。
/// 3. 把「MediaElement 播放进度」映射为「当前听到的节目时刻」，
///    由它同时驱动标题与卡拉OK逐词高亮，保证两者都与音频严格同步。
/// 说明：播放进度用 MediaElement.Position（真实播放位置），不用 Stopwatch 墙钟——
/// 直播流一旦缓冲/卡顿，墙钟会继续走而音频不会，字幕和标题就会越跑越快。
/// </summary>
public class RadioViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILiveRadioService _live;
    private readonly ITimingsService _timings;
    private readonly System.Timers.Timer _karaokeTimer;

    private readonly Stopwatch _sw = new();
    private readonly object _clockGate = new();
    private IReadOnlyList<WordTiming>? _words;
    /// <summary>按句号索引的词表（加载时一次建好）：换句时 O(1) 取词，不用每次扫描整集。</summary>
    private Dictionary<int, List<WordTiming>>? _wordsByTurn;
    /// <summary>当前显示的这一句（turn）包含的词，按顺序排列；用于把“当前词”换算成句中位置。</summary>
    private List<WordTiming>? _turnWords;
    private ObservableCollection<KaraokeWord> _currentTurnWords = new();
    private int _currentTurn = -1;
    private string? _anchoredEntryId;

    // ---- 字幕/标题同步核心：把「音频播放进度」映射到「节目时刻」 ----
    // 网站（app.js: livePlaybackNow）的做法是：接流时用 /api/live-clock 拿到 programmeAt，
    // 之后 programmeNow = programmeAt + audio.currentTime，即完全由音频播放进度推进。
    // 好处：音频卡顿/缓冲时字幕和标题跟着一起停，不会越播越快。
    // 旧实现用 Stopwatch（墙钟）推进、且每 30 秒用 programmeAt - 墙钟重新校准，
    // 每次都把服务端的缓冲区长度叠加进锚点 → 字幕与标题越来越超前（本次修复的问题）。
    private TimeSpan _position;                 // MediaElement.Position：真实播放进度
    private bool _positionReliable;             // 收到过真实的 Position 才认为可用
    private long _lastPositionTick;             // 上一次 Position「发生变化」的时刻（Stopwatch 时间戳）
    private TimeSpan _positionZero;             // 映射零点（播放进度侧）
    private DateTimeOffset _programmeAtZero = DateTimeOffset.UtcNow; // 映射零点（节目时刻侧）
    private bool _clockCalibrated;
    /// <summary>直播连接 id：流地址与 /api/live-clock 必须用同一个。每次重新连接都会换成新的。</summary>
    private string _connectionId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _calCts;
    /// <summary>校准循环在后台线程读、主线程/进度回调写，用 volatile 保证能及时看到变更。</summary>
    private volatile bool _recalibrateNow;

    /// <summary>
    /// 播放会话序号：每次「停止」和每次新建连接都 +1。
    /// 校准请求是异步的，返回时可能已经跨过一次停止/重播——旧会话的结果必须丢弃，
    /// 否则会把上一次播放的 programmeAt 与播放进度写进零点，字幕会定位到错误的节目
    /// （现象：停止再播放后字幕卡在一句话上不再推进）。
    /// </summary>
    private int _sessionEpoch;

    /// <summary>当前字幕集最后一个词条的结束秒数，用于检测时间映射跑偏。</summary>
    private double _wordsEnd;
    /// <summary>上次「自愈」校准请求的时间，避免反复请求。</summary>
    private DateTimeOffset _lastHealAt;
    /// <summary>上一次高亮词发生变化的时刻（Stopwatch 时间戳），用于看门狗检测时间基准被冻住。</summary>
    private long _lastAdvanceTick = Stopwatch.GetTimestamp();

    private bool _isPlaying;
    private string _programmeTitle = "Slow English FM";
    private string _nextTitle = string.Empty;
    private int _listeners;
    private bool _showKaraoke;
    private string _statusMessage = "点击播放按钮，开始收听实时广播";
    private bool _isLiveAvailable = true;
    private int _activeWordIndex = -1;
    private int _lastActiveIndex = -1;
    /// <summary>已有一帧高亮更新排在主线程队列里（1 = 有），用于防止卡顿时 tick 堆积。</summary>
    private int _tickPending;
    /// <summary>上一次 EntryAt 命中的时间轴下标与条目 Id，用作逐帧定位的快路径。</summary>
    private int _entryHintIndex = -1;
    private string? _entryHintId;

    public RadioViewModel(ILiveRadioService live, ITimingsService timings)
    {
        _live = live;
        _timings = timings;

        TogglePlayCommand = new Command(TogglePlay);

        _live.CurrentChanged += OnCurrentChanged;
        _live.Error += OnLiveError;

        // 60ms（≈16Hz）：tick 间隔直接决定高亮的响应延迟（平均一半、最坏一个周期）。
        // 之前 120ms 时最坏迟到 0.12s；TickKaraoke 本身只有一次锁读 + 二分查找，
        // 单帧开销远小于 1ms，低端车机也扛得住 16Hz。
        _karaokeTimer = new System.Timers.Timer(60);
        _karaokeTimer.AutoReset = true;
        _karaokeTimer.Elapsed += (_, _) =>
        {
            // 防堆积：主线程若正忙（换句重建、布局、GC），上一帧还没跑完就跳过本次投递。
            // 否则卡顿结束后会一次性补跑多个 tick —— 每个 tick 都按「此刻的节目时刻」重算，
            // 补跑纯属浪费，而且会进一步拖慢主线程，形成雪崩：表现为画面顿一下、
            // 高亮直接跳过两三个词。
            if (Interlocked.Exchange(ref _tickPending, 1) == 1) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { TickKaraoke(); }
                finally { Volatile.Write(ref _tickPending, 0); }
            });
        };

        // 立即同步一次当前节目信息（服务可能已先轮询到数据）。
        OnCurrentChanged(_live.Current);

        // 开始轮询实时状态。
        _live.Start();
    }

    /// <summary>
    /// 当前句要显示的词。整句替换而非逐个增删：换句时只触发一次 ItemsSource 变化，
    /// BindableLayout 一次性重建子视图，避免「Clear + N 次 Add」造成的多次布局与卡顿。
    /// </summary>
    public ObservableCollection<KaraokeWord> CurrentTurnWords
    {
        get => _currentTurnWords;
        private set => Set(ref _currentTurnWords, value);
    }

    public Command TogglePlayCommand { get; }

    /// <summary>实时音频流地址（带当前连接 id）。页面每次（重新）开始播放时把它设为 MediaElement 的源。</summary>
    public string StreamUrl
    {
        get { lock (_clockGate) return $"https://slowenglish.fm/live.mp3?connection={_connectionId}"; }
    }

    private string ConnectionId
    {
        get { lock (_clockGate) return _connectionId; }
    }

    /// <summary>
    /// 建立一条新的直播连接（换新的连接 id）并返回新的流地址。
    /// 每次点击播放都要调用：旧的连接在停止时已废弃，复用同一个 id 会让
    /// /api/live-clock 返回上一个连接的状态，校准结果不可信。
    /// </summary>
    public string BeginNewConnection()
    {
        // 新会话：先让旧的校准循环失效（在飞行的请求返回后会被 epoch 校验丢弃），
        // 再换连接 id，保证后续 /api/live-clock 用的确实是这条新连接。
        Interlocked.Increment(ref _sessionEpoch);
        StopClockCalibration();

        lock (_clockGate) _connectionId = Guid.NewGuid().ToString("N");
        return StreamUrl;
    }

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

    /// <summary>页面在 MediaElement 首次真正开始播放后调用：锚定计时、启动字幕。</summary>
    public void NotifyPlaybackStarted()
    {
        if (!IsPlaying) IsPlaying = true;
        IsLiveAvailable = _live.IsAvailable;

        // 从服务器给出的 current 起步；随后 TickKaraoke 会按“实际听到的时刻”自动纠正。
        if (_live.Current is { } cur) SwitchEntry(cur);

        lock (_clockGate)
        {
            _sw.Restart(); // 仅作为 Position 不可用时的兜底计时
            if (!_clockCalibrated)
            {
                // 还没拿到服务器时钟：先用墙钟兜底（会略超前），校准到达后自动纠正，
                // 否则会拿构造时刻当零点，节目时刻直接偏掉几分钟。
                _programmeAtZero = DateTimeOffset.UtcNow;
                _positionZero = _positionReliable ? _position : TimeSpan.Zero;
            }
        }

        if (!_karaokeTimer.Enabled) _karaokeTimer.Start();
        StartClockCalibration();
    }

    /// <summary>页面在 MediaElement 从暂停恢复播放后调用：直播流恢复可能跳到“新的直播边缘”，
    /// 先保持原映射不动（Position 连续时无需跳变），并立即触发一次时钟校准纠正跳边。</summary>
    public void ResumePlayback()
    {
        if (!IsPlaying) IsPlaying = true;

        lock (_clockGate)
        {
            // 只有走兜底计时（Position 不可用）时才需要平移零点，保证节目时刻连续。
            if (!_positionReliable)
            {
                var now = _programmeAtZero + (_sw.Elapsed - _positionZero);
                _positionZero = TimeSpan.Zero;
                _programmeAtZero = now;
            }
            // 兜底计时无论哪种情况都要重新起跑：暂停期间它被停掉了，
            // 若之后 Position 又变得不可用，指望它继续走时就不能是停着的。
            _sw.Restart();
        }

        _recalibrateNow = true;
    }

    /// <summary>页面在 MediaElement 暂停后调用，暂停本地计时。</summary>
    public void NotifyPlaybackPaused()
    {
        _sw.Stop();
    }

    /// <summary>
    /// 页面在「停止」播放后调用：断流后所有时间映射都失效，必须整体重置，
    /// 否则下次播放（新连接、Position 从 0 重新开始）会沿用旧映射，导致字幕错位甚至不再推进。
    /// </summary>
    public void NotifyPlaybackStopped()
    {
        // 关键顺序：先作废会话（epoch +1）并取消失败的校准循环，再重置映射。
        // 否则停止瞬间仍在飞行的 /api/live-clock 请求返回后，会把旧会话的零点写回来，
        // 下次播放就会沿用错误的节目时刻。
        Interlocked.Increment(ref _sessionEpoch);
        StopClockCalibration();

        IsPlaying = false;
        _sw.Reset();

        lock (_clockGate)
        {
            _position = TimeSpan.Zero;
            _positionReliable = false;
            _positionZero = TimeSpan.Zero;
            _programmeAtZero = DateTimeOffset.UtcNow;
            _clockCalibrated = false; // 下次播放是全新连接，必须重新校准
            _recalibrateNow = false;
        }

        _anchoredEntryId = null;
        _words = null;
        _wordsByTurn = null;
        _wordsEnd = 0;
        _currentTurn = -1;
        _lastActiveIndex = -1;
        ActiveWordIndex = -1;
        ShowKaraoke = false;
        ClearTurnWords();
        StatusMessage = "已停止 · 点击播放重新收听";
    }

    /// <summary>页面在 MediaElement 播放失败时调用。</summary>
    public void NotifyPlaybackFailed(string message)
    {
        IsPlaying = false;
        _sw.Stop();
        StatusMessage = $"播放失败：{message}";
    }

    /// <summary>
    /// 页面在 MediaElement.PositionChanged 时调用（可能在非 UI 线程）。
    /// 这是字幕/标题的时间基准：只有真实播放进度才能反映“用户此刻听到的是哪一段音频”。
    /// </summary>
    public void OnPlaybackPositionChanged(TimeSpan position)
    {
        if (position < TimeSpan.Zero) return;

        // 已停止（或正在停止）时，媒体元素可能还会补发上一会话的进度事件。
        // 这些值属于旧会话，写进来会污染新会话的零点，直接丢弃。
        if (!IsPlaying) return;

        lock (_clockGate)
        {
            // 尚未拿到过可信进度时，一直报 0 说明这个实现根本没推进播放位置，
            // 不能当作可信进度（否则字幕会卡在第一句不动），只记录、继续用 Stopwatch 兜底。
            if (position == TimeSpan.Zero && !_positionReliable)
            {
                _position = TimeSpan.Zero;
                return;
            }

            // 断点判定必须看「原始值相对上一次原始值的跳变」，绝不能拿外推值比：
            // 音频缓冲停顿时外推会跑到前面，恢复后原始值相对外推值"回退"数秒——
            // 那只是缓冲恢复，若误判成重连会触发多余的重新校准，字幕直接跳到
            // 服务器直播边缘（比实际听到的超前好几秒）= 字幕乱跳。
            bool wasReliable = _positionReliable;
            bool discontinuity = !wasReliable
                                 || _position - position > TimeSpan.FromSeconds(2)    // 原始值回退：流重连
                                 || position - _position > TimeSpan.FromSeconds(5);  // 原始值前跳

            // 外推值（连续时间轴此刻的读数）：断点平移零点以它为基准，保证节目时刻不断档。
            TimeSpan oldProgress = _positionReliable ? EffectiveProgressLocked() : _sw.Elapsed;

            if (position != _position)
                _lastPositionTick = Stopwatch.GetTimestamp();

            _position = position;
            _positionReliable = true;

            if (discontinuity)
            {
                _positionZero += position - oldProgress;

                // 回退说明直播流重连过，需要重新向服务器校准一次。
                if (wasReliable) _recalibrateNow = true;
            }
            else
            {
                // 软校正（限幅）：外推值与真实值有小偏差（上报延迟抖动）时只吸收一半，
                // 且单次最多 150ms —— 时间轴始终连续，不会整词整句地回跳。
                // 不限幅的话，缓冲恢复的数秒偏差会一次拉回，高亮大批回跳，看着就是乱跳。
                double deltaMs = (position - oldProgress).TotalMilliseconds;
                if (Math.Abs(deltaMs) > 25)
                {
                    double stepMs = Math.Clamp(deltaMs / 2, -150, 150);
                    _positionZero += TimeSpan.FromMilliseconds(stepMs);
                }
            }
        }
    }

    private void TogglePlay()
    {
        IsPlaying = !IsPlaying;
        // 页面侦听 IsPlaying 变化来调用 MediaElement.Play()/Pause()。
    }

    // ---- 当前节目变化 ----

    private void OnCurrentChanged(object? sender, LiveEntry? entry) => OnCurrentChanged(entry);
    private void OnCurrentChanged(LiveEntry? entry)
    {
        Listeners = _live.Listeners;
        IsLiveAvailable = _live.IsAvailable;

        // 播放中：标题与字幕一律由“实际播放进度”驱动（见 TickKaraoke），
        // 不能跟着服务器的 current 走 —— 服务器的 current 已包含流缓冲，会比听到的音频超前。
        if (IsPlaying) return;

        _anchoredEntryId = entry?.Id;
        _words = null;
        _wordsByTurn = null;
        _wordsEnd = 0;
        _currentTurn = -1;
        _lastActiveIndex = -1;
        ClearTurnWords();

        ProgrammeTitle = string.IsNullOrWhiteSpace(entry?.Title) ? "Slow English FM" : entry!.Title!;
        NextTitle = _live.Next is { Count: > 0 } ? _live.Next[0].Title ?? string.Empty : string.Empty;

        bool isEpisode = IsEpisode(entry);
        ShowKaraoke = false;
        StatusMessage = isEpisode
            ? "已就绪，点击播放开始收听并同步字幕"
            : (string.IsNullOrEmpty(entry?.Title) ? "电台暂未播出，请稍候…" : $"♪ {entry!.Title}");
    }

    private void OnLiveError(object? sender, string message)
    {
        if (!IsPlaying)
            StatusMessage = $"连接电台出错：{message}";
    }

    private static bool IsEpisode(LiveEntry? entry) =>
        entry is { Type: "episode" }
        && !string.IsNullOrEmpty(entry.RunId)
        && !string.IsNullOrEmpty(entry.Kind);

    /// <summary>
    /// 切换到某个节目：更新标题、清空上一集的字幕并加载这一集的逐词计时。
    /// 注意这里不再重置节目时钟 —— 时钟是连续的，跨节目切换时字幕应平滑接续。
    /// </summary>
    private void SwitchEntry(LiveEntry entry)
    {
        _anchoredEntryId = entry.Id;
        _currentTurn = -1;
        _lastActiveIndex = -1;
        _words = null;
        _wordsByTurn = null;
        _wordsEnd = 0;
        ClearTurnWords();
        UpdateTitles(entry);

        bool isEpisode = IsEpisode(entry);
        ShowKaraoke = isEpisode;

        if (!isEpisode)
        {
            StatusMessage = $"♪ {entry.Title}";
            return;
        }

        StatusMessage = "正在加载字幕…";
        _ = LoadTimingsAsync(entry.RunId!, entry.Kind!, entry.Id);
    }

    private void UpdateTitles(LiveEntry entry)
    {
        ProgrammeTitle = string.IsNullOrWhiteSpace(entry.Title) ? "Slow English FM" : entry.Title!;
        NextTitle = NextAfter(entry)?.Title ?? string.Empty;
    }

    /// <summary>时间轴上排在 entry 之后的下一个“像节目”的条目（跳过转场/台呼这类碎片）。</summary>
    private LiveEntry? NextAfter(LiveEntry entry)
    {
        var timeline = _live.Timeline;
        for (int i = 0; i < timeline.Count - 1; i++)
        {
            if (timeline[i].Id != entry.Id) continue;

            for (int j = i + 1; j < timeline.Count; j++)
            {
                var t = timeline[j].Type;
                if (t is "transition" or "station-id" or "programme-jingle") continue;
                return timeline[j];
            }
            return null;
        }
        return null;
    }

    // ---- 时间基准 ----

    /// <summary>
    /// 当前“播放进度”：优先用 MediaElement.Position，拿不到时退回 Stopwatch 兜底。
    /// </summary>
    private TimeSpan ProgressNow()
    {
        lock (_clockGate)
            return EffectiveProgressLocked();
    }

    /// <summary>
    /// 计算当前进度（调用方必须已持有 _clockGate）。
    ///
    /// PositionChanged 事件的粒度是数百毫秒，若直接用原始值，「节目时刻」就会以几百毫秒为
    /// 台阶跳跃前进 —— 高亮不是迟半拍就是连跳几个词。所以只要拿到过可信进度，
    /// 就始终「以最后一次已知进度为基点、按真实流逝时间外推」，每个 tick 都连续推进；
    /// 新的真实进度到达后由 OnPlaybackPositionChanged 做软校正收敛，外推不会累积误差。
    /// 音频真正缓冲停顿时外推会暂时跑前一点，恢复后由限幅的软校正平滑拉回 ——
    /// 与网站（完全用 audio.currentTime，停顿即冻结）相比是可接受的取舍。
    /// </summary>
    private TimeSpan EffectiveProgressLocked()
    {
        if (!_positionReliable) return _sw.Elapsed;

        var stalled = Stopwatch.GetElapsedTime(_lastPositionTick);
        return _position + stalled;
    }

    /// <summary>当前听到的音频所对应的节目时刻（网站 livePlaybackNow 的等价实现）。</summary>
    private DateTimeOffset ProgrammeNow()
    {
        lock (_clockGate)
            return _programmeAtZero + (EffectiveProgressLocked() - _positionZero);
    }

    /// <summary>在时间轴上定位“此刻正在播出的条目”；时间轴还没数据时退回服务器的 current。</summary>
    private LiveEntry? EntryAt(DateTimeOffset now)
    {
        // 未校准时节目时刻还不准（墙钟会略超前），先沿用服务器 current，等校准后再按进度定位，
        // 避免刚开播时标题在 current / next 之间来回跳。
        if (!_clockCalibrated)
            return _live.Current;

        var timeline = _live.Timeline;

        // 绝大多数帧命中的还是同一条节目：拿上次的结果和它的下一条比两次即可，
        // 命中就完全跳过整条时间轴的遍历（每帧省掉上百次比较）。
        int hint = _entryHintIndex;
        if (hint >= 0 && hint < timeline.Count
            && timeline[hint].Id == _entryHintId
            && timeline[hint].StartsAt <= now
            && (hint + 1 >= timeline.Count || timeline[hint + 1].StartsAt > now))
        {
            return timeline[hint];
        }

        LiveEntry? found = null;
        int foundIndex = -1;
        for (int i = 0; i < timeline.Count; i++)
        {
            if (timeline[i].StartsAt <= now)
            {
                found = timeline[i];
                foundIndex = i;
            }
            else
            {
                break; // 时间轴已按 StartsAt 升序
            }
        }

        _entryHintIndex = foundIndex;
        _entryHintId = found?.Id;
        return found ?? _live.Current;
    }

    /// <summary>
    /// 启动后台时钟校准。与网站一致：**只在接流时校准一次**（拿到 programmeAt 作为
    /// 「零播放进度 = 该节目时刻」的基准），之后完全由音频播放进度推进。
    /// 仅在校准失败、暂停恢复、检测到流重连时重新校准。
    /// </summary>
    private void StartClockCalibration()
    {
        if (_calCts is not null) return;
        var cts = new CancellationTokenSource();
        _calCts = cts;
        _ = CalibrateClockAsync(Volatile.Read(ref _sessionEpoch), cts.Token);
    }

    /// <summary>
    /// 结束当前校准循环。不 Dispose：循环可能仍持着该 token 在 await 中，
    /// 只发取消信号，由它自己退出即可（每个会话最多一个 CTS，代价可忽略）。
    /// </summary>
    private void StopClockCalibration()
    {
        var cts = Interlocked.Exchange(ref _calCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* 已释放/已取消，忽略 */ }
    }

    private async Task CalibrateClockAsync(int epoch, CancellationToken ct)
    {
        TimeSpan backoff = TimeSpan.FromMilliseconds(500);

        while (!ct.IsCancellationRequested && epoch == Volatile.Read(ref _sessionEpoch))
        {
            if (!IsPlaying)
            {
                if (!await DelayAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false)) break;
                continue;
            }

            // 已校准且没有被要求复校：空转等待（不再周期性重算，避免把缓冲区叠加进锚点）。
            if (_clockCalibrated && !_recalibrateNow)
            {
                if (!await DelayAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false)) break;
                continue;
            }

            // 用“请求发出瞬间的播放进度”与 programmeAt 配对，误差最小；
            // 连接 id 也在这里定格，避免请求过程中被新会话改写。
            var connectionId = ConnectionId;
            var progressAtRequest = ProgressNow();

            DateTimeOffset? programmeAt = null;
            try
            {
                programmeAt = await _live.GetProgrammeAtAsync(connectionId).ConfigureAwait(false);
            }
            catch
            {
                // 网络异常不应让校准循环永久退出（否则本次播放再也拿不到校准结果）。
                programmeAt = null;
            }

            // 请求期间用户可能已经停止或重新播放：本次结果作废，直接退出，由新会话重新校准。
            if (ct.IsCancellationRequested || epoch != Volatile.Read(ref _sessionEpoch)) break;

            if (programmeAt.HasValue)
            {
                // 零点写入调度到主线程，避免与 TickKaraoke 的读取交错产生撕裂读。
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    // 再次校验会话：BeginInvoke 排队期间也可能已经停止重播。
                    if (epoch != Volatile.Read(ref _sessionEpoch) || !IsPlaying) return;

                    lock (_clockGate)
                    {
                        _programmeAtZero = programmeAt.Value;
                        _positionZero = progressAtRequest;
                    }
                    _clockCalibrated = true;
                    _recalibrateNow = false;
                });
                backoff = TimeSpan.FromMilliseconds(500);
            }
            else
            {
                // 连接尚未就绪（{"error":"Connection not ready"}）：像网站一样退避重试。
                if (!await DelayAsync(backoff, ct).ConfigureAwait(false)) break;
                backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 5000));
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task LoadTimingsAsync(string runId, string kind, string? expectedEntryId)
    {
        var words = await _timings.GetTimingsAsync(runId, kind).ConfigureAwait(false);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            // 加载期间节目可能已经切换，丢弃过期结果。
            if (expectedEntryId is not null && _anchoredEntryId != expectedEntryId) return;

            if (words is null or { Count: 0 })
            {
                // 少数节目确实没有逐词计时文件，保持收听即可，不打断。
                if (IsPlaying) StatusMessage = "暂无字幕计时，继续收听中…";
                return;
            }

            // 二分定位「当前词」要求按开始时间升序：计时文件通常是升序的，
            // 这里做一次性排序兜底，避免个别文件乱序导致高亮一直卡在某个词。
            var sorted = words.OrderBy(w => w.Start).ToList();

            _words = sorted;
            _wordsByTurn = sorted.GroupBy(w => w.Turn)
                                 .ToDictionary(g => g.Key, g => g.ToList());
            _wordsEnd = sorted.Max(w => w.End);
            _lastAdvanceTick = Stopwatch.GetTimestamp();
            ShowKaraoke = true;
            StatusMessage = string.Empty;
            _currentTurn = -1; // 强制下一帧重建当前句
        }).ConfigureAwait(false);
    }

    // ---- 字幕逐帧更新 ----

    private void TickKaraoke()
    {
        if (!IsPlaying) return;

        // 1) 用“此刻听到的节目时刻”在时间轴上定位节目（而不是服务器的 current），
        //    这样标题、字幕与音频始终是同一时刻的一致视图。
        var now = ProgrammeNow();
        var entry = EntryAt(now);
        if (entry is null) return;

        if (entry.Id != _anchoredEntryId)
            SwitchEntry(entry);
        // 同一个节目内标题不会变，不必每帧重算 NextAfter（那是整条时间轴的二次扫描）。

        // 2) 逐词高亮（仅 episode 有逐词计时）。
        if (_words is null or { Count: 0 }) return;

        double elapsed = Math.Max(0, (now - entry.StartsAt).TotalSeconds)
                         + entry.AudioOffsetSeconds;

        // 自愈：节目时刻已经明显超过这一集字幕的结尾，说明时间映射跑偏了
        // （典型场景就是停止→重播时沿用了上一次的零点）。这时字幕会永久停在最后一句、
        // 高亮不再移动；主动请求一次重新校准把映射拉回来，而不是干等着。
        if (elapsed > _wordsEnd + 30
            && !_recalibrateNow
            && DateTimeOffset.UtcNow - _lastHealAt > TimeSpan.FromSeconds(30))
        {
            _lastHealAt = DateTimeOffset.UtcNow;
            _recalibrateNow = true;
        }

        var active = WordAt(elapsed);
        int turn = active?.Turn ?? 0;

        if (turn != _currentTurn)
        {
            _currentTurn = turn;
            RebuildTurn(turn);
            // 换句后句内下标全部作废：旧索引不能拿去更新新集合，否则会把新句里
            // 恰好同位置的词误标成「已读」。
            _lastActiveIndex = -1;
        }

        // 关键：不能用 word.index 定位——服务端给的 index 多为“全文件累计序号”，
        // 个别文件又按句重置，两种口径混用会导致除第一句外整句都不高亮。
        // 这里改为“该词在本句列表中的位置”。
        int idx = IndexInTurn(active);
        if (idx != _lastActiveIndex)
        {
            ApplyHighlight(_lastActiveIndex, idx);
            _lastActiveIndex = idx;
            _lastAdvanceTick = Stopwatch.GetTimestamp();
            ActiveWordIndex = idx;
        }
        else if (elapsed > 0 && elapsed < _wordsEnd
                 && Stopwatch.GetElapsedTime(_lastAdvanceTick).TotalSeconds > 6)
        {
            // 看门狗：播放中高亮已经 6 秒没有任何变化，而节目时刻仍在这集字幕范围内，
            // 说明时间基准被冻住了（Position 停更 + 兜底计时也被停掉等极端情况）。
            // 先确保兜底计时在跑，再请求一次重新校准 —— 字幕会立刻跳回正确位置继续走。
            lock (_clockGate)
            {
                if (!_sw.IsRunning) _sw.Start();
            }
            _recalibrateNow = true;
            _lastAdvanceTick = Stopwatch.GetTimestamp();
        }
    }

    private void RebuildTurn(int turn)
    {
        _turnWords = _wordsByTurn is not null && _wordsByTurn.TryGetValue(turn, out var words)
            ? words
            : new List<WordTiming>();

        var existing = CurrentTurnWords;

        // 词数相同时直接复用现有视图，只换文字并复位状态：
        // 换句时重建整句 Label 会触发一次完整的测量与布局（低端车机上就是「卡一下」），
        // 复用则只产生几次文本变更，几乎不触发重排。
        if (existing.Count > 0 && existing.Count == _turnWords.Count)
        {
            for (int i = 0; i < existing.Count; i++)
            {
                existing[i].Text = _turnWords[i].Text;
                existing[i].IsActive = false;
                existing[i].IsPast = false;
            }
            _lastActiveIndex = -1;
            TurnChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 一次性替换整句集合（而不是逐个 Add）：BindableLayout 只重建一次子视图，
        // 换句时不会因为「先清空、再一个个加」而产生多次布局与闪烁。
        var items = new ObservableCollection<KaraokeWord>(
            _turnWords.Select(w => new KaraokeWord { Text = w.Text }));
        CurrentTurnWords = items;
        _lastActiveIndex = -1;
        TurnChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>换句了（整句文字已变化）。页面据此把字幕滚动位置归零，避免新句从旧偏移处开始显示。</summary>
    public event EventHandler? TurnChanged;

    /// <summary>
    /// 在整集词表里二分定位「此刻正在发音（或刚读完）的词」。
    /// 原先每帧用 LINQ 线性扫描整集（可达上千词），会产生大量闭包分配，
    /// GC 抖动在低端设备上就是可感知的一顿。
    /// </summary>
    private WordTiming? WordAt(double elapsed)
    {
        var words = _words;
        if (words is null || words.Count == 0) return null;

        double t = elapsed + 0.08;   // 稍微提前一点，词一开口就亮起来
        int lo = 0, hi = words.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (words[mid].Start <= t) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best >= 0 ? words[best] : null;
    }

    /// <summary>
    /// 只更新「状态真正变化」的词，而不是每帧把整句所有词重新赋值。
    /// 一次跨越多个词（主线程被占用后追上进度）时，中间被越过的词一次性标为已读，
    /// 不做逐个补亮 —— 补亮会让画面闪一串，反倒更像卡了。
    /// </summary>
    private void ApplyHighlight(int from, int to)
    {
        var list = CurrentTurnWords;
        int n = list.Count;
        if (n == 0) return;

        if (from >= 0 && from < n)
        {
            list[from].IsActive = false;
            // 回退（to < from）时不要把停下的那个词标成已读
            list[from].IsPast = to < 0 || from < to;
        }

        if (to < 0 || to >= n) return;

        int lo = from < 0 ? 0 : Math.Min(from, to);
        int hi = Math.Max(from, to);
        for (int i = lo; i <= hi; i++)
        {
            if (i == from || i == to) continue;
            list[i].IsPast = i < to;
        }

        list[to].IsPast = false;
        list[to].IsActive = true;
    }

    /// <summary>当前词在本句中的位置（0 起）；不属于本句时返回 -1。</summary>
    private int IndexInTurn(WordTiming? active)
    {
        if (active is null || _turnWords is null) return -1;
        for (int i = 0; i < _turnWords.Count; i++)
        {
            if (ReferenceEquals(_turnWords[i], active)) return i;
        }
        return -1;
    }

    private void ClearTurnWords()
    {
        _turnWords = null;
        _lastActiveIndex = -1;
        if (CurrentTurnWords.Count > 0)
        {
            CurrentTurnWords = new ObservableCollection<KaraokeWord>();
            // 清空同样要让页面把滚动归零，否则下次开播的第一句会从上次的偏移处开始显示。
            TurnChanged?.Invoke(this, EventArgs.Empty);
        }
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
