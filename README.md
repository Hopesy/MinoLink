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
| `MinoLink` | 主机入口（Generic Host + 配置加载） |

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

```bash
cd MinoLink
dotnet run
```

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
- 如果传入的是绝对路径，则按绝对路径处理。
- 如果会话原来绑定的目录后来被用户手动删除：
  - MinoLink 会在启动 Claude 前先检查目录是否存在
  - 若不存在，则自动回退到默认工作目录
  - 同时清空失效的 Claude `session_id`，避免把旧目录的会话强行恢复到新目录
  - 并向用户提示已回退到默认目录

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
- **System.Threading.Channels** — Agent 事件流
- **FeishuNetSdk.WebSocket** — 飞书长连接
- **子进程 JSON 流** — Claude Code CLI 通信
