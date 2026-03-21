# MinoLink Web Dashboard Design

## Overview

Add a Blazor Server frontend to MinoLink, enabling users to configure Agent settings, Feishu credentials, manage sessions, and monitor runtime status through a browser-based dashboard.

## Architecture

### Host Upgrade

`MinoLink/Program.cs` upgrades from `Host.CreateApplicationBuilder` to `WebApplication.CreateBuilder`. WebApplication is fully compatible with `IHostedService`, so existing services (EngineHostedService, Feishu WebSocket) remain unchanged.

### Project Structure

```
MinoLink (Host)
  ├── MinoLink.Core          (existing)
  ├── MinoLink.ClaudeCode    (existing)
  ├── MinoLink.Feishu        (existing)
  └── MinoLink.Web (new)     (references MinoLink.Core)
      ├── Components/
      │   ├── Layout/
      │   │   └── MainLayout.razor
      │   ├── Pages/
      │   │   ├── Dashboard.razor       /
      │   │   ├── AgentConfig.razor     /agent
      │   │   ├── FeishuConfig.razor    /feishu
      │   │   └── Sessions.razor        /sessions
      │   ├── App.razor
      │   └── Routes.razor
      ├── wwwroot/
      │   └── css/app.css
      └── MinoLink.Web.csproj
```

### Binding

Default `http://localhost:5000`, localhost only, no authentication.

## Core Layer Changes

### IConfigService (new)

```csharp
// MinoLink.Core/Interfaces/IConfigService.cs
public interface IConfigService
{
    MinoLinkOptions GetConfig();
    void UpdateConfig(Action<MinoLinkOptions> update);
}
```

Implementation reads/writes `appsettings.json` and triggers hot reload via `IOptionsMonitor<T>`.

### Engine Status Query (new)

```csharp
// Engine adds:
public IReadOnlyCollection<SessionStatus> GetActiveStatuses();
```

Returns read-only snapshot of active session statuses (session key, user name, platform, last active time, whether currently processing).

### SessionManager

Already exposes `GetAllSessions`, `RemoveActive`, `SwitchTo`. Web layer injects directly, no new interfaces needed.

## UI Design

### Visual Style

- Reference: YYDS Mail clean aesthetic
- White/light gray background, generous whitespace
- Card-based layout, border-radius 6-8px
- Primary accent: purple; action buttons: black
- Form inputs: light gray background, rounded
- Top navigation bar
- No external CSS framework, pure CSS

### Navigation

Top fixed navbar with Logo + 4 page links: Dashboard, Agent, Feishu, Sessions.

### Pages

#### Dashboard `/`

- Top row: status cards (Engine status, active session count, Agent type, current mode)
- Below: recent active sessions list (last 5), showing user name, platform, last active time
- Read-only, no edit functionality

#### Agent Config `/agent`

- Form card with fields:
  - WorkDir (text input)
  - Mode (dropdown: default / acceptEdits / plan / bypassPermissions)
  - Model (text input, optional)
- Save button at bottom, changes take effect immediately via hot reload

#### Feishu Config `/feishu`

- Form card with fields:
  - AppId (text input)
  - AppSecret (password field, masked display)
  - VerificationToken (password field, masked display)
  - AllowFrom (text input with format hint)
- Sensitive fields show `••••••` when not editing, reveal input on edit
- Save shows notice: "Saved. Restart required for Feishu connection changes."

#### Sessions `/sessions`

- Table display: user name, platform, session name, work directory, created at, last active at
- Action column: delete button with confirmation dialog
- Platform filter support

## Sensitive Field Handling

AppSecret and VerificationToken are masked in the UI with `***`. Only on explicit edit action does the input field appear for entering a new value. These values should be stored via `appsettings.json` (or user-secrets for production).

## Data Flow

```
Browser ←→ Blazor Server (SignalR)
              ├── IConfigService    → appsettings.json (read/write)
              ├── SessionManager    → sessions.json (read/delete)
              └── Engine            → in-memory state (read-only)
```

## Out of Scope

- Authentication/authorization (localhost only)
- Real-time log streaming (future enhancement)
- Session creation/switching from UI (done via Feishu commands)
- Multi-user support
