# Feishu Blank Lines Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 找出飞书回复出现大片空行的根因，并用最小改动修复双层 markdown 规范化导致的段落膨胀问题。

**Architecture:** 先用测试和差分证据锁定 `Engine` 与 `FeishuPlatform` 两层文本整形的职责边界，再删除语义猜测型拆行规则，仅保留结构保护型规则。测试通过反射调用现有私有规范化方法，先复现缺陷，再驱动最小实现修改。

**Tech Stack:** .NET 8, C#, xUnit, Moq, PowerShell, `dotnet build`, `dotnet test`

---

### Task 1: 建立可重复的失败用例

**Files:**
- Create: `MinoLink.Tests/MinoLink.Tests.csproj`
- Create: `MinoLink.Tests/Feishu/FeishuMarkdownNormalizationTests.cs`
- Modify: `MinoLink.slnx`

**Step 1: Write the failing test**

- 添加 xUnit 测试项目，引用 `MinoLink.Core` 与 `MinoLink.Feishu`。
- 在 `FeishuMarkdownNormalizationTests` 中准备真实问题样本文本。
- 通过反射调用：
  - `MinoLink.Core.Engine.NormalizeFinalReplyForFeishu`
  - `MinoLink.Feishu.FeishuPlatform.BuildMarkdownCardJson`
- 断言：
  - 不应在 `优点：/缺点：/如果这个设计没问题，直接回复：` 前后制造额外空行
  - 最终卡片 markdown 不应出现 `\n\n优点：`、`\n\n缺点：`、`\n\n如果这个设计没问题，直接回复：`
  - 代码块与普通列表的基本结构仍存在

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter FeishuMarkdownNormalizationTests -v minimal
```

Expected:
- 至少一个与“额外空行/过度拆行”相关的断言失败

**Step 3: Write minimal implementation**

- 先不大改架构，只删除或收敛导致问题的语义猜测型拆行规则：
  - `Engine.cs` 中面向固定中文文案的强制换行规则
  - 必要时同步收敛 `FeishuPlatform.cs` 中重复拆行规则
- 保留 CRLF 统一、代码块保护、连续空行压缩

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter FeishuMarkdownNormalizationTests -v minimal
```

Expected:
- 测试通过

**Step 5: Commit**

```powershell
git add MinoLink.Tests MinoLink.Core/Engine.cs MinoLink.Feishu/FeishuPlatform.cs MinoLink.slnx
git commit -m "fix: reduce feishu markdown blank lines"
```

### Task 2: 做最小回归验证

**Files:**
- Modify: `MinoLink.Tests/Feishu/FeishuMarkdownNormalizationTests.cs`

**Step 1: Write the failing test**

- 增补两个回归用例：
  - 带代码块回复
  - 带普通编号列表/短答复回复

**Step 2: Run test to verify it fails**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter FeishuMarkdownNormalizationTests -v minimal
```

Expected:
- 如果当前实现破坏代码块或列表结构，则新增用例失败

**Step 3: Write minimal implementation**

- 只针对失败用例补最小调整
- 不顺手做无关重构

**Step 4: Run test to verify it passes**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter FeishuMarkdownNormalizationTests -v minimal
```

Expected:
- 所有相关用例通过

**Step 5: Commit**

```powershell
git add MinoLink.Tests MinoLink.Core/Engine.cs MinoLink.Feishu/FeishuPlatform.cs
git commit -m "test: cover feishu markdown normalization cases"
```

### Task 3: 编译级验证

**Files:**
- Modify: none

**Step 1: Run focused build**

Run:

```powershell
dotnet build .\MinoLink.slnx -m:1
```

Expected:
- Build 成功，无新增编译错误

**Step 2: Run focused tests**

Run:

```powershell
dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj -v minimal
```

Expected:
- 测试通过

**Step 3: Review workspace changes**

Run:

```powershell
git diff -- MinoLink.Core/Engine.cs MinoLink.Feishu/FeishuPlatform.cs MinoLink.Tests MinoLink.slnx
```

Expected:
- 只包含本次问题相关改动

**Step 4: Commit**

```powershell
git add Docs/plans/2026-03-28-feishu-blank-lines-design.md Docs/plans/2026-03-28-feishu-blank-lines-implementation.md
git commit -m "docs: add feishu blank line fix plan"
```
