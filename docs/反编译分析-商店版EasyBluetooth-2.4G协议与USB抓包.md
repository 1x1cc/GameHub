# 商店版 EasyBluetooth 3.5.8 反编译分析
## —— 插件市场的 2.4G 协议是怎么做的、电量怎么读、USB 抓包辅助工具的原理

> 分析日期：2026-10-24
> 分析对象：`C:\Program Files\WindowsApps\xhx.EasyBluetooth_3.5.8.0_x64__3nw8y1963rkpj`
> 目的：搞清参考实现（商店版）的 2.4G 电量读取机制与配套抓包工具原理，用于对照/改进 GameHub 的 EasyBluetooth 模块。

---

## 0. 结论速览（TL;DR）

1. **它的「插件市场」不是下载代码，而是开关内置的品牌插件。** 安装包里内置 64 个插件（`rog-2.4g-plugin`、`razer-2.4g-plugin`、`logitech-hidpp-plugin`…），每个插件 = 一个品牌 + 一张 PID/通道/字节偏移表。用户在插件市场里勾选启用，**2.4G 插件属于付费加购（24G addon）**，所以本机日志里 64 个插件全是 `Skipped (disabled)`。
2. **读 2.4G 电量的统一模型只有三步**：找到该品牌 VID/PID 的 HID 集合 → 发一条 3 字节只读查询 `[reportId, 0x12, 子命令]` → 从回复里按**固定偏移**取电量/充电位。没有任何驱动、没有任何内核态东西。
3. **回复只有三种结果**：正常电量、`[1]=0xFF [2]=0xAA`（接收器代答「设备无应答」）、`[1]=[2]=[3]=0`（无效）。判定设备在不在线，只看**转发通道有没有正经应答**。
4. **关键实现细节（GameHub 目前没做的一点）**：它给每台设备**枚举出所有候选 HID 集合并打分排序**，然后**逐个试探**直到有一个集合答上来。GameHub 目前是「PID → 固定一条通道」，通道猜错就整台设备读不到。
5. **USB 抓包辅助工具 = USBPcapCmd 的外壳 + pcapng 实时解析器**：枚举 USBPcap 接口 → 以管理员身份拉起 `USBPcapCmd.exe`（命令行见 §4.2）→ 让 USBPcap 把报文直接写进**命名管道**，程序一边落盘 `.pcapng` 一边增量解析。它的定位是：**抓官方驱动和 2.4G 接收器之间的 HID 报文，反向出上面那张协议表**。
6. 本机状态：**USBPcap 没有安装**（`C:\Program Files\USBPcap\USBPcapCmd.exe` 不存在，服务也不在），所以抓包辅助窗口在本机只能显示 `ready=False`。

---

## 1. 分析方法与证据来源

| 项目 | 内容 |
|---|---|
| 反编译工具 | ILSpy 命令行版 `ilspycmd 9.1.0.7988`（本机已有），全程序集反编译 → 320,600 行 C# |
| 关键发现 | 程序集保留了**原始源码路径**：`F:\Desktop\项目\蓝牙代码\EasyBluetooth\EasyBluetooth\...`（Logger 调用里带文件名+行号） |
| 字符串混淆 | 字符串/部分类名被加密（形如 `F2B27C5F-F3A7-452C-8041-9368254D42BA.acu()` 的静态取串调用），**但协议常量、字节数组、PID 表、偏移量全是明文**，所以协议能 100% 还原 |
| 运行日志 | `%LOCALAPPDATA%\Packages\xhx.EasyBluetooth_3nw8y1963rkpj\LocalState\Logs\app_20261024.log`、`app_20261020.log` |
| 状态数据 | 同目录 `LocalState\BatteryHistory\*.json`（电量历史，key 如 `RAZER_1532_00B7`） |
| 排除项 | 安装目录里的 `D4XGaming.Devices.dll/.winmd` 是第三方 C++/WinRT 原生组件（PDB：`C:\Users\DC\Desktop\D4XDeviceSample\bin\x64\Release\D4XDevice\D4XGaming.Devices.pdb`），只有一个 `D4XDevice` 类，**与 2.4G 电量协议无关** |

> 反编译产物（临时目录，可随时重建）：
> `%TEMP%\eb_re\src\EasyBluetooth.decompiled.cs`、`%TEMP%\eb_re\types.txt`

---

## 2. 插件市场（PluginMarketplace）到底是什么

### 2.1 插件契约（反混淆后的类型名）

```csharp
namespace C {
    public interface P {              // 插件
        Q Metadata { get; }           // 元数据
        bool IsEnabled { get; set; }
        IBatteryProvider A();         // 该品牌的电量提供者
    }
}
```

元数据类型 `Q` 的字段（全部实证）：

| 字段 | 含义 | ROG 插件取值 |
|---|---|---|
| `Id` | 插件 ID，与日志/市场快照一致 | `rog-2.4g-plugin` |
| `BrandName` | 品牌名（中文/英文） | ASUS |
| `Version` | 插件版本 | 1.0.0 |
| `Is24GPlugin` | 是否 2.4G 插件 | `true` |
| `Requires24GAddon` | 是否必须买 24G 加购 | — |
| `IsAddonRequired` | 派生：`Is24GPlugin ? true : Requires24GAddon` | `true` |
| `DeviceSource` | 枚举 `DeviceSource`（Rog/Razer/Logitech/Eweadn/WLMouse…，共 60+ 项） | `Rog` |
| `RequiresSystemBluetooth` | 是否依赖系统蓝牙栈 | — |
| `RequiresAuthorization` | 是否需要登录授权 | `false` |
| `Description` / `DescriptionEn` | 市场里的说明文案 | — |
| `SupportedDevices` | 支持型号名数组 | 36 项 |
| `SearchKeywords` | 市场搜索关键词 | 2 项 |
| `Author` | 作者 | — |

### 2.2 插件清单：64 个，几乎全是 `*-2.4g-plugin`

插件市场日志（`app_20261024.log:668`）里的完整快照，共 **64** 个：

```
8bitdo, acer, ajazz, angrymiao, antgamer, rog, atk-hid, audeze, aula, betop, bloody,
bose-bluetooth, cherry, coolermaster, corsair, dareu, darmoshark, delux, edifier, epos,
eweadn, finalmouse, flesports, flydigi, fractal, g-wolves, gamesir, ganss, glorious, hp,
hyperx, incott, inphic, jbl, jingzao, lamzu, lenovo, leobog, logitech-hidpp, lunafury,
mchose, mision, ninjutso, pmo, pulsar, rapoo, razer, redragon, roccat, royalaxe, sony,
steam, steelseries, togar, turtlebeach, valkyrie, vaxee, wanling-jingji, xiaomi, xinmeng,
rawm, langtu, scyrox, maono
```

命名规律：`<品牌>-2.4g-plugin` 表示走私有 2.4G/HID 协议；特例 `logitech-hidpp-plugin`（罗技 HID++）、`atk-hid-plugin`、`bose-bluetooth-plugin`。

### 2.3 为什么本机 64 个插件「全部被禁用」

`app_20261024.log:92` 起，连续 64 行：

```
[INFO] [Plugin] Skipped (disabled): ASUS
[INFO] [Plugin] Skipped (disabled): Logitech / 罗技
...
```

同一次启动里还有：

```
[StoreEntitlement] GetAppLicenseAsync ok, appActive=True, addOns=0, addOnKeys=(empty)
[StoreEntitlement] Fallback GetUserCollectionAsync: legacyVipOwned=False, legacyAddonOwned=False, proOwned=False, products=0
```

代码侧对应关系：
- 插件市场 UI 的开关 → `PluginMarketplaceViewModel.SetPluginEnabledAsync(pluginId, enabled)` → `BatteryMonitorService.A(pluginId, enabled)`（真的去增删运行时插件实例）；
- 加购商品 ID 来自设置：`StoreVipProductId` / `StoreAddon24GProductId` / `StoreProProductId`；
- 会员对比表里也有 `IncludedInAddon24G` 字段。

**结论：2.4G 插件是「内置代码 + 付费解锁」。** 商店版本身完全具备读 ROG 键盘电量的能力，但你没买 24G 加购，它就整条分支不加载（`Skipped (disabled)`），而不是「读不到」。

---

## 3. 2.4G 电量到底怎么读（核心章节）

### 3.1 统一模型三步走

```
① 按 VID/PID 找 HID 集合（可能一台设备有 6~10 个集合，只有 1 个会答）
       ↓
② 发 3 字节查询：[reportId, 0x12, 子命令]
       键盘 0x01 / 鼠标 0x07 / 接收器 0x00·0x08·0x12
       ↓
③ 读回复，校验 [1]==0x12 && [2]==子命令，然后按固定偏移取数
```

### 3.2 底层会话实现（类 `c.L`，行 126186 起）

**打开设备（HidSharp）**

```csharp
var cfg = new OpenConfiguration();
cfg.SetOption(OpenOption.Interruptible, true);   // 可被中断，不阻塞
cfg.SetOption(OpenOption.Exclusive,  false);     // 共享打开：官方驱动同时持有也能读
cfg.SetOption(OpenOption.Priority,   10);
stream = device.Open(cfg);
stream.ReadTimeout  = 300;                       // ms
stream.WriteTimeout = 300;
```

**收发与重试**

```csharp
foreach (byte[] query in protocol.QueryPackets) {      // 可有多条候选查询
    int attempts = 3;                                   // 超时重试 3 次
    while (attempts > 0) {
        byte[] buf = new byte[protocol.PacketSize];     // 64 或 65
        stream.Write(query);                            // 3 字节：[rid,0x12,sub]
        stream.Read(buf);                               // 读应答
        if (IsNoAnswer(buf)) return buf;                // 0xFF 0xAA
        if (IsAllZeroInvalid(buf)) return null;         // [1]=[2]=[3]=0
        if (Matches(buf, query)) return buf;            // [1]==0x12 && [2]==sub
        for (int i = 0; i < 5; i++) {                   // 首次没对上，最多再补读 5 次
            stream.Read(buf);
            if (Matches(buf, query)) return buf;
        }
    }
}
```

**应答判定（类 `c.q`，明文常量）**

```csharp
// 「接收器在，但设备没应答」
[1]==0xFF && [2]==0xAA                       → 无应答（设备休眠/关机/没连上）
[0]==0xCC && [1]==0x12 && [5]==0xFF && [6]==0xAA → 无应答（0xCC 系列设备）
// 「无效回复」
[1]==0 && [2]==0 && [3]==0                   → 当没读到
// 「命中」
([0]==查询用的reportId || [0]==0) && [1]==0x12 && [2]==子命令
// 0xCC 系列（接收器自答/底座）
[0]==0xCC && [1]==0x12 && [2]==子命令
```

### 3.3 协议描述符（类 `c.P`）字段含义——这就是「协议怎么搞的」的答案

```csharp
P(Kind,               // 通道种类：A=鼠标  a=键盘  B=0xCC 系列
  QueryPackets,       // 要发的查询包（可多条，逐条试）
  PacketSize,         // 期望回复长度（64 或 65）
  SubCommand,         // 0x12 之后那个字节
  BatteryLevelOffset, // 电量百分比下标
  ChargingStateOffset,// 充电状态下标
  AlternateBatteryLevelOffsetWhenReportIdZero, // 回复首字节为 0 时的替代电量下标
  RightBatteryLevelOffset,                     // 双电池（键盘左右/耳机左右）
  CaseBatteryLevelOffset,                      // 第三电池（充电盒）
  QueryHeadsetChargingState,                   // 充电状态要另发一条 0x12/0x08 查
  DeviceTypeName)                              // 展示用设备类型名
```

**所以「支持一款新 2.4G 设备」= 填这么一行描述符 + 一个 PID。** 这就是插件市场里每个品牌插件的内容量级。

### 3.4 华硕 ROG 协议完整还原（类 `c.q`，行 127625 起，全部明文）

常量：

```csharp
ROG_VID              = 2821  (0x0B05)
接收器专用 PID        = 7340  (0x1CAC)
CMD                  = 0x12  // 所有查询都是 0x12
SUB_KEYBOARD         = 0x01
SUB_MOUSE            = 0x07
SUB_MOUSE_OMNI       = 0x12  // 0x1CAC 接收器的多电池查询
SUB_0x08             = 0x08  // 充电状态查询
REPORT_ID_WIRED      = 0xCC  // 204
NO_ANSWER_A          = 0xFF
NO_ANSWER_B          = 0xAA
```

四种协议描述符（原文照抄，数字已还原）：

| 场景 | 查询包 | PacketSize | 子命令 | 电量 | 充电 | 备注 |
|---|---|---|---|---|---|---|
| **无线键盘**（含 OMNI 转发） | `[rid, 0x12, 0x01]` | rid∈{2,3} → 64，否则 65 | `0x01` | **[6]** | **[9]** (`>0`) | 回复首字节为 0 时电量改取 **[11]** |
| 无线鼠标 | `[rid, 0x12, 0x07]` | 同上 | `0x07` | **[5]** | **[10]** | 变体布局：电量 **[4]**、充电 **[9]** |
| 有线/自答（非 0x1CAC） | `[0xCC,0x12,0x07]`、`[0x00,0x12,0x07]` | 64 | `0x07` | **[6]** | **[5]**（另发 `0x12/0x08` 确认） | 两条查询逐条试 |
| 接收器 0x1CAC | `[0xCC, 0x12, 0x12]` | 64 | `0x12` | 左 **[5]**、右 **[6]**、盒 **[7]** | 取左右较小值 | 多电池上报 |

配套解析函数（原文）：

```csharp
// 校验 0xCC/0x12/0x12 三电池回复
if (p[0]==0xCC && p[1]==0x12 && p[2]==0x12) { Left=p[5]; Right=p[6]; Case=p[7]; }
// 回复长度下限 = 用到的最大下标 + 1
```

**设备表（明文数字）**

| 类别 | 数量 | reportId | 说明 |
|---|---|---|---|
| 无线鼠标 PID | **73** 个 | `3` | 通道大多带路径特征 `mi_02&col03` 一类 |
| 无线键盘 PID | **76** 个 | `2` | 无固定路径，靠候选集合打分挑选 |
| 底座/自答 PID | 3 个：`0x1AFA`、`0x1B84`、`0x1CAC` | `0xCC (204)` | `0x1CAC` 走多电池布局 |
| **OMNI 多设备接收器** | **2** 个：`0x1ACE`、`0x1AD0` | 由集合路径决定 | 键盘通道 → rid `2` + 键盘协议；鼠标通道 → rid `3` + 鼠标协议 |

> 注意：接收器 PID 是**两个**（`0x1ACE` 和 `0x1AD0`），且这两个 PID 会被从「普通鼠标表」里**排除**，单独走接收器分支。

其余 PID 完整清单见 **附录 A**。

### 3.5 其他品牌证明确实是同一套机制（几例明文常量）

| 品牌/文件 | 明文协议常量（原文） |
|---|---|
| WLMouse | `QueryPrefix = {0,0,0,2,2,0,131}`、`B = "wlmouse-mini"`、`PacketSize=64` |
| 某品牌（行 107076） | `QueryPrefix={5,1,22}`、`QueryResponseHeader={5,1,22,3,1}`、`KeyboardQueryAndResponseHeader={4,32,0,26,6}` |
| 某品牌（行 146572） | `ActivationPayloadPrefix={0,1,135,4}`、`ResponseHeader={1,8,129,1}` |
| 某品牌（行 156340） | `BatteryQueryPrefix={196,9,0,0,4,66,0,237,254}`、`ChargingQueryPrefix={196,9,0,0,4,73,0,230,254}` |
| 某品牌（行 196494） | `QueryPrefix={3,34,80,0,2,79,129}`、`ResponseHeader={3,80,0,11}` |
| 某品牌（行 203445） | `M900ProReportHeader={3,61,64}`、`M800ProQueryPrefix={12,1,32,0}` |
| 某品牌（串口方案） | 走 `SerialPort` 460800-8-N-1，`DtrEnable/RtsEnable=true`，读 3000ms 窗口 |

结论：**「2.4G 协议」在它这里就是一个 (VID, PID, 通道, report id, 查询字节, 偏移) 元组表**，没有任何通用魔法。新品牌就是逆向出这几个数字，然后写进插件。

### 3.6 为什么「候选集合打分 + 逐个试探」是关键设计

同一台接收器在 Windows 上会暴露很多 HID 集合（`mi_00`/`mi_01`/`mi_02` 下的 `col01..col0A`），**只有其中一两个会答**。参考实现的做法（行 126079 起）：

```
score = 0
+1000  与「上次成功用过的设备路径」相同
+ 300  设备类型（键盘/鼠标）与已知信息一致
+ 150  PID 一致
+  80  reportId 一致
+ 200  路径里含该型号登记的路径特征（如 mi_02&col02 / mi_02&col03）
+ 120  键盘类型 且 路径含某特征
+  40  鼠标类型 且 路径不含该特征
+  20  有输出报告长度
+  10  有输入报告长度
→ 按分数降序，逐个 open+发查询+读回复，第一个答上来的就采用，并记住这条路径
```

这解释了「为什么它能认出一堆型号、而硬编码通道的实现容易翻车」。

---

## 4. USB 抓包辅助工具的工作原理

程序里有两个辅助窗口：`UsbCaptureHelperWindow`（通用 USB 抓包）和 `BluetoothCaptureHelperWindow`（蓝牙专用抓包）。

### 4.1 依赖：USBPcap（外置，需用户自己装）

```csharp
private const string DefaultUsbPcapPath = @"C:\Program Files\USBPcap\USBPcapCmd.exe";
// 找不到就去 PATH 里按名字搜；还找不到 → 环境未就绪
EnvironmentStatusText = "Checking USBPcapCmd.exe...";
// 环境检查结果（本机实测）：
//   [BluetoothCaptureHelperWindow] USBPcap environment check completed: ready=False, usbPcapPath=<empty>
```

**本机没有安装 USBPcap**，两个窗口目前都只能停在「环境未就绪」。

### 4.2 启动抓包：命令行 + UAC

```csharp
new ProcessStartInfo {
    FileName         = resolvedUsbPcapPath,       // USBPcapCmd.exe
    Arguments        = BuildCaptureArguments(),
    UseShellExecute  = true,
    Verb             = "runas",                   // ← 提权（UAC）
    WindowStyle      = ProcessWindowStyle.Minimized
};

// BuildCaptureArguments() 原文拼接结果：
"-d " + interfaceName
     + " --capture-from-all-devices --inject-descriptors -b 4096 -o " + outputPath
//  -d 指定 USBPcap 接口（USBPcap1/2/3）
//  --capture-from-all-devices  抓该根集线器下所有设备
//  --inject-descriptors        把设备描述符一并注入抓包
//  -b 4096                     缓冲
//  -o 输出：正常是 .pcapng 文件；启用实时模式时是一个命名管道路径
```

界面提示文字（原文，说明它的使用姿势）：

```
1) 完全退出官方驱动
2) 选择 USBPcap1/2/3 并开始抓包
3) 打开官方驱动，直到能看到电量
4) 停止抓包
```

也就是说：**抓包工具的作用是让用户自己造一份「官方软件读电量」的报文样本，作为逆向协议的一手材料。**

### 4.3 实时管道：USBPcap 直接写命名管道

```csharp
// 会话（类 b.x）
PipeName = "…" + Guid.NewGuid()            // 每次新 GUID
PipePath = @"\\.\pipe\" + PipeName         // 交给 USBPcapCmd -o
// 服务端：
new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                          PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
await pipe.WaitForConnectionAsync();       // 等 USBPcapCmd 连上来（2 秒超时）
// 连上后：一边把字节写进 .pcapng 文件，一边喂给增量解析器
while (read > 0) { file.Write(span); parser.Feed(span) → List<Packet>; PacketsRead?.Invoke(...); }
```

- 管道连不上（例如 USBPcap 版本不支持写管道）→ **自动回退到文件轮询**：定时检查 `.pcapng` 文件大小变化，解析新增部分。
- 抓包输出目录：桌面（若路径是纯 ASCII），否则退回 `…\BluetoothCapture`（代码明确规避中文路径，说明它踩过 USBPcap 处理非 ASCII 路径的坑）。

### 4.4 报文解析（pcapng 增量解析器）

解析出的包对象字段（原文）：`Number`（包序号）、`Timestamp`、`RelativeSeconds`（相对首包秒数）、`CapturedLength`、`OriginalLength`、`PacketData`（原始字节）。

界面上实时显示的就是：包计数、最近一个包的时间/长度/数据，以及把 USB 事务翻译成「请求 → 响应」的实时事件列表。**用户看到的是官方软件发给接收器的 HID 报文原文**，把这些字节和上面 §3.4 的表对照，就能反推出新设备的 `(reportId, 0x12, sub)` 与偏移。

### 4.5 接口枚举：借 USBPcapCmd 的 extcap 模式

```csharp
// 列出接口/设备（UsbCaptureHelperWindow 的「设备快照」）
ExecuteReadProcess(resolvedUsbPcapPath,
    "--extcap-interface \"" + interfaceName + "\" --extcap-config");
// 用正则解析输出：interface\s*\{value=(.*?)\}(?:\{display=(.*?)\})?
```

`BluetoothCaptureHelperWindow` 会据此标注「哪个接口像蓝牙控制器」，并提示要先选 `USBPcap1/2/3`。

---

## 5. 与 GameHub 现状的对照

| 维度 | 商店版（参考） | GameHub 现状 | 差距/风险 |
|---|---|---|---|
| 架构 | 插件市场 + 每品牌一个插件（64 个），2.4G 付费加购 | 单文件 `RogBatteryReader.cs` 等硬编码读取器 | 架构差异，不影响功能 |
| 设备识别 | 键盘 76 PID / 鼠标 73 PID / 底座 3 PID 表 | 表内 40 余型号 | **未收录 PID 无任何兜底** |
| 通道选择 | 枚举**所有** HID 集合 → 打分排序 → 逐个试探 | 每型号**固定一条**通道（mi_1 / 0xFF00 / col02 / col03） | **通道猜错就整台读不到**（最大嫌疑） |
| OMNI 接收器 PID | `0x1ACE` **和** `0x1AD0` | 只有 `0x1ACE` | `0x1AD0` 会被当成普通设备处理 |
| 键盘查询 report id | 无线键盘一律 rid=`2`，回复首字节为 0 时电量改取 `[11]` | 直连走 rid=`0`（`00 12 01`）；OMNI 走 rid=`2` | **无线自带接收器的键盘若按 rid 2 应答，GameHub 发 rid 0 会超时** |
| 充电判定 | `回复[9] > 0` | `回复[9] == 1` | 若设备回 `0x02` 等值会被判成未充电 |
| 打开方式 | `Exclusive=false`、`Interruptible=true`、300ms 超时、超时重试 3 次 + 补读 5 次 | 同样用 overlapped + 共享打开（`HidNative`），500ms | 基本对等 |
| 无应答判定 | `FF AA` / `CC 12 FF AA` / `00 00 00` | `FF AA` | 少了两种无效判定 |
| 抓包工具 | 内置 USBPcap 封装（管道 + pcapng 实时解析） | `Tools\HidCapture`（自研 HID 抓取） | 定位不同，可互补 |

---

## 6. 「GameHub 读不到 ROG 键盘电量」的根因排序（待实测确认）

> 说明：写这份文档时**本机没有插 ROG 设备**（`Get-PnpDevice` 里没有任何 `VID_0B05` 设备），所以下面是按代码差异给出的**排序假设**，每条都附验证方法。

1. **通道假设错位（最可能）**：GameHub 对「自带 2.4G 接收器的键盘」固定在 `mi_01` 上发 `00 12 01`。参考实现里无线键盘是 **rid=`2` 且不限定接口**（靠打分挑集合）。若你的键盘/接收器只在别的集合（如 `mi_02` 的 `0xFF00`）用 rid 2 应答，GameHub 会一直等到超时。
   *验证*：插好接收器后看 `%TEMP%\gamehub.log` 里有没有 `EasyBluetooth/ROG：` 的回复十六进制行；没有就说明包没发对通道。
2. **PID 不在表里**：GameHub 的 `QueryDirectDevices` 只处理 `Models` 里 `Pid == pid` 的型号，**未收录 PID 完全没有兜底**（OMNI 分支有「未知型号试探」，直连分支没有）。
   *验证*：`Get-PnpDevice -PresentOnly | ? InstanceId -match 'VID_0B05'` 看 PID，对照 `Utils\RogBatteryReader.cs` 的 `CreateModels()`。
3. **接收器 PID 只认一个**：只把 `0x1ACE` 当 OMNI 接收器；参考实现是 `{0x1ACE, 0x1AD0}`。若你的是 `0x1AD0`，会掉进「直连设备」分支且 PID 不在表里 → 静默失败。
4. **`0x1ACE` 的键盘/鼠标通道判定**：GameHub 固定 `col02=键盘`、`col03=鼠标`。参考实现是用路径特征判定的（实测结论一致），但如果某固件把这俩顺序换了，也会失败。
5. **充电位判定过严**：`reply[9] == 1` vs 参考的 `> 0`。
6. **报告长度门槛**：GameHub 要求 `InputReportByteLength >= 12` 且 `OutputReportByteLength >= 8`，比参考实现的 64/65 字节假设更严，但方向上是安全的（不太可能是主因）。

---

## 7. 建议的改法（按性价比排序）

1. **给直连分支补「未知 PID 兜底探测」**（收益最大，改动小）
   对任何 `VID_0B05` 的设备，如果 PID 不在表里：枚举它**所有** HID 集合，按下面顺序只读试探，第一次成功就采用并缓存路径：
   ```
   集合按 打分（有输出长度 +20 / 有输入长度 +10 / 路径含 col0x +200）降序
   ├─ rid=2, 0x12/0x01（键盘）→ 电量[6]、充电[9]、[6]<=10 时电量取[11]
   ├─ rid=3, 0x12/0x07（鼠标）→ 电量[5]、充电[10]
   ├─ rid=0, 0x12/0x01 与 0x12/0x07（无编号报告，65 字节）
   └─ rid=0xCC, 0x12/0x07（0xCC 系列）
   跳过：FF AA、CC 12 FF AA、00 00 00、超时
   ```
2. **把 `0x1AD0` 加进 OMNI 接收器判定**（一行）。
3. **充电判定放宽为 `> 0`**（一行）。
4. **补上参考实现那套候选打分**：把「型号 → 固定通道」改成「型号 → 候选通道列表 + 打分」，并在成功时把路径缓存下来（下次直接命中，避免每轮都试探）。
5. **把参考实现的 PID 表并入型号表**：附录 A 的 73/76/3 个 PID 可以直接当「已知 ROG 2.4G 设备」全集用（型号名需要自己补；表里名字在商店版是加密的）。
6. **想彻底不靠猜**：用 `Tools\HidCapture` 或装一个 USBPcap（`-d USBPcapx --capture-from-all-devices --inject-descriptors -b 4096 -o dump.pcapng`），一边开官方软件读电量一边抓，拿到真实 `[rid,0x12,sub]` 和回复，再照 §3.3 那张表填一行——这就是商店版做每个插件的过程。

---

## 附录 A：参考实现里的 ROG PID 全表（明文提取）

**A.1 无线鼠标型（73 个，reportId = 3）**

```
0x17E0 0x1807 0x1822 0x1837 0x183D 0x1845 0x1846 0x1847 0x184A 0x1854 0x185B 0x1866
0x1869 0x1877 0x1898 0x18A0 0x18B4 0x18C6 0x18CD 0x18E1 0x18E3 0x18E5 0x1906 0x1908
0x1910 0x1947 0x1949 0x1958 0x195C 0x195E 0x1960 0x1977 0x1979 0x197B 0x197D 0x197F
0x19B6 0x19F4 0x1A00 0x1A01 0x1A02 0x1A03 0x1A18 0x1A1A 0x1A30 0x1A57 0x1A59 0x1A66
0x1A68 0x1A70 0x1A72 0x1A88 0x1A8D 0x1A92 0x1A94 0x1A9B 0x1ABE 0x1AF3 0x1AF5 0x1B0A
0x1B0C 0x1B16 0x1B22 0x1B23 0x1B2C 0x1B4C 0x1B63 0x1B67 0x1B6E 0x1C0C 0x1C5A 0x8502
0x8854
```

**A.2 键盘型（76 个，reportId = 2）**

```
0x1A87 0x1A85 0x1A86 0x1B84 0x1B82 0x1B83 0x1B81 0x1A88 0x1A89 0x1AA8 0x1AA6 0x1AA7
0x1A85 0x1A83 0x184D 0x196B 0x193C 0x193E 0x1875 0x18AF 0x19FE 0x19FC 0x18CF 0x18F8
0x1AB3 0x1B78 0x1AAE 0x1AB5 0x19F8 0x19F6 0x1951 0x1A07 0x1B12 0x1A05 0x190C 0x1954
0x1835 0x1945 0x194B 0x1B30 0x1C5E 0x1899 0x18AA 0x1866 0x1864 0x1865 0x1906 0x1904
0x1905 0x18A8 0x18A6 0x18A7 0x1A44 0x1A42 0x1A43 0x1A41 0x1B74 0x1B72 0x1B73 0x1B71
0x1843 0x1841 0x1842 0x1840 0x1914 0x1912 0x1913 0x1911 0x1A30 0x1A2E 0x1A2F 0x1A2D
0x1AA0 0x1A9E 0x1A9F 0x1A9D
```

**A.3 底座/自答型（3 个，报告首字节 0xCC）**

```
0x1AFA (6906)   0x1B84 (7044)   0x1CAC (7340，多电池左/右/盒布局)
```

**A.4 OMNI 多设备接收器（2 个）**

```
0x1ACE (6862)   0x1AD0 (6864)
```

---

## 附录 B：证据索引（可复现）

| 结论 | 证据位置 |
|---|---|
| 64 个插件、全部被禁用 | `app_20261024.log:92-183`、`app_20261024.log:668` |
| 无 24G 加购权益 | `app_20261024.log:34-36`（`addOns=0`、`addonOwned=False`） |
| USBPcap 未安装 | `app_20261020.log:419`（`ready=False, usbPcapPath=<empty>`） |
| 抓包命令原文 | 反编译 `BuildCaptureArguments()`（对应源码行 `UsbCaptureHelperWindow.xaml.cs`） |
| 抓包流程提示 | `UsbCaptureHelperWindow.CaptureHintFlow.*` 资源串 |
| ROG 协议常量 | 反编译 `namespace C → internal static class q`（行 127625-127853） |
| ROG PID 表 | 反编译 `internal static class n`（行 126729-126931）、`internal static class O`（126932） |
| 会话/重试逻辑 | 反编译 `internal sealed class L`（行 126186-126520） |
| 候选打分 | 反编译 `private static int A(B, string?, c.N?)`（行 126079-126124） |
| 插件元数据 | 反编译 `namespace C → public sealed class Q`（行 103478-103712） |
| ROG 插件定义 | 反编译 `namespace h → public sealed class o : C.P`（行 127033-127113） |

---

## 附录 C：本机当前实测状态（2026-10-24 13:5x）

| 检查项 | 结果 |
|---|---|
| 当前是否有 ROG（`VID_0B05`）设备 | **无**。`Get-PnpDevice -PresentOnly` 里没有任何 `VID_0B05` 节点，因此本次无法对 ROG 键盘做在线实测 |
| 当前有的 2.4G 复合 HID 设备 | `USB\VID_24AE&PID_1462`：`MI_00`=HID 鼠标、`MI_01` 下 `COL01~COL0A` 共 10 个集合（含多个「符合 HID 标准的供应商定义设备」）、`MI_02`=厂商定义集合 |
| GameHub 支持的 VID | 只有 4 个：`0x0B05`(ROG)、`0x1532`(雷蛇)、`0x36A7`(WLMouse)、`0x372E`(前行者) → **`0x24AE` 这台设备在 GameHub 里完全不会出现**（这是「读不到其他 2.4G 设备电量」的一个现成实例） |
| USBPcap | 未安装（`C:\Program Files\USBPcap\USBPcapCmd.exe` 不存在），商店版抓包辅助窗口 `ready=False` |
| 商店版能否读 ROG | 不能——ROG 插件被禁用（无 24G 加购权益），日志里是 `Skipped (disabled): ASUS` |

> 结论：商店版目前也读不到你的 ROG 键盘电量（因为插件没解锁）；能读的只有免费插件覆盖的品牌（本机历史数据里只出现 `RAZER_1532_00B7`）。

---

*本文档由 GameHub 反编译分析任务生成，用于指导 EasyBluetooth 模块的 2.4G 设备兼容性改进。*
