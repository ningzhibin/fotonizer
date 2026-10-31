# 连接 Google 照片：一次性设置指南

Fotonizer 使用 Google Photos Library API 上传照片。Google 要求每个用户创建自己的 OAuth 客户端（客户端 ID + 密钥），所以需要花几分钟在 Google Cloud Console 里配置一次。

> 你只需要做一次。完成后把客户端 ID 和密钥填进 Fotonizer，点“连接 Google 照片”即可。
> 所有配置都在你自己的 Google 账号下，Fotonizer 的作者看不到你的凭据。

## 0. 准备

- 一个 Google 账号；
- 能访问 [Google Cloud Console](https://console.cloud.google.com/) 的浏览器；
- 不需要信用卡，Photos Library API 的上传本身不收费（占用 Google 账号存储配额）。

## 1. 创建 Google Cloud 项目

1. 打开 <https://console.cloud.google.com/projectcreate>。
2. 项目名称填 `Fotonizer`（名字随意），位置默认即可。
3. 点击“创建”，等待几秒后选择该项目。

## 2. 启用 Photos Library API

1. 打开 <https://console.cloud.google.com/apis/library/photoslibrary.googleapis.com>。
2. 确认右上角选中的是刚创建的项目。
3. 点击“启用”。如果显示“管理”，说明已经启用。

## 3. 配置 OAuth 同意屏幕

1. 打开 <https://console.cloud.google.com/apis/credentials/consent>。
2. User Type 选择 **外部（External）**，点击“创建”。
3. 填写：
   - 应用名称：`Fotonizer`（或任意名称）
   - 用户支持邮箱：你的邮箱
   - 开发者联系邮箱：你的邮箱
4. 在“数据访问 / Scopes”步骤，点击“添加或移除范围”，搜索并添加：
   - `https://www.googleapis.com/auth/photoslibrary.appendonly`
5. 在“测试用户”步骤，点击“添加用户”，填入**你自己的 Google 账号邮箱**（必须填，否则授权会被拒绝）。
6. 保存。

> **关于“测试”状态**：测试状态下的 refresh token 通常 7 天后失效。个人长期使用时，可以在同意屏幕页面点击“发布应用（Publish app）”。由于只申请 appendonly 权限，Google 会显示“未验证应用”警告；个人使用可以点“高级 → 继续前往 Fotonizer（不安全）”继续授权。发布后 refresh token 不会每周过期。
>
> 如果不想发布，也可以每周重新点一次“连接 Google 照片”，程序会提示授权过期。

## 4. 创建 OAuth 客户端 ID

1. 打开 <https://console.cloud.google.com/apis/credentials>。
2. 点击“创建凭据” → **OAuth 客户端 ID**。
3. 应用类型选择 **桌面应用（Desktop app）**，名称填 `Fotonizer Desktop`。
4. 点击“创建”，弹窗中会显示：
   - **客户端 ID**（形如 `1234567890-abc...apps.googleusercontent.com`）
   - **客户端密钥**（形如 `GOCSPX-...`）
5. 复制这两个值。客户端密钥之后也可以在该客户端的详情页重新查看。

> 必须选“桌面应用”。选“Web 应用”会因为没有注册回调地址而报 `redirect_uri_mismatch`。
>
> Fotonizer 使用本机回环地址接收授权码（`http://127.0.0.1:随机端口/`），桌面应用类型天然支持，不需要手动填回调地址。

## 5. 在 Fotonizer 中连接

1. 打开 Fotonizer → “Google 照片”。
2. 勾选“启用自动上传”。
3. 粘贴客户端 ID 和客户端密钥。
4. 点击“连接 Google 照片”。
5. 浏览器会打开 Google 授权页：
   - 选择你的账号；
   - 如果出现“未验证应用”，点“高级” → “继续前往 Fotonizer”；
   - 勾选/确认照片上传权限，点击“继续/允许”。
6. 浏览器显示“授权成功”后关闭标签页，回到 Fotonizer。状态应显示“已连接 Google 照片”。

之后导入的新照片会自动上传。可以在“总览”看到“等待上传 / 已上传”的数量。

## 6. 验证上传

- 导入一张测试照片（“立即导入文件夹”选择 `sample-data/source`，或插一张卡）。
- 打开 <https://photos.google.com/>，在“照片”时间线或对应月份相册里查看。
- 如果开启了相册模板（默认 `{yyyy}-{MM}`），Fotonizer 会创建类似 `2024-05` 的相册。

## 权限说明

Fotonizer 申请的 scope 只有：

```
https://www.googleapis.com/auth/photoslibrary.appendonly
```

这意味着：

- ✅ 上传照片和视频；
- ✅ 创建相册、把**本程序上传的**内容加入相册；
- ✅ 查看/管理本程序上传的内容；
- ❌ 读取你已有的 Google 照片；
- ❌ 删除你已有的照片或相册；
- ❌ 访问 Google 云端硬盘、Gmail、联系人等任何其他数据。

刷新令牌保存在 `%APPDATA%\Fotonizer\secrets.bin`，使用 Windows DPAPI（当前用户）加密。把文件复制到另一台电脑或另一个 Windows 用户下无法解密。

## 7. 撤销授权

- 在 Fotonizer 里点“断开连接”（只删除本地令牌）；
- 或访问 <https://myaccount.google.com/permissions>，找到 Fotonizer，点击“删除访问权限”。

## 常见错误

| 现象 | 原因 / 解决 |
| --- | --- |
| `Access blocked: Fotonizer has not completed the Google verification process` | 账号不在“测试用户”里。回到 OAuth 同意屏幕添加你的邮箱；或把应用发布。 |
| `invalid_client` / `The OAuth client was not found` | 客户端 ID 或密钥填错、项目不对，或客户端已被删除。重新复制。 |
| `redirect_uri_mismatch` | 客户端类型建成了“Web 应用”。删掉，重建为“桌面应用”。 |
| `invalid_grant` | refresh token 已失效（测试模式 7 天、改密码、手动撤销）。重新点“连接 Google 照片”。 |
| `403 PERMISSION_DENIED` / `Photos Library API has not been used` | 第 2 步没有启用 API，或项目选错。 |
| `429 Too Many Requests` | 触发配额限制。Fotonizer 会自动退避重试，稍等即可。 |
| 浏览器授权后一直不返回 | 本机防火墙阻止了回环端口，或浏览器没有打开。关闭 VPN/代理后重试；也可以查看日志中的“无法监听本地回调端口”。 |
| 上传成功但照片不在相册里 | 相册模板为空（只上传到时间线），或相册是本次运行新建的。到 photos.google.com 的“相册”里查看。 |

## API 限制（2025 年起）

自 2025 年 3 月 31 日起，Google Photos Library API 只允许应用访问**自己上传的**媒体项。因此：

- Fotonizer 不能列出你已有的 Google 照片；
- 不能判断某张照片是否已经在 Google 照片里；
- 不能做云端去重或双向同步。

Fotonizer 的本地数据库会记录每张照片的上传状态，避免重复上传。如果你在 Google 照片网页端删除了一张照片，Fotonizer 不会知道，也不会重新上传。

## 官方文档

- Photos Library API 概览：<https://developers.google.com/photos/library/guides/overview>
- 上传媒体项：<https://developers.google.com/photos/library/guides/upload-media>
- OAuth 2.0 for desktop apps：<https://developers.google.com/identity/protocols/oauth2/native-app>
