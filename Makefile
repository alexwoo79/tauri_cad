# Tauri + Vue 开发/发布任务
# 使用方式：
#   make help
#   make dev
#   make release

NPM ?= npm
CARGO ?= cargo
TAURI_ARGS ?=

.PHONY: help install dev web-dev check-rust check-web check fmt-rust clippy release clean-rust clean

help:
	@echo "可用任务:"
	@echo "  make install      安装前端依赖"
	@echo "  make dev          启动 Tauri 开发模式"
	@echo "  make web-dev      仅启动 Vite 前端开发服务"
	@echo "  make check-rust   Rust 编译检查"
	@echo "  make check-web    前端构建检查 (vue-tsc + vite build)"
	@echo "  make check        全量检查 (check-web + check-rust)"
	@echo "  make fmt-rust     Rust 代码格式化"
	@echo "  make clippy       Rust Clippy 检查"
	@echo "  make release      构建桌面发布包 (tauri build)"
	@echo "  make clean-rust   清理 Rust 构建缓存"
	@echo "  make clean        一键清理本地缓存与产物"

install:
	$(NPM) install

dev:
	$(NPM) run tauri dev -- $(TAURI_ARGS)

web-dev:
	$(NPM) run dev

check-rust:
	$(CARGO) check --manifest-path src-tauri/Cargo.toml

check-web:
	$(NPM) run build

check: check-web check-rust

fmt-rust:
	$(CARGO) fmt --manifest-path src-tauri/Cargo.toml --all

clippy:
	$(CARGO) clippy --manifest-path src-tauri/Cargo.toml --all-targets -- -D warnings

release:
	$(NPM) run tauri build -- $(TAURI_ARGS)

clean-rust:
	$(CARGO) clean --manifest-path src-tauri/Cargo.toml

clean:
	powershell -ExecutionPolicy Bypass -File ./scripts/clean.ps1
