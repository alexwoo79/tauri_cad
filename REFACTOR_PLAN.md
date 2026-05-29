# AutoCAD 面积导出（Rust + Vue）重构计划

## 目标
- 使用 Tauri（Rust）+ Vue3（TypeScript）替代 PySide6 界面。
- 保留“连接 AutoCAD → 选择 polyline → 导出 CSV → 预览”完整流程。
- 当前已完成纯 Rust 化，移除 Python 运行依赖。

## 架构
- 前端：Vue3 + TS
  - 路径输入、覆盖开关、导出触发、预览表格、状态提示
- 后端：Tauri Rust Commands
  - `default_output_path`
  - `open_cad`
  - `select_entities`
  - `export_selected_data`
  - `read_excel_selection`
  - `analyze_excel_selection`

## 错误码约定
- `AUTOCAD_NOT_FOUND`
- `SELECTION_EMPTY`
- `CSV_EXISTS`
- `COM_ERROR`
- `EXCEL_READ_FAILED`
- `CLIPBOARD_ERROR`

## 已完成（2026-05-28）
- [x] 初始化 Tauri + Vue3 + TypeScript 工程
- [x] Rust 命令接口与返回结构搭建
- [x] AutoCAD COM 交互改为纯 Rust 实现
- [x] Excel/WPS 选区读取与分析（首行作为表头）
- [x] Vue 界面替换与联调
- [x] 通过 `npm run build` 与 `cargo check`
- [x] Python 过渡脚本停用（保留最小废弃占位）

## 下一步（建议优先级）
1. 增加“选择保存路径”按钮（接入 Tauri dialog 插件）
2. 增加日志落盘（统一 Rust 侧日志）
3. 增加排序/汇总（按图层聚合面积）
4. 完成打包测试（有/无 AutoCAD、Excel/WPS 场景）
5. 设计 C# 插件桥接协议（面向 Autodesk/Tekla 扩展）
