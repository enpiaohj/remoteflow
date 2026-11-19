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
- **KDE Breeze 光标**（`openhand`、`closedhand`/`dnd-move` 两个光标的矢量路径，用于拖动标签时的手掌光标，`src/RemoteFlow.App/Views/DragCursors.cs`）—— LGPL-3.0-or-later，Copyright 2014 Uri Herrera and others，https://invent.kde.org/plasma/breeze （`cursors/Breeze`，其 COPYING-ICONS 声明该美术库适用 LGPL 且不限制在图形界面中使用）；已改动：仅更换渲染方式，按 32 / 48 / 64 像素栅格化后写入 .cur；许可证全文见文末。

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

### KDE Breeze 光标（GNU Lesser General Public License v3.0）

```
                   GNU LESSER GENERAL PUBLIC LICENSE
                       Version 3, 29 June 2007

 Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 Everyone is permitted to copy and distribute verbatim copies
 of this license document, but changing it is not allowed.


  This version of the GNU Lesser General Public License incorporates
the terms and conditions of version 3 of the GNU General Public
License, supplemented by the additional permissions listed below.

  0. Additional Definitions.

  As used herein, "this License" refers to version 3 of the GNU Lesser
General Public License, and the "GNU GPL" refers to version 3 of the GNU
General Public License.

  "The Library" refers to a covered work governed by this License,
other than an Application or a Combined Work as defined below.

  An "Application" is any work that makes use of an interface provided
by the Library, but which is not otherwise based on the Library.
Defining a subclass of a class defined by the Library is deemed a mode
of using an interface provided by the Library.

  A "Combined Work" is a work produced by combining or linking an
Application with the Library.  The particular version of the Library
with which the Combined Work was made is also called the "Linked
Version".

  The "Minimal Corresponding Source" for a Combined Work means the
Corresponding Source for the Combined Work, excluding any source code
for portions of the Combined Work that, considered in isolation, are
based on the Application, and not on the Linked Version.

  The "Corresponding Application Code" for a Combined Work means the
object code and/or source code for the Application, including any data
and utility programs needed for reproducing the Combined Work from the
Application, but excluding the System Libraries of the Combined Work.

  1. Exception to Section 3 of the GNU GPL.

  You may convey a covered work under sections 3 and 4 of this License
without being bound by section 3 of the GNU GPL.

  2. Conveying Modified Versions.

  If you modify a copy of the Library, and, in your modifications, a
facility refers to a function or data to be supplied by an Application
that uses the facility (other than as an argument passed when the
facility is invoked), then you may convey a copy of the modified
version:

   a) under this License, provided that you make a good faith effort to
   ensure that, in the event an Application does not supply the
   function or data, the facility still operates, and performs
   whatever part of its purpose remains meaningful, or

   b) under the GNU GPL, with none of the additional permissions of
   this License applicable to that copy.

  3. Object Code Incorporating Material from Library Header Files.

  The object code form of an Application may incorporate material from
a header file that is part of the Library.  You may convey such object
code under terms of your choice, provided that, if the incorporated
material is not limited to numerical parameters, data structure
layouts and accessors, or small macros, inline functions and templates
(ten or fewer lines in length), you do both of the following:

   a) Give prominent notice with each copy of the object code that the
   Library is used in it and that the Library and its use are
   covered by this License.

   b) Accompany the object code with a copy of the GNU GPL and this license
   document.

  4. Combined Works.

  You may convey a Combined Work under terms of your choice that,
taken together, effectively do not restrict modification of the
portions of the Library contained in the Combined Work and reverse
engineering for debugging such modifications, if you also do each of
the following:

   a) Give prominent notice with each copy of the Combined Work that
   the Library is used in it and that the Library and its use are
   covered by this License.

   b) Accompany the Combined Work with a copy of the GNU GPL and this license
   document.

   c) For a Combined Work that displays copyright notices during
   execution, include the copyright notice for the Library among
   these notices, as well as a reference directing the user to the
   copies of the GNU GPL and this license document.

   d) Do one of the following:

       0) Convey the Minimal Corresponding Source under the terms of this
       License, and the Corresponding Application Code in a form
       suitable for, and under terms that permit, the user to
       recombine or relink the Application with a modified version of
       the Linked Version to produce a modified Combined Work, in the
       manner specified by section 6 of the GNU GPL for conveying
       Corresponding Source.

       1) Use a suitable shared library mechanism for linking with the
       Library.  A suitable mechanism is one that (a) uses at run time
       a copy of the Library already present on the user's computer
       system, and (b) will operate properly with a modified version
       of the Library that is interface-compatible with the Linked
       Version.

   e) Provide Installation Information, but only if you would otherwise
   be required to provide such information under section 6 of the
   GNU GPL, and only to the extent that such information is
   necessary to install and execute a modified version of the
   Combined Work produced by recombining or relinking the
   Application with a modified version of the Linked Version. (If
   you use option 4d0, the Installation Information must accompany
   the Minimal Corresponding Source and Corresponding Application
   Code. If you use option 4d1, you must provide the Installation
   Information in the manner specified by section 6 of the GNU GPL
   for conveying Corresponding Source.)

  5. Combined Libraries.

  You may place library facilities that are a work based on the
Library side by side in a single library together with other library
facilities that are not Applications and are not covered by this
License, and convey such a combined library under terms of your
choice, if you do both of the following:

   a) Accompany the combined library with a copy of the same work based
   on the Library, uncombined with any other library facilities,
   conveyed under the terms of this License.

   b) Give prominent notice with the combined library that part of it
   is a work based on the Library, and explaining where to find the
   accompanying uncombined form of the same work.

  6. Revised Versions of the GNU Lesser General Public License.

  The Free Software Foundation may publish revised and/or new versions
of the GNU Lesser General Public License from time to time. Such new
versions will be similar in spirit to the present version, but may
differ in detail to address new problems or concerns.

  Each version is given a distinguishing version number. If the
Library as you received it specifies that a certain numbered version
of the GNU Lesser General Public License "or any later version"
applies to it, you have the option of following the terms and
conditions either of that published version or of any later version
published by the Free Software Foundation. If the Library as you
received it does not specify a version number of the GNU Lesser
General Public License, you may choose any version of the GNU Lesser
General Public License ever published by the Free Software Foundation.

  If the Library as you received it specifies that a proxy can decide
whether future versions of the GNU Lesser General Public License shall
apply, that proxy's public statement of acceptance of any version is
permanent authorization for you to choose that version for the
Library.
```
