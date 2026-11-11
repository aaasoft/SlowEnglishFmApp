using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using SlowEnglishFmApp.Services;
using SlowEnglishFmApp.ViewModels;

namespace SlowEnglishFmApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			// 启用 Android 前台服务，使直播音频在应用切到后台（锁屏/车机其他界面）时仍能继续播放。
			.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

		// API：实时状态（slowenglish.fm）
		builder.Services.AddSingleton<ILiveRadioService>(_ =>
			new LiveRadioService(new HttpClient { BaseAddress = new Uri("https://slowenglish.fm/") }));

		// API：逐词字幕计时（media.slowenglish.fm）
		builder.Services.AddSingleton<ITimingsService>(_ =>
			new TimingsService(new HttpClient { BaseAddress = new Uri("https://media.slowenglish.fm/") }));

		builder.Services.AddSingleton<RadioViewModel>();

#if DEBUG
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
