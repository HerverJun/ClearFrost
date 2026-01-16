# 清霜视觉检测系统 V2

## 项目说明

这是一个基于 C# .NET 8.0 和 OpenCV 的工业视觉检测系统，支持传统视觉算法和 YOLO AI 检测。

## ⚠️ 重要提示：运行前必读

**Git仓库中只包含源代码（约470KB），不包含运行所需的依赖库和模型文件。**

在另一台电脑上需要完成以下配置才能运行。

---

## 📋 运行环境要求

### 系统要求
- **操作系统**: Windows 10/11 (x64)
- **.NET SDK**: .NET 8.0 SDK 或更高版本
- **Visual Studio**: 2022 或更高版本（推荐）

### 硬件要求（可选）
- **工业相机**: 支持华睿 (Huaray) 工业相机、海康威视 (Hikvision)
- **PLC**: 支持 Modbus TCP 或西门子 S7 协议
- **GPU**: 支持 DirectML 的显卡（用于 AI 推理加速）

---

## 🔧 配置步骤

### 1. 克隆代码
```bash
git clone https://gitee.com/jiao-xiake/ClearForst.git
cd ClearForst
```

### 2. **必需** - 安装 NuGet 包依赖

在项目根目录执行：
```bash
dotnet restore
```

这将自动安装以下依赖：
- `Microsoft.ML.OnnxRuntime.DirectML` (1.16.3) - ONNX 模型推理
- `Microsoft.Web.WebView2` (1.0.3650.58) - 内嵌浏览器UI
- `OpenCvSharp4.Windows` (4.8.0.20230708) - OpenCV 图像处理
- `OpenCvSharp4.Extensions` (4.8.0.20230708) - OpenCV 扩展

### 3. **必需** - 手动添加第三方 DLL

由于以下DLL文件较大且有版权限制，未包含在Git仓库中，需要手动配置：

#### 3.1 创建 `ClearFrost/DLL/` 目录
```bash
mkdir ClearFrost\DLL
```

#### 3.2 添加以下文件到 `ClearFrost/DLL/`:

**HslCommunication.dll** (工业通讯库)
- 用途: PLC 通讯（Modbus/S7）
- 来源: 购买或使用开源版本
- 官网: https://www.hslcommunication.cn/
- 如果不需要PLC功能，可注释掉相关代码

**MVSDK_Net.dll** (华睿相机SDK)
- 用途: 工业相机驱动
- 来源: 华睿科技官网下载相机SDK
- 官网: https://www.huaray.com/
- 如果不使用工业相机，可注释掉相关代码

#### 3.3 创建 `x64依赖包/` 目录（相机SDK依赖）

如果使用华睿工业相机，需要在项目根目录创建 `x64依赖包/` 文件夹，并放入以下文件：

```
x64依赖包/
├── MVSDKmd.dll          (主SDK库 ~5MB)
├── ImageConvert.dll
├── ImageSave.dll
├── VideoRender.dll
├── GCBase_MD_VC120_v3_0.dll
├── GenApi_MD_VC120_v3_0.dll
├── ...（其他22个DLL文件）
```

**这些文件可以从华睿官方SDK包中获取。**

### 4. **可选** - 添加 ONNX 模型文件

如果要使用 AI 检测功能，需要准备训练好的 YOLO ONNX 模型：

#### 4.1 创建 `ClearFrost/ONNX/` 目录
```bash
mkdir ClearFrost\ONNX
```

#### 4.2 放入你的 ONNX 模型文件
```
ClearFrost/ONNX/
├── your-model.onnx
├── yolo11n.onnx         (示例：YOLOv11 nano模型)
└── ...
```

**注意**: ONNX 模型文件通常很大（10-100MB），需要自行训练或下载。

推荐的 YOLO 模型来源：
- Ultralytics YOLOv8/v11: https://github.com/ultralytics/ultralytics
- 导出为 ONNX 格式: `yolo export model=yolo11n.pt format=onnx`

---

## 🚀 编译和运行

### 方式1: 使用 Visual Studio
1. 打开 `ClearFrost.sln`
2. 选择 **x64** + **Release** 配置
3. 按 `F5` 或点击"▶ 开始"

### 方式2: 使用命令行
```bash
dotnet build -c Release --arch x64
dotnet run --project ClearFrost/ClearFrost.csproj -c Release
```

### 方式3: 发布独立可执行文件
```bash
# 使用提供的发布脚本
.\publish.bat

# 或手动执行
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o ./PublishOutput
```

发布后的文件将在 `PublishOutput/` 目录中。

---

## 📁 项目结构

```
ClearForst/
├── ClearFrost/                    # 主项目代码
│   ├── Vision/                    # 视觉算法模块
│   │   ├── Operators.cs          # 图像处理算子
│   │   ├── PipelineProcessor.cs  # 处理流水线
│   │   ├── RobustOrbExtractor.cs # ORB特征提取
│   │   ├── GradientShapeMatcher.cs # 形状匹配
│   │   └── ...
│   ├── html/                      # WebView2 前端UI
│   │   └── index.html
│   ├── BW_yolo.cs                 # YOLO 检测器
│   ├── WebUIController.cs         # UI控制器
│   ├── 主窗口.cs                  # 主窗口
│   ├── PlcAdapters.cs            # PLC通讯适配器
│   ├── DLL/                       # ⚠️ 需手动添加
│   └── ONNX/                      # ⚠️ 需手动添加
├── x64依赖包/                     # ⚠️ 需手动添加
├── .gitignore
├── ClearFrost.sln
├── README.md                      # 本文件
└── publish.bat                    # 发布脚本
```

---

## 🎯 功能特性

### 传统视觉模式
- ✅ 多种图像处理算子（二值化、形态学、边缘检测等）
- ✅ ORB 特征匹配
- ✅ 梯度形状匹配
- ✅ 模板匹配
- ✅ 处理流水线可视化配置

### AI 检测模式
- ✅ YOLOv8/v11 ONNX 模型推理
- ✅ DirectML GPU 加速
- ✅ 实时检测可视化
- ✅ 多类别目标检测

### 工业集成
- ✅ 工业相机采集（华睿/海康威视）
- ✅ PLC 通讯（Modbus TCP / S7）
- ✅ 检测结果统计和历史记录
- ✅ 友好的 Web UI 界面

---

## 🔍 常见问题

### Q1: 缺少 DLL 导致编译失败
**A**: 确保已按照"配置步骤3"正确放置 `HslCommunication.dll` 和 `MVSDK_Net.dll`。

### Q2: 运行时提示找不到相机SDK
**A**: 检查 `x64依赖包/` 目录是否包含所有22个DLL文件。

### Q3: 不使用相机和PLC，可以运行吗？
**A**: 可以，但需要注释掉相关功能代码。建议在 `主窗口.cs` 中跳过相机和PLC初始化。

### Q4: ONNX模型推理失败
**A**: 确保：
   - 模型文件在 `ClearFrost/ONNX/` 目录
   - 模型是 YOLO 格式的 ONNX 文件
   - 输入尺寸匹配（默认640x640）

---

## 📝 开发文档

详细的使用指南请参考：
- `ClearFrost/传统视觉模式使用指南.md` - 传统视觉模式说明

---

## 📧 联系方式

如有问题，请提交 Issue 或联系开发者。

---

## 📄 许可证

[请根据实际情况添加许可证信息]

---

**最后更新**: 2025-12-31
