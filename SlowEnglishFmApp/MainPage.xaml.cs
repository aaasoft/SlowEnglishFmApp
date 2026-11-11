using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui;
using Microsoft.Maui.Devices;
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
    /// （现象：横屏看不到字幕）。这里在横屏下把上下两块收紧（缩小内边距、字号、按钮高度），
    /// 把省下来的高度全部让给字幕。
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
            TitlePanel.Margin = new Thickness(0, 6, 0, 0);
            ProgrammeLabel.FontSize = 20;
            ProgrammeLabel.MaxLines = 1;
            ProgrammeLabel.LineBreakMode = LineBreakMode.TailTruncation;
            NextLabel.FontSize = 12;

            SubtitleBorder.Margin = new Thickness(12, 2);
            SubtitleBorder.Padding = new Thickness(10, 6);

            BottomPanel.Padding = new Thickness(16, 2, 16, 10);
            StatusLabel.FontSize = 12;
            PlayButton.FontSize = 20;
            PlayButton.HeightRequest = 56;
            PlayButton.CornerRadius = 28;
            PlayButton.Margin = new Thickness(0, 2);
        }
        else
        {
            TopPanel.Padding = new Thickness(24, 18, 24, 8);
            OnAirBadge.Padding = new Thickness(10, 4);
            OnAirLabel.FontSize = 16;
            ListenersLabel.FontSize = 16;
            TitlePanel.Margin = new Thickness(0, 12, 0, 0);
            ProgrammeLabel.FontSize = 30;
            ProgrammeLabel.MaxLines = 2;
            ProgrammeLabel.LineBreakMode = LineBreakMode.WordWrap;
            NextLabel.FontSize = 16;

            SubtitleBorder.Margin = new Thickness(16, 4);
            SubtitleBorder.Padding = new Thickness(20);

            BottomPanel.Padding = new Thickness(24, 10, 24, 24);
            StatusLabel.FontSize = 16;
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
        else if (e.PropertyName == nameof(RadioViewModel.CurrentTurnWords))
            HandleTurnRebuilt();
        else if (e.PropertyName == nameof(RadioViewModel.ActiveWordIndex))
            _ = ScrollToActiveWordAsync();
    }

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
        _ = ResetScrollToTopAsync();
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

        if (playing)
        {
            if (!_playbackBegun)
            {
                // 每次开始播放都换一个新的连接 id：停止时旧连接已废弃，
                // 复用同一个 id 会让 /api/live-clock 校准到上一个连接的时刻。
                _player.Source = MediaSource.FromUri(new Uri(_vm.BeginNewConnection()));
            }
            _player.Play();
        }
        else
        {
            StopPlayback();
        }
    }

    /// <summary>
    /// 停止播放并断开直播流（不是暂停）。
    /// 暂停会让 MediaElement 保留连接，恢复时 Position 语义不确定，字幕容易停住；
    /// 停止后把 Source 置空，下次播放重新建立连接并重新校准时钟。
    /// </summary>
    private void StopPlayback()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            _playbackBegun = false;

            try
            {
                _player.Stop();
                _player.Source = null;
            }
            catch
            {
                // 某些状态下 Stop/清空 Source 可能抛错，忽略：_playbackBegun 已复位，
                // 下次播放会重新赋一个新的 Source 覆盖掉。
            }

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
        if (!_playbackBegun)
        {
            _playbackBegun = true;
            _vm.NotifyPlaybackStarted();
        }
    }

    private void OnMediaStateChanged(object? sender, EventArgs e)
    {
        switch (_player.CurrentState)
        {
            case MediaElementState.Playing:
                if (!_playbackBegun)
                {
                    _playbackBegun = true;
                    _vm.NotifyPlaybackStarted();
                }
                else
                {
                    _vm.ResumePlayback();
                }
                break;

            case MediaElementState.Paused:
                // 用户界面上已没有「暂停」，这只可能是系统/音频焦点导致的暂停：
                // 停住计时即可，不要改变用户的播放意图。
                _vm.NotifyPlaybackPaused();
                break;

            // Stopped 不在这里处理：切换 Source、Stop() 都会触发它，
            // 若在此处做停止清理，会打断「停止后重新播放」的流程。
            // 真正的停止逻辑统一由 StopPlayback() 负责。
        }
    }

    private void OnMediaFailed(object? sender, MediaFailedEventArgs e)
    {
        _playbackBegun = false;
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
