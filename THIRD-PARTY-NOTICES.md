# Fremd-Komponenten

FlowCap selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Es nutzt folgende Komponenten mit eigenen Lizenzen:

| Komponente | Version | Lizenz | Wo |
|---|---|---|---|
| [WinDivert](https://github.com/basil00/WinDivert) | 2.2.2 | LGPL-3.0 oder GPL-2.0 (Wahl) | `SpeedLimit/native/` – unveränderte Binärdateien aus dem offiziellen Release, wird im Installer mitgeliefert |
| [Bouncy Castle für .NET](https://www.bouncycastle.org/) | 2.7.0 | MIT | NuGet-Paket `BouncyCastle.Cryptography` (X25519, BLAKE2s für WireGuard) |
| [IP to Country Lite von DB-IP](https://db-ip.com) | 2026-10 | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/) | `SpeedLimit/Data/dbip-country-lite.csv.gz`, unverändert eingebettet (Länder in der Verbindungsliste) |
| Länderflaggen von [flagcdn.com](https://flagcdn.com) (Flagpedia) | 2026-10 | gemeinfrei (laut Anbieter frei für jede Nutzung) | `SpeedLimit/Data/flags-w40.zip`, unverändert eingebettet (40 px breite PNGs) |

Länderdaten: [IP Geolocation by DB-IP](https://db-ip.com).

WinDivert wird dynamisch geladen (`WinDivert.dll`) und nicht verändert; der Quellcode ist unter dem Link oben verfügbar.

„WireGuard“ ist eine eingetragene Marke von Jason A. Donenfeld. FlowCap implementiert das öffentlich dokumentierte WireGuard-Protokoll und steht in keiner Verbindung zum WireGuard-Projekt.
