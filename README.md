# CloudLight Video Compressor

Windows 桌面批量视频压缩工具，基于 C#、.NET 8、WPF、FFmpeg 与 ffprobe。

## 已安装版本

当前正式版本：`1.1.0`

`CloudLight-Video-Compressor-Setup-x64-<version>.exe` 是 Windows x64、self-contained 的正式安装包。它将 .NET 8 Windows Desktop Runtime、应用程序和内置 FFmpeg/ffprobe 一起安装到：

```text
C:\Program Files\CloudLight\CloudLight Video Compressor
```

普通用户无需安装 .NET Desktop Runtime、FFmpeg 或配置 `PATH`。内置工具默认位于 `ffmpeg\ffmpeg.exe` 与 `ffmpeg\ffprobe.exe`；“命名与性能”页仍可手动选择 `ffmpeg.exe`，以便高级用户显式覆盖内置工具。

应用设置保存在 `%USERPROFILE%\Documents\CloudLight\CloudLight Video Compressor\settings.json`，不在安装目录。首次升级时，旧版 `%LOCALAPPDATA%\CloudLight Video Compressor\settings.json` 会被安全复制到新位置；旧文件不会被删除。视频的临时文件和最终输出继续写在用户选择的视频输出位置，不会写入 Program Files。

## 开发运行

开发运行需要 .NET 8 SDK；项目受控的 FFmpeg 文件位于 `third_party\ffmpeg`。在项目根目录运行：

```powershell
dotnet restore CloudLight.VideoCompressor.sln
dotnet run --project src/CloudLight.VideoCompressor/CloudLight.VideoCompressor.csproj
```

## 构建正式安装包

在安装了 Inno Setup 6 的 Windows x64 构建机上运行：

```powershell
.\build-installer.ps1
```

脚本会重新生成 `icon.ico`、清理发布暂存目录、恢复/构建/测试、以 `win-x64` self-contained 方式发布、验证内置 FFmpeg/ffprobe，并调用 Inno Setup 输出 `artifacts\installer\CloudLight-Video-Compressor-Setup-x64-<version>.exe`。

## FFmpeg 归属与许可证

发布包使用项目内 `third_party\ffmpeg` 的 Gyan.dev Windows x64 static full build。当前版本、来源、SHA-256 与 GPLv3 许可证说明见 [third_party/ffmpeg/README.md](third_party/ffmpeg/README.md)；完整 GPLv3 文本随应用安装在 `ffmpeg\LICENSE.txt`。

## 安全承诺

FFmpeg 始终输出到同目录的 GUID 临时文件。只有编码成功、ffprobe 验证存在视频流、文件大小正常且时长接近源文件后，才会提交最终文件；原文件移动永远发生在该验证之后。取消、失败、目标大小超限或压缩结果不小于原文件时，原文件保持不动。

同名输出会自动添加 `(1)`、`(2)`；并发任务会提前预留最终路径，避免统一输出目录下的名称冲突。

## 测试

```powershell
dotnet test CloudLight.VideoCompressor.sln
```

真实 FFmpeg 集成测试可额外指定工具目录：

```powershell
$env:CLOUDLIGHT_FFMPEG_TEST_DIR = 'C:\path\to\ffmpeg\bin'
dotnet test CloudLight.VideoCompressor.sln --filter 'Category=Integration'
```
