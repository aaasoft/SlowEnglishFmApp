using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AndroidX.Media3.Common;
using CommunityToolkit.Maui.Core.Handlers;
using CommunityToolkit.Maui.Core.Views;
using CommunityToolkit.Maui.Views;

namespace SlowEnglishFmApp.Services;

/// <summary>
/// 运行中刷新「系统媒体控制区」（状态栏 / 通知栏 / 锁屏媒体卡片）显示的标题。
///
/// 背景：MediaElement.MetadataTitle 只在**赋 Source 时**被工具包读取一次（构造 MediaItem 时取值），
/// 直播流的 Source 一直不换，节目切换后系统里显示的仍是开播那一刻的节目名。
///
/// 做法：直接改 ExoPlayer 当前 MediaItem 的 MediaMetadata。
/// ExoPlayer 对「仅元数据不同、URI 相同」的替换走 canUpdateMediaItem → updateMediaItem
/// 原地换掉 MediaItem，**不重建数据源**：直播流不断开、无重新缓冲、无流量突增。
/// 元数据变化会经 MediaSession 推送给系统媒体控制区；通知栏的标题由工具包的前台服务
/// 从 player.MediaMetadata 取值（只在赋 Source 时重发一次），这里再手动补发一次刷新。
///
/// 唯一取不到的两个成员（MediaElementHandler.MediaManager、MediaManager.Player 是 protected）
/// 用反射读取并缓存 PropertyInfo；反射失败时静默降级 —— MetadataTitle 仍会在下一次
/// 赋 Source 时生效，不影响播放与字幕。
/// </summary>
public static class MediaSessionMetadata
{
    static PropertyInfo? s_mediaManagerProperty;
    static PropertyInfo? s_playerProperty;

    /// <summary>
    /// 把系统媒体控制区的标题/副标题改成当前节目信息。必须在主线程调用（ExoPlayer 线程约束）。
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(MediaElementHandler))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicProperties, typeof(MediaManager))]
    public static void Update(MediaElement mediaElement, string title, string? artist)
    {
        try
        {
            if (mediaElement.Handler is not MediaElementHandler handler)
                return;
            if (!TryResolveMembers())
                return;
            if (s_mediaManagerProperty!.GetValue(handler) is not MediaManager manager)
                return;
            if (s_playerProperty!.GetValue(manager) is not IPlayer player)
                return;

            MediaItem? current = player.CurrentMediaItem;
            if (current is null)
                return;

            // 保留原元数据里的封面等字段，只换标题/副标题。
            MediaMetadata.Builder? metaBuilder = current.MediaMetadata?.BuildUpon() ?? new MediaMetadata.Builder();
            metaBuilder.SetTitle(title);
            if (!string.IsNullOrEmpty(artist))
                metaBuilder.SetArtist(artist);
            MediaMetadata? metadata = metaBuilder.Build();
            if (metadata is null)
                return;

            MediaItem.Builder? itemBuilder = current.BuildUpon();
            if (itemBuilder is null)
                return;
            itemBuilder.SetMediaMetadata(metadata);
            MediaItem? updated = itemBuilder.Build();
            if (updated is null)
                return;
            player.ReplaceMediaItem(player.CurrentMediaItemIndex, updated);

            // Android 13+ 通知内容标题由工具包前台服务自绘且只在赋 Source 时重发；
            // 这里补发一次。Android 12 及以下的 PlayerNotificationManager 会自己跟随
            // 元数据变化重新发通知，此调用对它们是无害的重复刷新。
            manager.UpdateNotifications();
        }
        catch
        {
            // 反射成员缺失（工具包升级改名）等情况：静默降级，不打断播放。
        }
    }

    static bool TryResolveMembers()
    {
        if (s_mediaManagerProperty is not null && s_playerProperty is not null)
            return true;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        s_mediaManagerProperty = typeof(MediaElementHandler).GetProperty("MediaManager", flags);
        s_playerProperty = typeof(MediaManager).GetProperty("Player", flags);
        return s_mediaManagerProperty is not null && s_playerProperty is not null;
    }
}
