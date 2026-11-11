# Coworker Windows App

Coworker for Windows is the native Windows companion app for Humanlike, built with WinUI 3 / .NET 9. It mirrors the macOS Coworker app's appearance and functionality.

## Requirements

- Windows 10 (1809+) or Windows 11
- .NET 9 SDK
- Visual Studio 2022 with Windows App SDK workload (optional, for IDE development)

## Build & Run

```powershell
# Restore packages
dotnet restore

# Build
dotnet build -p:Platform=x64

# Run
dotnet run -p:Platform=x64
```

## Project Structure

```
coworker-windows/
├── App.xaml(.cs)           # Application entry point
├── MainWindow.xaml(.cs)    # Shell with sidebar navigation
├── Pages/
│   ├── DashboardPage       # Home dashboard with metrics + quick actions
│   ├── DictionaryPage      # Custom words & replacements for dictation
│   ├── PlaybooksPage       # Prompt templates
│   ├── HistoryPage         # Transcription history
│   ├── ChatPage            # AI-powered chat
│   ├── ContactsPage        # Contacts management
│   ├── ListsPage           # Task lists
│   ├── AnalyticsPage       # Productivity analytics + typing speed test
│   ├── SettingsPage        # App configuration
│   └── LoginPage           # Google sign-in
├── Services/
│   ├── ApiService           # HTTP API client
│   ├── SessionManager       # Auth/session management
│   ├── ClipboardService     # Clipboard history monitoring
│   └── AudioService         # Microphone recording (NAudio)
├── Models/
│   ├── AppModels            # Data models
│   └── AppSettings          # Persisted settings
├── Helpers/
│   └── Constants            # API URLs, language options
└── Theme/
    └── CoworkerTheme.xaml   # Brand colors, typography, spacing
```

## Brand Colors

| Color        | Hex       | Usage                          |
|-------------|-----------|--------------------------------|
| cwAccent    | `#2473F2` | Primary blue                   |
| cwSecondary | `#4DD9FF` | Cyan glow                      |
| cwDeep      | `#142973` | Dark navy (sidebar)            |
| cwGlow      | `#B3D9FF` | Soft blue-white borders        |
| cwBackground| `#F5F8FF` | Frosted white background       |

## Features

- Sidebar navigation matching macOS app
- Dashboard with metrics cards
- Dictionary with inline add/edit/delete
- AI Chat with streaming responses + workspace/company scope picker (`/coworker/contexts`)
- Transcription history with search
- Playbooks (prompt templates)
- Contacts management
- Analytics & typing speed test
- Settings with toggles, model picker, language selector
- Clipboard history monitoring
- Google sign-in authentication

## Publish, installer and updates

```powershell
# Self-contained publish (Properties\PublishProfiles\win-x64.pubxml) -> bin\Release\net9.0-windows10.0.22621.0\win-x64\publish\
dotnet publish -c Release -p:Platform=x64

# Signed installer + auto-update feed entry (Inno Setup, Ed25519, Authenticode); see installer\README.md
.\installer\create_installer.ps1 -UpdateAppcast
```

The version lives in `Coworker.csproj` (`AppVersion`, `AppBuild`) and is read at runtime by `ApiConstants.AppVersion` / `AppBuild`. Installed copies update themselves from `https://humanlike.co/coworker/appcast-windows.xml` (`Services/UpdateService.cs`). The release procedure, signing options and tests are in [installer/README.md](installer/README.md).
