# 飞书同轮补充消息合并重算设计

## 背景

当前 `MinoLink.Core/Engine.cs` 对同一 `sessionKey` 的处理语义是：

- 通过 `SemaphoreSlim` 保证同一会话串行执行；
- 如果上一条消息仍在处理中，新的普通消息直接回复“上一条消息还在处理中，请稍候...”；
- 新消息不会入队、不会缓存、也不会自动并入当前执行上下文。

这套行为在终端式交互里还能接受，但放到飞书 IM 场景里问题很明显。用户常见输入模式是：

1. 先发一句主问题；
2. 紧接着补一句上下文；
3. 再发日志、图片或文件；
4. 希望机器人按“最新、完整输入”来处理。

当前实现会把后续补充直接拒绝，导致用户以为消息“已经被系统记住”，但实际上根本没有进入当前 turn。

## 目标

统一飞书端普通消息的处理语义：

1. **短时间内的连续普通消息自动合并为同一 turn**
2. **如果 Claude 已经开始处理当前 turn，补充消息到达时立即打断本轮执行**
3. **取消后不立刻重跑，而是等待一个很短的防抖窗口，只按合并后的最新输入重新启动一次**
4. **命令消息、权限确认、问题回答、卡片选项等交互消息永不参与普通消息合并**
5. **这次设计不考虑副作用回滚，统一采用激进式 cancel-and-rerun 语义**

## 核心决策

### 1. 从“忙时拒绝”改成“同轮聚合 + 重算”

保留“同一会话不并发喂给同一个 AgentSession”这个原则，但入口语义不再是“忙时拒绝”，而是：

- 普通消息进入会话级 turn 协调器；
- 若当前无活跃 turn，则开启一个新的聚合 turn；
- 若当前已有活跃 turn，且仍处于合并窗口内，则把补充消息并入当前 turn；
- 若当前 turn 已在执行，则取消当前执行，等待补充稳定后只重启一次。

### 2. 普通消息和控制/交互消息分层

消息入口按以下优先级处理：

1. `PendingUserQuestion` 文本回答
2. `PendingPermission` 文本回答
3. 命令消息（如 `/stop`、`/clear`、`/new`、`/resume`）
4. 普通文本 / 图片 / 文件消息

只有第 4 类进入同轮合并逻辑。这样可以避免把权限确认、选项回答、命令消息误合并进普通 prompt。

### 3. 采用 Aggressive Turn Merge 语义

本次方案明确采用激进策略：

- 不判断“是否已经产生副作用”；
- 只要后续消息被判定为同一 turn 的补充，并且当前 turn 已在 Claude 执行，就立即取消当前执行；
- 然后以**合并后的完整输入**重新启动，而不是把补充内容作为下一轮单独追加。

这里的正确语义是 **cancel-and-replace**，不是简单的“停掉第一条，再处理第二条”。

### 4. 必须增加 debounce，避免频繁重启

如果执行中每收到一条补充都立即重跑，用户连续发 3~4 条补充时会导致 Claude 被反复打断。

因此需要两段时间窗口：

- **初始合并窗口**：首条消息到达后，先等一小段时间收集连续补充；
- **重启防抖窗口**：运行中收到补充并 cancel 后，再等一小段 quiet time，只重启一次。

建议默认值：

- `InitialMergeWindow = 2s`
- `RestartDebounceWindow = 1s`

## 架构设计

### 1. 新增会话级 turn 协调层

在 `Engine.HandleMessageAsync` 和 `ProcessMessageAsync` 之间新增一层轻量协调器，负责每个 session 当前 turn 的聚合与执行重算。

建议新增以下对象：

#### `SessionTurnCoordinator`

职责：

- 维护 `sessionKey -> TurnRuntime`
- 接收普通消息并判断是“新建 turn”还是“合并到当前 turn”
- 在运行中收到补充时触发 cancel-and-rerun
- 管理 debounce 定时
- 生成实际送给 Agent 的 `TurnSnapshot`

#### `TurnRuntime`

职责：

- 表示单个 `sessionKey` 当前活跃 turn 的运行态
- 保存状态机状态、当前聚合 turn、当前执行 CTS、最近输入时间、revision 和重启标记

建议状态：

- `Idle`
- `Buffering`
- `Running`
- `RestartPending`

#### `TurnAggregate`

职责：

- 聚合同一个 turn 内的多条文本和附件
- 记录 `FirstMessageAt` / `LastMessageAt`
- 每次补充时 `Revision++`
- 提供统一的 prompt 渲染方法

#### `TurnSnapshot`

职责：

- 表示某一次真正启动 Agent 时使用的不可变输入快照
- 避免运行中 aggregate 继续被追加，导致本次执行输入不稳定

### 2. Engine 入口改造

`HandleMessageAsync` 调整为：

1. 先尝试处理待回答的问题；
2. 再尝试处理待确认权限；
3. 再处理命令；
4. 剩余普通消息交给 `SessionTurnCoordinator`；
5. `SessionTurnCoordinator` 决定何时真正调用底层执行。

也就是说，当前那段“拿不到 `SemaphoreSlim` 就直接回复忙碌”的逻辑需要下沉或删除，不能继续作为普通消息入口语义。

### 3. 执行输入改成 snapshot，而不是原始 message

现在 `ProcessMessageAsync` 直接使用 `msg.Content` 和 `msg.Attachments`。改造后，真正送给 Agent 的输入应来自 `TurnSnapshot`：

- `snapshot.PromptText`
- `snapshot.Attachments`

这样才能保证“取消并重算”时，Claude 吃到的是合并后的完整输入快照，而不是任意一条原始消息。

## Prompt 组装规则

不建议直接裸拼接多条文本。统一渲染成固定结构：

```text
用户当前请求：
{第一条文本}

补充信息：
- {第二条文本}
- {第三条文本}

附件：
- {文件名1}
- {文件名2}
```

规则：

- 第一条文本作为“用户当前请求”
- 后续文本作为“补充信息”
- 附件名列在“附件”段落
- 实际附件对象仍通过现有 attachments 传给 Agent

这样既符合用户心智，也便于测试和日志核对。

## 状态机

### `Idle -> Buffering`

首条普通消息到达：

- 创建 `TurnAggregate`
- 记录首条文本/附件
- 启动初始合并窗口

### `Buffering -> Running`

合并窗口到期：

- 生成 `TurnSnapshot`
- 创建本轮执行 CTS
- 启动 Agent 执行

### `Running -> RestartPending`

运行中收到同一 turn 的补充消息：

- `TurnAggregate.AppendMessage`
- 取消当前执行 CTS
- 标记 `RestartRequested`
- 启动或重置 debounce

### `RestartPending -> RestartPending`

重启等待中继续收到补充：

- 持续追加到 aggregate
- 重置 debounce

### `RestartPending -> Running`

debounce 到期：

- 基于 aggregate 最新 revision 生成新 snapshot
- 重新启动本轮执行

### `Running -> Idle`

执行结束且期间没有新的补充消息：

- 清理当前 runtime
- 回到空闲态

## 与现有命令/交互逻辑的边界

### 保留现有旁路逻辑

以下逻辑保留并继续优先处理：

- `TryHandlePendingUserQuestionTextAsync`
- `TryHandlePendingPermissionTextAsync`
- `TryHandleCommandAsync`

这些分支不进入普通 turn merge。

### `/stop` 语义

`/stop` 需要同时做到：

- 取消当前 Agent 执行；
- 清空当前 turn runtime；
- 取消 debounce；
- 让会话状态回到 `Idle`

### `/clear` / `/new` / `/switch`

这些命令除保留现有 session/state 销毁逻辑外，还要同步清理当前 turn runtime，避免 runtime 和 `_states` 残留错位。

## 可观测性

建议为后续排障增加统一日志点：

- `TurnCreated`
- `TurnMerged`
- `TurnBuffered`
- `TurnExecutionStarted`
- `TurnCancelledForMerge`
- `TurnRestartScheduled`
- `TurnRestarted`
- `TurnCompleted`
- `TurnDiscardedByStop`

这样可以从日志直接看出：

- 本轮输入被合并了几次；
- 是否发生过自动打断；
- 最终是按哪个 revision 执行的。

## 成功标准

1. 用户在 2 秒内连续发送多条普通文本时，只启动一次 Claude 执行
2. 当前 turn 已在执行时，后续补充消息会触发 cancel，并在 debounce 后按合并后的内容重新执行
3. 多条连续补充不会导致多次重复重启，而是稳定收敛为一次最终重启
4. `/stop`、`/clear`、`/new`、`/switch`、权限回答、问题回答都不会误进入普通合并逻辑
5. 文本、图片、文件可以被合并到同一 turn 内，并以统一快照发送给 Agent

## 不做的事

本次设计明确不覆盖以下内容：

- 副作用检测或回滚
- 外部 Broker（RabbitMQ / Redis Stream / Kafka）
- 多实例分布式协调
- 跨 session 的全局公平调度
- 飞书以外平台的差异化输入策略

本次只解决单进程 MinoLink 内，飞书端普通消息的同轮合并与重算问题。
