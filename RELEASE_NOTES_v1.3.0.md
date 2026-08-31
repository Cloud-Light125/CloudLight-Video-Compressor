# CloudLight Video Compressor 1.3.0

CloudLight Video Compressor 1.3.0 聚焦于媒体扫描、压缩规划、编码器选择、长任务稳定性和输出文件安全。

## 主要更新

### 媒体库、缓存与健康检查

- 新增 MediaProbeCache，未变化文件的重复扫描可复用探测结果
- 新增 Smart/VMAF 结果缓存，并根据文件、FFmpeg 和算法版本自动失效
- 支持 Quick / Deep 源文件健康检查
- 智能判断视频是否真正值得重新压缩

### 压缩任务与长时间运行

- 支持独立压缩任务工作区和压缩前处理计划
- 展示压缩前、计划压缩后和实际压缩后的关键媒体信息
- 支持每个视频进度、总体进度、Current ETA 和 Queue ETA
- 新增低配置/长时间稳定模式，以及速度优先等性能策略
- 支持 Prevent Sleep，降低长任务期间系统自动睡眠导致的中断风险
- 支持暂停任务队列和任务完成后的关闭软件、睡眠、休眠或关机操作
- 增加编码无进展 Watchdog

### 编码器与视频质量

- 新增 Encoder Benchmark
- Auto Encoder 2.0 根据实际性能、媒体特征、质量偏好和硬件能力选择编码器
- 新增 Encoder Tuning Preset
- 完成 Codec、Encoder、RateControl 和 BitDepth 的解耦
- 支持 HEVC Main10 / 10-bit，并对 10-bit 输入进行策略保持
- 增加 HDR 色彩信息的基础保护
- 增加复杂度感知的 VMAF 抽样质量校准
- 硬件编码不可用时，按目标 Codec 和位深执行安全 fallback

### 文件完整性与容器兼容性

- 完善多音轨、字幕、Chapters、Metadata、语言标签以及 default / forced disposition 的保留
- 增加 Container Compatibility Safety，在压缩前识别不兼容流
- 增加 Cover Art / attached_pic 的兼容策略
- 输出完成后继续使用真实 ffprobe 验证

### 任务恢复与文件安全

- 支持保存未完成任务并在程序重启后恢复任务队列
- 已完成任务不会重复执行；中断任务可安全重新开始
- 源文件发生变化时自动阻止旧计划执行
- 压缩继续采用临时输出 → FFmpeg → ffprobe → Validation → Safe Commit 流程
- 失败、取消或验证失败时保留源文件；结果未小于源文件时默认放弃结果

## 系统要求

- Windows x64
- 正式安装包已包含 .NET 8、FFmpeg 和 ffprobe，正常情况下无需额外配置运行环境或 PATH

## 数字签名

当前安装包未进行 Authenticode 正式签名，Windows SmartScreen 可能显示未知发布者提示。
