SHELL := /bin/bash

PROJECT := SunlightPlugin.csproj
SOLUTION := SunlightPlugin.slnx
PACKAGES_CONFIG := packages.config
PACKAGES_DIR ?= packages
CONFIG ?= Debug
PLATFORM ?= AnyCPU
ASSEMBLY_NAME ?= SunlightPlugin
OUTPUT_DIR := bin/$(CONFIG)
DEPLOY_DIR ?=

ifeq ($(OS),Windows_NT)
NUGET_EXE := nuget.exe
else
NUGET_EXE := nuget
endif

MSBUILD ?= $(shell command -v msbuild 2>/dev/null)
ifeq ($(strip $(MSBUILD)),)
MSBUILD_DOTNET := $(shell command -v dotnet 2>/dev/null)
ifneq ($(strip $(MSBUILD_DOTNET)),)
MSBUILD := dotnet msbuild
endif
endif

.DEFAULT_GOAL := help

.PHONY: help doctor install-prereq install-nuget check-msbuild check-nuget check-net48-targeting-pack check-acad-managed restore build build-debug build-release clean rebuild dev launch copy-deploy deploy deploy-only

help:
	@echo "SunlightPlugin Make targets"
	@echo "-------------------------"
	@echo "make doctor          - 检查构建工具与关键环境变量"
	@echo "make install-prereq  - 安装/检查构建前置条件"
	@echo "make install-nuget   - 自动安装 NuGet CLI(按系统选择包管理器)"
	@echo "make restore         - 基于 packages.config 还原 NuGet 包"
	@echo "make build           - 按 CONFIG 构建(默认 Debug)"
	@echo "make build-debug     - 构建 Debug"
	@echo "make build-release   - 构建 Release"
	@echo "make clean           - 清理输出"
	@echo "make rebuild         - 清理后重建 Debug"
	@echo "make copy-deploy     - 构建后复制产物到 DEPLOY_DIR"
	@echo "make deploy          - copy-deploy 的别名"
	@echo "make deploy-only     - 仅复制现有产物到 DEPLOY_DIR(不构建)"
	@echo "make dev             - 开发模式: restore + build-debug + launch"
	@echo "make launch          - 启动 AutoCAD(需设置 ACAD_EXE_PATH)"
	@echo ""
	@echo "可选变量:"
	@echo "  CONFIG=Debug|Release"
	@echo "  PLATFORM=AnyCPU"
	@echo "  PACKAGES_DIR=packages"
	@echo "  DEPLOY_DIR=...          (部署目录，如 AutoCAD 插件目录)"
	@echo "  ACAD_MANAGED_DIR=...    (acmgd.dll/acdbmgd.dll/accoremgd.dll 所在目录)"
	@echo "  ACAD_EXE_PATH=...       (acad.exe 完整路径)"

resolve-nuget-cmd:
	@nuget_cmd="$$(command -v nuget 2>/dev/null || true)"; \
	if [ -z "$$nuget_cmd" ] && [ "$(OS)" = "Windows_NT" ] && command -v where.exe >/dev/null 2>&1; then \
		nuget_cmd="$$(where.exe nuget.exe 2>/dev/null | head -n 1 | tr -d '\\r')"; \
	fi; \
	if [ -z "$$nuget_cmd" ] && [ "$(OS)" = "Windows_NT" ]; then \
		for p in \
			"/c/ProgramData/chocolatey/bin/nuget.exe" \
			"/mnt/c/ProgramData/chocolatey/bin/nuget.exe"; do \
			if [ -f "$$p" ]; then nuget_cmd="$$p"; break; fi; \
		done; \
	fi; \
	if [ -z "$$nuget_cmd" ]; then \
		echo "[ERROR] 未找到 NuGet CLI。可先运行: make install-nuget" >&2; \
		exit 1; \
	fi; \
	echo "$$nuget_cmd"

check-msbuild:
	@if [ -z "$(MSBUILD)" ]; then \
		echo "[ERROR] 未找到 msbuild 或 dotnet。"; \
		echo "请安装 Visual Studio Build Tools / MSBuild，或 .NET SDK(带 dotnet msbuild)。"; \
		exit 1; \
	fi

check-nuget:
	@$(MAKE) resolve-nuget-cmd >/dev/null

check-net48-targeting-pack:
	@if [ "$(OS)" = "Windows_NT" ]; then \
		if [ -d "/c/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8" ] || [ -d "/mnt/c/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8" ]; then \
			echo "[info] 检测到 .NET Framework 4.8 Targeting Pack"; \
		else \
			echo "[ERROR] 未检测到 .NET Framework 4.8 Targeting Pack。"; \
			echo "请安装 Visual Studio Build Tools 或 .NET Framework 4.8 Developer Pack。"; \
			exit 1; \
		fi; \
	else \
		echo "[warn] 当前非 Windows 环境，跳过 .NET Framework 4.8 Targeting Pack 本机检查。"; \
		echo "[warn] 若要完整构建该 AutoCAD 插件，请在 Windows 环境执行。"; \
	fi

check-acad-managed:
	@if [ -z "$$ACAD_MANAGED_DIR" ]; then \
		echo "[warn] ACAD_MANAGED_DIR 未设置，将依赖 csproj 自动探测。"; \
		exit 0; \
	fi; \
	if [ ! -f "$$ACAD_MANAGED_DIR/acmgd.dll" ]; then echo "[ERROR] 缺少 $$ACAD_MANAGED_DIR/acmgd.dll"; exit 1; fi; \
	if [ ! -f "$$ACAD_MANAGED_DIR/acdbmgd.dll" ]; then echo "[ERROR] 缺少 $$ACAD_MANAGED_DIR/acdbmgd.dll"; exit 1; fi; \
	if [ ! -f "$$ACAD_MANAGED_DIR/accoremgd.dll" ]; then echo "[ERROR] 缺少 $$ACAD_MANAGED_DIR/accoremgd.dll"; exit 1; fi; \
	echo "[info] ACAD_MANAGED_DIR 检查通过: $$ACAD_MANAGED_DIR"

install-nuget:
	@if command -v nuget >/dev/null 2>&1; then \
		echo "[info] nuget 已安装: $$(command -v nuget)"; \
		exit 0; \
	elif [ "$(OS)" = "Windows_NT" ]; then \
		if command -v winget >/dev/null 2>&1; then \
			echo "[info] 使用 winget 安装 NuGet CLI..."; \
			winget install --id NuGet.NuGet.CommandLine -e --accept-source-agreements --accept-package-agreements; \
		elif command -v choco >/dev/null 2>&1; then \
			echo "[info] 使用 choco 安装 NuGet CLI..."; \
			choco install nuget.commandline -y; \
		else \
			echo "[ERROR] 未找到 winget/choco，无法自动安装 NuGet CLI。"; \
			exit 1; \
		fi; \
	elif [ "$$(uname -s)" = "Darwin" ]; then \
		if command -v brew >/dev/null 2>&1; then \
			echo "[info] 使用 Homebrew 安装 NuGet CLI..."; \
			brew install nuget; \
		else \
			echo "[ERROR] 未找到 brew，无法自动安装 NuGet CLI。"; \
			exit 1; \
		fi; \
	else \
		if command -v apt-get >/dev/null 2>&1; then \
			echo "[info] 使用 apt-get 安装 NuGet CLI..."; \
			sudo apt-get update && sudo apt-get install -y nuget; \
		else \
			echo "[ERROR] 未找到可用包管理器，无法自动安装 NuGet CLI。"; \
			exit 1; \
		fi; \
	fi
	@$(MAKE) check-nuget

install-prereq: check-msbuild install-nuget check-net48-targeting-pack check-acad-managed
	@echo "[info] 前置条件检查完成。"
	@$(MAKE) doctor

doctor:
	@echo "[info] project: $(PROJECT)"
	@echo "[info] solution: $(SOLUTION)"
	@echo "[info] msbuild: $(MSBUILD)"
	@echo "[info] nuget: $$( $(MAKE) --no-print-directory resolve-nuget-cmd 2>/dev/null || echo not-found )"
	@echo "[info] output: $(OUTPUT_DIR)"
	@echo "[info] deploy_dir: $${DEPLOY_DIR:-not-set}"
	@echo "[info] ACAD_MANAGED_DIR: $${ACAD_MANAGED_DIR:-not-set}"
	@echo "[info] ACAD_EXE_PATH: $${ACAD_EXE_PATH:-not-set}"

restore: check-nuget
	@if [ ! -f "$(PACKAGES_CONFIG)" ]; then \
		echo "[ERROR] 未找到 $(PACKAGES_CONFIG)"; \
		exit 1; \
	fi
	@nuget_cmd="$$( $(MAKE) --no-print-directory resolve-nuget-cmd )"; \
	"$$nuget_cmd" restore "$(PACKAGES_CONFIG)" -PackagesDirectory "$(PACKAGES_DIR)" -SolutionDirectory "."

build: check-msbuild
	$(MSBUILD) "$(PROJECT)" /t:Build /p:Configuration=$(CONFIG) /p:Platform=$(PLATFORM)

build-debug:
	@$(MAKE) build CONFIG=Debug

build-release:
	@$(MAKE) build CONFIG=Release

clean: check-msbuild
	$(MSBUILD) "$(PROJECT)" /t:Clean /p:Configuration=$(CONFIG) /p:Platform=$(PLATFORM)

rebuild:
	@$(MAKE) clean CONFIG=Debug
	@$(MAKE) restore
	@$(MAKE) build CONFIG=Debug

copy-deploy: build
	@if [ -z "$(DEPLOY_DIR)" ]; then \
		echo "[ERROR] DEPLOY_DIR 未设置。"; \
		echo "示例: make copy-deploy DEPLOY_DIR='C:/Users/<you>/AppData/Roaming/Autodesk/ApplicationPlugins/SunlightPlugin'"; \
		exit 1; \
	fi
	@mkdir -p "$(DEPLOY_DIR)"
	@if [ ! -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll" ]; then \
		echo "[ERROR] 未找到构建产物: $(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll"; \
		exit 1; \
	fi
	@cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll" "$(DEPLOY_DIR)/"
	@if [ -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll.config" ]; then cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll.config" "$(DEPLOY_DIR)/"; fi
	@if [ -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).pdb" ]; then cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).pdb" "$(DEPLOY_DIR)/"; fi
	@if [ -f "$(OUTPUT_DIR)/AUTOLOADSUN.lsp" ]; then cp -f "$(OUTPUT_DIR)/AUTOLOADSUN.lsp" "$(DEPLOY_DIR)/"; fi
	@echo "[info] 已部署到: $(DEPLOY_DIR)"

deploy-only:
	@if [ -z "$(DEPLOY_DIR)" ]; then \
		echo "[ERROR] DEPLOY_DIR 未设置。"; \
		echo "示例: make deploy-only DEPLOY_DIR='C:/Users/<you>/AppData/Roaming/Autodesk/ApplicationPlugins/SunlightPlugin'"; \
		exit 1; \
	fi
	@mkdir -p "$(DEPLOY_DIR)"
	@if [ ! -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll" ]; then \
		echo "[ERROR] 未找到现有产物: $(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll"; \
		echo "请先执行: make build 或使用 make copy-deploy"; \
		exit 1; \
	fi
	@cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll" "$(DEPLOY_DIR)/"
	@if [ -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll.config" ]; then cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).dll.config" "$(DEPLOY_DIR)/"; fi
	@if [ -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).pdb" ]; then cp -f "$(OUTPUT_DIR)/$(ASSEMBLY_NAME).pdb" "$(DEPLOY_DIR)/"; fi
	@if [ -f "$(OUTPUT_DIR)/AUTOLOADSUN.lsp" ]; then cp -f "$(OUTPUT_DIR)/AUTOLOADSUN.lsp" "$(DEPLOY_DIR)/"; fi
	@echo "[info] 已从现有产物部署到: $(DEPLOY_DIR)"

deploy: copy-deploy

dev:
	@$(MAKE) restore
	@$(MAKE) build-debug
	@$(MAKE) launch

launch:
	@if [ -z "$$ACAD_EXE_PATH" ]; then \
		echo "[ERROR] ACAD_EXE_PATH 未设置，无法启动 AutoCAD。"; \
		echo "示例:"; \
		echo "  export ACAD_EXE_PATH='C:/Program Files/Autodesk/AutoCAD 2026/acad.exe'"; \
		exit 1; \
	fi
	"$$ACAD_EXE_PATH"
