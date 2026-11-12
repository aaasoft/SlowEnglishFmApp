# 发布签名密钥（公开，仅供本项目自用分发）

`slowenglishfm.keystore` 是 `.github/workflows/android-release.yml` 在**未配置签名 secrets** 时
使用的固定签名密钥。它随仓库公开 —— 这是有意为之，不是泄漏事故。

## 参数

| 项 | 值 |
| --- | --- |
| 文件 | `.github/keystore/slowenglishfm.keystore` |
| 格式 | PKCS12 |
| 别名 alias | `slowenglishfm` |
| storepass | `slowenglishfm` |
| keypass | `slowenglishfm`（PKCS12 下该值被忽略，与 storepass 相同） |
| 算法 | RSA 2048 / SHA384withRSA |
| 有效期 | 10950 天（30 年） |
| 证书主体 | `CN=Slow English FM, OU=Release, O=SlowEnglishFM, C=CN` |
| SHA-256 | `85:22:D3:A5:2E:37:5D:51:27:22:9C:99:96:A7:F8:F9:12:BC:86:20:51:6A:E8:CD:43:94:44:F8:76:4C:6C:C6` |

重新生成（例如需要换密钥时，注意旧版本将无法覆盖升级）：

```bash
keytool -genkeypair -v -keystore slowenglishfm.keystore -storetype PKCS12 \
  -alias slowenglishfm -keyalg RSA -keysize 2048 -validity 10950 \
  -storepass slowenglishfm -keypass slowenglishfm \
  -dname "CN=Slow English FM, OU=Release, O=SlowEnglishFM, C=CN"
```

## 为什么放仓库里

Android 只允许「相同包名 + 相同签名」的 APK 覆盖安装。CI 每次都新生成密钥的话，
每个版本的签名都不同，车机上只能先卸载再装。固定一份密钥后，升级可以平滑覆盖。

## 风险与替代

私钥公开意味着：任何人拿到这个文件，都能签出与官方包同包名、同签名的 APK，
Android 会把它当成同一个应用允许覆盖安装。因此这个密钥**只适合内部/自用分发**，
不适合上架应用商店，也不适合作为安全边界。

需要更强的保证时，改用仓库 secrets（workflow 会自动优先使用）：

- `ANDROID_KEYSTORE_BASE64`：`base64 -w0 your.keystore` 的输出
- `ANDROID_KEYSTORE_PASSWORD`
- `ANDROID_KEYSTORE_ALIAS`
- `ANDROID_KEY_PASSWORD`

配置后本目录的文件不再被使用。**注意**：从本密钥切换到新密钥时，签名发生变化，
已安装的旧版本必须先卸载才能安装新版本（一次性代价）。
