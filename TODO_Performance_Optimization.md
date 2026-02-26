# 清霜V3 低配工控机性能优化 TODO

> 基于 2026-02-26 深度性能审计报告  
> 原则：**稳健优先** — 所有优化不改变外部接口、不替换硬件驱动模式、不影响检测精度  
> 目标：单次检测 ≤400ms，内存 ≤500MB

---

## 第一阶段：纯代码级瘦身（预计 4~5 工作日）

> 只删减冗余操作，不改变任何业务逻辑和外部依赖

### 1. 消除检测流程中的冗余拷贝

- [x] **1.1** 移除 `DetectionService.DetectAsync` L241 的 `new Bitmap(image)` 深拷贝，改为按需时才复制
- [x] **1.2** `btnCapture_LogicAsync()` 中保存图像和发送前端共用同一份渲染结果，不要各渲染一次
- [x] **1.3** `SaveDetectionImage` 中不合格图像的渲染结果从调用方传入，避免二次 `ToBitmap()` + `GenerateResultImage()`
- [x] **1.4** `CameraService.CaptureLoop` 中 `_lastFrame.Clone()` 只做一次，`FrameCaptured` 事件传递同一份引用

### 2. 渲染器增加工业精简模式

- [x] **2.1** `AppConfig` 新增 `bool IndustrialRenderMode`（默认 `false`，工控机上手动开启）
- [x] **2.2** `YoloRenderer.DrawDetectionBoxes` 增加分支：`IndustrialRenderMode = true` 时仅绘制纯色矩形框 + 简单文本标签
  - 去掉 6 层辉光循环
  - 去掉 LinearGradientBrush 渐变
  - 去掉 GraphicsPath 圆角标签
  - 去掉四角强化线段
  - Graphics 质量从 HighQuality 降为 Default
- [x] **2.3** 原有 Premium 渲染完整保留，仅通过配置切换

### 3. WebView2 图像传输减负

- [x] **3.1** 传输前用 `Cv2.Resize` 将图像缩到前端实际显示尺寸（如 960×540），再编码 Base64
- [x] **3.2** 合并 `updateImage()` + `redrawROI()` 为一次 `ExecuteScriptAsync` 调用
- [x] **3.3** JPEG 编码质量从默认 75 降到 60（工业场景可接受，体积减 30~40%）

### 4. 预处理热路径微优化

- [x] **4.1** `ImageToTensor_Parallel` 中 `Marshal.ReadByte` 改为 `unsafe` 指针批量读取（项目已启用 `AllowUnsafeBlocks`）
- [x] **4.2** `LetterboxResize` 中 GDI+ `Graphics.DrawImage` 改为 `Cv2.Resize` + `Cv2.CopyMakeBorder`（纯库调用替换，无逻辑变化）

---

## 第二阶段：异步化与去阻塞（预计 3~5 工作日）

> 将检测主线程中的阻塞 IO 和不必要的等待移到后台

### 5. 图像保存异步化

- [x] **5.1** 新建 `ImageSaveQueue` 类，内部使用 `Channel<(Mat image, string path)>` 后台写入
- [x] **5.2** `SaveDetectionImage` 改为仅入队操作（≤1ms），后台线程负责实际文件 IO
- [x] **5.3** 队列满时丢弃最旧的待保存项（防止 eMMC 慢速时内存堆积）

### 6. PLC 触发延迟可配置

- [x] **6.1** 将 `PlcService.MonitoringLoop` 中硬编码的 `triggerDelay = 800` 改为 `AppConfig.PlcTriggerDelayMs`
- [x] **6.2** 默认值保持 800ms（兼容现有部署），前端设置页新增输入框供调整
- [x] **6.3** 轮询间隔 `pollingIntervalMs` 也暴露为可配置项（当前默认 500ms）

### 7. 日志写入优化

- [x] **7.1** `StorageService.WriteDetectionLog` 改用 `StreamWriter` + 缓冲模式（非每条 `File.AppendAllText`）
- [x] **7.2** WebView 前端日志调用增加 100ms 节流，避免检测高频时大量 `ExecuteScriptAsync`

### 8. MultiModelManager 锁粒度优化

- [x] **8.1** lock 仅保护模型引用的读取（获取 `_primaryModel` 引用），推理本身在 lock 外执行
- [x] **8.2** 不改变 fallback 串行逻辑和同步推理行为，仅缩小 lock 范围

---

## 第三阶段：轻量架构调整（预计 3~4 工作日）

> 挑风险可控的小改动，不涉及大规模重构

### 9. 检查并移除 WPF 依赖

- [x] **9.1** 搜索项目中是否实际使用了 WPF 命名空间（`System.Windows.Media` 等）
- [x] **9.2** 如无实际使用：将 `<UseWPF>true</UseWPF>` 改为 `false`（预计省 50~80MB 内存，启动快 2~3s）
- [x] **9.3** 检索结果未发现 WPF 命名空间实际使用位置，因此无需记录并保留现状

### 10. GC 压力降低

- [x] **10.1** 后处理中 `Tensor.ToArray()` 大数组改为访问 `DenseTensor.Buffer` 的 `Span<float>`（避免 2.7MB 拷贝）
- [x] **10.2** `YoloRenderer.GenerateMaskImageParallel` 中的 `byte[] colorInfo` 改为栈分配 `stackalloc byte[4]`
- [x] **10.3** 已评估 `ArrayPool<float>.Shared`：`BasicData` 生命周期跨后处理/渲染链路，当前结构下池化回收边界不清晰，暂不引入以避免悬挂引用/重复回收风险

### 11. WebView2 启动优化

- [x] **11.1** 移除 `ClearBrowsingDataAsync()` 每次启动清缓存（改用 URL querystring 版本号控制）
- [x] **11.2** WebView2 初始化与模型加载并行执行（当前是串行）

---

## ✅ 每阶段完成后的验证步骤

- [ ] 全流程检测冒烟测试（相机→检测→PLC→前端→图像保存）
- [x] 用 `Stopwatch` 记录优化前/后各环节耗时并输出到日志
- [ ] 连续运行 30 分钟无崩溃、无内存持续增长
- [ ] 如有工控机，在目标机器上运行 50 轮检测记录 P95 延迟

### 当前验证状态（2026-02-26）

- [x] `dotnet build ClearFrost.sln -c Debug -p:Platform=x64` 通过
- [x] `dotnet test ClearFrost.Tests/ClearFrost.Tests.csproj` 全量通过（46/46）
- [x] 手动检测与 PLC 检测链路已接入分段耗时日志（写入 `Logs/DetectionLogs`）

---

> 编写日期: 2026-02-26
