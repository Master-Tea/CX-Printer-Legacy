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

## 启动故障排查

v1.0.1 修复了窗口分栏初始化时发生的启动异常。请完整解压新版到新目录后运行，不要只复制 EXE。

程序发生托管异常时会显示错误消息，并优先在程序目录写入 `CX-Printer-Legacy-error.log`。如果目录不可写，日志会依次尝试写入 `%LOCALAPPDATA%\CX Printer Legacy\Logs` 或临时目录。请提供该日志中的完整异常信息以便定位。运行 `"CX Printer.exe" --self-test` 可检查窗口构造和缩放尺寸布局，成功时退出代码为 0；它不安装服务或修改共享设置。

v1.0.2 将服务连接等待限制为最多 4 秒；打印机逐台显示，不再等待整批驱动全部返回。驱动读取在独立子进程中进行：打印机列表最多等待 10 秒，每台打印机能力最多等待 20 秒，超时会跳过并显示原因，避免阻塞后续 PDF 等队列。后台服务尚未连接时仍可点击“安装 / 修复后台服务”。如果更新前已安装服务，请用新版点击该按钮更新后台程序。

运行 `"CX Printer.exe" --diagnose-printers` 会在最多约 60 秒后将打印机队列、能力和错误写入程序目录中的 `printer-diagnostics.json`，便于区分系统列表、单个驱动和服务连接问题。

v1.0.3 修复了 .NET 6 拒绝管道首实例标志导致后台服务启动即退出的问题，保留管道 ACL 和首实例保护。安装窗口会显示复制、注册、防火墙和启动等具体进度；失败会回传真实错误，管理端记录位于 `%LOCALAPPDATA%\CX Printer Legacy\Logs\setup-ui.log`。安装器只有在服务状态为运行中且控制请求成功后才报告完成；服务启动后立即退出时会附上最近的服务日志。驱动子进程通过异步 I/O 等待，管理状态查询不再启动基础界面用不到的耗材读取，避免慢驱动占满控制请求线程。

v1.0.4 增加管理端运行权限检查。若从带有低完整性标签的沙盒/构建目录启动，Windows 可能同时拒绝服务管道连接及普通用户日志写入；新版会在安装前指出该原因。请把完整 ZIP 解压到普通用户目录后运行，已有服务无需因此反复安装。不要通过放宽服务管道权限解决此问题。

安装日志现在依次尝试 `%LOCALAPPDATA%\CX Printer Legacy\Logs`、程序目录和临时目录；只有写入成功才显示日志路径。服务安装成功后的客户端连接失败会单独说明，不再误报成安装器失败。运行 `"CX Printer.exe" --diagnose-service` 可用真实 WinForms 管理端连续查询服务三次，记录运行身份 SID、完整性级别、版本、连接耗时、完整异常和日志写入结果，报告名为 `service-diagnostics.json`，位置使用上述日志目录回退顺序。此诊断不安装服务，也不修改共享设置。诊断返回 0 表示运行权限、连接和日志全部通过，2 表示检查未通过。

打包脚本在生成 ZIP 前会运行界面自检，模拟服务无响应、驱动阻塞及 PDF 部分结果返回，并验证管道首实例保护、异步通信、安装进度回传、完整服务控制台启动、连续控制请求和 HTTP 健康检查。控制台测试使用独立临时数据，不安装或修改系统服务。安装服务后，还可对普通目录中解压的正式包运行 `powershell -ExecutionPolicy Bypass -File .\scripts\verify-client.ps1 -Directory "解压目录"`，验证真实管理端的权限、三次连接及日志写入；开发时对已标记低完整性的测试目录可额外传入 `-ExpectRestricted` 检查拒绝原因与日志回退。这不替代 Windows 7 实机及实际打印测试。

## 版本说明

此独立仓库复用 CX Printer 的 IPP、DNS-SD、打印队列与配置逻辑；Windows 7 入口及 UI 位于 `src/CxPrint.Win7`。项目目标为 `net6.0-windows7.0`，发布仅支持 x64。PDF 页面使用 PDFium 108 渲染，以兼容 Windows 7。
