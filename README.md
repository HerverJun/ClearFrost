# 清霜视觉检测系统 V6.0.0 正式版 (ClearFrost)

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
  <img src="https://img.shields.io/badge/Release-V6.0.0-16A34A" alt="ClearFrost V6.0.0">
</p>

> V6.0.0 的重点是生产闭环：稳定检测链路、多模型追溯、回放验证审批、配置迁移、可版本化发布，以及更适合产线维护的发布流程。

> V6_test 默认采用“现场轻量模式”，普通产线不强制模型审批证据；严格模型准入和回放验证作为工程师高级维护能力保留。详见 `docs/V6_FIELD_DEPLOYMENT_MODE.md`。

---

## 当前版本

| 项目 | 内容 |
|------|------|
| 当前系列 | `V6.0.0` |
| 默认项目版本 | `6.0.0` |
| 目标框架 | `net8.0-windows10.0.17763.0` |
| 平台 | Windows x64 |
| 主项目 | `ClearFrost/ClearFrost.csproj` |
| 测试项目 | `ClearFrost.Tests/ClearFrost.Tests.csproj` |

发布版本号由发布器传入。窗口标题、exe 文件版本、配置迁移导出的应用版本、WebView2 缓存隔离目录都会自动使用同一套版本信息。

---

## V6.0.0 重点能力

### 视觉检测

- YOLO ONNX 推理，支持 DirectML GPU 加速
- 兼容 YOLO v8 / v11 / v26 相关输出结构
- 主模型 + 辅助模型的多模型切换
- 目标标签、数量、置信度、IOU 阈值可配置
- 检测记录写入模型名称、模型版本、模型哈希等追溯字段

### 工业现场集成

- 华睿、海康威视工业相机接入
- Modbus TCP、三菱 MC、S7 等 PLC 通讯场景
- 支持 PLC 触发、串口光电触发、手动检测
- 检测结果自动写入 PLC
- 重拍机制、健康检查、启动诊断

### 数据与配置

- SQLite 检测记录存储
- 图像追溯、渲染图追溯、历史查询
- 项目预设管理
- 配置迁移导入/导出
- 运行时配置写入用户目录，减少发布目录权限问题

### 发布与维护

- 新增统一发布器 `脚本/publish.ps1`
- 支持 Lite / Full / Both 三种发布模式
- 发布时输入版本号，例如 `6.0.0`
- 自动生成版本化输出目录和 `VERSION.txt`
- 可选生成 zip 包
- 缺少根目录 `check_env.bat` 时自动生成环境检查脚本

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
6.0.0
```

### 命令行发布

```bat
脚本\publish_lite.bat 6.0.0 -Zip
脚本\publish_full.bat 6.0.0 -Zip
```

或直接使用 PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\脚本\publish.ps1 -Mode Full -Version 6.0.0 -Zip -OpenOutput
```

输出目录示例：

```text
PublishOutput\ClearFrost_6.0.0_Lite
PublishOutput\ClearFrost_6.0.0_Lite.zip
PublishOutput\ClearFrost_6.0.0_Full
PublishOutput\ClearFrost_6.0.0_Full.zip
```

版本号建议：

| 类型 | 示例 | 说明 |
|------|------|------|
| 当前正式版 | `6.0.0` | V6.0.0 初始正式包 |
| 修复版 | `6.0.1` | 小 bug 修复、补丁更新 |
| 功能更新 | `6.1.0` | 新增一批功能但仍在 V6 系列 |
| 大版本 | `7.0.0` | 兼容性或架构级变化 |

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

如果当前是 `6.0.0`，后续修复版输入：

```text
6.0.1
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

### v6.0.0 (2026-07-04)

- 新增统一发布器，支持发布时指定版本号
- 窗口标题从程序集版本自动生成，避免手工忘改
- 配置迁移导出使用统一应用版本
- WebView2 用户数据目录按版本隔离
- 增加生产模型上线管控、回放验证审批和 PLC 握手闭环
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

- 发布正式包时固定使用三段版本号，例如 `6.0.0`
- 每次现场交付保留 `PublishOutput` 中的 zip 包和 `VERSION.txt`
- 模型文件、相机 SDK、现场配置不要直接提交到 Git
- 修改发布流程后至少验证一次 Lite 发布和一次 Debug x64 构建

---

**最后更新**: 2026-07-04
