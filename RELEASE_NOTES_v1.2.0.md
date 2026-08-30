# CloudLight Video Compressor 1.2.0

本版本对视频扫描、压缩规划、硬件编码和任务执行流程进行了较大升级。

## 主要更新

- 新增独立“压缩任务”页面
- 压缩前可查看完整处理计划与参数变化
- 区分“压缩前 / 计划压缩后 / 实际压缩后”
- 新增每个视频独立压缩进度、总体进度和 ETA
- 新增 Intel Quick Sync H.264 / H.265 支持
- 新增 NVIDIA NVENC H.264 / H.265 支持
- 新增 AMD AMF H.264 / H.265 支持
- 自动检测 FFmpeg 编码器及本机硬件实际可用性
- 硬件编码失败时支持保持目标 Codec 的安全回退
- 新增智能自动压缩模式
- 根据分辨率、FPS、码率、Codec、文件大小和用途生成独立压缩计划
- 新增可选 VMAF 抽样质量校准
- 新增编码无进展 Watchdog，避免硬件编码任务无限停滞
- 重构 Codec / Encoder / RateControl 模型
- 扫描、规划、预览、执行、验证和安全提交统一为完整处理 Pipeline
- 改进条件判断、智能跳过和跳过原因显示
- 改进目标文件大小模式，H.264 / H.265 不再发生 Codec 漂移
- 增强输出验证和源文件保护机制
- 压缩结果未小于源文件时默认放弃结果并保留源文件
- 改进任务历史、Fallback 信息和高级诊断信息

## 编码器

支持：

- libx264
- libx265
- h264_qsv
- hevc_qsv
- h264_nvenc
- hevc_nvenc
- h264_amf
- hevc_amf

硬件编码器是否可用取决于实际 GPU、驱动和 FFmpeg 环境。

## 安全机制

CloudLight Video Compressor 不直接覆盖正在编码的源视频。

处理流程仍然是：

临时输出 → FFmpeg → ffprobe 验证 → 输出检查 → 安全提交

压缩失败、取消或验证失败时保留源文件。

## 系统要求

Windows x64

安装包已包含：

- .NET 8 Runtime
- FFmpeg
- ffprobe

正常情况下无需额外安装运行环境。

## 校验

SHA256：

`DBA38A50311E76C22A83DF9808E98B6E39699E73ED3EAF984262A2CA50DC4DC0`

注意：当前安装包未进行 Authenticode 数字签名，因此 Windows SmartScreen 可能显示未知发布者提示。
