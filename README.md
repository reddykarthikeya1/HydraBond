# HydraBond

<img src="logo.png" width="96" alt="HydraBond logo">

Windows app that **pools several internet connections** (Wi-Fi, Ethernet, a USB-tethered phone, ...) into one.
Adapters feed into a central hub that shows your combined Mbps live; new connections are spread across every
enabled adapter, least-busy first, with automatic retry on another adapter if one fails.

By Karthikeya Reddy.

## Run

1. Install [Node.js](https://nodejs.org) (the engine runs on it).
2. Double-click `HydraBond.exe`.
3. Flip **Boost all apps** on in the header so every app on the PC uses the combined connections.
   (Advanced: you can also point an app at SOCKS5 `127.0.0.1:1080` / HTTP proxy `127.0.0.1:8080`.)

Each adapter has a switch to include/exclude it from the pool. Any number of adapters is supported.

## Your internet settings are always put back

Boost works by pointing Windows at HydraBond's local proxy. Your original Windows proxy settings are saved before HydraBond changes them and restored when:

| Situation | Handled by |
|---|---|
| Close the window / logoff / shutdown | the app |
| App force-killed (Task Manager, `taskkill /f`) | engine watchdog (~1s) |
| Whole process tree killed (`taskkill /f /t`) | detached guardian process (~2s) |
| Engine crashes or is killed alone | the app restores, then restarts the engine |
| Everything dies at once (power loss) | recovered automatically on the next launch |

Manual escape hatch: `node server.js --restore`. Logs: `%APPDATA%\HydraBond\hydrabond.log`.

## Build from source

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

`make-icon.ps1` regenerates the app icon and `logo.png`.

Only the .NET Framework that ships with Windows is needed (`csc.exe`, WPF). The UI is `HydraBondApp.cs`;
the engine is `server.js` (no npm dependencies).

## Notes

- Connections are balanced **per connection**: a single download uses one adapter, so the speed-up shows
  across many parallel connections (browsers, download managers, speed tests with multiple streams).
- The control API on `127.0.0.1:4545` rejects cross-site requests and unexpected `Host` headers.
- `public/`, `HydraBond.cs` and `Launch-HydraBond.bat` are the older browser-dashboard launcher, kept for reference.
