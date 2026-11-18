# CX Printer Legacy

Windows 7 SP1 x64 兼容版。它把 Windows 已安装的打印机驱动通过 IPP 和 DNS-SD 发布到同一局域网，供 iPhone、iPad 和 Mac 发现并打印。

## 基础功能

- 查看 Windows 打印机并选择要共享的队列。
- 设置 Apple 设备上显示的打印机名称和默认纸张。
- 安装、修复 Windows 后台服务；服务负责 IPP 打印与 mDNS 发现。
- 使用 Windows 打印驱动完成实际打印。纸张、彩色和双面能力取决于驱动。

界面基于传统 WinForms，不依赖 Windows App SDK。高级诊断、作业记录和耗材状态不包含在此版本中。

## 系统要求

- Windows 7 SP1 x64，并安装运行自包含 .NET 6 程序所需的 SHA-2/系统更新。
- 电脑与 Apple 设备位于允许互通的局域网。
- 安装后台服务时需要管理员授权。

本程序自包含 .NET 6，不要求目标计算机另装 .NET。Windows 7 和 .NET 6 均已结束 Microsoft 支持；此版本用于需要旧系统兼容的环境，不应将打印端口暴露到互联网。

## 构建与运行

在 Windows 上安装 Visual Studio 2022 Build Tools（.NET 桌面开发组件）和 .NET SDK 9，运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-win7.ps1
```

生成的目录为 `artifacts\CX-Printer-win7-x64`，压缩包为 `artifacts\CX-Printer-Legacy-win7-x64.zip`。复制压缩包到 Windows 7 电脑并完整解压，运行 `CX Printer.exe`，点击“安装 / 修复后台服务”，然后选择共享的打印机并保存。

安装完成后，Windows 服务会在后台持续运行。默认端口为 TCP 8631，mDNS 使用 UDP 5353；安装服务时会创建限于专用/域网络和本地子网的 Windows 防火墙规则。不要在不可信网络共享打印服务。

## 版本说明

此独立仓库复用 CX Printer 的 IPP、DNS-SD、打印队列与配置逻辑；Windows 7 入口及 UI 位于 `src/CxPrint.Win7`。项目目标为 `net6.0-windows7.0`，发布仅支持 x64。PDF 页面使用 PDFium 108 渲染，以兼容 Windows 7。
