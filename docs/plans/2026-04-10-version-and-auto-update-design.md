# MinoLink Versioning and Auto Update Design

**Date:** 2026-04-10

## Goal

为 MinoLink 建立一套稳定、单一来源的版本与更新策略，让用户只需要理解一件事：安装包就是完整产品，版本号、GitHub Release、MSI 安装包、应用内更新提示应始终保持一致。

## Product Positioning

从用户视角，MinoLink 是一套桌面安装包，而不是 Desktop/Core/Web/Feishu 等多个独立产品。更新设计必须围绕以下问题展开：

1. 我当前安装的是什么版本
2. 是否有新版本
3. 是否可以安全升级
4. 升级后配置和数据会不会丢

## Versioning Strategy

### Single source of truth

产品版本号只保留一个来源：

- `MinoLink.Desktop/MinoLink.Desktop.csproj` 的 `<Version>`

该版本号同时驱动：

- 应用内显示版本号
- MSI 版本号
- MSI 文件名
- Git tag，例如 `v1.0.3`
- GitHub Release，例如 `v1.0.3`
- 应用内检查更新时的当前版本号比较

### Semantic versioning

建议继续使用三段式版本号：

- `major.minor.patch`

建议约定：

- `patch`：修复发布链路、安装包、Bug、小范围行为修正
- `minor`：新增功能、可见体验增强
- `major`：兼容性变化、重大重构或产品层级变化

### Immutable release unit

默认将“版本号 = tag = release = 安装包资产”视为不可变发布单元。

这意味着：

- 同一个版本号不应被重复当成新版本发布
- 已经发布成功的版本，不应无提示地覆盖成另一份不同内容的安装包
- 如果某次 Release 已经创建，但安装包上传失败，应将其视为“重试已有 Release 上传”，而不是强行 bump 版本号

## Release Source of Truth

更新源建议保持单一：

- GitHub Release

理由：

- 当前仓库已经有 `publish-installer.ps1/.cmd`
- 当前已建立 `release-installer.yml` 自动上传链路
- GitHub Release 同时提供版本号、发布时间、更新说明和安装包下载地址
- 不需要额外维护独立更新服务器

## Update Channel Recommendation

当前阶段只建议保留一个正式渠道：

- stable

不要在第一版自动更新里同时引入 beta/nightly/preview，否则会放大版本判断、更新源选择和用户理解成本。

## Recommended Update Model

### Phase A: Manual check + guided update

最小可用版本建议先做到：

- 应用内显示当前版本
- 在设置页或关于页提供“检查更新”按钮
- 请求 GitHub Release 最新正式版本
- 对比当前版本和最新版本
- 如果存在新版本，则显示：
  - 当前版本
  - 最新版本
  - 发布时间
  - 更新说明
  - 下载或安装入口

这个阶段仍然允许用户手动下载安装包升级。

### Phase B: Auto download + handoff to MSI

推荐的正式方案是做到半自动更新：

- 启动时自动检查更新，或由用户手动触发
- 发现新版本后，应用自动下载 MSI 到本地更新缓存目录
- 下载完成后提示用户开始升级
- 用户确认后启动 MSI
- 当前应用退出，由 MSI 执行覆盖升级

这是当前最平衡的方案。

### Phase C: Full silent self-update

不建议当前阶段直接做：

- 后台静默下载
- 进程自替换
- 自动重启

原因：

- Windows + MSI 协同复杂
- 失败恢复和半更新状态难处理
- 对当前项目来说收益不如实现成本高

## Update UX Recommendation

### Where to expose update info

建议至少提供两处入口：

1. 设置页 / 关于页中的“检查更新”按钮
2. 启动时后台检查后弹出的轻量提示

### Suggested update prompt

检测到新版本时，建议 UI 文案包含：

- 当前版本：`1.0.3`
- 最新版本：`1.0.4`
- 发布时间：`2026-04-10`
- 更新摘要：取 GitHub Release 正文前几段

按钮建议：

- `稍后提醒`
- `查看更新内容`
- `下载并安装`

## Download and Cache Strategy

建议将安装包缓存到安装目录之外，例如：

- `%LocalAppData%/MinoLink/updates/`

原因：

- 不污染安装目录
- 不与 MSI 已安装文件混用
- 用户数据、更新缓存、程序文件可以明确分层

下载后建议至少校验：

- 文件存在
- 文件大小
- 如可行，再增加 SHA-256 校验

## Data Safety Strategy

自动更新设计必须保证：

- 配置不丢
- 会话/用户数据不丢
- 升级只替换程序文件，不清理用户数据目录

因此建议坚持：

- 程序文件在 MSI 安装目录
- 用户数据目录放在 `%LocalAppData%/MinoLink/` 或 `%AppData%/MinoLink/`
- 升级逻辑只交给 MSI 处理程序文件替换

## Failure Handling

### First publish

默认发布路径必须要求：

- 当前在 `master`
- 工作区干净
- 目标版本的 tag / release 不存在

### Retry existing release asset upload

长期来看应支持一个显式重试模式，例如：

- 对已有 tag / release 重新触发上传流程
- 只用于“Release 已存在但资产缺失/上传失败”的情况
- 不应作为默认覆盖行为

默认不建议“同版本直接覆盖所有已发布资产”，因为这会破坏版本不可变性。

## Recommended Roadmap

### Stage 1

先实现：

- 应用内显示版本号
- 手动检查更新
- GitHub Release 最新版本查询

### Stage 2

再实现：

- 启动时自动检查更新
- 发现新版本后弹提示框

### Stage 3

再实现：

- 自动下载 MSI
- 用户确认后启动安装器升级

### Stage 4

最后再考虑：

- 忽略某版本
- 渠道（stable/beta）
- 更新日志增强展示
- 已有 Release 失败重试模式

## Final Recommendation

对当前 MinoLink 最合适的方案是：

- 继续保持单版本号
- 继续用 GitHub Release + MSI 作为唯一发布与更新源
- 先做应用内“检查更新 / 下载更新 / 启动安装器升级”
- 不要急于做完全静默自动更新

这套方案最符合当前项目已有发布链路，也最容易保持版本、资产和用户认知的一致性。
