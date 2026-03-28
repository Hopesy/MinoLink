# Agent 默认路由与恢复语义设计

## 背景

当前 MinoLink 中，`SessionRecord.AgentType` 同时承担了两层含义：

1. 当前会话显式选中的 Agent（Claude / Codex）
2. 无显式前缀时下一次启动默认使用哪个 Agent

这会导致一个典型混乱：

- 用户某次显式切到了 Codex；
- 该值被持久化到 `sessions.json`；
- 之后用户不写 `#codex`，普通消息仍然启动 Codex；
- 用户误以为“默认 Agent 已经变成了 Codex”。

但与此同时，`/continue`、`/resume`、`/switch` 本质上是恢复命令，它们又必须跟随当前选中的 Agent。

## 目标

统一以下行为：

1. **新会话默认 Claude**
2. **显式 `#codex` / `#claude` 只改变当前会话选中的 Agent**
3. **`/continue`、`/resume`、`/switch` 始终作用于当前会话选中的 Agent**
4. **普通消息在没有显式前缀、且需要“重新启动”Agent 时，默认回到 Claude**
5. **已在运行中的 Agent 不应被普通消息无故强切**

## 决策

### 1. 保留 `SessionRecord.AgentType` 字段

第一版不新增字段，仍沿用 `AgentType`，但明确其语义：

- 它表示 **当前会话选中的 Agent**
- 不再天然等价于“下一次普通启动默认 Agent”

### 2. 启动决策分为两类

#### 普通启动

当发生以下情况时：

- 当前没有运行中的 state
- 用户发送的是普通消息
- 没有 `#claude` / `#codex` 前缀
- 也不是 `/continue` / `/switch` 这类恢复命令

则本次启动 **默认使用 Claude**。

#### 显式 / 恢复式启动

以下情况必须使用当前会话选中的 Agent：

- 用户发送了 `#claude` / `#codex`
- 当前运行中的 state 已经存在
- `/continue`
- `/switch`

### 3. 提示文案要带 Agent 上下文

避免用户再误会 `/resume` / `/switch` 在切谁：

- `/continue`：明确显示 “继续最近一次 Claude/Codex 会话”
- `/resume`：标题显示 “Claude 会话列表” 或 “Codex 会话列表”
- `/switch` 成功提示显示切到的是哪个 Agent 的会话
- `/current` 继续明确显示当前会话 Agent

## 成功标准

1. 新会话首次普通消息启动 Claude
2. 显式 `#codex` 后，可正常启动 Codex
3. 显式进入 Codex 后，`/continue` 恢复的是 Codex
4. 历史持久化里如果 `AgentType=codex`，普通消息在无恢复命令时仍默认 Claude
5. `/resume` / `/switch` 的提示文案清楚标出当前 Agent

