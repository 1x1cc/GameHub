# GameHub - 音频与游戏工具箱

一个基于 C# WPF（.NET 8）的 Windows 桌面应用，为游戏玩家提供统一的音频设备管理和游戏工具集成。
内置音频设备热键切换与蓝牙音频接收管理，全部功能本地实现、零后台轮询、对游戏帧率无影响。

## 下载

从 [Releases](https://github.com/1x1cc/GameHub/releases/latest) 页面下载：

| 版本 | 文件 | 大小 | 说明 |
|------|------|------|------|
| **自包含版（推荐）** | `GameHub-v1.0.2-win-x64.zip` | 69 MB | 免安装 .NET，解压后双击 `GameHub.exe` 即用 |
| 精简版 | `GameHub-v1.0.2-win-x64-lite.zip` | 6.7 MB | 需先安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |

### 国内下载加速

直连 GitHub 较慢时，在下载链接前加镜像前缀即可（实测可用）。当前最新版（v1.0.2）的现成镜像链接：

```
# 自包含版（推荐）
https://gh-proxy.com/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64.zip
https://ghfast.top/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64.zip
https://ghproxy.net/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64.zip
https://hk.gh-proxy.com/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64.zip

# 精简版（需 .NET 8 桌面运行时）
https://gh-proxy.com/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64-lite.zip
https://ghfast.top/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64-lite.zip
https://ghproxy.net/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64-lite.zip
https://hk.gh-proxy.com/https://github.com/1x1cc/GameHub/releases/download/v1.0.2/GameHub-v1.0.2-win-x64-lite.zip
```

镜像前缀与 GitHub 原始路径的对应关系：

```
https://gh-proxy.com/          + https://github.com/1x1cc/GameHub/releases/download/...
https://ghfast.top/            + https://github.com/1x1cc/GameHub/releases/download/...
https://ghproxy.net/           + https://github.com/1x1cc/GameHub/releases/download/...
https://hk.gh-proxy.com/       + https://github.com/1x1cc/GameHub/releases/download/...
```

> 镜像为第三方公益服务，可用性可能变化；如失效请直接用原始 GitHub 链接。
>
> 2026-10-19 实测（v1.0.2 发布包）：`gh-proxy.com` / `ghfast.top` / `hk.gh-proxy.com` 均返回 200 且文件大小正确；`ghproxy.net` 当前返回 **502 Bad Gateway**，暂不可用。

## 覆盖安装 / 更新（重复安装新版不报错）

GameHub 是绿色免安装的：解压后直接双击 `GameHub.exe` 即可。
如果你想把新版覆盖到固定目录，或**已装过旧版想无报错覆盖更新**，用仓库里的安装脚本：

1. 下载并解压新版压缩包；
2. 在解压目录里双击 `安装 GameHub.bat`，或右键 `install.ps1` →「使用 PowerShell 运行」；
3. 脚本会自动完成：
   - 关闭正在运行的旧 GameHub（避免“文件被占用”的覆盖报错）；
   - 把新版**覆盖**到安装目录 `%LOCALAPPDATA%\Programs\GameHub`；
   - **保留你的设置** `%APPDATA%\GameHub\settings.json`（热键、关闭行为等不丢）；
   - 清理缓存：`%TEMP%\gamehub.log`、`gamehub-res.log` 以及旧崩溃报告；
   - 修正开机自启动指向的新路径，并在开始菜单创建「GameHub」快捷方式。

安装目录默认是 `%LOCALAPPDATA%\Programs\GameHub`，可用 `-InstallDir` 改成别处：

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -InstallDir "D:\Tools\GameHub"
```

常用参数：`-KeepLogs`（保留日志不清理）、`-NoShortcut`（不创建快捷方式）。

## 资源占用

在 Windows 11 + .NET 8 上实测（空闲状态，窗口打开、无操作）：

| 版本 | 工作集内存 | 私有内存 | CPU |
|------|-----------|---------|-----|
| 自包含版 | ≈ 372 MB | ≈ 223 MB | ≈ 0% |
| 精简版 | ≈ 219 MB | ≈ 145 MB | ≈ 0% |

- 工作集包含与其他 .NET 程序共享的运行时页；精简版因共享系统运行时，占用明显更低
- 空闲时 CPU 占用约 0%，无定时轮询；仅在按热键/切换设备时有瞬时开销
- 对 16 GB 内存机器约占 1.4%–2.3%，因不占用 GPU 与游戏主线程，对帧率无可感知影响

## 功能特性

### 主界面

- 参考米家 APP 的卡片式 UI 设计
- 每个模块卡片包含：
  - 模块图标和名称
  - 状态文本（当前默认设备 / 蓝牙连接状态等）
  - 描述信息
  - 两个操作按钮：「打开软件」和「开启自启动」
  - 点击卡片其他区域可打开模块设置

### 关闭行为

- **默认行为**：关闭主窗口时弹出选择对话框，让用户选择：
  - **完全退出软件**：结束所有功能，全局热键立即失效，托盘图标一并消失
  - **最小化到托盘**：程序在后台继续运行，全局热键仍然生效；双击托盘图标可恢复主界面
- **记住选择**：用户可勾选「记住我的选择，下次不再询问」，设置会保存到 `settings.json`
- **软件设置**：可通过右上角「设置」按钮在软件设置中更改关闭行为
- **托盘图标**：最小化到托盘后，系统托盘区会显示 GameHub 图标，双击可唤回主窗口
- **二次启动**：托盘状态下再次启动程序，会自动唤回已有实例的窗口，不会弹出「已经在运行」提示

### 软件设置

- 右上角「设置」按钮打开软件设置窗口
- 当前设置项：关闭主窗口时的行为（每次询问 / 完全退出 / 最小化到托盘）
- 设置保存到 `%APPDATA%\GameHub\settings.json`

### 内置模块

#### 1. SoundSwitch（简化版）

**功能：**

- ✅ 输出设备手动切换
- ✅ 输入设备手动切换
- ✅ 热键循环切换（可在勾选的设备间循环，见模块设置）

**默认热键：**

| 热键 | 功能 |
|------|------|
| `Ctrl+Alt+F11` | 循环切换输出设备 |
| `Ctrl+Alt+F7`  | 循环切换输入设备 |

**实现方式：** 直接调用 Windows CoreAudio（WASAPI）API 枚举与切换默认端点，不依赖任何外部程序。

**灵感来源：** [Belphemur/SoundSwitch](https://github.com/Belphemur/SoundSwitch) (GPLv3)

#### 2. AudioPlaybackConnector

**功能：**

- 蓝牙 A2DP Sink 连接管理（让电脑作为蓝牙音箱，接收手机等设备的音频）
- 设备列表显示
- 连接/断开控制
- 热键暂停/恢复蓝牙音频（`Ctrl+Alt+F12`，只静音蓝牙会话，不影响电脑其它声音）

**实现方式：** 基于 Windows 10 2004+ 自带的 `AudioPlaybackConnection` WinRT API 与 WASAPI 会话控制，同样不依赖外部程序。

**灵感来源：** [ysc3839/AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) (MIT)

#### 3. EasyBluetooth

**功能：**
- ✅ 显示蓝牙设备列表（经典蓝牙 + BLE）
- ✅ 显示 2.4G 无线设备电量（通过 HID/USB 电源接口）
- ✅ 电量百分比实时读取（PnP 属性 / GATT 电池服务 / 系统电源接口 / HID 标准电量）
- ✅ 厂商私有协议直读（仅只读查询，不修改设备设置）：华硕 ROG 键盘鼠标（夜魔 / AZOTH / AZOTH X 等，含 OMNI 多设备接收器）、WLMouse 万灵/花兽全系（含充电状态）、雷蛇 Razer、前行者 EWEADN
- ✅ XInput 手柄电量（微软公开 API，覆盖有线 / 2.4G 手柄）
- ✅ 低电量通知（可自定义阈值，默认 20%）
- ✅ 定时轮询（默认 30 秒，可配置 10-3600 秒）
- ✅ 常驻悬浮窗（可锁定/穿透/置顶/透明度设置）

**实现方式：** 基于 Windows 原生 API（PnP 设备属性 / Windows.Devices.Power.Battery / 蓝牙 GATT / HID）与厂商私有只读协议查询，不依赖外部程序。

**灵感来源：** [muedsa/BluetoothLEBatteryMonitor](https://github.com/muedsa/BluetoothLEBatteryMonitor)（MIT），本模块完全自研实现。

### 与游戏反作弊的兼容性

GameHub 面向边玩游戏边使用的场景，代码层面刻意避开了所有反作弊敏感行为：

- 只使用 `RegisterHotKey` 系统热键注册（由系统转发按键），**不安装任何键盘/鼠标钩子**，不模拟输入
- 只调用音频相关的系统 API（CoreAudio / WASAPI / 蓝牙），**不读写其它进程内存、不注入 DLL、不加载驱动**
- 以普通用户权限运行（不提权），无游戏内叠加层（overlay）

因此与 EAC、EA 反作弊、ACE（腾讯）、Vanguard 等主流反作弊在技术特征上没有交集，可以放心在游戏中后台运行。

## 构建与运行

### 环境要求

- **操作系统：** Windows 10 2004 或更高版本（蓝牙模块需要 2004+）
- **.NET SDK：** .NET 8.0 或更高版本
- **IDE（可选）：** Visual Studio 2022 / Rider / VS Code

### 方式一：命令行

```bash
# 还原依赖
dotnet restore

# 编译并运行
dotnet run
```

### 方式二：一键脚本

- `启动 GameHub.bat` / `启动 GameHub.ps1`：自动编译 Release 版本并启动，退出时显示最后 10 行日志
- `dev.bat`：开发调试用的快速编译启动
- `setup_dev_env.py`：一键安装 VS Code 调试所需扩展并打开项目（可选）

### 方式三：Visual Studio

1. 打开 `GameHub.sln`
2. 选择「生成」->「重建解决方案」
3. 按 F5 运行

## 项目结构

```
GameHub/
├── GameHub.sln              # 解决方案文件
├── GameHub.csproj           # 项目文件（.NET 8, WPF + WinForms 托盘）
├── App.xaml / App.xaml.cs   # 应用入口（单实例互斥、DPI 感知、激活监听）
├── app.manifest             # DPI 感知 / UTF-8 代码页声明
├── Interfaces/              # 接口定义
│   ├── IModule.cs
│   └── IModuleViewModel.cs
├── Utils/                   # 工具类
│   ├── AudioDeviceService.cs      # 音频端点枚举/切换（CoreAudio）
│   ├── AudioSessionMute.cs        # WASAPI 会话静音（蓝牙暂停）
│   ├── BluetoothAudioService.cs   # 蓝牙 A2DP Sink 连接管理
│   ├── CoreAudioInterop.cs        # CoreAudio COM 互操作层
│   ├── HotkeyManager.cs           # 全局热键（RegisterHotKey）
│   ├── NotificationManager.cs     # 右下角轻量通知
│   ├── TrayIconManager.cs         # 系统托盘图标
│   ├── AutoStartManager.cs        # 开机自启动（注册表）
│   └── SettingsStore.cs           # settings.json 读写
├── ViewModels/
│   └── ModuleCardViewModel.cs
├── Views/
│   ├── MainWindow.xaml(.cs)       # 主界面
│   ├── CloseConfirmWindow.xaml    # 关闭行为选择对话框
│   └── HubSettingsWindow.xaml     # 软件设置
├── Converters/
│   └── AutoStartConverter.cs
├── Modules/                 # 模块实现
│   ├── SoundSwitch/
│   │   ├── SoundSwitchModule.cs
│   │   └── SoundSwitchSettingsWindow.xaml(.cs)
│   ├── AudioPlaybackConnector/
│   │   ├── AudioPlaybackConnectorModule.cs
│   │   └── AudioPlaybackConnectorSettingsWindow.xaml(.cs)
│   └── EasyBluetooth/
│       ├── EasyBluetoothModule.cs
│       ├── EasyBluetoothMonitor.cs
│       ├── EasyBluetoothSettingsWindow.xaml(.cs)
│       ├── EasyBluetoothFloatingWindow.xaml(.cs)
│       └── EasyBluetoothFloatingController.cs
└── Assets/
    └── Icons/               # 图标资源
```

## 模块开发指南

### 创建新模块

1. 在 `Modules/` 目录下创建新文件夹
2. 实现 `IModule` 接口：

```csharp
public class MyModule : IModule
{
    public string Name => "MyModule";
    public string Description => "模块描述";
    public string IconPath => "pack://application:,,,/Assets/Icons/mymodule.png";

    public Task<string> GetStatusTextAsync()
        => Task.Run(() => "模块当前状态");

    public void OpenPanel() { /* 打开模块功能面板 */ }
}
```

3. 创建对应的设置窗口（可选）
4. 在 `MainWindow.xaml.cs` 的 `LoadModules()` 方法中注册新模块

## 故障排查

- **热键没反应**：查看 `%TEMP%\gamehub.log`，热键注册失败（如被其它程序占用）会记录原因，可在模块设置里换个组合键
- **蓝牙模块显示「需要 Windows 10 2004 或更高版本」**：系统版本过低，A2DP Sink API 不可用
- **托盘图标不显示**：见 `%TEMP%\gamehub.log` 中「托盘图标」相关记录

## 许可证

本项目基于 **GPLv3** 许可证开源，详见 [LICENSE](LICENSE)。

- **GameHub 主程序**：GPLv3
- **SoundSwitch 模块**：灵感来源于 [Belphemur/SoundSwitch](https://github.com/Belphemur/SoundSwitch)（GPLv3），本项目按 GPLv3 义务整体开源
- **AudioPlaybackConnector 模块**：灵感来源于 [ysc3839/AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector)（MIT）
- **EasyBluetooth 模块**：灵感来源于 [muedsa/BluetoothLEBatteryMonitor](https://github.com/muedsa/BluetoothLEBatteryMonitor)（MIT），本模块完全自研实现

## 更新日志

### v1.0.2（2026-10-19）

- **修复 ROG OMNI 接收器下的无线键盘认不出来**：`HidNative.CollectionNameOf` 的集合名正则写成了 `col_(\d+)`，而 Windows 的 HID 接口路径实际是 `...&mi_02&col01#...`（没有下划线），导致 OMNI 接收器分支（配对列表 / 无线键盘电量）**静默失效**，只有键盘插线时能读到电量
- ROG 型号表补录 **ROG AZOTH X**（有线 `0x1C24` / 无线配对 `0x1C25`，Azoth 布局：`[6]` 百分比、`[9]==1` 充电中）；实机验证无线 99%→100% 充电中
- OMNI 接收器在线、但配对列表为空时，列表会多一行**接收器状态**并直接写出排查提示（键盘档位不在 2.4G / 未配对 / 只能用有线时请插线）
- 每个设备行末尾新增「原因说明」：读不到电量时写清原因（设备休眠未应答 / XInput 只报有线 / 接收器无已连设备）
- XInput 库探测改为 `xinput1_4` → `xinput1_3` → `xinput9_1_0` 逐级回退（`xinput9_1_0.dll` 根本没导出 `XInputGetBatteryInformation`），并修掉旧版每轮轮询刷 4 条异常堆栈的问题

## 未来规划

- [ ] 添加更多音频相关模块（音量均衡、按应用静音等）
- [x] 添加系统托盘图标
- [x] 支持关闭行为自定义（完全退出 / 最小化到托盘）
- [ ] 支持模块拖拽排序
- [ ] 添加模块搜索功能
- [ ] 支持主题切换（深色模式）
- [x] 发布自包含单文件 exe
- [ ] 代码签名（降低杀软误报）

## 贡献

欢迎提交 Issue 和 Pull Request！

## 注意事项

1. **SoundSwitch 许可证：** 本仓库按 GPLv3 开源，衍生作品需遵守同一许可证
2. **音频设备 API：** 需要 Windows CoreAudio API 支持（Windows 10+）
3. **蓝牙功能：** AudioPlaybackConnector 需要 Windows 10 2004+ 版本
4. **构建要求：** 需要安装 .NET 8.0 SDK
