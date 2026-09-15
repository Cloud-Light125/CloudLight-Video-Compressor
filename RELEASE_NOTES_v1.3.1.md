# CloudLight Video Compressor 1.3.1

CloudLight Video Compressor 1.3.1 是针对压缩任务预览页和硬件编码能力检测的稳定性补丁。

## 修复

- 修复点击“压缩已选视频”时，`CommandPreviewDisplay` 只读属性因 `TextBox.Text` 默认 TwoWay 绑定而导致任务页无法打开的问题
- 修复同一任务页中智能计算明细、质量校准明细和诊断错误等只读显示属性的同类绑定风险
- 增加任务页 XAML 只读绑定静态审计和真实 WPF 窗口创建回归测试
- 修复部分新电脑上 RTX 4070 已可由 FFmpeg 使用、但应用仍将 NVIDIA NVENC 判定为不可用的问题
- NVENC 初始化检测改用 1280×720 `testsrc2` smoke encode，并记录完整 FFmpeg 路径、编码器列表、命令、stderr 与 exit code
- 新增带 GPU、NVIDIA 驱动、FFmpeg 完整版本、FFmpeg 路径和时间戳的能力缓存；环境变化、缓存过期或旧 NVENC 失败记录都会触发重新检测
- 保留 Intel QSV 与 CPU 软件编码回退；瞬时检测失败不再永久覆盖用户的硬件编码偏好

本补丁不改变正常压缩计划、正式编码参数或媒体文件处理逻辑。

## 系统要求

- Windows x64
- 正式安装包已包含 .NET 8、FFmpeg 和 ffprobe，正常情况下无需额外配置运行环境或 PATH

## 数字签名

当前安装包未进行 Authenticode 正式签名，Windows SmartScreen 可能显示未知发布者提示。
