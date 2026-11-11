using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Dispatching;   // IDispatcher.DispatchDelayed
using SlowEnglishFmApp.ViewModels;

namespace SlowEnglishFmApp;

public partial class MainPage : ContentPage
{
    private readonly RadioViewModel _vm;
    private readonly MediaElement _player;
    private bool _playbackBegun;

    // 字幕滚动控制
    private bool _scrolling;          // 正在执行一次滚动
    private bool _pendingScroll;      // 滚动期间又有新目标，结束后补一次
    private bool _turnResetPending;   // 刚换句，需要先把滚动位置归零
    private bool _stopping;           // 正在停止，防止 Stop 事件重入
    private int _attemptId;           // 播放请求序号：每次播放/停止 +1，用于作废过期的兜底检查
    private bool _hasPlayed;          // 本次会话是否真正到达过 Playing（区分「外部暂停」与「Play() 被吞」）
    private bool _sourceStale;        // 播放器里留着的是「停止后遗留的旧连接」，被外部拉起来时要换新源
    private const int ExternalPauseConfirmMs = 600;  // 外部暂停确认窗口：期间恢复 Playing 则不算停止

    // 布局模式：null = 尚未确定；true = 横屏紧凑，false = 竖屏常规
    private bool? _compact;


    public MainPage()
    {
        InitializeComponent();

        _vm = IPlatformApplication.Current!.Services.GetRequiredService<RadioViewModel>();
        BindingContext = _vm;

        // MediaElement 在代码中创建（避免在 XAML 中引用工具包自定义控件，
        // 以兼容 MAUI XAML 源生成器对第三方控件的解析限制）。
        _player = new MediaElement
        {
            IsVisible = false,
            ShouldAutoPlay = false,
            ShouldShowPlaybackControls = false,
            Volume = 1.0,   // 音量固定为原始响度，由系统音量键调节
        };
        _player.BindingContext = _vm;

        // 系统通知栏 / 锁屏媒体卡片的标题来自这里。注意 Android 实现是「设置 Source 时读取元数据」，
        // 所以元数据必须在赋 Source 之前写好（见 HandlePlayState），运行中再改不会刷新通知。
        _player.MetadataTitle = _vm.ProgrammeTitle;
        _player.MetadataArtist = "Slow English FM · 实时直播";

        _player.MediaOpened += OnMediaOpened;
        _player.StateChanged += OnMediaStateChanged;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        // 真实播放进度是字幕/标题的唯一时间基准（墙钟会在缓冲、卡顿时跑偏）。
        _player.PositionChanged += OnMediaPositionChanged;
        Grid.SetRow(_player, 0);
        Grid.SetRowSpan(_player, 3);
        RootGrid.Children.Add(_player);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.TurnChanged += OnTurnChanged;

        SizeChanged += (_, _) => ApplyLayoutMode();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ApplyLayoutMode();
    }

    /// <summary>
    /// 按屏幕方向切换布局密度。
    ///
    /// 手机/车机横屏时可用高度只有竖屏的一半左右，而顶部台标+标题、底部播放按钮都是固定高度，
    /// 结果「*」行的字幕卡片被压成一条窄缝，ScrollView 只能露出一条被裁切的文字
    /// （现象：横屏看不到字幕）。横屏采用两项措施：
    /// 1. 顶部/底部收紧（缩小内边距、字号、标题限制为 1 行）；
    /// 2. 底部大按钮换成顶部台标右侧的 64dp 圆形按钮，底部整块高度让给字幕。
    /// </summary>
    private void ApplyLayoutMode()
    {
        if (Width <= 0 || Height <= 0) return;

        bool compact = Width > Height;      // 宽 > 高 ⇒ 横屏
        if (_compact == compact) return;
        _compact = compact;

        if (compact)
        {
            TopPanel.Padding = new Thickness(16, 6, 16, 2);
            OnAirBadge.Padding = new Thickness(8, 2);
            OnAirLabel.FontSize = 12;
            ListenersLabel.FontSize = 12;
            ListenersIcon.Scale = 0.8;
            TitlePanel.Margin = new Thickness(0, 6, 0, 0);
            ProgrammeLabel.FontSize = 20;
            ProgrammeLabel.MaxLines = 1;
            ProgrammeLabel.LineBreakMode = LineBreakMode.TailTruncation;
            NextLabel.FontSize = 12;

            SubtitleBorder.Margin = new Thickness(12, 2);
            SubtitleBorder.Padding = new Thickness(10, 6);

            BottomPanel.Padding = new Thickness(16, 0, 16, 6);
            StatusLabel.FontSize = 12;

            // 横屏：控制按钮移到顶部台标右侧（圆形），底部大按钮隐藏。
            PlayButton.IsVisible = false;
            CompactPlayButton.IsVisible = true;
        }
        else
        {
            TopPanel.Padding = new Thickness(24, 18, 24, 8);
            OnAirBadge.Padding = new Thickness(10, 4);
            OnAirLabel.FontSize = 16;
            ListenersLabel.FontSize = 16;
            ListenersIcon.Scale = 1.0;
            TitlePanel.Margin = new Thickness(0, 12, 0, 0);
            ProgrammeLabel.FontSize = 30;
            ProgrammeLabel.MaxLines = 2;
            ProgrammeLabel.LineBreakMode = LineBreakMode.WordWrap;
            NextLabel.FontSize = 16;

            SubtitleBorder.Margin = new Thickness(16, 4);
            SubtitleBorder.Padding = new Thickness(20);

            BottomPanel.Padding = new Thickness(24, 10, 24, 24);
            StatusLabel.FontSize = 16;
            // 竖屏：恢复底部大按钮，隐藏顶部圆形按钮。
            PlayButton.IsVisible = true;
            CompactPlayButton.IsVisible = false;
            PlayButton.FontSize = 28;
            PlayButton.HeightRequest = 84;
            PlayButton.CornerRadius = 42;
            PlayButton.Margin = new Thickness(0, 6);
        }

        // 视口尺寸变了，旧的字幕滚动偏移不再有效，回到顶部由接下来的高亮重新定位。
        WordsHost.InvalidateMeasure();
        _turnResetPending = true;
        _ = ResetScrollToTopAsync();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioViewModel.IsPlaying))
            HandlePlayState(_vm.IsPlaying);
        else if (e.PropertyName == nameof(RadioViewModel.ActiveWordIndex))
            _ = ScrollToActiveWordAsync();
        else if (e.PropertyName == nameof(RadioViewModel.ProgrammeTitle))
        {
            // 节目信息来自后台轮询线程，改 BindableProperty 必须回到主线程。
            string title = _vm.ProgrammeTitle;
            MainThread.BeginInvokeOnMainThread(() => _player.MetadataTitle = title);
        }
    }

    private void OnTurnChanged(object? sender, EventArgs e) => HandleTurnRebuilt();

    /// <summary>
    /// 换句（整句字幕被替换）时调用。
    /// Android 的 ScrollView 在内容被整体替换后会保留旧的滚动偏移，
    /// 新句第一帧就会出现在底部，再被动画拉回顶部 —— 表现为「从最底部慢慢滚到顶部」。
    /// 这里直接把偏移无动画归零，新句从第一行开始显示。
    /// </summary>
    private void HandleTurnRebuilt()
    {
        _turnResetPending = true;
        _scrolling = false;
        _pendingScroll = false;

        // 换句重建和滚动归零若挤在同一帧，这一帧会明显变长（就是「字幕顿一下」）。
        // 把归零推到下一轮消息循环：重建完成后新内容已有正确高度，滚一次就到位。
        Dispatcher.Dispatch(() => _ = ResetScrollToTopAsync());
    }

    private async Task ResetScrollToTopAsync()
    {
        try
        {
            if (SubtitleScroll.ScrollY != 0)
                await SubtitleScroll.ScrollToAsync(0, 0, false);
        }
        catch
        {
            // 此时内容可能正处于替换过程中，忽略即可：下面对目标词的定位会再校正一次。
        }
    }

    private void HandlePlayState(bool playing)
    {
        DeviceDisplay.Current.KeepScreenOn = playing;
        int attempt = ++_attemptId;   // 作废上一次尚未跑完的兜底检查

        if (playing)
        {
            if (!_playbackBegun)
            {
                // 每次开始播放都换一个新的连接 id：停止时旧连接已废弃，
                // 复用同一个 id 会让 /api/live-clock 校准到上一个连接的时刻。
                // 元数据必须在赋 Source 之前设置：Android 侧只在设置源时读取一次，
                // 否则通知栏会一直显示应用名而看不到正在播的节目。
                _hasPlayed = false;   // 新会话：还没真正播过，此时出现的 Paused 只是「Play() 被吞」
                _sourceStale = false; // 已经是新连接了，不再需要「换源重建」
                _player.MetadataTitle = _vm.ProgrammeTitle;
                _player.MetadataArtist = "Slow English FM · 实时直播";
                _player.Source = MediaSource.FromUri(new Uri(_vm.BeginNewConnection()));
            }
            _player.Play();

            // 快速「停止→播放」时，Play() 可能落在播放器还没准备好的时刻而被忽略，
            // 结果是界面显示「停止」但其实没播：PositionChanged 不再上报，
            // 字幕的时间基准整条冻住（现象：字幕卡在一个词上不动）。
            // 这里在很短的时间窗口内补一次 Play()，确保真的播起来。
            _ = EnsurePlayingAsync(attempt);
        }
        else
        {
            StopPlayback();
        }
    }

    /// <summary>
    /// 播放启动后的兜底检查：若用户意图是「播放」但播放器仍停在 Paused/Stopped，
    /// 再补一次 Play()。只在刚点播放的一两秒内生效，不会干扰系统正常暂停（如来电）。
    /// </summary>
    private async Task EnsurePlayingAsync(int attempt)
    {
        try
        {
            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(700);

                // 期间用户又点了停止/播放，或者页面已经停止播放：本次检查作废。
                if (attempt != _attemptId || !_vm.IsPlaying || _stopping) return;
                if (_player.Source is null) return;

                if (_player.CurrentState is MediaElementState.Paused or MediaElementState.Stopped)
                    _player.Play();
            }
        }
        catch
        {
            // 页面已销毁 / 播放器已释放：忽略。
        }
    }

    /// <summary>
    /// 停止播放（不是暂停）。
    /// 「停止」在业务上的含义是：作废这一场播放会话 —— 立即静音、清空字幕、时钟映射全部重置、
    /// 界面回到可重新播放的状态。底层把播放器留在 Paused（而不是 Stop 后的 IDLE），
    /// 是为了让通知栏/锁屏卡片的播放键仍然可用；一旦它被按下，TryRestartStaleSource()
    /// 会用新的 connection id 重开一条流并重新校准时钟。
    /// </summary>
    private void StopPlayback()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            _playbackBegun = false;
            _hasPlayed = false;

            try
            {
                // 底层用 Pause 而不是 Stop：两件事必须同时成立 ——
                // 1. 立即静音、停止推进播放位置（Stop 也能做到）；
                // 2. 播放器要停在「可恢复」的状态，通知栏/锁屏卡片的播放键才有意义。
                //    Stop 会把播放器打到 IDLE，此时只收到一个 Play 请求却不会重新 prepare，
                //    表现就是「点通知栏播放没有任何反应」；Paused 是播放器唯一能直接恢复的状态。
                // 停止语义由上面/下面的状态复位（清字幕、作废旧连接、时钟重新校准）保证，不靠 Stop。
                _player.Pause();
            }
            catch
            {
                // 某些状态下 Pause 可能抛错，忽略：_playbackBegun 已复位，
                // 下次播放会重新赋一个新的 Source 覆盖掉。
            }

            // 保留下来的是「上一场的旧连接」：若用户从通知栏点播放把它拉起来，
            // 那个 connection id 已废弃、拿不到时钟校准，必须换新连接重来（见 OnMediaStateChanged）。
            _sourceStale = true;

            DeviceDisplay.Current.KeepScreenOn = false;
            _vm.NotifyPlaybackStopped();
        }
        finally
        {
            _stopping = false;
        }
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        // MediaOpened 通常先于 Playing 到达。停止后从通知栏恢复播放时，
        // 这里会先看到「旧连接重新打开」——必须在这里就接管，否则会先把
        // _playbackBegun 置真并调用 NotifyPlaybackStarted，Playing 到来时就
        // 认不出这是废弃连接，字幕会拿不到校准。
        if (TryRestartStaleSource()) return;

        if (!_playbackBegun)
        {
            _playbackBegun = true;
            _vm.NotifyPlaybackStarted();
        }

        // 媒体已就绪但没在播放：说明之前的 Play() 落在了未就绪的时刻被忽略，
        // 这里按用户意图补一次（否则会「显示停止中、实际没声音、字幕也不动」）。
        if (_vm.IsPlaying && !_stopping && _player.CurrentState is not MediaElementState.Playing)
            _player.Play();
    }

    private void OnMediaStateChanged(object? sender, EventArgs e)
    {
        switch (_player.CurrentState)
        {
            case MediaElementState.Playing:
                _hasPlayed = true;
                if (!_playbackBegun)
                {
                    if (TryRestartStaleSource()) break;
                    _playbackBegun = true;
                    _vm.NotifyPlaybackStarted();
                }
                else
                {
                    _vm.ResumePlayback();
                }
                break;

            case MediaElementState.Paused:
                // 用户界面上已没有「暂停」，这里只可能是外部发起的暂停：
                // 通知栏/锁屏媒体卡片的暂停键、耳机线控，或音频焦点被其他应用抢走。
                // 先停住本地计时，再判断是否把这次暂停「升级」为停止（见 ConfirmExternalPause）。
                _vm.NotifyPlaybackPaused();
                if (_hasPlayed && !_stopping && _vm.IsPlaying)
                    ConfirmExternalPause();
                break;

            // Stopped 不在这里处理：切换 Source、Stop() 都会触发它，
            // 若在此处做停止清理，会打断「停止后重新播放」的流程。
            // 真正的停止逻辑统一由 StopPlayback() 负责。
        }
    }

    /// <summary>
    /// 「停止」保留了媒体项（否则通知栏的播放键没有东西可播），所以外部按播放时，
    /// 播放器会真的把那条**已废弃的旧连接**拉起来。这里在第一次看到它打开/开始播放时接管，
    /// 把播放意图交回正常开播流程（新 connection id + 重新校准时钟）。
    ///
    /// 为什么必须换源：旧 connection id 在停止时就作废了，`/api/live-clock` 不会再为它
    /// 返回 `programmeAt`，校准失败后字幕只能退回墙钟兜底，整体会偏快十几秒。
    /// </summary>
    /// <returns>true 表示已接管，调用方不要再按「正常开始播放」处理。</returns>
    private bool TryRestartStaleSource()
    {
        if (!_sourceStale || _stopping) return false;
        _sourceStale = false;

        // 不能在播放器状态回调里同步改 Source（会打断播放器内部的状态机），推到下一轮消息循环。
        // IsPlaying=true 会触发 HandlePlayState(true)：换新源 + Play + 兜底补播。
        Dispatcher.Dispatch(() =>
        {
            try { _vm.IsPlaying = true; } catch { /* 页面已销毁 */ }
        });

        return true;
    }

    /// <summary>
    /// 把「外部发起的暂停」（通知栏 / 锁屏媒体卡片 / 耳机线控）升级为与 App 内「停止」完全等价的动作。
    ///
    /// 为什么需要这个：直播流不提供暂停/续播的语义，MediaElement 进入 Paused 后会保留连接、
    /// Position 停在原处，字幕时间基准随之冻住；再恢复播放时 Position 语义也不可靠
    /// （之前「暂停后字幕不动」的根因）。所以用户从通知栏暂停时，直接按停止处理：
    /// 断流、清空字幕、按钮回到「播放」，下次开播是新连接并重新校准。
    ///
    /// 两个判据避免误伤：
    /// 1. `_hasPlayed`：本次会话必须真正到达过 Playing。快速「停止→播放」时 Play() 可能被吞，
    ///    播放器会短暂停在 Paused 却从未播过 —— 那种情况必须留给 EnsurePlayingAsync 补播，
    ///    在这里停止会让补救逻辑永远失效。
    /// 2. 确认窗口：音频焦点被导航播报等短暂抢占时也会 Paused，若窗口内自行恢复 Playing 就不停止。
    /// </summary>
    private void ConfirmExternalPause()
    {
        int attempt = _attemptId;

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(ExternalPauseConfirmMs), () =>
        {
            if (attempt != _attemptId) return;                    // 期间用户又点了播放/停止
            if (_stopping || !_vm.IsPlaying) return;               // 已经被别的路径停掉了
            if (_player.CurrentState != MediaElementState.Paused) return;  // 已自行恢复播放

            // 与点 App 内「停止」走完全相同的路径：IsPlaying=false → HandlePlayState(false) → StopPlayback()。
            _vm.IsPlaying = false;
        });
    }

    private void OnMediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _playbackBegun = false;
        _hasPlayed = false;
        _sourceStale = true;   // 失败的源不可复用，外部再拉起时换新连接
        DeviceDisplay.Current.KeepScreenOn = false;
        _vm.NotifyPlaybackFailed(e.ErrorMessage ?? "未知错误");
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        // 直播流通常不会自然结束；若发生，按停止处理，界面回到可重新播放的状态。
        StopPlayback();
    }

    private void OnMediaPositionChanged(object? sender, MediaPositionChangedEventArgs e)
    {
        _vm.OnPlaybackPositionChanged(e.Position);
    }

    /// <summary>
    /// 让当前词保持可见。
    /// 策略（针对车机/低端机的流畅度）：
    /// 1. 只在当前词**不在可视区域**时才滚动 —— 大多数句子一屏放得下，完全不滚动；
    /// 2. 滚动不使用动画（瞬时对齐），避免 200~300ms 的动画与下一个词的高亮相互排队造成卡顿；
    /// 3. 串行化，避免并发滚动互相打断。
    /// </summary>
    private async Task ScrollToActiveWordAsync()
    {
        int idx = _vm.ActiveWordIndex;
        if (idx < 0 || idx >= WordsHost.Children.Count)
            return;

        if (WordsHost.Children[idx] is not View target)
            return;

        // 换句后第一次定位：先确保已回到顶部，再判断是否需要滚到目标词。
        if (_turnResetPending)
        {
            _turnResetPending = false;
            await ResetScrollToTopAsync();
        }

        double viewHeight = SubtitleScroll.Height;
        double wordHeight = target.Height;
        if (viewHeight <= 0 || wordHeight <= 0)
            return;

        double top = target.Y;
        double bottom = top + wordHeight;
        double scrollY = SubtitleScroll.ScrollY;

        // 完全可见就什么都不做（最常见路径，零开销）。
        bool visible = top >= scrollY && bottom <= scrollY + viewHeight;
        if (visible)
            return;

        if (_scrolling)
        {
            _pendingScroll = true;
            return;
        }

        _scrolling = true;
        try
        {
            // 把当前词放到视口靠上位置（约 28%），后续几个词都能顺带看到。
            double targetY = Math.Max(0, top - viewHeight * 0.28);
            await SubtitleScroll.ScrollToAsync(0, targetY, false);
        }
        catch
        {
            // 内容正在重建时可能失败，忽略。
        }
        finally
        {
            _scrolling = false;
            if (_pendingScroll)
            {
                _pendingScroll = false;
                _ = ScrollToActiveWordAsync();
            }
        }
    }
}
