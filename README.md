# FlowCap

Bandbreite pro Programm unter Windows steuern: alle laufenden Programme mit Live-Download/-Upload sehen, einzeln drosseln oder sperren, über einen Proxy oder einen WireGuard-VPN leiten – nur dieses eine Programm, alles andere bleibt direkt.

![Live-Ansicht](docs/screenshots/live.png)

| Datenverbrauch | Pingtest |
|---|---|
| ![Datenverbrauch](docs/screenshots/verbrauch.png) | ![Pingtest](docs/screenshots/ping.png) |
| **Speedtest** | **Einstellungen** |
| ![Speedtest](docs/screenshots/speedtest.png) | ![Einstellungen](docs/screenshots/einstellungen.png) |

Neu erstellen: FlowCap öffnen, dann in einer Administrator-PowerShell `tools\screenshots.ps1` ausführen.

## Funktionen

- **Live-Dashboard** – Verlaufsgraph, Tacho, Liste aller Programme mit Mini-Verlauf, Ping-Anzeige
- **Limits pro Programm** – Download/Upload per Schieberegler, Internet sperren, Gesamtlimit für alle Programme
- **Spielmodus** – läuft ein Programm mit Vorrang, teilen sich alle anderen ein Limit
- **Profile & Zeitpläne** – z. B. „Arbeit“ Mo–Fr 9–17 Uhr automatisch
- **Datenverbrauch** – pro Programm und Tag/Woche/Monat, Datenlimits mit Warnung und automatischer Sperre
- **Verbindungen** – offene TCP-Verbindungen eines Programms mit Hostnamen
- **Proxy pro Programm** – SOCKS5 oder HTTP-CONNECT, auch für Programme ohne Proxy-Unterstützung
- **VPN pro Programm (WireGuard)** – `.conf` laden, Programm zuweisen; TCP und UDP laufen durch den Tunnel, mit Kill-Switch
- **Speedtest** – Ping, Jitter, Download und Upload (über Cloudflare)
- **Pingtest** – Dauerping zu wählbarem Ziel mit Verlauf, Min/Mittel/Max, Jitter und Paketverlust
- **Automatische Updates** – über die GitHub-Releases, Installation mit einem Klick
- **Tray-Schnellmenü, Benachrichtigungen, Autostart**

## Voraussetzungen

- Windows 10/11, 64 Bit
- Administratorrechte (der Netzwerktreiber WinDivert lässt sich nur so öffnen)

## Bauen

Benötigt das [.NET 10 SDK](https://dotnet.microsoft.com/) und für den Installer [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
.\build.ps1
```

Führt die Tests aus, veröffentlicht eine eigenständige Version nach `publish\` und baut den Installer nach `dist\FlowCap-Setup-<Version>.exe`.

Nur Tests: `dotnet test SpeedLimit.slnx`

## Aufbau

| Pfad | Inhalt |
|---|---|
| `SpeedLimit/` | WPF-Anwendung (Projektname historisch, die App heißt FlowCap) |
| `SpeedLimit/Throttler.cs` | Paketerfassung per WinDivert, Drosselung, Sperren, Proxy-/VPN-Weiche |
| `SpeedLimit/WireGuard*.cs` | eigene WireGuard-Implementierung (Handshake, Verschlüsselung, Timer, Config) |
| `SpeedLimit/ProxyRelay.cs`, `Proxy.cs` | lokales Relay und SOCKS5/HTTP-Anmeldung für „Proxy pro Programm“ |
| `SpeedLimit.Tests/` | xUnit-Tests |
| `installer/FlowCap.iss` | Inno-Setup-Skript |
| `tools/socks5-testproxy.py` | kleiner protokollierender SOCKS5-Proxy zum Testen |

## Datenschutz & Sicherheit

- Einstellungen und Verbrauchsdaten liegen lokal unter `%AppData%\FlowCap`.
- Proxy-Passwörter und WireGuard-Configs (inkl. privatem Schlüssel) werden mit Windows-DPAPI verschlüsselt gespeichert.
- FlowCap sendet keine Telemetrie. Netzwerkanfragen gehen nur an deine Proxys/VPN-Server, an Cloudflare (Ping-Anzeige und Speedtest) und für Hostnamen an deinen DNS-Server.

## Bekannte Grenzen

- DNS-Anfragen laufen auch bei Proxy/VPN über den normalen Anschluss (Windows löst Namen zentral auf).
- Proxy: nur TCP über IPv4; UDP und IPv6 werden für zugewiesene Programme gesperrt.
- VPN: nur IPv4; Bandbreiten-Limits gelten nicht für VPN-Verkehr.
- Zuordnung erfolgt über den Programmnamen – einzelne Windows-Dienste in `svchost` lassen sich nicht trennen.
- Exe und Installer sind nicht signiert (SmartScreen-Warnung „Unbekannter Herausgeber“). Virenscanner stufen WinDivert teils als „potenziell unerwünscht“ ein.

## Lizenz

MIT – siehe [LICENSE](LICENSE). Fremd-Komponenten: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
