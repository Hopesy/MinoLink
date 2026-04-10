# Version and Auto Update Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a safe, GitHub Release-based update experience for MinoLink that shows the current version, checks for newer releases, and later hands off upgrade installation to the existing MSI flow.

**Architecture:** Keep `MinoLink.Desktop.csproj` `<Version>` as the only product version source. Add a small update service in the Desktop app that queries GitHub Release metadata, compares semantic versions, and surfaces update state in the UI. Keep installation and replacement responsibility in the MSI installer rather than inventing a custom self-replacement pipeline.

**Tech Stack:** .NET 8, WPF/Desktop host, existing MinoLink desktop services, GitHub Release HTTP API, xUnit

---

### Task 1: Surface current product version inside the desktop app

**Files:**
- Inspect: `MinoLink.Desktop/App.xaml.cs`
- Inspect: `MinoLink.Desktop/` existing settings/about UI files
- Create/Modify: a lightweight version service or existing view model file where app metadata is displayed
- Test: `MinoLink.Tests/Desktop/` version-related test file

**Step 1: Write the failing test**

Add a test proving the desktop app can resolve the current product version from the single source-of-truth assembly metadata.

**Step 2: Run test to verify it fails**

Run: `dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter "FullyQualifiedName~Version" -v minimal`
Expected: FAIL until a version service or binding path exists.

**Step 3: Implement minimal version service**

Read version information from the Desktop assembly and expose it to the UI.

**Step 4: Run test to verify it passes**

Run the same test and confirm PASS.

**Step 5: Commit**

```bash
git add MinoLink.Desktop MinoLink.Tests
git commit -m "feat: expose desktop app version"
```

### Task 2: Add release metadata client for GitHub Release checks

**Files:**
- Create: Desktop-side update service and DTO files
- Modify: DI registration / service setup in Desktop host
- Test: `MinoLink.Tests/Desktop/` update service tests

**Step 1: Write failing tests**

Cover:
- parsing GitHub release metadata
- rejecting draft/prerelease releases
- comparing current version vs latest version

**Step 2: Run tests to verify failure**

Run: `dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter "FullyQualifiedName~UpdateService" -v minimal`
Expected: FAIL before service implementation.

**Step 3: Implement minimal release check service**

Use GitHub Release API as the only update source and return a typed update result.

**Step 4: Run tests to verify pass**

Run the same filter and confirm PASS.

**Step 5: Commit**

```bash
git add MinoLink.Desktop MinoLink.Tests
git commit -m "feat: add github release update check service"
```

### Task 3: Add UI entry for manual update checks

**Files:**
- Modify: settings/about page view and view model
- Modify: any dialog/message abstraction used by Desktop UI
- Test: view model command tests in `MinoLink.Tests/Desktop/`

**Step 1: Write failing tests**

Cover manual check command behavior:
- idle -> checking
- update available
- no update available
- network failure surface

**Step 2: Run tests to verify failure**

Run: `dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter "FullyQualifiedName~UpdateCheck" -v minimal`
Expected: FAIL before UI command wiring.

**Step 3: Implement minimal UI flow**

Add a manual “检查更新” entry and bind it to the update service.

**Step 4: Run tests to verify pass**

Run the same filter and confirm PASS.

**Step 5: Commit**

```bash
git add MinoLink.Desktop MinoLink.Tests
git commit -m "feat: add manual update check ui"
```

### Task 4: Add update download handoff to MSI installer

**Files:**
- Create/Modify: update download helper, local cache path helper
- Modify: UI flow to show release notes and trigger MSI handoff
- Test: download/cache path tests and command tests

**Step 1: Write failing tests**

Cover:
- update cache path is outside install directory
- MSI asset selection from release assets
- handoff command only triggers on valid MSI asset

**Step 2: Run tests to verify failure**

Run: `dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj --filter "FullyQualifiedName~UpdateDownload" -v minimal`
Expected: FAIL before implementation.

**Step 3: Implement minimal download + handoff**

Download MSI to a user-local update cache directory and launch installer after user confirmation.

**Step 4: Run tests to verify pass**

Run the same filter and confirm PASS.

**Step 5: Commit**

```bash
git add MinoLink.Desktop MinoLink.Tests
git commit -m "feat: add msi update download handoff"
```

### Task 5: Documentation and final verification

**Files:**
- Modify: `README.md`
- Modify: relevant Desktop docs if present
- Verify: update-related tests and desktop build

**Step 1: Update docs**

Document:
- single version source
- GitHub Release as update source
- manual check behavior
- MSI handoff behavior

**Step 2: Run final verification**

Run:
- `dotnet test .\MinoLink.Tests\MinoLink.Tests.csproj -v minimal`
- `dotnet build .\MinoLink.Desktop\MinoLink.Desktop.csproj -m:1`

Expected:
- tests pass
- desktop build passes

**Step 3: Commit**

```bash
git add README.md MinoLink.Desktop MinoLink.Tests
git commit -m "docs: describe version and update flow"
```
