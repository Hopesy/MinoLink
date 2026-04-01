# Feishu Turn Merge Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 为 MinoLink 引入飞书普通消息“短窗聚合 + 执行中打断 + 合并后重算”的 turn 模型，替换当前同会话忙时直接拒绝后续消息的行为。

**Architecture:** 在 `Engine` 入口保留命令、权限回答、问题回答的旁路处理，把普通文本/图片/文件消息交给新的 `SessionTurnCoordinator`。每个 session 使用一个 `TurnRuntime` 跟踪聚合状态、执行 CTS 和 debounce，真正发送给 Agent 的内容由 `TurnSnapshot` 统一渲染生成。

**Tech Stack:** .NET 8, C#, xUnit, PowerShell, `dotnet test`, `dotnet build`, CommunityToolkit-compatible code style

---

### Task 1: 写出 turn merge 的失败用例

**Files:**
- Create: `MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs`
- Reference: `MinoLink.Core/Engine.cs`

**Step 1: Write the failing test**

- 为 `Engine` 增加最小可控 fake agent / fake platform 测试桩，先写出这些失败用例：
  1. 首条普通消息进入短窗缓冲，窗口结束后只执行一次；
  2. 短窗内第二条普通消息会与第一条合并，不会触发第二次独立执行；
  3. 合并后的输入文本会以“用户当前请求 + 补充信息”格式发给 Agent。

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 至少一个“短窗内只执行一次”或“输入被正确合并”的断言失败。

**Step 3: Write minimal implementation**

- 在 `Engine` 旁边新增最小 turn 聚合骨架：
  - `MinoLink.Core/TurnMerge/SessionTurnCoordinator.cs`
  - `MinoLink.Core/TurnMerge/TurnRuntime.cs`
  - `MinoLink.Core/TurnMerge/TurnAggregate.cs`
  - `MinoLink.Core/TurnMerge/TurnSnapshot.cs`
- 先只支持 `Idle -> Buffering -> Running`：
  - 普通消息进入协调器；
  - 等待 2 秒合并窗口；
  - 生成 snapshot 后启动一次执行。

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 基础 turn merge 用例通过。

**Step 5: Commit**

```powershell
git add MinoLink.Core/TurnMerge MinoLink.Core/Engine.cs MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs Docs/plans/2026-03-31-feishu-turn-merge-design.md Docs/plans/2026-03-31-feishu-turn-merge-implementation.md
git commit -m "test: cover initial feishu turn merge behavior"
```

### Task 2: 接入运行中打断与 debounce 重启

**Files:**
- Modify: `MinoLink.Core/Engine.cs`
- Modify: `MinoLink.Core/TurnMerge/SessionTurnCoordinator.cs`
- Modify: `MinoLink.Core/TurnMerge/TurnRuntime.cs`
- Modify: `MinoLink.Core/TurnMerge/TurnAggregate.cs`
- Modify: `MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs`

**Step 1: Write the failing test**

- 新增测试：
  1. Claude 已开始处理当前 turn 时，补充消息到达会取消当前执行；
  2. 取消后不会立刻重复执行多次，而是在 debounce 后只重启一次；
  3. 最终执行使用的是最新 revision 的合并输入，而不是旧输入。

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 至少一个“取消并重启”相关断言失败。

**Step 3: Write minimal implementation**

- 在 `TurnRuntime` 中增加：
  - 当前执行 `CancellationTokenSource`
  - `RestartRequested`
  - debounce 定时控制
- 在 `SessionTurnCoordinator` 中实现：
  - `Running -> RestartPending`
  - `RestartPending -> Running`
  - 运行中补充消息到达时执行 cancel-and-rerun
- `Engine` 的真正执行入口改为接受 `TurnSnapshot`，不能再直接使用原始 `Message.Content`。

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- cancel / debounce / rerun 相关测试通过。

**Step 5: Commit**

```powershell
git add MinoLink.Core/Engine.cs MinoLink.Core/TurnMerge MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs
git commit -m "feat: rerun active turn when feishu supplements arrive"
```

### Task 3: 保住命令与交互旁路语义

**Files:**
- Modify: `MinoLink.Core/Engine.cs`
- Modify: `MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs`

**Step 1: Write the failing test**

- 新增测试：
  1. `/stop` 不会进入 turn merge；
  2. `PendingPermission` 回答不会被并入普通 turn；
  3. `PendingUserQuestion` 回答不会被并入普通 turn；
  4. `/stop` 会清空 runtime 并回到 `Idle`。

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 至少一个旁路语义断言失败。

**Step 3: Write minimal implementation**

- 调整 `HandleMessageAsync` 的入口顺序：
  1. pending question
  2. pending permission
  3. command
  4. normal message -> coordinator
- 扩展 `/stop`、`/clear`、`/new`、`/switch` 所调用的清理路径，使其同步清理 `TurnRuntime`。

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 旁路相关测试全部通过。

**Step 5: Commit**

```powershell
git add MinoLink.Core/Engine.cs MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs
git commit -m "fix: keep command and interactive messages out of turn merge"
```

### Task 4: 合并附件并统一渲染最终 prompt

**Files:**
- Modify: `MinoLink.Core/TurnMerge/TurnAggregate.cs`
- Modify: `MinoLink.Core/TurnMerge/TurnSnapshot.cs`
- Modify: `MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs`

**Step 1: Write the failing test**

- 新增测试：
  1. 文本 + 图片/文件能落在同一个 turn；
  2. 生成的 prompt 文本包含附件摘要；
  3. snapshot 附件列表保留所有补充消息带来的附件。

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 至少一个附件合并断言失败。

**Step 3: Write minimal implementation**

- 在 `TurnAggregate.AppendMessage` 中合并附件；
- 在 `BuildPromptText()` 中统一渲染：
  - `用户当前请求`
  - `补充信息`
  - `附件`
- `TurnSnapshot` 输出稳定的 `PromptText` 和附件快照。

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- 附件相关测试通过。

**Step 5: Commit**

```powershell
git add MinoLink.Core/TurnMerge/TurnAggregate.cs MinoLink.Core/TurnMerge/TurnSnapshot.cs MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs
git commit -m "feat: merge attachments into feishu turn snapshots"
```

### Task 5: 编译与回归验证

**Files:**
- Modify: none

**Step 1: Run targeted tests**

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter TurnMergeBehaviorTests -v minimal
```

Expected:
- Turn merge 相关测试全部通过。

**Step 2: Run full tests**

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj -v minimal
```

Expected:
- 全量测试通过或仅保留已知无关失败。

**Step 3: Run build**

```powershell
dotnet build .\MinoLink.slnx -m:1
```

Expected:
- 解决方案编译通过。

**Step 4: Review diff**

```powershell
git diff -- MinoLink.Core/Engine.cs MinoLink.Core/TurnMerge MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs Docs/plans/2026-03-31-feishu-turn-merge-design.md Docs/plans/2026-03-31-feishu-turn-merge-implementation.md
```

**Step 5: Final commit**

```powershell
git add MinoLink.Core/Engine.cs MinoLink.Core/TurnMerge MinoLink.Tests/TurnMerge/TurnMergeBehaviorTests.cs Docs/plans/2026-03-31-feishu-turn-merge-design.md Docs/plans/2026-03-31-feishu-turn-merge-implementation.md
git commit -m "feat: add feishu turn merge rerun flow"
```
