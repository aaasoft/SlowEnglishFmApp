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
    //
    // 这里刻意不做「动画排队 / 动画中合并请求」那套状态机：只要有一个标志位没被复位，
    // 后续所有滚动就只能排队、不再执行，表现为「字幕卡在某一行、激活词跑到屏幕外」（已复现两次）。
    // 现在每次换行都直接下发平滑滚动：Android 的 smoothScrollTo 天然支持改目标（从当前位置续滚），
    // 不会互相打断出抖动；即使某次滚动失败，下一个词也会再校正一次，不存在卡死状态。
    private bool _turnResetPending;        // 刚换句，需要先把滚动位置归零
    private double _lastScrolledLineY = double.NaN;  // 上次滚到的「行」（行顶 Y），行内换词不重复滚
    private double _lastScrollTarget = double.NaN;   // 上次下发的滚动目标，同一行同一目标不重复下发
    private int _scrollFixBudget;                    // 每个新词允许的「滚动后校正」次数（防抖动）
    private int _scrollEpoch;                        // 换句/换字号/转屏时自增，作废过期的校正回调
    private const int ScrollVerifyDelayMs = 300;     // 判定平滑滚动已停下来的时间点（原生约 250ms）
    private const int ScrollCallTimeoutMs = 600;     // 等原生滚动回执的上限，超时放弃等待（防止卡住调用方）
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

    // 词距的基准值（对应「小」档 40 号）：横向 1 + 6 ⇒ 相邻词间距 14dp ≈ 0.35em。
    // 各档位按 size / 40 等比缩放这三组值，词距的 em 比例保持不变。
    private const double KaraokeBaseFontSize = 40;
    private const double WordMarginH = 1;      // Label.Margin 左右
    private const double WordMarginV = 2;      // Label.Margin 上下
    private const double WordPaddingH = 6;     // Label.Padding 左右（同时是高亮色块的内边距）
    private const double WordPaddingV = 2;     // Label.Padding 上下

    // 当前档位实际生效的排版参数（新建的词 Label 也从这里取值）
    private double _wordFontSize = KaraokeFontLevels[0].Size;
    private Thickness _wordMargin;
    private Thickness _wordPadding;


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
        // 留白变化会改变可滚动范围：重算后必须重新定位一次激活行，否则大字号下行高变大、
        // 滚动范围又还没跟上，激活词会停在视口下缘之外（「激活单词在屏幕外」）。
        WordsHost.SizeChanged += (_, _) => Dispatcher.Dispatch(RefreshSpacerAndReposition);
        SubtitleScroll.SizeChanged += (_, _) => Dispatcher.Dispatch(RefreshSpacerAndReposition);

        // BindableLayout 换句重建时会新建词 Label。它们从共享样式取排版参数，
        // 这里再兜一层：无论样式变更有没有被新元素读到，新建的词都使用当前档位的字号与词距。
        WordsHost.ChildAdded += (_, e) =>
        {
            if (e.Element is Label wordLabel)
                ApplyWordTypography(wordLabel);
        };

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
        ResetScrollTracking();
        UpdateBottomSpacer();
        _ = ResetScrollToTopAsync();
    }

    /// <summary>
    /// 作废全部滚动记忆（换句、换字号、转屏、停止时调用）：
    /// 行缓存、目标缓存、校正预算、在途的校正回调一起清掉，让下一次高亮重新定位一次。
    /// </summary>
    private void ResetScrollTracking()
    {
        _turnResetPending = true;
        _lastScrolledLineY = double.NaN;
        _lastScrollTarget = double.NaN;
        _scrollFixBudget = 1;
        _scrollEpoch++;           // 让已经排队的校正回调自动过期
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
    /// 1. 改共享样式 KaraokeLabel 的 FontSize / Margin / Padding：BindableLayout 后续新建的
    ///    词 Label 在应用样式时读取的就是新值（注意：MAUI 不会把样式变更推回已创建的元素，
    ///    所以已有的 Label 必须显式赋值，见 ApplyWordTypography）；
    /// 2. 词距必须跟着字号一起缩放，理由见 UpdateKaraokeStyle 的注释；
    /// 3. 作废在途的平滑滚动（旧行 Y 已全部失效），等待重新布局后把激活行重新居中。
    /// </summary>
    private void ApplyKaraokeFontSize(double size)
    {
        FontSizeLabel.Text = KaraokeFontLevels[_fontLevelIndex].Label;

        // 基准：字号 40 时 Margin(1,2) + Padding(6,2) ⇒ 相邻词间距 14dp ≈ 0.35em。
        // 按同一比例缩放字号、边距，词距就始终维持 0.35em —— 视觉密度不随档位变化。
        double k = size / KaraokeBaseFontSize;
        _wordFontSize = size;
        _wordMargin = new Thickness(WordMarginH * k, WordMarginV * k);
        _wordPadding = new Thickness(WordPaddingH * k, WordPaddingV * k);

        UpdateKaraokeStyle();

        foreach (View child in WordsHost.Children)
        {
            if (child is Label wordLabel)
                ApplyWordTypography(wordLabel);
        }

        // 行高与换行全部失效：作废滚动记忆，强制重新测量。
        _lastScrolledLineY = double.NaN;   // 清掉「行已居中」缓存，允许重新滚到激活行
        _lastScrollTarget = double.NaN;
        _scrollFixBudget = 1;
        _scrollEpoch++;
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

    /// <summary>
    /// 单个词 Label 的排版参数（字号 + 词距）。本地值优先于样式，与样式中的值保持一致。
    /// </summary>
    private void ApplyWordTypography(Label label)
    {
        label.FontSize = _wordFontSize;
        label.Margin = _wordMargin;
        label.Padding = _wordPadding;
    }

    /// <summary>
    /// 同步共享样式 KaraokeLabel 的 FontSize / Margin / Padding。
    ///
    /// 词与词的视觉间隙 = 相邻两个 Label 的 (Margin 横向 + Padding 横向) 之和。
    /// 字号 40 时是 2 × (1 + 6) = 14dp ≈ 0.35em，接近英文正常词距；
    /// 但 64 / 92 档若仍用 14dp，字母本身变宽了间隙却没变（0.35em ⇒ 0.15em），
    /// 词与词就粘在一起 —— 所以边距必须与字号同比例缩放。
    /// </summary>
    private void UpdateKaraokeStyle()
    {
        if (!Resources.TryGetValue("KaraokeLabel", out object? res) || res is not Style style)
            return;

        foreach (Setter setter in style.Setters)
        {
            switch (setter.Property.PropertyName)
            {
                case nameof(Label.FontSize):
                    setter.Value = _wordFontSize;
                    break;
                case nameof(View.Margin):
                    setter.Value = _wordMargin;
                    break;
                case nameof(Label.Padding):
                    setter.Value = _wordPadding;
                    break;
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioViewModel.IsPlaying))
            HandlePlayState(_vm.IsPlaying);
        else if (e.PropertyName == nameof(RadioViewModel.ActiveWordIndex))
        {
            _scrollFixBudget = 1;   // 每个新词给一次「滚动后重新校正」的机会
            _ = ScrollToActiveWordAsync();
        }
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
        ResetScrollTracking();

        // 换句重建和滚动归零若挤在同一帧，这一帧会明显变长（就是「字幕顿一下」）。
        // 把归零推到下一轮消息循环：重建完成后新内容已有正确高度，滚一次就到位。
        Dispatcher.Dispatch(() =>
        {
            UpdateBottomSpacer();
            _ = ResetScrollToTopAsync();
        });
    }

    /// <summary>
    /// 重算底部留白；若留白真的变了（可滚动范围随之变化），再校正一次激活行位置。
    /// 大字号下这一步很关键：行高变大 ⇒ 内容更高 ⇒ 需要的滚动偏移更大，
    /// 而滚动可能发生在内容测量完成之前，那次滚动会被夹短，激活行就停在视口外。
    /// </summary>
    private void RefreshSpacerAndReposition()
    {
        UpdateBottomSpacer();
        if (_vm.IsPlaying)
            _ = ScrollToActiveWordAsync();
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
    /// 1. 以「行」为单位而不是「词」：FlexLayout 里同一行各词的 Y 相同，行内换词不必重滚，
    ///    否则每换一个词就推一次视口，画面会持续晃动。
    /// 2. 平滑滚动（ScrollToAsync animated=true → Android 原生 smoothScrollTo）：
    ///    文字是「滑过去」而不是「闪一下」。
    /// 3. 不做动画排队：每次换行都直接下发目标。原生滚动支持中途改目标（从当前位置续滚），
    ///    即使某一次没滚到位，下一个词还会重新算一次 —— 不存在「卡在某个状态后不再跟随」的死角。
    /// 4. 滚动位置不能用 ContentSize 推算后夹紧：换句 / 换字号后内容尺寸常常滞后（偏小），
    ///    按偏小值夹紧会把滚动提前截断，激活行就停在视口下缘之外（大字号下尤其明显）。
    ///    这里用「目标行自身的几何」做下界，保证算出的偏移一定能让本行完整进入视口。
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
            _lastScrollTarget = double.NaN;
            await ResetScrollToTopAsync();
        }

        double lineTop = target.Y;
        if (!TryComputeTargetY(target, out double targetY))
            return;

        double scrollY = SubtitleScroll.ScrollY;
        bool sameLine = !double.IsNaN(_lastScrolledLineY) && Math.Abs(lineTop - _lastScrolledLineY) < 1;
        _lastScrolledLineY = lineTop;

        // 已经在目标位置：不必滚动（行内换词的常见路径，零开销）。
        if (Math.Abs(targetY - scrollY) < 2)
            return;

        // 同一行、同一目标已经下发过：不重复下发（连续下发会让原生动画不断重启）。
        if (sameLine && Math.Abs(targetY - _lastScrollTarget) < 2)
            return;

        _lastScrollTarget = targetY;
        await ScrollSmoothAsync(targetY);
        ScheduleVisibilityCorrection();
    }

    /// <summary>
    /// 计算「让这一行居中」所需的滚动偏移。
    ///
    /// 可滚动范围取三个下界里的最大值：ScrollView 量到的内容高度、FlexLayout 高度 + 底部留白、
    /// 目标行底边 + 底部留白。后两项是关键 —— 换句 / 换字号后内容尺寸常常滞后（偏小），
    /// 按偏小值夹紧会把滚动提前截断，激活行正好停在视口下缘之外（大字号下最明显）。
    /// </summary>
    private bool TryComputeTargetY(View lineInRow, out double targetY)
    {
        targetY = 0;

        double viewHeight = SubtitleScroll.Height;
        double lineHeight = lineInRow.Height;
        if (viewHeight <= 0 || lineHeight <= 0) return false;

        double lineTop = lineInRow.Y;
        double contentHeight = Math.Max(
            Math.Max(SubtitleScroll.ContentSize.Height, WordsHost.Height + BottomSpacer.HeightRequest),
            lineTop + lineHeight + BottomSpacer.HeightRequest);
        double maxScroll = Math.Max(0, contentHeight - viewHeight);

        targetY = Math.Clamp(lineTop + lineHeight / 2 - viewHeight / 2, 0, maxScroll);
        return true;
    }

    /// <summary>
    /// 激活词所在的行是否完整落在视口内（滚动后校验用）。
    /// </summary>
    private bool IsActiveWordFullyVisible()
    {
        int idx = _vm.ActiveWordIndex;
        if (idx < 0 || idx >= WordsHost.Children.Count) return true;
        if (WordsHost.Children[idx] is not View word) return true;

        double scrollY = SubtitleScroll.ScrollY;
        double viewHeight = SubtitleScroll.Height;
        if (viewHeight <= 0) return true;

        return word.Y >= scrollY - 2 && word.Y + word.Height <= scrollY + viewHeight + 2;
    }

    /// <summary>
    /// 下发一次平滑滚动。
    ///
    /// ScrollToAsync(animated: true) 在 Android 上落到原生 smoothScrollTo：调用即刻返回，
    /// 而且内容重建时回调可能根本不回来。所以等待加了一个上限 ——
    /// 超时就不再等它、直接返回，绝不让一次没回执的滚动把后续滚动全部堵住。
    /// </summary>
    private async Task ScrollSmoothAsync(double targetY)
    {
        try
        {
            Task scroll = SubtitleScroll.ScrollToAsync(0, targetY, true);
            Task finished = await Task.WhenAny(scroll, Task.Delay(ScrollCallTimeoutMs));
            if (finished == scroll)
            {
                try { await scroll; } catch { /* 内容重建中滚动失败：忽略 */ }
            }
            else
            {
                // 超时放弃等待，但把可能的异常标记为已观察，避免未观察异常。
                _ = scroll.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch
        {
            // 内容正在重建时可能失败，忽略。
        }
    }

    /// <summary>
    /// 平滑滚动停下后复核一次：若激活行仍有部分在视口外（滚动之后内容才被重新测量放大、
    /// 实际可滚范围变大，这次滚动停得偏早；或原生动画压根没执行），直接用**无动画**滚动
    /// 跳到正确位置。
    ///
    /// 为什么用无动画：这是兜底路径，只在平滑滚动已经失效时才走到；瞬时跳一下
    /// （几十毫秒的视觉变化）远比「激活词留在屏幕外」可接受，而且它是确定性的，
    /// 不依赖原生动画回调是否回来。每个词只给一次额度，避免来回抖动。
    /// </summary>
    private void ScheduleVisibilityCorrection()
    {
        int epoch = _scrollEpoch;

        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(ScrollVerifyDelayMs), () =>
        {
            if (epoch != _scrollEpoch || !_vm.IsPlaying) return;   // 期间换句/换字号/转屏/停止
            if (_scrollFixBudget <= 0) return;

            int idx = _vm.ActiveWordIndex;
            if (idx < 0 || idx >= WordsHost.Children.Count) return;
            if (WordsHost.Children[idx] is not View word) return;
            if (IsActiveWordFullyVisible()) return;

            _scrollFixBudget--;
            if (!TryComputeTargetY(word, out double targetY)) return;
            if (Math.Abs(targetY - SubtitleScroll.ScrollY) < 2) return;

            _lastScrollTarget = targetY;
            _lastScrolledLineY = word.Y;

            try
            {
                _ = SubtitleScroll.ScrollToAsync(0, targetY, false);
            }
            catch
            {
                // 内容正在重建：忽略，下一次高亮还会再校正。
            }
        });
    }
}
