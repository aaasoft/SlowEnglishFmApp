using Android.App;
using Android.Content.PM;
using Android.OS;

namespace SlowEnglishFmApp;

// Theme=AppTheme（继承 Maui.SplashTheme）：把系统状态栏/导航栏从模板默认的紫色
    // 换成与页面底色 #0E1116 一致的深色，见 Resources/values/styles.xml。
    //
    // ResizeableActivity / SupportsPictureInPicture：分屏（车机 DiLink 等国内车机的
    // 分屏选择器、桌面卡片嵌入）只收录清单里**显式**声明可缩放的 Activity ——
    // 仅靠 targetSdk 的默认值不够，缺省一律不列出。PiP 声明是卡片嵌入类桌面
    // 同时校验的第二个标志（音频应用不会真正进画中画，只作能力声明）。
    // 配合 ConfigurationChanges 里的 ScreenSize|SmallestScreenSize|ScreenLayout，
    // 分屏拖动分隔条改窗口尺寸时 Activity 不会重建，布局随 SizeChanged 自适应。
    [Activity(Theme = "@style/AppTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ResizeableActivity = true, SupportsPictureInPicture = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
