# 清霜视觉检测系统 V5.9 正式版 (ClearFrost)

<p align="center">
  <img src="ClearFrost/icon_transparent.png" width="120" alt="ClearFrost Logo">
</p>

<p align="center">
  <strong>工业级智能视觉检测平台</strong><br>
  C# .NET 8.0 | WinForms + WebView2 | YOLO ONNX 推理 | 多相机 | PLC 联动 | SQLite 追溯
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/YOLO-v8%2Fv11%2Fv26-00FFFF" alt="YOLO">
  <img src="https://img.shields.io/badge/OpenCV-4.8-5C3EE8?logo=opencv" alt="OpenCV">
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows" alt="Windows x64">
  <img src="https://img.shields.io/badge/Release-V5.9-16A34A" alt="ClearFrost V5.9">
</p>

> V5.9 的重点是生产稳定性硬化与检测管线深度解耦：引入了配方快照联动、历史数据原图复判机制，大幅优化了高频检测下的内存与拷贝开销，并增强了硬件断联的自动恢复与全面启动诊断。

---

## 当前版本

| 项目 | 内容 |
|------|------|
| 当前系列 | `V5.9` |
| 默认项目版本 | `5.9.0` |
| 目标框架 | `net8.0-windows10.0.17763.0` |
| 平台 | Windows x64 |
| 主项目 | `ClearFrost/ClearFrost.csproj` |
| 测试项目 | `ClearFrost.Tests/ClearFrost.Tests.csproj` |

发布版本号由发布器传入。窗口标题、exe 文件版本、配置迁移导出的应用版本、WebView2 缓存隔离目录都会自动使用同一套版本信息。

---

## V5.9 核心能力

### 视觉检测与智能判定

- **多尺寸 YOLO 推理**：深度适配 YOLO v8 / v11 / v26 检测、分割与分类架构，支持基于 DirectML 的硬件加速与异常回退。
- **解耦检测管线**：剥离复杂的图像推理与判定逻辑，将核心检测流程管道化，支持精细化的多模型候选评估与判定规则。
- **生产配方快照**：支持检测结果与配方生产参数、ROI 规范化配置的快照级绑定，完美留存每一帧检测时的判定标准。
- **历史记录原图复判**：支持对数据库已存图像进行当前活动规则的动态复判，极大缩短新规则调优的离线评估耗时。

### 工业现场集成

- **多相机智能接入**：支持华睿、海康等主流工业相机，具备全生命周期的相机断线自动重连与取帧容错机制。
- **混合 PLC 通讯**：原生支持 Modbus TCP、三菱 MC、西门子 S7 通讯，强化异常地址解析及非 PLC 触发模式下的连接挂起保护。
- **零拷贝高频处理**：严格控制高频触发场景下的图像内存队列上限，通过复用缓冲区减少图像在渲染、追溯、检测过程中的内存拷贝，规避 OOM 风险。
- **多维度启动诊断**：覆盖 PLC 握手、相机拉流、WebView2 环境等检测链的自检与自动诊断服务。

### 数据与配置

- **SQLite 检测记录存储**：高性能、大吞醒持久化，支持渲染图及原图的分离式追溯。
- **异常原因追溯与回填**：数据库表补充故障诊断建议与 ROI 区域信息，便于精细化审计和产线质量追溯。
- **预设与安全迁移**：预设配置一键应用与参数持久化，支持配置信息跨设备导入导出，降低部署难度。

### 发布与维护

- **统一发布工作流**：支持 Lite（依赖运行时）与 Full（自包含运行时）的高性能 PowerShell 一键发布脚本。
- **全生命周期环境检查**：自动生成 `check_env.bat` 并嵌入版本元数据，支持版本级隔离的 WebView2 用户缓存目录。

---

## 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10/11 x64 |
| 开发 SDK | .NET SDK 8.0 或更高 |
| Lite 运行环境 | .NET 8 Desktop Runtime x64 |
| Full 运行环境 | 自包含包，通常不需要额外安装 .NET Runtime |
| Web UI | Microsoft Edge WebView2 Runtime |
| GPU 可选 | 支持 DirectML 的显卡和驱动 |

---

## 快速开始

### 1. 克隆代码

```bash
git clone https://github.com/HerverJun/ClearFrost.git
cd ClearFrost
```

### 2. 还原依赖

```bash
dotnet restore
```

### 3. 构建

```bash
dotnet build ClearFrost.sln -c Debug -p:Platform=x64
```

### 4. 运行

```bash
dotnet run --project ClearFrost/ClearFrost.csproj -c Debug
```

也可以使用 Visual Studio 2022 打开 `ClearFrost.sln`，选择 `x64` 后运行。

---

## 发布

### 交互式发布

双击或运行：

```bat
脚本\publish.bat
```

按提示选择发布模式：

```text
1. Lite  - framework-dependent, smaller package
2. Full  - self-contained, includes .NET runtime
3. Both  - build both packages
```

版本输入示例：

```text
5.8.7
```

### 命令行发布

```bat
脚本\publish_lite.bat 5.8.7 -Zip
脚本\publish_full.bat 5.8.7 -Zip
```

或直接使用 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\脚本\publish.ps1 -Mode Full -Version 5.8.7 -Zip -OpenOutput
```

输出目录示例：

```text
PublishOutput\ClearFrost_5.8.7_Lite
PublishOutput\ClearFrost_5.8.7_Lite.zip
PublishOutput\ClearFrost_5.8.7_Full
PublishOutput\ClearFrost_5.8.7_Full.zip
```

版本号建议：

| 类型 | 示例 | 说明 |
|------|------|------|
| 当前正式版 | `5.9.0` | 与 `ClearFrost.csproj` `<Version>` 保持一致 |
| 修复版 | `5.9.1` | 小 bug 修复、补丁更新 |
| 功能更新 | `5.9.0` | 新增一批功能但仍在 V5 系列 |
| 大版本 | `6.0.0` | 兼容性或架构级变化 |

---

## 测试

```bash
# 运行所有测试
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj

# 运行单个测试类
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj --filter "FullyQualifiedName~YoloResultTests"

# 详细输出
dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj -v n
```

---

## 项目结构

```text
ClearFrost/
├── ClearFrost/                 # 主项目
│   ├── AppRuntime.cs           # 应用运行时服务装配
│   ├── Config/                 # 配置管理与迁移
│   ├── Core/                   # 规则、模型注册、配方等核心逻辑
│   ├── Hardware/               # 相机、PLC、触发器硬件接口
│   ├── Helpers/                # 运行路径、版本信息、窗口工具等
│   ├── Interfaces/             # 服务接口与 DTO
│   ├── Services/               # 检测、存储、统计、数据库、诊断等服务
│   ├── Views/                  # WinForms 主窗口与 WebView2 控制器
│   ├── Yolo/                   # YOLO 推理与后处理
│   └── html/                   # Web UI 前端资源
├── ClearFrost.Tests/           # xUnit 测试项目
├── tools/                      # 调试、压测、PLC 探测工具
├── 依赖/                       # 本地 SDK 依赖目录
├── 脚本/                       # 发布器和辅助脚本
└── README.md
```

---

## 依赖说明

Git 仓库不提交模型文件和部分本地 DLL。部署或开发前需要准备：

| 路径 | 内容 | 说明 |
|------|------|------|
| `ClearFrost/DLL/` | `MVSDK_Net.dll` 等 | 华睿相机托管 SDK |
| `ClearFrost/ONNX/` | `*.onnx` | YOLO 模型文件 |
| `依赖/x64依赖包/` | 原生相机 SDK DLL | 华睿 SDK 依赖 |
| `依赖/海康依赖包/` | 海康 SDK DLL | 海康相机依赖 |

NuGet 依赖由 `dotnet restore` 自动还原，主要包括：

- `Microsoft.ML.OnnxRuntime.DirectML`
- `OpenCvSharp4.Windows`
- `Microsoft.Web.WebView2`
- `HslCommunication`
- `Microsoft.Data.Sqlite`

---

## 常见问题

### 发布时输入什么版本号？

如果当前是 `5.9.0`，后续修复版输入：

```text
5.9.1
```

如果只是重新打当前版本的包，直接按回车使用默认值即可。

### Lite 和 Full 怎么选？

- `Lite`：包更小，目标机器需要安装 .NET 8 Desktop Runtime x64。
- `Full`：包更大，但自带 .NET 运行时，更适合直接交付现场电脑。
- `Both`：同时生成两种包，适合正式归档。

### 发布后怎么看版本？

发布目录里会生成：

```text
VERSION.txt
```

同时 exe 文件属性里的 `ProductVersion` 和 `FileVersion` 也会更新。

### 缺少 ONNX 模型怎么办？

发布器会给出警告，但不会阻止发布。实际检测前需要把模型放入 `ClearFrost/ONNX/` 或发布目录的 `ONNX/`。

---

## 更新日志

### v5.9 (2026-06-15)

- **管线解耦**：正式抽离检测管线逻辑，支持与配方快照（Recipe Snapshots）联动保存。
- **历史复判**：新增对历史归档图片使用当前运行规则进行本地复判的功能。
- **内存优化**：引入内存队列大小受限的高频图像缓冲，并大幅削减图像在预览与追溯链路中的内存拷贝。
- **强健容错**：重构启动诊断与异常回退方案，添加模型注册表刷新的异常保护以隔离文件占用冲突。
- **UI 精雕**：升级 NG 追溯弹窗并集成缺陷类别、ROI 位置的追溯显示，统一设置面板交互。

### v5.8 (2026-06-05)

- **硬件健壮性**：增强 PLC 通讯在网络颠簸状态下的重连鲁棒性，并在非 PLC 触发模式下挂起连接。
- **算法硬化**：重构 YOLO 契约感知（Contract-aware）后处理框架，修复输出维度元数据处理逻辑。
- **诊断系统**：全新引入启动诊断、在线健康监控（Health Probing）与诊断包导出服务。
- **测试覆盖**：新增 YOLO 推理的 official probe 测试覆盖度报告，以及对 AppRuntime 的单元测试。

### v5.7 (2026-05-22)

- 新增统一发布器，支持发布时指定版本号
- 窗口标题从程序集版本自动生成，避免手工忘改
- 配置迁移导出使用统一应用版本
- WebView2 用户数据目录按版本隔离
- 发布产物自动生成 `VERSION.txt`
- 缺少环境检查脚本时自动生成 `check_env.bat`

### v5.x

- 多相机管理与相机切换
- 模型注册表与模型包追溯字段
- 检测记录 SQLite 存储与图像追溯
- 配置迁移、项目预设、健康诊断
- 面向低配工控机的图像链路与渲染优化

---

## 维护建议

- 发布正式包时固定使用三段版本号，例如 `5.9.0`
- 每次现场交付保留 `PublishOutput` 中的 zip 包 and `VERSION.txt`
- 模型文件、相机 SDK、现场配置不要直接提交到 Git
- 修改发布流程后至少验证一次 Lite 发布 and 一次 Debug x64 构建

---

**最后更新**: 2026-06-15
