using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Views;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Shapes;  // RoundRectangle（圆形按钮尺寸统一设置）
using Microsoft.Maui.Devices;
using Microsoft.Maui.Dispatching;   // IDispatcher.DispatchDelayed
using Microsoft.Maui.Essentials;    // Preferences（字号档位持久化）
using SlowEnglishFmApp.ViewModels;

namespace SlowEnglishFmApp;

public partial class MainPage : ContentPage
{
    private readonly RadioViewModel _vm;
    private readonly MediaElement _player;
    private bool _playbackBegun;

    // 字幕滚动控制
    private bool _turnResetPending;        // 刚换句，需要先把滚动位置归零
    private double _lastScrolledLineY = double.NaN;  // 上次滚动所针对的「行」（行顶 Y），行内换词不重复滚
    private double _pendingScrollY = double.NaN;     // 平滑滚动进行中收到的新目标，动画结束后补一次
    private bool _scrollAnimating;                   // 平滑滚动动画进行中
    private int _scrollAnimId;                       // 动画序号：换句/停止时自增，作废在途的补滚
    private const int ScrollAnimSettleMs = 260;      // 原生平滑滚动的时长估算，用于挡掉动画期间的重复滚动
    private bool _stopping;           // 正在停止，防止 Stop 事件重入
    private int _attemptId;           // 播放请求序号：每次播放/停止 +1，用于作废过期的兜底检查
    private bool _hasPlayed;          // 本次会话是否真正到达过 Playing（区分「外部暂停」与「Play() 被吞」）
    private bool _sourceStale;        // 播放器里留着的是「停止后遗留的旧连接」，被外部拉起来时要换新源
    private const int ExternalPauseConfirmMs = 600;  // 外部暂停确认窗口：期间恢复 Playing 则不算停止

    // 布局模式：null = 尚未确定；true = 横屏紧凑，false = 竖屏常规
    private bool? _compact;

    // ---------------------------------------------------------------------------
    // 字幕字号档位（右上角「小/中/大」按钮循环切换）
    //
    // 档位按「车机横屏下的可见行数」标定：车机视口高度下默认 40 号约显示 7 行，
    // 驾驶位距离较远时偏小。行高 ≈ FontSize × LineHeight(1.15) + 纵向 Padding/Margin(约 8dp)，
    // 由此推得：64 号约 4~5 行，92 号约 3 行 —— 最大档正是车机远距离观看的目标。
    // 档位写入 Preferences，重启后保留（车机上每次启动都重按一遍会很烦）。
    // ---------------------------------------------------------------------------
    private static readonly (string Label, double Size)[] KaraokeFontLevels =
    {
        ("小", 40),
        ("中", 64),
        ("大", 92),
    };
    private const string FontSizePrefKey = "karaoke_font_size";
    private int _fontLevelIndex;


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

        // 字幕内容的实际高度变化（换句、字体/视口变化）时重算底部留白，
        // 保证最后一行也能滚到视口正中。
        // 推到下一轮消息循环再改 HeightRequest：避免在布局周期内改需求引起重入布局。
        WordsHost.SizeChanged += (_, _) => Dispatcher.Dispatch(UpdateBottomSpacer);
        SubtitleScroll.SizeChanged += (_, _) => Dispatcher.Dispatch(UpdateBottomSpacer);

        SizeChanged += (_, _) => ApplyLayoutMode();

        // 恢复上次选择的字号档位（车机上默认 40 偏小，切到「中/大」后应一直生效）。
        // 此时字幕尚未创建，ApplyKaraokeFontSize 只会改共享样式，无副作用。
        double savedSize = Preferences.Get(FontSizePrefKey, KaraokeFontLevels[0].Size);
        _fontLevelIndex = Array.FindIndex(KaraokeFontLevels, l => Math.Abs(l.Size - savedSize) < 0.5);
        if (_fontLevelIndex < 0) _fontLevelIndex = 0;
        ApplyKaraokeFontSize(KaraokeFontLevels[_fontLevelIndex].Size);
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
            TitlePanel.Margin = new Thickness(16, 0, 16, 0);
            ProgrammeLabel.FontSize = 20;
            ProgrammeLabel.MaxLines = 1;
            ProgrammeLabel.LineBreakMode = LineBreakMode.TailTruncation;
            NextLabel.FontSize = 12;

            // ---- 横屏头部布局：按钮组靠左、标题居中、台标与在线人数移右 ----
            // 按钮组放最左：车机屏幕宽、驾驶位手边就是左缘，按钮离手近、也不遮挡标题。
            // 两个按钮统一 56dp（原来字号 48 / 播放 64 一大一小，视觉上像主次两级）。
            Grid.SetColumn(ButtonsPanel, 0);
            Grid.SetRow(ButtonsPanel, 0);
            Grid.SetRowSpan(ButtonsPanel, 2);
            ButtonsPanel.HorizontalOptions = LayoutOptions.Start;
            ButtonsPanel.Spacing = 10;
            SetCircleSize(FontSizeButton, 56);
            SetCircleSize(CompactPlayButton, 56);
            FontSizeLabel.FontSize = 20;

            // 台标/在线人数挪到最右，列已收缩为 Auto，不会挤压标题。
            Grid.SetColumn(BadgePanel, 2);
            Grid.SetRow(BadgePanel, 0);
            Grid.SetRowSpan(BadgePanel, 2);
            BadgePanel.HorizontalOptions = LayoutOptions.End;
            BadgePanel.VerticalOptions = LayoutOptions.Center;

            // 标题占中间整列（RowSpan 2 ⇒ 与按钮组垂直居中对齐），文字居中。
            Grid.SetColumn(TitlePanel, 1);
            Grid.SetRow(TitlePanel, 0);
            Grid.SetRowSpan(TitlePanel, 2);
            TitlePanel.VerticalOptions = LayoutOptions.Center;
            ProgrammeLabel.HorizontalTextAlignment = TextAlignment.Center;
            NextLabel.HorizontalTextAlignment = TextAlignment.Center;

            SubtitleBorder.Margin = new Thickness(12, 2);
            SubtitleBorder.Padding = new Thickness(10, 6);

            BottomPanel.Padding = new Thickness(16, 0, 16, 6);
            StatusLabel.FontSize = 12;

            // 横屏：控制按钮移到顶部（圆形），底部大按钮隐藏。
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

            // ---- 竖屏头部布局：台标(0,0) + 标题(0,1)，按钮组靠右 ----
            Grid.SetColumn(ButtonsPanel, 2);
            Grid.SetRow(ButtonsPanel, 0);
            Grid.SetRowSpan(ButtonsPanel, 2);
            ButtonsPanel.HorizontalOptions = LayoutOptions.End;
            ButtonsPanel.Spacing = 8;
            SetCircleSize(FontSizeButton, 48);
            SetCircleSize(CompactPlayButton, 64);   // 竖屏隐藏，尺寸仅作占位
            FontSizeLabel.FontSize = 18;

            Grid.SetColumn(BadgePanel, 0);
            Grid.SetRow(BadgePanel, 0);
            Grid.SetRowSpan(BadgePanel, 1);
            BadgePanel.HorizontalOptions = LayoutOptions.Fill;
            BadgePanel.VerticalOptions = LayoutOptions.Center;

            Grid.SetColumn(TitlePanel, 0);
            Grid.SetRow(TitlePanel, 1);
            Grid.SetRowSpan(TitlePanel, 1);
            TitlePanel.VerticalOptions = LayoutOptions.Fill;
            ProgrammeLabel.HorizontalTextAlignment = TextAlignment.Start;
            NextLabel.HorizontalTextAlignment = TextAlignment.Start;

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
        _lastScrolledLineY = double.NaN;
        _pendingScrollY = double.NaN;
        _scrollAnimId++;
        _scrollAnimating = false;
        UpdateBottomSpacer();
        _ = ResetScrollToTopAsync();
    }

    /// <summary>
    /// 圆形按钮的「尺寸 + 圆角」一起改，保证任意模式下两个按钮大小完全一致
    /// （只改 Width/Height 而不改圆角会变成圆角方块）。
    /// </summary>
    private static void SetCircleSize(Border border, double size)
    {
        border.WidthRequest = size;
        border.HeightRequest = size;
        if (border.StrokeShape is RoundRectangle round)
            round.CornerRadius = new CornerRadius(size / 2);
    }

    /// <summary>
    /// 点击右上角「小/中/大」按钮：循环切换字幕字号档位并持久化。
    /// </summary>
    private void OnFontSizeToggled(object? sender, TappedEventArgs e)
    {
        _fontLevelIndex = (_fontLevelIndex + 1) % KaraokeFontLevels.Length;
        var level = KaraokeFontLevels[_fontLevelIndex];
        Preferences.Set(FontSizePrefKey, level.Size);
        ApplyKaraokeFontSize(level.Size);
    }

    /// <summary>
    /// 把字幕字号应用到整句（共享样式 + 已存在的词 Label），并重新定位滚动。
    ///
    /// 字号变化会让每个词重新测量、整句重新换行 —— 与横竖屏切换是同类「视口级」变化：
    /// 1. 改共享样式 KaraokeLabel 的 FontSize：BindableLayout 后续新建的词 Label
    ///    在应用样式时读取的就是新值（注意：MAUI 不会把样式变更推回已创建的元素，
    ///    所以已有的 Label 必须显式赋值）；
    /// 2. 已存在的词 Label 直接设置本地 FontSize（本地值优先于样式，二者保持一致）；
    /// 3. 作废在途的平滑滚动（旧行 Y 已全部失效），等待重新布局后把激活行重新居中。
    /// </summary>
    private void ApplyKaraokeFontSize(double size)
    {
        FontSizeLabel.Text = KaraokeFontLevels[_fontLevelIndex].Label;

        if (Resources.TryGetValue("KaraokeLabel", out object? res) && res is Style karaokeStyle)
        {
            foreach (Setter setter in karaokeStyle.Setters)
            {
                if (setter.Property == Label.FontSizeProperty)
                {
                    setter.Value = size;
                    break;
                }
            }
        }

        foreach (View child in WordsHost.Children)
        {
            if (child is Label wordLabel)
                wordLabel.FontSize = size;
        }

        // 行高与换行全部失效：作废滚动状态，强制重新测量。
        _scrollAnimId++;
        _scrollAnimating = false;
        _pendingScrollY = double.NaN;
        _lastScrolledLineY = double.NaN;   // 清掉「行已居中」缓存，允许重新滚到激活行
        WordsHost.InvalidateMeasure();
        UpdateBottomSpacer();

        // 等重新布局完成（新行高/新换行生效）后，把当前激活行重新滚到视口正中。
        // 不重置回顶部：用户只是调字号，正读到哪一句应该继续看哪一句。
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(180), () =>
        {
            UpdateBottomSpacer();
            if (_vm.IsPlaying)
                _ = ScrollToActiveWordAsync();
        });
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
        _lastScrolledLineY = double.NaN;
        _pendingScrollY = double.NaN;
        _scrollAnimId++;          // 作废还在等动画结束的补滚任务
        _scrollAnimating = false;

        // 换句重建和滚动归零若挤在同一帧，这一帧会明显变长（就是「字幕顿一下」）。
        // 把归零推到下一轮消息循环：重建完成后新内容已有正确高度，滚一次就到位。
        Dispatcher.Dispatch(() =>
        {
            UpdateBottomSpacer();
            _ = ResetScrollToTopAsync();
        });
    }

    /// <summary>
    /// 重算字幕底部的留白高度。
    ///
    /// 只有「这一句字幕本身高过一屏」时才垫留白：否则最后几行永远被压在视口底部，
    /// 无论怎么滚都无法居中（用户看到的会是"停在最后一行、不在中间"）。
    /// 留白取「半个视口 − 半行」，正好让最后一行能滚到正中；短句不垫，
    /// 避免把整句顶到上方、露出一片空白。
    /// </summary>
    private void UpdateBottomSpacer()
    {
        double viewHeight = SubtitleScroll.Height;
        if (viewHeight <= 0) return;

        double contentHeight = WordsHost.Height;   // 停止播放后清空字幕时为 0

        double lineHeight = 0;
        if (WordsHost.Children.Count > 0 && WordsHost.Children[0] is View first)
            lineHeight = first.Height;

        double spacer = 0;
        if (contentHeight > viewHeight + 1 && lineHeight > 0)
            spacer = Math.Max(0, viewHeight / 2 - lineHeight / 2);

        if (Math.Abs(BottomSpacer.HeightRequest - spacer) > 1)
            BottomSpacer.HeightRequest = spacer;
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
    /// 让「当前激活词所在的那一行」平滑滚到视口垂直正中。
    ///
    /// 设计要点：
    /// 1. 以「行」为单位而不是「词」：FlexLayout 里同一行各词的 Y 相同，行内换词不触发滚动；
    ///    否则每换一个词就推一次视口，画面会持续晃动。
    /// 2. 使用平滑滚动（ScrollToAsync animated=true，Android 走原生 smoothScrollTo），
    ///    而不是原来的瞬时跳转 —— 文字是「滑过去」而不是「闪一下」。
    /// 3. 目标位置做过夹紧：内容不足一屏时停在顶部；句首、句尾的行受可滚动范围限制
    ///    （句尾的居中由 BottomSpacer 保证）。
    /// 4. 动画期间到来的新目标只保留最后一个，等这一次动画结束后再补滚，
    ///    避免多次滚动互相截断导致抖动。
    /// </summary>
    private async Task ScrollToActiveWordAsync()
    {
        int idx = _vm.ActiveWordIndex;
        if (idx < 0 || idx >= WordsHost.Children.Count)
            return;

        if (WordsHost.Children[idx] is not View target)
            return;

        // 换句后第一次定位：先确保已回到顶部，再判断是否需要滚到目标行。
        if (_turnResetPending)
        {
            _turnResetPending = false;
            _lastScrolledLineY = double.NaN;
            await ResetScrollToTopAsync();
        }

        double viewHeight = SubtitleScroll.Height;
        double lineHeight = target.Height;
        if (viewHeight <= 0 || lineHeight <= 0)
            return;

        double lineTop = target.Y;

        // 同一行内换词：行位置没变，已经处于居中状态，直接返回（最常见路径，零开销）。
        if (!double.IsNaN(_lastScrolledLineY) && Math.Abs(lineTop - _lastScrolledLineY) < 1)
            return;
        _lastScrolledLineY = lineTop;

        // 行中心对齐到视口中心。底部占位（BottomSpacer）已计入内容高度，
        // 所以最后一行同样能滚到正中。
        double lineCenter = lineTop + lineHeight / 2;
        double contentHeight = Math.Max(
            SubtitleScroll.ContentSize.Height,
            WordsHost.Height + BottomSpacer.HeightRequest);
        double maxScroll = Math.Max(0, contentHeight - viewHeight);
        double targetY = Math.Clamp(lineCenter - viewHeight / 2, 0, maxScroll);

        // 已经基本居中（不足 2dp）：不必滚动。
        if (Math.Abs(targetY - SubtitleScroll.ScrollY) < 2)
            return;

        if (_scrollAnimating)
        {
            _pendingScrollY = targetY;   // 动画结束后补滚到最新目标
            return;
        }

        await AnimateScrollAsync(targetY);
    }

    /// <summary>
    /// 平滑滚动到指定偏移。
    ///
    /// ScrollToAsync(animated: true) 在 Android 上落到原生的 smoothScrollTo：调用即刻返回、
    /// 不等待动画播完，所以这里用一个短窗口（ScrollAnimSettleMs）把动画期间的重复请求合并掉，
    /// 否则一行只隔几百毫秒时会出现两次动画互相打断、看着一卡一卡。
    /// </summary>
    private async Task AnimateScrollAsync(double targetY)
    {
        int id = ++_scrollAnimId;
        _scrollAnimating = true;

        try
        {
            await SubtitleScroll.ScrollToAsync(0, targetY, true);
        }
        catch
        {
            // 内容正在重建时可能失败，忽略。
        }

        try
        {
            await Task.Delay(ScrollAnimSettleMs);
        }
        catch
        {
            // 页面已销毁：忽略。
        }

        if (id != _scrollAnimId) return;   // 期间换句 / 停止 / 视口变化：本次作废

        _scrollAnimating = false;

        double pending = _pendingScrollY;
        _pendingScrollY = double.NaN;
        if (!double.IsNaN(pending) && Math.Abs(pending - SubtitleScroll.ScrollY) > 2)
            await AnimateScrollAsync(pending);
    }
}
