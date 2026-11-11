# Slow English FM — 实时广播播放器（.NET 10 + MAUI）

一个面向 **Android 手机与车机（Android / Android Automotive）** 的慢速英语实时广播播放器。
功能：播放 SlowEnglish.FM 的实时广播，并在屏幕上以「卡拉OK」方式逐词高亮显示实时字幕（听读同步）。

## 技术栈

- .NET 10 / .NET MAUI（`net10.0-android`）
- [CommunityToolkit.Maui.MediaElement](https://github.com/CommunityToolkit/Maui)（`MediaElement` 自 .NET 9 起从主包拆分，需单独引用）负责音频播放；已启用 Android 前台服务以支持后台续播
- 纯 `HttpClient` + `System.Text.Json` 消费网站公开 API

## 已验证的 API 契约（逆向自 slowenglish.fm 前端）

> 以下端点均为网站公开使用，未做任何鉴权。

### 1. 实时音频流（MP3，连续直播）
```
https://slowenglish.fm/live.mp3?connection=<随机连接ID>
```
直接用 `MediaElement` 播放即可。连接 ID 仅用于网站侧统计，随意生成即可。

### 2. 实时状态 — `GET https://slowenglish.fm/api/live`
带 `Cache-Control: no-store`。返回（节选）：
- `available`：电台是否正在播出
- `listeners`：当前在线人数
- `current`：当前正在播出的条目
  - `id` / `type`（如 `episode`）/ `kind`（如 `show`/`flash`）/ `title`
  - `episodeId` / `runId`：用于定位字幕
  - `startsAt`、`endsAt`：服务器端时间（ISO-8601，UTC）
  - `audioOffsetSeconds`
- `next`：下一个节目
- `catalogRevision`、`history` 等

**同步策略（关键）**：直播流存在**服务端缓冲延迟（约 10~15 秒）**，若直接用本地墙钟 `(now - startsAt)` 推算，字幕会比实际听到的音频**快十几秒**。本播放器与网站（`app.js` 的 `livePlaybackNow()`）保持完全一致的机制：

1. 连接直播流时带上连接 ID：`live.mp3?connection=<id>`；
2. 接流后请求 `GET /api/live-clock?connection=<id>`，得到 `programmeAt`（**这段音频在服务器端的真实节目时刻**，已含缓冲延迟），作为「播放进度 0 秒 ↔ 该节目时刻」的基准；
3. **只在接流时校准一次**，之后节目时刻 = `programmeAt + (MediaElement.Position − 校准时的 Position)`，即完全由**真实音频播放进度**推进；
4. 节目内已播秒数 = `当前节目时刻 − 节目 startsAt + audioOffsetSeconds − 手动微调`；
5. 当前节目（标题/字幕）由同一「当前节目时刻」在节目时间轴上定位 —— 所以**标题和字幕永远与听到的音频处在同一时刻**。

> 两个容易踩的坑，都已规避：
> - **不能用墙钟推进**：音频一旦缓冲/卡顿，墙钟继续走而音频不走，字幕与标题会越来越超前。
> - **不能周期性重新校准**：每次 `programmeAt` 都对应服务器推流的「队头」，比正在播放的音频超前一个缓冲区长度；反复用它校准会把这份超前不断叠加，造成「一开始同步、过一会儿就越跑越快」。

界面上的「字幕同步」±0.5s 仅用于补偿残余误差。若时钟一直未就绪（`Connection not ready`），会临时回退到墙钟（略超前），时钟生效后自动纠正。

### 2.1 服务器时钟 — `GET https://slowenglish.fm/api/live-clock?connection=<id>`
带 `Cache-Control: no-store`。返回 `{ "programmeAt": <Unix 毫秒> }`，即该连接当前播放音频对应的**真实节目时刻**。
- 必须使用**正在拉流的同一个 connection ID**；否则返回 `{"error":"Connection not ready"}`。
- 这是字幕与音频对齐的基准，已包含直播流的服务端缓冲延迟。

### 3. 逐词字幕计时 — `GET https://slowenglish.fm/timings/runs/{runId}/{kind}/word-timings.json`
返回：
```json
{ "words": [ { "turn": 0, "index": 0, "text": "Welcome", "start": 0.28, "end": 0.88 } ], "quality": {...} }
```
- 路径规则来自 `/api/state` 中 episode 的 `wordTimings` 字段（或由 `audio` 把文件名换成 `word-timings.json`），即 `runs/{runId}/{kind}/word-timings.json`，前缀为 `https://slowenglish.fm/timings/`。
- 旧的 `https://media.slowenglish.fm/radio/{runId}/{kind}/timings.json` **只对部分节目存在**（`flash`/`brief`/`day_recap`/`show` 等多类会 404），已弃用。
- `words[]` 即卡拉OK字幕源：`turn`=句子序号，`start`/`end`=相对节目开始的发音起止秒数。
  `index` 不可用于定位——它多为**全文件累计序号**，个别文件又按句重置（口径不统一），
  因此代码按「该词在本句列表中的位置」换算。
- 定位当前词：`last word where word.start <= 节目内已播秒数 + 0.08`，并高亮其所在整句。

## 构建与运行

前置条件：安装 .NET 10 与 `maui` 工作负载（`dotnet workload install maui`），以及 Android SDK（或 Android Studio）。

```bash
# 还原并构建 Android 包
dotnet build -f net10.0-android -c Debug

# 连接设备/模拟器后部署运行
dotnet build -f net10.0-android -c Debug /t:Install
```

也可在 Visual Studio / VS Code（C# + MAUI 扩展）中打开 `SlowEnglishFmApp.csproj`，选择 Android 目标设备运行。
车机（Android Automotive / 车规 Android）可直接安装生成的 APK。

> 如需在 Windows 上本地调试 UI，可在 `SlowEnglishFmApp.csproj` 的 `<TargetFrameworks>` 追加 `net10.0-windows10.0.19041.0`，并安装 `maui-windows` 工作负载。

## 项目结构

| 文件 | 说明 |
| --- | --- |
| `MauiProgram.cs` | DI 注册（`LiveRadioService` / `TimingsService` / `RadioViewModel`）+ `UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)` |
| `Models/LiveState.cs` | `/api/live` 返回模型 |
| `Models/Timing.cs` | 字幕计时模型 |
| `Services/LiveRadioService.cs` | 每 3 秒轮询 `/api/live`，跟踪当前节目并合成节目时间轴（history 近 40 条 + current + next）；提供 `/api/live-clock` 时钟查询 |
| `Services/TimingsService.cs` | 按 `runId`+`kind` 获取并缓存逐词字幕 |
| `ViewModels/RadioViewModel.cs` | 播放状态 + 「播放进度 → 节目时刻」映射，驱动标题与卡拉OK定位 |
| `MainPage.xaml(.cs)` | 车机/手机友好大字号深色 UI + MediaElement 协调 |
| `Converters/StringNotEmptyConverter.cs` | 文本空值→可见性 |

## 界面说明（车机 / 手机友好）

- 顶部：`ON AIR` 标识、当前节目名、在线人数、下一节目预告。
- 中部：超大字号卡拉OK字幕——当前句整句显示，正在发音的词琥珀色高亮，已读词淡显，未读词亮白。
  高亮只改颜色/背景，**不改变字号、字重、边距**，因此整句布局稳定、不会因高亮而重排。
- 字幕滚动策略：换句时把滚动位置**无动画瞬时归零**（新句直接从头显示，不会“从底部滚回顶部”）；
  句内仅当当前词移出可视区域时才滚动，且不使用动画，避免动画排队造成卡顿。
- 底部：大号播放/停止按钮、音量滑块、字幕同步微调（±0.5s）。
  按钮是**停止**而非暂停：停止即断开直播流并清空时间映射；再次点击播放会**新建连接**
  （新的 `connection` id）并重新校准时钟，字幕重新从头对齐，不会因旧的播放进度残留而停住。
- 播放时保持屏幕常亮（`DeviceDisplay.KeepScreenOn`），适合车机长时间展示。

## 已知边界 / 可扩展

- **后台播放**：已通过 `UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: true)` 启用 Android 前台服务，应用切到后台（锁屏 / 车机其他界面）时直播音频可继续播放；并已在 `AndroidManifest.xml` 声明 `FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_MEDIA_PLAYBACK`、`POST_NOTIFICATIONS`、`WAKE_LOCK` 权限。Android 最低版本要求 26（Android 8.0）。
- 进一步可加入 Android `MediaSession`（音频焦点 / 锁屏媒体控制、通知栏播放控件）。
- 字幕与标题同步基于「`/api/live-clock` 单次校准 + `MediaElement.Position` 播放进度」；若该端点暂时不可用会回退墙钟（此时略超前），恢复后自动纠正；残余误差仍可用「字幕同步」手动微调。
- 仅 `episode` 类型节目提供逐词字幕；音乐/过场会显示「♪ 标题」并无字幕。
