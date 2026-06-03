PROJECT := SunlightPlugin.csproj
SOLUTION := SunlightPlugin.slnx
CONFIG ?= Debug
PLATFORM ?= AnyCPU
PACKAGES_DIR ?= packages
ASSEMBLY_NAME ?= SunlightPlugin
DEPLOY_DIR ?=
ACAD_MANAGED_DIR ?=
ACAD_EXE_PATH ?=

PS_SCRIPT := $(CURDIR)\scripts\sunlight_dev.ps1

.DEFAULT_GOAL := help

.PHONY: help doctor install-prereq install-nuget check-msbuild check-nuget check-net48-targeting-pack check-acad-managed restore build build-debug build-release clean rebuild dev launch copy-deploy deploy deploy-only package-bundle install-bundle

help:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" help

doctor:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" doctor -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -DeployDir "$(DEPLOY_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AutoCADExePath "$(ACAD_EXE_PATH)" -AssemblyName "$(ASSEMBLY_NAME)"

install-prereq:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" install-prereq -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AutoCADExePath "$(ACAD_EXE_PATH)"

install-nuget:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" install-nuget

check-msbuild:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" check-msbuild

check-nuget:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" check-nuget

check-net48-targeting-pack:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" check-net48-targeting-pack

check-acad-managed:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" check-acad-managed -AutoCADManagedDir "$(ACAD_MANAGED_DIR)"

restore:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" restore -PackagesDir "$(PACKAGES_DIR)"

build:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" build -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)"

build-debug:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" build-debug -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)"

build-release:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" build-release -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)"

clean:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" clean -Configuration "$(CONFIG)" -Platform "$(PLATFORM)"

rebuild:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" rebuild -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)"

copy-deploy:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" copy-deploy -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -DeployDir "$(DEPLOY_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AssemblyName "$(ASSEMBLY_NAME)"

deploy-only:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" deploy-only -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -DeployDir "$(DEPLOY_DIR)" -AssemblyName "$(ASSEMBLY_NAME)"

deploy: copy-deploy

package-bundle:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" package-bundle -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AssemblyName "$(ASSEMBLY_NAME)"

install-bundle:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" install-bundle -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AssemblyName "$(ASSEMBLY_NAME)"

dev:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" dev -Configuration "$(CONFIG)" -Platform "$(PLATFORM)" -PackagesDir "$(PACKAGES_DIR)" -AutoCADManagedDir "$(ACAD_MANAGED_DIR)" -AutoCADExePath "$(ACAD_EXE_PATH)"

launch:
	@powershell -NoProfile -ExecutionPolicy Bypass -File "$(PS_SCRIPT)" launch -AutoCADExePath "$(ACAD_EXE_PATH)"