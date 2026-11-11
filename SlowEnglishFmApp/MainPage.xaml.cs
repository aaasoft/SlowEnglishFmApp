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
        };
        _player.BindingContext = _vm;
        _player.SetBinding(MediaElement.VolumeProperty, nameof(RadioViewModel.Volume));
        _player.MediaOpened += OnMediaOpened;
        _player.StateChanged += OnMediaStateChanged;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        Grid.SetRow(_player, 0);
        Grid.SetRowSpan(_player, 3);
        RootGrid.Children.Add(_player);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioViewModel.IsPlaying))
            HandlePlayState(_vm.IsPlaying);
        else if (e.PropertyName == nameof(RadioViewModel.ActiveWordIndex))
            _ = ScrollToActiveWordAsync();
    }

    private void HandlePlayState(bool playing)
    {
        DeviceDisplay.Current.KeepScreenOn = playing;

        if (playing)
        {
            if (!_playbackBegun)
            {
                if (_player.Source is null)
                    _player.Source = MediaSource.FromUri(new Uri(_vm.StreamUrl));
                _player.Play();
            }
            else
            {
                _player.Play();
            }
        }
        else
        {
            _player.Pause();
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
            case MediaElementState.Stopped:
                _vm.NotifyPlaybackPaused();
                break;
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
        // 直播流通常不会自然结束；若结束则按停止处理，便于重新连接。
        _playbackBegun = false;
        _vm.NotifyPlaybackPaused();
    }

    private async Task ScrollToActiveWordAsync()
    {
        int idx = _vm.ActiveWordIndex;
        if (idx < 0 || idx >= WordsHost.Children.Count)
            return;

        if (WordsHost.Children[idx] is View target)
            await SubtitleScroll.ScrollToAsync(target, ScrollToPosition.Center, true);
    }
}
