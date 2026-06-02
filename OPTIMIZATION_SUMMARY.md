# SunlightPlugin 性能优化总结文档

## 1. 背景与目标
本轮优化目标是在**不改变 C# 主体架构**的前提下，降低计算与交互过程中的 CPU/GPU 开销、减少 GC 压力、提升实时推敲流畅度，并提高工程对不同 AutoCAD 版本的兼容能力。

优化范围覆盖以下模块：
- `SunlightPlugin/2_Engine`
- `SunlightPlugin/3_UI`
- `SunlightPlugin/4_Jig`
- `SunlightPlugin/SunlightPlugin.csproj`

---

## 2. 已完成优化项（动作 1~8）

### 动作 1：ILGPU Kernel 缓存复用
**文件**：`SunlightPlugin/2_Engine/SunlightCalculatorGPU.cs`

**问题**：计算路径中多次重复获取 Kernel，存在不必要开销。

**改动**：
- 新增 `_rayCastKernel` 字段并在初始化时加载。
- `CalculateGridMask`、`ComputeJigFrame` 复用同一 Kernel。

**收益**：
- 减少重复初始化与调度准备开销。
- 稳定长周期计算性能。

---

### 动作 2：移除 `fullRays.IndexOf` 的 O(n²) 路径
**文件**：`SunlightPlugin/3_UI/SunControlUI.xaml.cs`

**问题**：筛选 surviving rays 时存在 `IndexOf` 逐项查找，整体复杂度偏高。

**改动**：
- 改为直接构建 `survivingIndices`，避免在循环内执行 `IndexOf`。

**收益**：
- 大样本光线计算下显著降低 CPU 时间。
- 缓解 UI 卡顿。

---

### 动作 3：Jig 每帧分配优化（降低 GC）
**文件**：`SunlightPlugin/4_Jig/SunlightJig.cs`

**问题**：`WorldDraw` 每帧创建临时集合和颜色数组，GC 压力大。

**改动**：
- 引入 `_movingBldgsFrame`、`_movingVerticesFrame` 进行帧内复用。
- 复用 `_meshColors`。
- 预计算静态包围盒，减少每帧重复 Min/Max。

**收益**：
- 降低频繁分配与回收。
- 实时推敲帧率更稳定。

---

### 动作 4：AABB 预过滤后再做点在多边形判断
**文件**：
- `SunlightPlugin/3_UI/SunControlUI.xaml.cs`
- `SunlightPlugin/4_Jig/SunlightJig.cs`

**问题**：大量点直接进入 `IsPointInPolygon`，几何判定开销高。

**改动**：
- 新增建筑 2D 包围盒缓存（BuildingBounds2D）。
- 先进行 AABB 粗筛，命中后再做精确点内判断。

**收益**：
- 显著减少昂贵几何计算调用次数。
- 大场景收益明显。

---

### 动作 5：光线缓存 + 工程引用路径增强
**文件**：
- `SunlightPlugin/3_UI/SunControlUI.xaml.cs`
- `SunlightPlugin/SunlightPlugin.csproj`
- `SunlightPlugin/SunlightPlugin.csproj.user`

**问题**：
- 光线重复生成。
- 工程对 AutoCAD 2024 路径强绑定。

**改动**：
- 新增 `GetOrCreateSunRays` 缓存复用。
- 项目改为支持 AutoCAD 2020~2026 自动探测。
- 支持 `AutoCADManagedDir` / `AutoCADExePath` 及环境变量覆盖。
- 增加构建期引用校验（acmgd/acdbmgd/accoremgd）。
- `.csproj.user` 启动路径改为 `$(AutoCADExePath)`。

**收益**：
- 减少重复计算。
- 消除版本硬编码，便于多环境协作。

---

### 动作 6：UI 参数集中解析与校验
**文件**：`SunlightPlugin/3_UI/SunControlUI.xaml.cs`

**问题**：参数解析分散，错误处理不统一。

**改动**：
- 新增 `UiCalcParams`、`TryParseDouble`、`TryReadUiCalcParams`。
- 统一解析 `timeStep/lat/spacing/calcZ` 等关键输入。

**收益**：
- 减少解析重复代码。
- 提升参数异常时的可控性与可维护性。

---

### 动作 7：容量预分配与热点 ToList 降频
**文件**：
- `SunlightPlugin/3_UI/SunControlUI.xaml.cs`
- `SunlightPlugin/2_Engine/SunEngine.cs`

**问题**：热路径 List 扩容频繁、临时集合分配偏多。

**改动**：
- 对 `bldgCache`、`testPts`、`tempNodes`、`survivingIndices`、`currentBldgCache`、`pts`、`surviving` 等集合进行容量预估。
- 减少不必要 `ToList()` 链式转换。

**收益**：
- 降低内存抖动与扩容成本。
- 提升峰值场景稳定性。

---

### 动作 8：太阳光线时间循环改为整数分钟步进
**文件**：`SunlightPlugin/2_Engine/SunEngine.cs`

**问题**：浮点步进有累积误差风险。

**改动**：
- 时间循环改为整数分钟步进。

**收益**：
- 结果更可重复、数值更稳定。
- 避免长区间采样偏移。

---

## 3. 新增性能观测能力
**文件**：`SunlightPlugin/3_UI/SunControlUI.xaml.cs`

**改动**：
- 增加 `PerfLogEnabled`、`PerfLog(Editor, phase, Stopwatch)`。
- 在 `GenerateBaseGrid` / `RecalculateGlobalCacheSilent` 中增加分阶段计时（Prepare/GPU/Post/Total）。

**价值**：
- 能快速定位下一阶段优化重点（CPU 前处理、GPU 核心、后处理）。
- 便于后续将性能数据接入 UI 开关或日志系统。

---

## 4. 兼容性成果

已实现：
- 从“强制 2024”改为“多版本自动探测（2020~2026）”。
- 支持手动覆盖路径，适配不同安装目录。
- 构建前校验核心托管引用，减少隐式失败。

说明：
- 当前编辑器中的 `Autodesk.*` 级联错误主要来自**本机未解析到 AutoCAD 托管程序集**，不是本轮优化代码逻辑错误。

---

## 5. 预期收益（定性）

1. 大规模网格与多建筑场景：CPU 开销下降明显（特别是点筛选与几何判定路径）。
2. 实时推敲（Jig）场景：帧内分配减少，卡顿与抖动下降。
3. 长时段日照计算：数值稳定性和结果一致性提升。
4. 多机器协作与部署：工程可移植性和版本兼容性增强。

---

## 6. 建议的验证流程

1. **功能一致性回归**：固定输入建筑和参数，对比优化前后遮罩/统计结果。
2. **性能回归**：开启 PerfLog，记录 `Prepare/GPU/Post/Total`，在同一图纸下对比。
3. **Jig 流畅度验证**：连续拖拽推敲 2~3 分钟，观察是否出现明显卡顿或突发 GC。
4. **兼容性验证**：分别在至少两个 AutoCAD 版本（例如 2022/2024）构建并启动验证。

---

## 7. 后续可选优化（下一阶段）

1. 把 PerfLog 开关暴露到 UI，并支持输出 CSV。
2. 将 AABB + 点内判定进一步向 SIMD/并行批处理演进。
3. 对 GPU 数据传输（Host <-> Device）做批次与内存复用优化。
4. 增加基准场景（小/中/大）并沉淀性能基线文档。

---

## 8. 结论
本轮已完成的动作 1~8 和配套兼容性优化，重点解决了：
- 热路径复杂度过高
- 高频分配导致的交互抖动
- 时间步进数值稳定性
- 工程对特定 CAD 版本路径强绑定

在不改动核心业务架构的前提下，当前版本已经具备“可持续优化、可观测、可跨版本部署”的基础。