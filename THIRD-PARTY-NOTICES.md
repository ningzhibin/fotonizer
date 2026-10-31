# 第三方组件与许可

Fotonizer 使用以下开源组件。完整许可证文本以各项目仓库为准。

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [.NET 8 / WPF](https://github.com/dotnet/wpf) | 8.0 | MIT | 运行时、桌面界面 |
| [MetadataExtractor](https://github.com/drewnoakes/metadata-extractor-dotnet) | 2.8.1 | Apache-2.0 | 读取 EXIF / 拍摄时间 |
| [MediaDevices](https://github.com/Bassman2/MediaDevices) | 1.10.2 | MIT | MTP/PTP 相机与手机导入（DLL 已随项目附带，见 `third_party/MediaDevices`） |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) / [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | 8.0.x | MIT | 本地照片索引与上传队列 |
| [SQLite](https://sqlite.org) | 3.x | Public Domain | 嵌入式数据库 |
| [System.Management](https://github.com/dotnet/runtime) | 8.0 | MIT | WMI 设备事件 |
| [xUnit](https://xunit.net) | 2.9 | Apache-2.0 | 单元测试（不随成品分发） |

## MediaDevices 说明

`third_party/MediaDevices/MediaDevices.dll` 来自 NuGet 包 `MediaDevices 1.10.2`（MIT）。
之所以直接附带 DLL，而不是使用 PackageReference，是因为该包内包含一个 `.chm` 帮助文件，
在部分 Windows Defender 配置下会导致 NuGet 解包失败；直接引用编译后的程序集可以避免这一问题。
