# Fremd-Komponenten

FlowCap selbst steht unter der MIT-Lizenz (siehe `LICENSE`). Es nutzt folgende Komponenten mit eigenen Lizenzen:

| Komponente | Version | Lizenz | Wo |
|---|---|---|---|
| [WinDivert](https://github.com/basil00/WinDivert) | 2.2.2 | LGPL-3.0 oder GPL-2.0 (Wahl) | `SpeedLimit/native/` – unveränderte Binärdateien aus dem offiziellen Release, wird im Installer mitgeliefert |
| [Bouncy Castle für .NET](https://www.bouncycastle.org/) | 2.7.0 | MIT | NuGet-Paket `BouncyCastle.Cryptography` (X25519, BLAKE2s für WireGuard) |

WinDivert wird dynamisch geladen (`WinDivert.dll`) und nicht verändert; der Quellcode ist unter dem Link oben verfügbar.

„WireGuard“ ist eine eingetragene Marke von Jason A. Donenfeld. FlowCap implementiert das öffentlich dokumentierte WireGuard-Protokoll und steht in keiner Verbindung zum WireGuard-Projekt.
