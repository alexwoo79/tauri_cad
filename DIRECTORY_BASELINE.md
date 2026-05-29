# 项目目录基线（Rust + Vue + Tauri）

更新时间：2026-05-29

## 1) 建议保留（源码与配置）

- .vscode/extensions.json
- index.html
- Makefile
- package.json
- package-lock.json
- README.md
- REFACTOR_PLAN.md
- tsconfig.json
- tsconfig.node.json
- vite.config.ts
- src/
  - App.vue
  - main.ts
  - vite-env.d.ts
  - assets/
- public/
  - tauri.svg
  - vite.svg
- src-tauri/
  - build.rs
  - Cargo.toml
  - Cargo.lock
  - tauri.conf.json
  - .gitignore
  - capabilities/default.json
  - icons/
  - src/
    - main.rs
    - lib.rs
    - cad.rs
    - excel.rs
- scripts/
  - clean.ps1

## 2) 允许存在但不入库（缓存/产物）

- node_modules/
- dist/
- dist-ssr/
- src-tauri/target/
- src-tauri/gen/
- logs/
- tmp/
- artifacts/
- .venv/
- .mypy_cache/
- __pycache__/

## 3) 快速校验清单

在 app 根目录执行：

- `make check`：前后端编译检查
- `make clean`：清理本地产物
- `make dev`：启动开发模式

## 4) 版本控制建议

- 已统一忽略规则：见 .gitignore
- 若需最小仓库体积，提交前执行 `make clean`。
- 通常不建议删除 `node_modules`（除非要重装依赖）。
