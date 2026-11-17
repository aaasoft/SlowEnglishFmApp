using Android.App;
using Android.Content.PM;
using Android.OS;

namespace SlowEnglishFmApp;

// Theme=AppTheme（继承 Maui.SplashTheme）：把系统状态栏/导航栏从模板默认的紫色
    // 换成与页面底色 #0E1116 一致的深色，见 Resources/values/styles.xml。
    [Activity(Theme = "@style/AppTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
