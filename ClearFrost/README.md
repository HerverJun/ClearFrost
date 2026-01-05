# 清霜 Pro 视觉检测系统

**Gree ClearFrost Vision Pro v1.0**

基于 YOLO 深度学习的工业视觉检测系统，采用 WinForms + WebView2 现代化架构。

---

## 🎯 核心功能

### 1. AI 视觉检测
- **YOLO 目标检测**：支持 YOLOv8/v11 ONNX 模型推理
- **GPU 加速**：可选 CUDA 加速，提升推理速度
- **多模型切换**：前端下拉框实时切换检测模型
- **参数调节**：置信度阈值、IOU 阈值实时调整
- **ROI 区域**：支持在界面上绘制感兴趣区域进行局部检测

### 2. 工业相机集成
- **华睿相机支持**：通过 MVSDK 驱动控制工业相机
- **一键连接**：自动查找并匹配配置的相机序列号
- **参数配置**：曝光时间、增益等参数可在设置中调整
- **实时预览**：检测结果实时显示在 WebUI 界面

### 3. PLC 多协议通讯
支持以下 PLC 通讯协议：
| 协议 | 支持品牌 |
|------|----------|
| Mitsubishi MC ASCII | 三菱 FX/Q 系列 |
| Mitsubishi MC Binary | 三菱 FX/Q 系列 |
| Modbus TCP | 通用 |
| Siemens S7 | 西门子 S7-1200/1500 |
| Omron Fins TCP | 欧姆龙 |

- **触发检测**：PLC 写入触发信号自动拍照检测
- **结果反馈**：检测结果（OK/NG）自动写回 PLC

### 4. NG 重检机制
- 首次检测 NG 时，等待 2 秒后自动重检
- 第二次结果为最终判定，减少误判

### 5. 统计与日志
- **今日统计**：实时显示总数/合格/不合格数量及饼状图
- **历史记录**：保存最近 7 天的统计数据
- **检测日志**：每次检测详情记录，支持历史查看
- **NG 图片库**：按日期/小时分类存储不合格图片

### 6. 数据管理
- **自动清理**：超过 30 天的图片和日志自动清理
- **自定义存储路径**：可在设置中修改数据存储位置
- **统计持久化**：重启后自动加载当日统计数据

---

## 🖥️ 界面特性

### 现代化 WebUI
- **无边框全屏**：沉浸式工业界面
- **窗口拖动**：标题栏区域支持拖动
- **窗口控制**：最小化、最大化/还原、退出
- **响应式布局**：自适应屏幕尺寸

### 管理员设置
- **密码保护**：设置界面需管理员密码验证
- **参数配置**：PLC、相机、YOLO 参数集中管理
- **实时生效**：参数修改后立即应用

---

## 📁 项目结构

```
ClearFrost/
├── 主窗口.cs          # 主程序逻辑
├── WebUIController.cs # WebView2 前后端通讯
├── AppConfig.cs       # 配置管理
├── PlcAdapters.cs     # PLC 多协议适配器
├── DetectionStatistics.cs  # 统计模块
├── StatisticsHistory.cs    # 历史统计
├── BW_yolo.cs         # YOLO 推理引擎
├── IPlcDevice.cs      # PLC 接口定义
├── html/
│   └── index.html     # WebUI 主界面
├── ONNX/              # 模型文件目录
└── DLL/               # 依赖库
```

---

## ⚙️ 可配置项

| 配置项 | 说明 | 默认值 |
|--------|------|--------|
| PlcIp | PLC IP 地址 | 192.168.22.44 |
| PlcPort | PLC 端口 | 4999 |
| PlcProtocol | PLC 协议类型 | Mitsubishi_MC_ASCII |
| CameraSerialNumber | 相机序列号 | - |
| ExposureTime | 曝光时间 (μs) | 50000 |
| TargetLabel | 检测目标标签 | screw |
| TargetCount | 合格所需数量 | 4 |
| EnableGpu | 启用 GPU 加速 | false |
| StoragePath | 数据存储路径 | C:\GreeVisionData |

---

## 🚀 系统要求

- **操作系统**：Windows 10/11 (x64)
- **运行时**：.NET 8.0 Runtime
- **显卡**：可选 NVIDIA GPU (CUDA 支持)
- **相机驱动**：华睿 MVSDK

---

## 📝 更新日志

### v1.0 Pro (2025-12)
- 重构为 WebView2 架构
- 新增多协议 PLC 支持
- 新增 NG 重检机制
- 新增历史统计功能
- 新增 NG 图片库
- 优化无边框窗口拖动

