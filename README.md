# AetherPC

<p>
  <img src="src/AetherPC.App/Assets/Brand/AetherPC.png" alt="AetherPC" width="96" />
</p>

**Free and open-source** Windows tool for optimization, diagnostics and maintenance.

Created and maintained by [Sebastián Leyton](https://github.com/sLEYTONs).

AetherPC helps you understand what is happening on a Windows PC and apply system changes in a controlled way: analyze, recommend, confirm, apply, verify and record.

It is not a “miracle cleaner”. Aggressive changes require confirmation, and rollback is used when Windows and the change type allow it.

## Features

Based on the current app navigation and shipped behavior:

- **Home** — system overview, temperatures when hardware allows it, and recommendation impact
- **Hardware** — CPU, GPU, memory, disks, displays and network
- **Monitor** — live monitoring
- **Processes** — CPU/RAM usage; end or adjust with confirmation
- **Services** — Windows services list, status, start type and detail (UI language aware)
- **Optimize** — recommended change plan with selection and confirmed apply
- **Beast Mode** — more aggressive profile than Optimize, still analysis-driven, with restore point when Windows allows it
- **Cleanup** — common temps and leftovers, without touching personal files
- **Drivers** — inventory and access to Windows driver tools
- **History** — applied actions and soft rollback when tokens exist
- **Settings** — Spanish / English and Light / Dark theme

Installer and single-file Portable builds are available on [Releases](https://github.com/sLEYTONs/AetherPC/releases).

## Screenshots

Official screenshots will live in [`docs/screenshots/`](docs/screenshots/).

*(Real captures of the running app will be added to this section; no mockups.)*

## How it works

1. **Analyze** the system  
2. **Detect** relevant state  
3. **Recommend** actions  
4. **You review** and choose what to apply  
5. **Apply** with confirmation where needed  
6. **Verify** when possible  
7. **Record** history for later review / soft rollback  

**Beast Mode** is more aggressive than **Optimize**, but it still follows analysis and verification rather than blind tweaks.

## Requirements

- Windows 10 or 11 (64-bit)
- Administrator privileges to apply real system changes

Distributed builds are self-contained; you do not need to install the .NET SDK to run them.

## Installation

### Installer

1. Download `AetherPC_Setup.exe` from [Releases](https://github.com/sLEYTONs/AetherPC/releases)
2. Run the wizard
3. Open AetherPC from the Start menu

Default install path: `C:\Program Files\AetherPC\`  
(root launcher + `Uninstall AetherPC.exe`; runtime under `app\`)

### Portable

1. Download `AetherPC_Portable.exe`
2. Run it directly

Settings and history are stored under `%LocalAppData%\AetherPC\`, not next to the executable.

## Windows SmartScreen

Because AetherPC is currently distributed without a trusted code-signing certificate, Windows may show a Microsoft Defender SmartScreen warning when running the Installer or Portable version for the first time.

You may see:

**Windows protected your PC**

and:

**Publisher: Unknown publisher**

This does not refer to the GPL license. It means the executable is currently unsigned by a publisher certificate recognized by Windows.

For transparency:

- AetherPC is open source and its source code is available in this repository.
- Official binaries are distributed through this repository’s [GitHub Releases](https://github.com/sLEYTONs/AetherPC/releases).
- SHA-256 hashes are published with each release so downloaded files can be verified.
- Users should only run AetherPC if the downloaded file matches the official release and they trust the software.

If you choose to continue after verifying the file, use the options Windows itself provides for that dialog. Do not disable Microsoft Defender or SmartScreen.

AetherPC may require administrator privileges for system-level optimization features. A future release may include code signing if a suitable certificate is available; that is not guaranteed for every update.

## Build from source

```bash
dotnet build AetherPC.sln -c Release
```

Distribution pipeline (Installer + Normal layout + Portable):

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-dist.ps1
```

Requires .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the Setup binary.

## Stack

- C# / .NET 8
- WPF
- WPF-UI
- CommunityToolkit.Mvvm
- LiveCharts2 (SkiaSharp)
- SQLite (`Microsoft.Data.Sqlite`)
- LibreHardwareMonitorLib

## Security

Many features need administrator rights because they change Windows (services, power, restore points, etc.).

AetherPC does not disable critical protections on its own. Important changes are confirmed first; results are verified when possible; soft rollback is used when tokens exist. There are no absolute guarantees: antivirus, policy or Windows itself may block or reverse a change.

See [SECURITY.md](SECURITY.md) for how to report vulnerabilities.

## Contributing

Issues, bug reports and suggestions are welcome.

Pull requests may be proposed; they are reviewed case by case. Acceptance is not guaranteed. Product direction stays with the maintainer.

Please do not open public issues for security vulnerabilities that could harm users — use the private channel described in [SECURITY.md](SECURITY.md).

## AI assistance

AetherPC was created and directed by Sebastián Leyton. AI tools were used as assistance during parts of development, research, review and technical help.

AI is a **tool**, not the author, owner or maintainer of the project.

## Author

**Sebastián Leyton**

GitHub: [https://github.com/sLEYTONs](https://github.com/sLEYTONs)

AetherPC is created and maintained by Sebastián Leyton.

## License

This project is licensed under the **GNU General Public License v3.0 or later**. See [LICENSE](LICENSE).

Copyright © 2026 Sebastián Leyton

Third-party components keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
