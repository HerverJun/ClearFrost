# 清霜视觉检测系统 V3 (ClearFrost V3)

<p align="center">
  <img src="ClearFrost/icon_transparent.png" width="120" alt="ClearFrost Logo">
</p>

<p align="center">
  <strong>工业级智能视觉检测平台</strong><br>
  基于 C# .NET 8.0 | YOLO AI 检测 | 多模型自动切换 | 现代 Web UI
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8.0">
  <img src="https://img.shields.io/badge/YOLO-v8%2Fv11-00FFFF" alt="YOLO">
  <img src="https://img.shields.io/badge/OpenCV-4.8-5C3EE8?logo=opencv" alt="OpenCV">
  <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6?logo=windows" alt="Windows">
</p>

---

## ✨ V3 新特性

- 🚀 **多模型自动切换**：主模型未检测到目标时，自动切换辅助模型
- 🎨 **全新 Web UI**：基于 TailwindCSS 的现代化界面
- 📊 **实时统计图表**：今日产出环形图 + 历史数据对比
- 🔧 **模块化架构**：Services / Views / Vision 分层设计
- 📷 **多品牌相机支持**：华睿 + 海康威视工业相机
- 🔌 **多协议 PLC 通讯**：Modbus TCP / 三菱 MC / 西门子 S7

---

## 📋 系统要求

| 项目 | 要求 |
|------|------|
| 操作系统 | Windows 10/11 (x64) |
| .NET SDK | 8.0 或更高 |
| 开发工具 | Visual Studio 2022 / VS Code |
| GPU（可选） | 支持 DirectML 的显卡 |

---

## 🚀 快速开始

### 1. 克隆代码

```bash
git clone https://gitee.com/jiao-xiake/ClearForst.git
cd ClearForst
```

### 2. 安装依赖

```bash
dotnet restore
```

### 3. 编译运行

```bash
# 命令行方式
dotnet build -c Debug -p:Platform=x64
dotnet run --project ClearFrost/ClearFrost.csproj

# 或使用 Visual Studio 打开 ClearFrost.sln，按 F5 运行
```

---

## ⚠️ 重要提示

**Git 仓库中只包含源代码，不包含以下大文件：**

| 文件类型 | 说明 | 如何获取 |
|----------|------|----------|
| `DLL/*.dll` | 工业通讯库 (HslCommunication) | 从厂商官网下载 |
| `ONNX/*.onnx` | YOLO 模型文件 | 自行训练或下载预训练模型 |
| `x64依赖包/` | 相机 SDK 依赖 | 从华睿/海康官网下载 |

详细配置步骤请参考下方「依赖配置」章节。

---

## 📁 项目结构

```
ClearForst/
├── ClearFrost/                 # 主项目
│   ├── Services/               # 核心服务
│   │   ├── DetectionService.cs # 检测服务（多模型支持）
│   │   ├── CameraService.cs    # 相机服务
│   │   ├── PlcService.cs       # PLC 通讯服务
│   │   └── StatisticsService.cs# 统计服务
│   ├── Views/                  # 界面逻辑（分模块）
│   │   ├── 主窗口.Init.cs      # 初始化
│   │   ├── 主窗口.Vision.cs    # 视觉检测
│   │   ├── 主窗口.PLC.cs       # PLC 控制
│   │   └── 主窗口.Camera.cs    # 相机控制
│   ├── Core/                   # 核心模块
│   │   └── MultiModelManager.cs# 多模型管理器
│   ├── Yolo/                   # YOLO 推理引擎
│   ├── Vision/                 # 传统视觉算法
│   ├── Hardware/               # 硬件驱动
│   ├── html/                   # Web UI 前端
│   │   ├── index.html
│   │   └── js/
│   └── Config/                 # 配置管理
├── ClearFrost_Lite/            # 简化版（SDK 依赖）
└── README.md
```

---

## 🎯 功能特性

### AI 检测模式
- ✅ YOLO v8/v11 ONNX 模型推理
- ✅ **多模型自动切换**（主模型 + 2个辅助模型）
- ✅ DirectML GPU 加速
- ✅ 可配置置信度/IOU 阈值
- ✅ 目标标签 + 数量判定逻辑

### 传统视觉模式
- ✅ 模板匹配 (Template Match)
- ✅ 特征匹配 (AKAZE/ORB)
- ✅ 形状匹配 (金字塔梯度)
- ✅ 有无检测 (背景差分)
- ✅ 可视化流水线配置

### 工业集成
- ✅ 华睿/海康威视工业相机
- ✅ 多协议 PLC 通讯
- ✅ 检测结果自动写入 PLC
- ✅ 重拍机制（可配置次数和间隔）
- ✅ SQLite 历史记录存储

### 用户界面
- ✅ 现代 Web UI（WebView2）
- ✅ 实时检测流水日志
- ✅ 今日产出统计图表
- ✅ 图像追溯库
- ✅ 密码保护的管理员设置

---

## 🔧 依赖配置

### 必需：工业通讯库

在 `ClearFrost/DLL/` 目录放入：

| 文件 | 用途 | 来源 |
|------|------|------|
| `HslCommunication.dll` | PLC 通讯 | [HSL 官网](https://www.hslcommunication.cn/) |
| `MVSDK_Net.dll` | 相机 SDK | [华睿官网](https://www.huaray.com/) |

### 可选：ONNX 模型

在 `ClearFrost/ONNX/` 目录放入 YOLO 模型：

```bash
# 使用 Ultralytics 导出
yolo export model=yolo11n.pt format=onnx
```

### 可选：相机 SDK 依赖

在 `ClearFrost_Lite/` 目录放入华睿 SDK 的原生 DLL 文件。

---

## 🔍 常见问题

<details>
<summary><strong>Q: 编译时报错缺少 DLL？</strong></summary>

确保 `ClearFrost/DLL/` 目录包含必要的 DLL 文件。如果不使用 PLC/相机功能，可以注释掉相关代码。
</details>

<details>
<summary><strong>Q: 多模型切换不生效？</strong></summary>

1. 确保在界面上勾选了「模型池」开关
2. 检查辅助模型是否正确加载
3. 查看调试日志确认切换逻辑
</details>

<details>
<summary><strong>Q: GPU 加速未启用？</strong></summary>

1. 确保显卡驱动是最新版本
2. 在设置中勾选「启用 GPU 加速 (CUDA)」
3. 确认安装了 DirectML 运行时
</details>

---

## 📧 联系方式

如有问题，请提交 Issue 或联系项目维护者。

---

## 📝 更新日志

### v3.2 (2026-01-19)
- 修复多模型切换时标签显示错误
- 优化今日产出图表位置
- 添加代码分享说明文档

### v3.0 (2026-01-14)
- 全新多模型自动切换架构
- 重构服务层（DetectionService/PlcService）
- 新增目标标签+数量判定逻辑
- 现代化 Web UI 界面

---

**最后更新**: 2026-01-19
