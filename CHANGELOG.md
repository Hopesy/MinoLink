# Changelog

本文件采用 Keep a Changelog 风格，版本号遵循 SemVer（`major.minor.patch`）。

## [Unreleased]

### Added
- 更新弹窗支持“下载并更新”一键流转：下载 MSI 成功后自动启动安装器并退出当前应用。

### Changed
- 自启按钮图标调整为更标准的电源圆环样式。

### Fixed
- Desktop 单实例启动路径处理 `AbandonedMutexException`，避免异常退出后下次启动直接崩溃。
- 安装器 `PublishDesktop` 目标固定使用 `dotnet publish ... -m:1`，降低构建阶段锁文件/并发导致的失败概率。

## [1.0.4] - 2026-04-10

### Added
- 顶栏版本显示与检查更新入口（更新弹窗内展示最新版本、发布时间与摘要）。
- Desktop 端 GitHub Release 检查更新 + MSI 下载能力。
- 安装器 ARP 图标恢复，MSI 文件名统一为 `MinoLink-{version}-win-x64.msi`。

### Changed
- 更新入口从配置页迁移到顶栏，减少配置页噪音。
- 版本解析增强：优先读取 InformationalVersion / FileVersion，兼容带前缀或后缀版本字符串。

### Fixed
- 修复更新按钮与自启按钮图标语义错位。
- 修复安装器构建链路中 `PublishDesktop` 的并发不稳定问题。

