# 第三方组件清单（THIRD-PARTY-NOTICES）

RemoteFlow 按GPL-3.0 许可证发布。本文件列出随源码分发 / 运行时使用的第三方开源组件及其许可证，供合规披露。

> 各组件的许可证以**上游项目仓库内的许可证文本**为准，本清单仅为摘要。

## 运行时依赖（Windows / macOS 共享层）

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM 框架（源生成器） |
| Microsoft.Extensions.DependencyInjection / .Abstractions | 10.0.11 | MIT | 依赖注入 |
| Microsoft.Extensions.Logging / .Abstractions | 10.0.11 | MIT | 日志抽象 |
| Microsoft.Data.Sqlite | 10.0.11 | MIT | 本地 SQLite 存储 |
| Serilog | 4.4.0 | Apache-2.0 | 日志 |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | 日志文件输出 |
| Serilog.Extensions.Logging | 10.0.0 | Apache-2.0 | Serilog 与 MEL 桥接 |
| SSH.NET (Renci.SshNet) | 2026.0.0 | MIT | SSH 协议 |
| Community.MarcusW.VncClient | 2.0.7 | MIT | VNC 协议 |
| Microsoft.Web.WebView2 | 1.0.4191.47 | MIT | SSH 终端宿主（xterm.js） |
| System.Security.Cryptography.ProtectedData | 10.0.11 | MIT | Windows DPAPI 凭据保护 |

## Windows 平台

- **.NET Runtime / Windows Desktop Runtime**（.NET 10）—— MIT（.NET Foundation）
- **mstscax.dll（RDP ActiveX）** —— 随 Windows 系统组件提供，不随本应用分发，遵循 Microsoft Windows 许可条款。
- **xterm.js**（经 WebView2 内嵌资源加载，SSH 终端前端）—— MIT，https://xtermjs.org
- **Fluent UI System Icons**（Microsoft，界面线性操作图标的矢量几何，由 `scripts/build-ui-icons.py` 生成 `Themes/UiIcons.xaml`）—— MIT，https://github.com/microsoft/fluentui-system-icons ；许可证全文见文末。

## macOS 平台

- **Avalonia**（12.1.2，含 Avalonia.Desktop / Themes.Fluent / Controls.WebView）—— MIT
- **FreeRDP**（应用内嵌，`native/rdp/`）—— Apache-2.0，https://www.freerdp.com
- **OpenSSL**（FreeRDP 构建依赖，经 `native/rdp/build-openssl.sh` 获取）—— Apache-2.0
- **WKWebView / AppKit** —— 随 macOS 系统提供。

## 测试依赖

| 组件 | 版本 | 许可证 |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT |
| xunit | 2.9.3 | Apache-2.0 |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 |

## 版本来源

组件版本以仓库根 `Directory.Packages.props`（集中式包版本管理）为准；本清单版本号为撰写时的快照，升级依赖时请同步更新。

## 许可证全文

### Fluent UI System Icons

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
