# MinoLink

多平台 AI Agent 消息网关 —— 将 Claude Code CLI 接入飞书等即时通讯平台。

## 架构

```
飞书 / 其他平台
      ↕ WebSocket
┌─────────────────────┐
│  MinoLink Engine    │
│  ├ 命令拦截         │
│  ├ 会话管理         │
│  ├ 权限审批         │
│  └ 流式预览         │
└────────┬────────────┘
         ↕ stdin/stdout JSON Stream
   Claude Code CLI
```

**插件化设计**：Platform（飞书、Slack…）和 Agent（Claude Code、其他 CLI）均通过注册表模式接入，互不耦合。

## 项目结构

| 项目 | 职责 |
|---|---|
| `MinoLink.Core` | 核心接口、模型、Engine、SessionManager |
| `MinoLink.ClaudeCode` | Claude Code CLI 适配器（子进程 JSON 流通信） |
| `MinoLink.Feishu` | 飞书平台适配器（WebSocket 长连接） |
| `MinoLink.Web` | Blazor 组件库（Dashboard / Config / Sessions 页面） |
| `MinoLink` | Web 主机入口（ASP.NET Core Blazor Server） |
| `MinoLink.Desktop` | WPF 桌面客户端（BlazorWebView + 系统托盘） |
| `MinoLink.Installer` | WixSharp MSI 安装包生成器 |

## 快速开始

### 前置要求

- .NET 8 SDK
- Claude Code CLI（`npm install -g @anthropic-ai/claude-code`）
- 飞书开放平台应用（需开启消息接收能力）

### 配置

编辑 `MinoLink/appsettings.json`：

```json
{
  "MinoLink": {
    "ProjectName": "my-project",
    "Agent": {
      "Type": "claudecode",
      "WorkDir": "C:/Users/<你的用户名>/Desktop",
      "Mode": "default"
    },
    "Feishu": {
      "AppId": "<你的飞书 App ID>",
      "AllowFrom": "*"
    }
  }
}
```

敏感项建议使用 `dotnet user-secrets` 或环境变量注入，不要长期明文写在仓库配置中：

```bash
dotnet user-secrets set "MinoLink:Feishu:AppSecret" "<你的飞书 App Secret>" --project MinoLink/MinoLink.csproj
dotnet user-secrets set "MinoLink:Feishu:VerificationToken" "<你的飞书 VerificationToken>" --project MinoLink/MinoLink.csproj
```

| 配置项 | 说明 |
|---|---|
| `Agent.WorkDir` | 默认工作目录。当前推荐直接设置为桌面目录，便于后续用 `/project hello` 快速切到桌面下的 `hello` |
| `Agent.Mode` | 权限模式：`default` / `acceptEdits` / `plan` / `bypassPermissions` |
| `Feishu.VerificationToken` | 飞书回调校验令牌。建议通过 `user-secrets` 注入 |
| `Feishu.AllowFrom` | 白名单（`*` 允许所有，或逗号分隔的用户/群 ID） |

### 运行

**Web 模式**（Blazor Server）：

```bash
cd MinoLink
dotnet run
```

**桌面模式**（WPF + BlazorWebView）：

```bash
cd MinoLink.Desktop
dotnet run
```

桌面模式特性：
- 关闭窗口自动隐藏到系统托盘，双击托盘图标恢复
- 托盘右键菜单：「显示窗口」「开机自启」「退出」
- 顶栏可直接切换开机自启与检查更新

### 构建安装包

只需一条命令，Installer 会自动 publish Desktop 再生成 MSI：

```bash
dotnet build MinoLink.Installer -c Release
# → MinoLink.Installer/output/MinoLink-1.0.6-win-x64.msi
```

在 Visual Studio 中：将配置切到 **Release** → 右键 `MinoLink.Installer` → **生成**，一步完成。

#### 一键打包并发布 GitHub Release

不再提供本地 `publish-installer` 脚本。安装包发布统一交给 GitHub Actions：

- `.github/workflows/release-installer.yml`
- 触发方式：推送版本 tag（如 `v1.0.6`）后自动触发

标准发布流程：

1. 提交并推送 `master`
2. 创建并推送 `v{Version}` tag
3. Workflow 自动在远端构建 MSI
4. Workflow 自动创建/更新对应 GitHub Release，并上传 MSI

前置条件：

- 当前分支必须是 `master`
- 工作区必须干净
- 不要求本地安装 WiX/Installer 构建链
- 远端 runner 负责 restore、build 与 MSI 上传

#### 打包原理

```
dotnet build MinoLink.Installer
        │
        ├─ 1. 编译 Installer.exe（net48 + WixSharp）
        │
        ├─ 2. MSBuild Target「PublishDesktop」自动触发
        │     └─ dotnet publish MinoLink.Desktop -c Release -r win-x64 --self-contained
        │        → 输出到 MinoLink.Desktop/bin/Release/net8.0-windows10.0.19041/win-x64/publish/
        │
        └─ 3. WixSharp Target「MSIAuthoring」执行 Installer.exe
              ├─ InstallerProjectPaths：从 Desktop.csproj 读取 Version
              ├─ 收集 publish 目录下所有文件 → 打入 MSI
              ├─ InstallerShellLayout：创建开始菜单 + 桌面快捷方式
              └─ 生成 MinoLink-{version}-win-x64.msi
```

| 组件 | 文件 | 职责 |
|---|---|---|
| `Installer.cs` | Main 入口 | 配置 WixSharp Project，调用 `BuildMsi()` |
| `InstallerProjectPaths.cs` | 路径解析 | 从仓库结构定位 publish 目录，从 csproj 读取版本号 |
| `InstallerShellLayout.cs` | 快捷方式 | 定义开始菜单和桌面快捷方式的目标路径 |

安装行为：
- 安装范围：**当前用户**（perUser），无需管理员权限
- 安装目录：`%LocalAppData%\Programs\MinoLink`
- 快捷方式：开始菜单 `MinoLink` + 桌面 `MinoLink`
- 升级：基于 UpgradeCode 的 MSI 标准升级，安装新版本自动替换旧版本
- 版本号：从 `MinoLink.Desktop.csproj` 的 `<Version>` 元素自动读取

### 应用内版本与更新

桌面端现在已经接通一套最小可用的版本 / 更新链路：

- 当前版本显示：顶栏直接显示 `MinoLink.Desktop.csproj` 的 `<Version>`
- 更新源：只认 GitHub Release 正式版
- 手动入口：顶栏里的 **检查更新**
- 版本判断：自动忽略 `draft` / `prerelease`
- 下载位置：`%LocalAppData%\\MinoLink\\updates\\{version}\\`
- 安装方式：点击 **下载并更新** 后，应用会下载 MSI、自动拉起安装器并关闭当前进程；安装成功后自动拉起新版本

默认更新仓库配置在 `MinoLink/appsettings.json` 顶层 `ReleaseUpdate`：

```json
"ReleaseUpdate": {
  "ApiBaseUrl": "https://api.github.com/",
  "GitHubOwner": "Hopesy",
  "GitHubRepo": "MinoLink"
}
```

说明：

- 当前实现是“手动检查 + 一键下载并更新”的模式
- 还没有做静默后台升级
- 如果你 fork 到自己的仓库，记得同步修改 `ReleaseUpdate`

### 更新日志（Changelog）

- 项目根目录维护 `CHANGELOG.md`
- GitHub Release 说明直接取对应版本的 `CHANGELOG.md` 区块
- 建议每次发布前先补 `Unreleased`，发布后将对应条目落到版本号区块（例如 `1.0.6`）

### 日志文件

- MinoLink 运行时日志会同时输出到控制台和文件。
- 日志目录：`MinoLink/bin/<配置>/<TFM>/logs/`
- 日志文件名：`MinoLink-YYYYMMDD.log`
- 如果你使用独立构建目录验证，例如 `-p:BaseOutputPath=artifacts/verify/`，对应日志也会落到该构建输出目录下的 `logs/`。

## 命令

在聊天中发送 `/` 命令进行会话管理：

| 命令 | 说明 |
|---|---|
| `/help` | 显示命令列表 |
| `/new [名称] [--project 路径]` | 创建新会话（可指定工作目录） |
| `/stop` | 中断当前正在运行的回复 |
| `/clear` | 清除当前会话的对话历史 |
| `/continue` | 继续最近一次 Claude Code 会话 |
| `/resume` | 列出所有会话 |
| `/switch <序号>` | 切换到指定会话 |
| `/current` | 查看当前会话信息 |
| `/project [路径]` | 查看/切换当前会话的工作目录 |
| `/mode [模式]` | 查看/切换权限模式 |
| `/file <要求>` | 生成文件并在回复完成后自动回传飞书 |

### `/new` 与 `/project` 的区别

- `/new [名称] [--project 路径]`
  - 创建一条全新的会话记录
  - 销毁当前 Claude 会话
  - 下一条消息会在新会话中重新启动 Claude
  - 适合“从头开始一段新任务”
- `/project [路径]`
  - 保留当前会话记录，但切换该会话绑定的工作目录
  - 会销毁当前 Claude 进程；下一条消息会在新目录重新启动 Claude
  - 适合“继续当前会话语义，但换一个目录工作”

### `/file` 的用法

- `/file <要求>` 会开启“文件输出模式”。
- Engine 会自动把用户要求补成严格协议，要求 Agent：
  - 所有产物默认写到 `output/` 目录
  - 子目录也必须位于 `output/` 下
  - 回复末尾输出固定区块：

```text
[FILES]
output/report.pdf
output/charts/
[/FILES]
```

- `[FILES]` 区块支持：
  - 单文件路径
  - 多文件路径（每行一个）
  - 目录路径（会递归展开并批量发送）
- Engine 会自动：
  - 隐藏 `[FILES]` 区块，不把它回显给飞书用户
  - 校验路径是否位于工作目录 / `output/` / `output/artifacts/`
  - 过滤不存在文件和超过 30 MB 的文件
  - 去重后发送，并回一条发送摘要

示例：

```text
/file 生成日报 PDF 和图表，放到 output/daily/ 目录
```

Agent 结束时返回：

```text
[FILES]
output/daily/
[/FILES]
```

随后 MinoLink 会把目录下的文件按规则发送到飞书。

### `/stop` 的行为

- `/stop`
  - 等同于一次“远程 Ctrl+C”
  - 立即中断当前正在运行的 Claude 回复
  - 不清除当前会话记录，也不新建会话
  - 用户下一条普通消息会继续在当前会话里处理
  - 适合“当前回复跑偏了，先打断，再继续聊”

### 目录解析与回退规则

- 默认工作目录是 `Agent.WorkDir`。当前推荐直接设为桌面目录。
- `/project hello` 和 `/new --project hello` 这类**相对路径**，会自动解析为“默认工作目录下的 `hello`”。
  - 如果默认目录是桌面，则 `/project hello` 等价于切到 `C:/Users/<你>/Desktop/hello`
- 如果目标目录不存在，MinoLink 会自动创建目录，然后在该目录启动 Claude。
- 如果传入的是绝对路径，则按绝对路径处理。
- 如果会话原来绑定的目录后来被用户手动删除：
  - MinoLink 会在启动 Claude 前先检查目录是否存在
  - 若不存在，则自动回退到默认工作目录
  - 同时清空失效的 Claude `session_id`，避免把旧目录的会话强行恢复到新目录
  - 并向用户提示已回退到默认目录

### 启动 / 恢复提示

- 当 Claude 子进程真正启动成功时，MinoLink 只会发送一次短提示：
  - 新连接：`🎉 客户端已连接`
  - 基于已有会话恢复：`🎉 客户端已恢复`
- 该提示只在“本次 Claude 会话启动/恢复”的那个时刻发送一次，不会在后续每条消息重复出现。

**权限模式**：

| 输入 | 效果 |
|---|---|
| `default` | 每次操作需确认 |
| `acceptedits` | 自动接受文件编辑 |
| `plan` | 只读规划模式 |
| `yolo` / `auto` | 自动批准所有操作 |

未识别的 `/` 命令直接转发给 Agent。

## 消息处理流程

1. 平台收到用户消息 → Engine 拦截检查命令
2. 非命令消息获取会话锁 → 转发到 Agent
3. Agent 事件流实时处理：
   - **Thinking** → 显示思考摘要
   - **Text** → 流式预览（卡片实时更新）
   - **ToolUse** → 显示工具调用
   - **PermissionRequest** → 发送审批卡片，阻塞等待用户点击
   - **Result** → 显示最终回复并补 `已完成` 标识

## 技术栈

- **.NET 8** / C# 12
- **WPF + BlazorWebView** — 桌面客户端
- **WixSharp** — MSI 安装包
- **System.Threading.Channels** — Agent 事件流
- **FeishuNetSdk.WebSocket** — 飞书长连接
- **子进程 JSON 流** — Claude Code CLI 通信
