# 清霜视觉检测系统 V3 (ClearFrost Pro)

> **此分支 (`source-only`) 仅包含核心源代码，不含 SDK 依赖文件，适合学习和二次开发。**

---

## 📦 分支说明

| 分支 | 内容 | 大小 |
|------|------|------|
| `main` | 完整项目（含 SDK 依赖） | ~800MB |
| `source-only` ⬅️ 当前 | **仅源代码** | ~5MB |

---

## ✨ 功能概览

### 🤖 AI 检测模式
- YOLO v8/v11 ONNX 模型推理
- **多模型自动切换**（主模型 + 2个辅助模型）
- DirectML GPU 加速
- 可配置置信度/IOU 阈值

### 🔍 传统视觉模式
- 模板匹配 (Template Match)
- 特征匹配 (AKAZE/ORB)
- 形状匹配 (金字塔梯度)
- 有无检测 (背景差分)

### 🏭 工业集成
- 华睿/海康威视工业相机支持
- PLC 通讯 (Modbus TCP / 三菱 MC / 西门子 S7)
- SQLite 历史记录
- 检测结果统计

### 🎨 现代化界面
- WebView2 嵌入式 Web UI
- TailwindCSS 样式
- 实时检测流水日志
- 今日产出统计图表

---

## 📁 项目结构

```
ClearFrost/
├── Services/               # 核心服务层
│   ├── DetectionService.cs # 检测服务（多模型支持）
│   ├── CameraService.cs    # 相机服务
│   ├── PlcService.cs       # PLC 通讯服务
│   └── StatisticsService.cs# 统计服务
├── Views/                  # 界面逻辑
│   ├── 主窗口.Init.cs      # 初始化
│   ├── 主窗口.Vision.cs    # 视觉检测逻辑
│   ├── 主窗口.PLC.cs       # PLC 交互
│   └── 主窗口.Camera.cs    # 相机控制
├── Core/
│   └── MultiModelManager.cs# 多模型管理器
├── Yolo/
│   └── BW_yolo.cs          # YOLO 推理引擎
├── Vision/                 # 传统视觉算法
│   ├── PipelineProcessor.cs
│   ├── GradientShapeMatcher.cs
│   └── Operators/          # 图像处理算子
├── Hardware/               # 硬件驱动
│   ├── Camera/             # 相机实现
│   └── PLC/                # PLC 适配器
├── html/                   # Web UI 前端
│   ├── index.html
│   ├── js/
│   └── css/
└── Config/                 # 配置管理
    └── AppConfig.cs
```

---

## 🚀 快速开始

### 1. 克隆源码分支
```bash
git clone -b source-only https://gitee.com/jiao-xiake/ClearForst.git
cd ClearForst
```

### 2. 安装 NuGet 依赖
```bash
dotnet restore
```

### 3. 添加必要的 DLL（见下方）

### 4. 编译运行
```bash
dotnet build -c Debug -p:Platform=x64
```

---

## ⚠️ 运行前必读

此分支**不包含**以下文件，需手动准备：

| 文件/目录 | 用途 | 获取方式 |
|-----------|------|----------|
| `ClearFrost/DLL/HslCommunication.dll` | PLC 通讯 | [HSL官网](https://www.hslcommunication.cn/) |
| `ClearFrost/DLL/MVSDK_Net.dll` | 相机 SDK | [华睿官网](https://www.huaray.com/) |
| `ClearFrost/ONNX/*.onnx` | YOLO 模型 | 自行训练或下载 |
| `x64依赖包/` | 相机原生 DLL | 华睿 SDK 安装包 |

---

## 📄 许可证

仅供学习交流使用。

---

**作者**: HerverJun  
**更新时间**: 2026-01-19
