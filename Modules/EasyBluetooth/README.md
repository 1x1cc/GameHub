# EasyBluetooth 模块搭建完成

## 模块概述

EasyBluetooth 是 GameHub 的第三个内置模块，用于显示本机无线设备（蓝牙 / 2.4G 无线外设）及其电量，支持定时刷新、低电量通知与常驻悬浮窗。

## 功能特性

### 核心功能
- ✅ 显示蓝牙设备列表（经典蓝牙 + BLE）
- ✅ 显示 2.4G 无线设备电量（通过 HID/USB 电源接口）
- ✅ 电量百分比实时读取（PnP 属性 / GATT 电池服务 / 系统电源接口）
- ✅ 低电量通知（可自定义阈值，默认 20%）
- ✅ 定时轮询（默认 30 秒，可配置 10-3600 秒）
- ✅ 常驻悬浮窗（可锁定/穿透/置顶/透明度设置）

### 数据来源（三级冗余 + 厂商私有协议）
1. **PnP 设备属性**：Windows 蓝牙堆栈写入的设备电量属性（和系统"蓝牙和其他设备"显示的是同一份数据）
2. **蓝牙 GATT 电池服务**：BLE 设备的 0x180F/0x2A19 服务（需要设备已连接）
3. **Windows 设备电源接口**：覆盖 HID / 2.4G 无线接收器及其子设备
4. **HID 标准电量**（用法页 0x06 / 用法 0x20 Battery Strength）：跨厂商通用，任何按 HID 规范上报电量的设备都能读
5. **厂商私有 HID 协议**（只读电量查询，不写设备设置）：
   - 雷蛇 Razer（协议参考开源社区逆向结论）
   - 前行者 EWEADN（参考 ccziwe/eweadn-battery）
   - 华硕 ROG：夜魔/Azoth 系列键盘 + 鼠标 + **OMNI 多设备接收器**（参考 SteveWang92/peripheral-battery-tray，协议事实源自 G-Helper）；**设备在不在线只看转发通道有没有应答**，型号表没收录时按回复布局识别（读带「推断」标注）
     - 实机验证（ROG OMNI RECEIVER，VID_0B05/PID_1ACE）：无线键盘走 `mi_02/col02`（0xFF00，report id 0x02）、鼠标走 `mi_02/col03`（0xFF01，report id 0x03）；**设备不在线时代答 `[1]=0xFF [2]=0xAA`**，在线时代答真实电量
     - 接收器自身的 `mi_02/col01`（report id 0x01）另有自答命令（`0xA0` 子命令 `0x00`、`0xA1` 子命令 `0x00/0x01` = 序列号/状态）。⚠️ **`0xA0/0x00` 的结果不能当在线判据**：2026-10-20 实测，键盘经接收器正常打字、电量也读得到时，它照样回全 0（v1.0.2 曾因此误报「键盘未连接」）
     - 实机验证（ROG AZOTH X）：2.4G 经接收器走 `col02`，`02 12 01` → `02 12 01 00 00 00 64 02 01 00 14 64 EB 10 …`（`[6]`=100%、`[9]`=0 未充电）；有线 PID `0x1C24` 走 `mi_01`（usage 0xFF00，65 字节无 report id），`00 12 01` → `00 12 01 00 00 00 63 02 01 01 0A 63 …`（`[6]`=99%、`[9]==1` 充电中）——两种链路布局一致
   - WLMouse 万灵/花兽：Beast/花兽家族 2024–2026 全系（参考 mee7ya/wlmouse-cli 与 Creationsss/wl-mouse，含充电状态）；未收录新型号自动走同协议试探
6. **XInput 手柄电量**（XInputGetBatteryInformation，微软公开 API）：覆盖有线/2.4G XInput 手柄（Xbox 及致动等第三方）；只有空/低/中/满档位，换算为近似百分比；蓝牙手柄走 BLE/HID 通道

   - 已知限制：不少第三方 2.4G 接收器（实测致动手柄 ZD 系）把自己仿真成「Xbox 360 有线手柄」，XInput 只回「有线」，因此**拿不到百分比**——此时列表会如实标注原因，而不是假装设备不存在

## v1.0.3 修复

- **修正「键盘在 2.4G 能打字、列表却显示未连接」**：v1.0.2 拿接收器 `0xA0/0x00` 的结果当在线判据，实测该命令在设备正常工作、电量可读时仍回全 0。现在**只看转发通道有没有应答**：`mi_02/col02`（键盘）/ `mi_02/col03`（鼠标）回 `FF AA` 才是真没设备，代答电量就是在线的实锤；`0xA0/0x00` 降级为「型号命名提示」
- 通道直读带布局判定：`[6]` 为 0–10 档位表时取 `[11]`（夜魔家族），否则 `[6]` 即百分比（Azoth 等）；接收器没报型号时显示「ROG 键盘（OMNI 接收器 · 2.4G）」，行末标注按哪种布局识别（仅供参考）
- 状态行措辞：「接收器在线，无已连接设备」→「接收器在线，设备未应答」，不把「没实测到」说成「确定没连」
- 对设备只发只读电量查询（键盘 `0x12/0x01`、鼠标 `0x12/0x07`；接收器 `0xA0/0x00` 读自身信息），不写设备设置、不改配对

## v1.0.2 修复

- 修复 `HidNative.CollectionNameOf` 的集合名正则（`col_(\d+)` → `col_?(\d+)`）：此前 Windows 的 `...&mi_02&col01#...` 路径全部解析失败，导致 **ROG OMNI 接收器整条分支静默失效**（软件认不出无线连接下的 ROG 夜魔 X / AZOTH X 键盘）
- ROG 型号表补录 **ROG Azoth X**（有线 0x1C24 / 无线配对 0x1C25，Azoth 布局）
- OMNI 接收器在线但配对列表为空时，列表会给出接收器状态行与排查提示（⚠️ 这一条的判断有误，见上方 v1.0.3：配对列表为空并不代表设备没连上）
- 列表行末尾新增「原因说明」，读不到电量时直接写清原因（设备休眠 / XInput 只报有线 / 接收器无设备）
- XInput 库探测改为 xinput1_4 → xinput1_3 → xinput9_1_0 逐级回退（`xinput9_1_0.dll` 没有该导出函数），并避免旧版每轮轮询刷 4 条异常堆栈

### 反作弊兼容性
- ✅ 只使用 Windows 原生 API（CoreAudio / WASAPI / 蓝牙 / PnP），不安装驱动
- ✅ 不注入进程、不读取内存、不挂钩键鼠
- ✅ 以普通用户权限运行，无游戏内叠加层
- ✅ 与 EAC、EA 反作弊、ACE（腾讯）、Vanguard 等主流反作弊无技术交集

## 项目结构

```
GameHub/
├── Modules/
│   └── EasyBluetooth/
│       ├── EasyBluetoothModule.cs              # 模块主体（实现 IModule 接口）
│       ├── EasyBluetoothMonitor.cs             # 后台轮询器（DispatcherTimer）
│       ├── EasyBluetoothSettingsWindow.xaml    # 设置窗口（XAML）
│       ├── EasyBluetoothSettingsWindow.xaml.cs # 设置窗口（C# 代码）
│       ├── EasyBluetoothFloatingWindow.xaml    # 悬浮窗（XAML）
│       ├── EasyBluetoothFloatingWindow.xaml.cs # 悬浮窗（C# 代码）
│       └── EasyBluetoothFloatingController.cs  # 悬浮窗控制器（拖动/锁定/穿透）
├── Utils/
│   ├── WirelessBatteryService.cs               # 无线设备电量读取服务（核心）
│   ├── PnpBatteryReader.cs                     # PnP 设备节点电量读取
│   └── SettingsStore.cs                        # settings.json 读写（含 EasyBluetoothSettings）
├── Assets/
│   └── Icons/
│       └── easybluetooth.png                   # 模块图标（256x256）
└── Views/
    └── MainWindow.xaml.cs                      # 主界面（已注册 EasyBluetoothModule 为第三个卡片）
```

## 设置项

所有设置保存在 `%APPDATA%\GameHub\settings.json` 的 `EasyBluetooth` 子对象中：

```json
{
  "EasyBluetooth": {
    "AutoRefreshEnabled": true,
    "RefreshIntervalSeconds": 30,
    "LowBatteryNotifyEnabled": true,
    "LowBatteryThresholdPercent": 20,
    "LowBatteryRepeatMinutes": 30,
    "ListOnlyConnected": false,
    "FloatingEnabled": false,
    "FloatingLocked": false,
    "FloatingTopmost": true,
    "FloatingClickThrough": false,
    "FloatingOpacity": 0.92,
    "FloatingOnlyConnected": true,
    "FloatingLeft": -1,
    "FloatingTop": -1
  }
}
```

### 设置说明
| 设置项 | 默认值 | 说明 |
|--------|--------|------|
| AutoRefreshEnabled | true | 是否定时自动刷新电量 |
| RefreshIntervalSeconds | 30 | 自动刷新间隔（10-3600 秒） |
| LowBatteryNotifyEnabled | true | 电量低于阈值时弹通知 |
| LowBatteryThresholdPercent | 20 | 低电量阈值（1-99%） |
| LowBatteryRepeatMinutes | 30 | 同一设备低电量提醒最小间隔（1-1440 分钟） |
| ListOnlyConnected | false | 设备列表只显示已连接的设备 |
| FloatingEnabled | false | 是否启用常驻悬浮窗 |
| FloatingLocked | false | 锁定后不能用鼠标拖动悬浮窗 |
| FloatingTopmost | true | 悬浮窗是否置顶 |
| FloatingClickThrough | false | 鼠标穿透（打游戏时不挡操作） |
| FloatingOpacity | 0.92 | 悬浮窗透明度（0.3-1.0） |
| FloatingOnlyConnected | true | 悬浮窗只显示已连接的设备 |
| FloatingLeft | -1 | 悬浮窗 X 坐标（-1 表示自动放到右上角） |
| FloatingTop | -1 | 悬浮窗 Y 坐标（-1 表示自动放到右上角） |

## 技术实现

### 蓝牙设备枚举
- **经典蓝牙**：`BluetoothDevice.GetDeviceSelectorFromPairingState(true)` + `DeviceInformation.FindAllAsync`
- **BLE**：`BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)` + AEP 属性过滤
- **PnP 安全网**：`CM_Get_Device_ID_ListW` 枚举 BTHLE 节点，补充蓝牙枚举未列出的设备

### 电量读取优先级
1. **PnP 设备属性**：`DevPropKey` 读取 `System.Devices.Battery.CurrentCapacity`（PID 2）
2. **蓝牙 GATT**：`GattCharacteristic.ReadValueAsync` 读取 0x2A19 特征值
3. **系统电源接口**：`Windows.Devices.Power.Battery.GetReport`

### 低电量通知
- 使用 `NotificationManager`（屏幕右下角轻量通知）
- 同一设备低电量提醒有最小间隔（默认 30 分钟），避免每轮都弹
- 通知内容：设备名称 + 电量百分比 + 建议（如"请尽快充电"）

### 悬浮窗
- 独立 WPF 窗口（`WindowStyle.None` + `AllowsTransparency`）
- 支持拖动（未锁定时）、锁定、置顶、鼠标穿透、透明度设置
- 位置持久化到 settings.json，下次启动恢复到上次位置
- 首次启动自动放到右上角

## 编译验证

✅ 编译通过（0 错误 0 警告）
✅ 探针测试：本机扫描到 7 台无线设备（4 台 BLE 有电量 + 3 台蓝牙已配对）
- DURGOD K330w：90%
- Logi POP Mouse：80%
- 未知 BLE 设备 1：52%
- 未知 BLE 设备 2：98%
- rk3288（蓝牙音频）：电量未知
- 未知蓝牙设备 1：电量未知
- 未知蓝牙设备 2：电量未知

## 后续工作

- [ ] 用户实际测试（需要连接真实蓝牙设备）
- [ ] 调整悬浮窗 UI 细节（图标、布局、动画）
- [ ] 添加更多设备类型识别（音频/输入/其他）
- [ ] 优化 GATT 会话管理（避免连接泄漏）
- [ ] 添加模块文档（README.md 更新）

## 注意事项

1. **蓝牙电量读取依赖系统**：部分设备可能不暴露电量属性（如某些旧款耳机），此时显示"电量未知"
2. **GATT 读取需要连接**：BLE 设备的 GATT 电量服务需要设备已连接才能读取
3. **PnP 属性有延迟**：设备电量变化后，PnP 属性可能不会立即更新（取决于系统实现）
4. **反作弊兼容性**：本模块只使用 Windows 原生 API，不安装驱动、不注入进程，可放心在游戏后台运行

## 参考实现

- [muedsa/BluetoothLEBatteryMonitor](https://github.com/muedsa/BluetoothLEBatteryMonitor)（MIT，C# WinForms）
- [SpriteOvO/AirPodsDesktop](https://github.com/SpriteOvO/AirPodsDesktop)（C# WPF，AirPods 电量显示）

---

**搭建完成时间**：2026-10-19 10:35 CST
**模块版本**：v1.0.0（初版框架）
**作者**：OpenSquilla Agent
