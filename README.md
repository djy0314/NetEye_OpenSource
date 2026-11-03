# NetEye

一款绿色单文件的 Windows 网络连接查看器（深色界面），用于替代系统自带资源监视器里不好用的网络页：谁在联网、连到哪里、跑了多少流量，一屏看清。

单文件、无安装、无依赖，exe 直接双击运行。

## 功能

- **连接总览**：实时列出全部 TCP/UDP 连接，含进程名、PID、协议、本地/远程地址、状态、收发速率
- **进程图标**：连接列表内嵌显示进程图标，一眼对上是哪个程序
- **远程 IP 归属地**：内置离线 IP 库（qqwry.dat）显示归属地，支持在设置里一键更新库文件
- **DNS 反查**：右键对远程地址做反向 DNS 解析
- **防火墙阻断**：右键一键调用 Windows 防火墙出站规则拦截可疑进程
- **记录模式**：记录模式下新出现的连接会留档，抓"闪连闪断"的进程
- **网速悬浮小图**：悬停速率单元格显示近期速率折线
- **搜索**：支持包含 / 精确 / 正则三种匹配模式
- **JSON 导出**：当前列表一键导出 JSON
- **体验细节**：表格紧凑密度切换、列宽自动记忆、中文排序规则、进程启动时长

## 构建

环境要求：Windows + .NET Framework 4.x（自带 csc 即可，无需安装额外 SDK）。

方式一（推荐，纯 csc）：

```bat
build.bat
```

产物：`build\NetEye.exe`（单文件，无外部依赖）。

方式二（dotnet CLI，需 .NET SDK）：

```
dotnet build ui/NetEye.csproj -c Release
```

产物：`ui/bin/Release/NetEye.exe`。

## 目录结构

```
src/           全部 C# 源码（WinForms，C# 5，.NET Framework 4.x）
  Program.cs         入口
  MainForm.cs        主窗口与交互
  NetTable.cs        连接表格
  NetSpeed.cs        网速采集
  ProcBadge.cs       进程信息
  ProcIcons.cs       进程图标提取与缓存
  Qqwry.cs           离线 IP 库（qqwry.dat）解析
  LibUpdater.cs      IP 库在线更新器（多镜像 + 三重校验 + 原子替换）
  SettingsForm.cs    设置窗口
  SpeedGraph.cs      网速悬浮小图
  DonateForm.cs      支持作者窗口
  ...
ui/NetEye.csproj    dotnet SDK 工程文件
build.bat           一键构建脚本（csc）
```

## 说明

- 本程序仅读取本机网络连接信息（IPHelper API），不回传任何数据；唯一的联网行为是在你主动点击"更新 IP 库"时下载 qqwry.dat。
- IP 离线库使用纯真 IP 数据库（qqwry.dat），首次运行如缺失可在设置中在线获取。

## License

MIT，详见 [LICENSE](LICENSE)。
