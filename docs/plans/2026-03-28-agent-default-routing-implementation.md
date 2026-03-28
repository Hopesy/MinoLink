# Agent Default Routing Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 修复 MinoLink 中“普通消息默认 Agent”与“恢复命令跟随当前 Agent”混在一起的问题，确保新会话默认 Claude，而 `/continue` `/resume` `/switch` 继续跟随当前选中的 Agent。

**Architecture:** 保留现有 `SessionRecord.AgentType` 字段作为“当前会话选中的 Agent”，将“普通启动默认 Claude”的决策独立到启动阶段逻辑里。通过集成测试覆盖普通消息、显式 `#codex` 和 `/continue` 三类路径。

**Tech Stack:** .NET 8, C#, xUnit, Channel, PowerShell, `dotnet test`, `dotnet build`

---

### Task 1: 写出默认路由的失败用例

**Files:**
- Create: `MinoLink.Tests/AgentRouting/AgentRoutingBehaviorTests.cs`

**Step 1: Write the failing test**

- 写 3 个测试：
  1. 新会话普通消息默认启动 Claude
  2. `#codex` 可以显式启动 Codex
  3. 会话记录里已保存 `AgentType=codex` 但无恢复命令时，普通消息仍默认启动 Claude

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter AgentRoutingBehaviorTests -v minimal
```

Expected:
- 至少“持久化 codex 但普通消息应默认 Claude”的用例失败

**Step 3: Write minimal implementation**

- 在 Engine 启动 Agent 时区分：
  - 普通启动默认 Claude
  - 显式/恢复启动跟随 `session.AgentType`

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter AgentRoutingBehaviorTests -v minimal
```

Expected:
- 上述测试通过

### Task 2: 写恢复命令与提示文案的回归用例

**Files:**
- Modify: `MinoLink.Tests/AgentRouting/AgentRoutingBehaviorTests.cs`

**Step 1: Write the failing test**

- 新增测试：
  1. 显式进入 Codex 后，`/continue` 提示与恢复都跟随 Codex
  2. `/resume` 标题带当前 Agent 名称

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter AgentRoutingBehaviorTests -v minimal
```

Expected:
- 至少一个与提示文案或恢复路径相关的断言失败

**Step 3: Write minimal implementation**

- 修正文案：
  - `Claude 会话列表`
  - `Codex 会话列表`
  - `/switch` 成功提示中包含 Agent 名称

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter AgentRoutingBehaviorTests -v minimal
```

Expected:
- 所有 Agent 路由测试通过

### Task 3: 编译级验证

**Files:**
- Modify: none

**Step 1: Run tests**

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj -v minimal
```

**Step 2: Run build**

```powershell
dotnet build .\MinoLink.slnx -m:1
```

**Step 3: Review diff**

```powershell
git diff -- MinoLink.Core/Engine.cs MinoLink.Tests/AgentRouting/AgentRoutingBehaviorTests.cs Docs/plans/2026-03-28-agent-default-routing-design.md Docs/plans/2026-03-28-agent-default-routing-implementation.md
```
