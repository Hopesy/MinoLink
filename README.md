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
      "WorkDir": "C:/path/to/your/project",
      "Mode": "default"
    },
    "Feishu": {
      "AppId": "<你的飞书 App ID>",
      "AppSecret": "<你的飞书 App Secret>",
      "AllowFrom": "*"
    }
  }
}
```

| 配置项 | 说明 |
|---|---|
| `Agent.WorkDir` | Claude Code 的工作目录 |
| `Agent.Mode` | 权限模式：`default` / `acceptEdits` / `plan` / `bypassPermissions` |
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
| `/clear [名称]` | 创建新会话 |
| `/list` | 列出所有会话 |
| `/switch <序号>` | 切换到指定会话 |
| `/current` | 查看当前会话信息 |
| `/mode [模式]` | 查看/切换权限模式 |

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
   - **Result** → 更新最终回复

## 技术栈

- **.NET 8** / C# 12
- **System.Threading.Channels** — Agent 事件流
- **FeishuNetSdk.WebSocket** — 飞书长连接
- **子进程 JSON 流** — Claude Code CLI 通信
