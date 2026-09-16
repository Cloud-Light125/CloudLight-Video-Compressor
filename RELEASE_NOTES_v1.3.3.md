# CloudLight Video Compressor 1.3.3

CloudLight Video Compressor 1.3.3 修复手动码率、目标大小和固定 CRF 模式在生成压缩计划时错误启动 VMAF/质量采样的问题。

## 修复

- 指定视频码率时不再运行 VMAF、质量校准、复杂度分析或 `ffmpeg -ss` 抽样
- 指定目标大小时只进行大小与码率估算，不再运行 VMAF
- 固定 CRF/CQ/CQP 与其他手动参数模式不再触发自动质量搜索
- 仅在智能压缩明确启用高级质量校准时允许 VMAF 自动质量搜索
- VMAF 服务入口增加二次模式防护，避免错误调用启动 FFmpeg
- VMAF 决策日志增加原因、码率控制模式和智能质量开关状态
- 增加 H.265 + NVIDIA NVENC 指定码率、目标大小、固定 CRF 和智能 VMAF 模式的回归测试

## 系统要求

- Windows x64
- 正式安装包已包含 .NET 8、FFmpeg 和 ffprobe，正常情况下无需额外配置运行环境或 PATH

## 数字签名

当前安装包未进行 Authenticode 正式签名，Windows SmartScreen 可能显示未知发布者提示。
