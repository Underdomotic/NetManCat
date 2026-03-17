<div align="center">

<img src="NetManCat/splash_logo.png" alt="NetManCat logo" width="180" />

# NetManCat
### Il monitor di rete real-time per Windows che ti dice la verità

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078d4?logo=windows&logoColor=white)](https://github.com/fabiodirauso/NetManCat)
[![.NET](https://img.shields.io/badge/.NET-8.0-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
[![Language](https://img.shields.io/badge/Language-C%23-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![Release](https://img.shields.io/badge/Release-v1.0.0-orange)](https://github.com/fabiodirauso/NetManCat/releases)
[![Blog](https://img.shields.io/badge/Blog-fabiodirauso.it-e65100?logo=rss&logoColor=white)](https://www.fabiodirauso.it/Informatica/Programmazione)

**NetManCat** mappa in tempo reale ogni connessione TCP del tuo PC, misura latenza, banda e packet loss per singola connessione, scopre i dispositivi della LAN e supporta NetFlow/sFlow/IPFIX.
Distribuito come **singolo EXE firmato digitalmente** — nessuna installazione, nessuna dipendenza.

[⬇ Scarica l'EXE](#-download) · [📖 Funzionalità](#-funzionalità) · [🏗️ Architettura](#️-architettura) · [🚀 Quick start](#-quick-start) · [📝 Blog post](https://www.fabiodirauso.it/Informatica/Programmazione)

</div>

---

## ✨ Funzionalità

### 🖥️ Monitor connessioni TCP (tab Monitor)
- Enumera tutte le connessioni TCP attive tramite `GetExtendedTcpTable` con PID e nome processo
- Griglia a gruppi **collassabili** — raggruppa per stato TCP o per processo
- **Latenza RTT** per ogni IP remoto tramite `IcmpSendEcho2` (asincrono, non bloccante)
- **Banda per singola connessione** — KB/s upload + download letti dal kernel via `GetPerTcpConnectionEStats`
- **Packet loss %** e jitter calcolati sui probe ICMP
- Colori soglia: 🟢 < 50 ms · 🟡 50–150 ms · 🔴 > 150 ms
- Click destro → aggiungi alla **Watchlist ★** per analisi approfondita

### 📊 Analisi approfondita (tab Analisi)
- Grafico temporale di latenza, banda e loss% per le connessioni in Watchlist
- **Traceroute automatico** hop-by-hop con RTT min/avg/max per ogni nodo
- Evidenzia automaticamente gli hop con ritardi anomali

### 🔍 Flow & Dispositivi LAN (tab Flusso)
- Listener UDP per **NetFlow v5**, **NetFlow v9 / IPFIX** (RFC 7011, template-based) e **sFlow v5**
- **Discovery LAN** con tre metodi combinati:
  - Tabella ARP (`GetIpNetTable2`) — zero pacchetti aggiuntivi
  - Risoluzione DNS inversa asincrona (PTR record)
  - **SNMP v1** opt-in: `sysDescr`, `sysName`, `sysUpTime` per fingerprinting dispositivi
- Riconosce switch/router di Cisco, Juniper, MikroTik, Aruba, HP, Netgear, D-Link e altri

### 🌐 Modalità Server / Client (tab Server)
- Il PC monitorato diventa un **server TCP**: serializza ogni snapshot Watchlist, lo comprime con **LZ4** e trasmette a tutti i client connessi
- Il PC di controllo aggiunge server remoti (IP + porta) e li vede tutti in un'unica griglia aggregata
- Riconnessione automatica con backoff esponenziale in caso di interruzione
- Toggle start/stop senza riavvio dell'applicazione

### ⚙️ Configurazione & Log (tab Configurazione)
- Soglie di alert configurabili: latenza (ms), packet loss (%), jitter (ms)
- **Log in-memory** (default) con buffer circolare — nessuna scrittura su disco
- **Log SQLite** attivabile/disattivabile a caldo, retention automatica configurabile
- **Avvio automatico con Windows** (registro utente `HKCU\...\Run`) — disabilitato di default
- Configurazione duale: UI ↔ `netmancat.json` sempre sincronizzati

### 🔔 Tray icon
- L'app rimane attiva nella **system tray** quando si chiude la finestra
- Menu contestuale: Apri · Avvia/Arresta server · Impostazioni · Chiudi
- Doppio click sull'icona → ripristina la finestra principale

---

## 📋 Requisiti

| Requisito | Dettaglio |
|-----------|-----------|
| Sistema operativo | Windows 10 versione 1607+ (64-bit) · Windows 11 |
| Architettura | x64 |
| Privilegi | **Amministratore consigliato** per metriche kernel complete (`EStats`, ICMP raw) |
| .NET runtime | Incluso nel singolo EXE — nessuna installazione separata |
| Dipendenze esterne | Nessuna |

---

## 🚀 Quick start

```
1. Scarica NetManCat.exe dalla sezione Releases
2. Click destro → "Esegui come amministratore" (per tutte le metriche)
3. Schermata di caricamento → tab Monitor già popolato
4. Click destro su una riga → ★ Aggiungi ad Analisi per il monitoraggio approfondito
```

> **Nota antivirus:** l'EXE è firmato digitalmente Authenticode (Fabiodirauso.it).
> Se Windows SmartScreen mostra un avviso alla prima esecuzione, clicca "Ulteriori informazioni" → "Esegui comunque". Il binario non è modificato.

---

## ⬇ Download

| Versione | Data | Dimensione | Note |
|----------|------|------------|------|
| [v1.0.0](https://github.com/fabiodirauso/NetManCat/releases/tag/v1.0.0) | Marzo 2026 | ~70 MB | Single-file EXE · Runtime .NET 8 incluso · Firmato Authenticode |

---

## 🏗️ Architettura

```
NetManCat/
├── Core/
│   ├── ProcessNetworkScanner.cs   # GetExtendedTcpTable → connessioni TCP per PID
│   ├── LatencyProbe.cs            # IcmpSendEcho2 → RTT + packet loss asincrono
│   ├── BandwidthTracker.cs        # GetPerTcpConnectionEStats → KB/s per connessione
│   ├── TraceRouteProbe.cs         # Traceroute TTL-incrementale hop-by-hop
│   ├── NetFlowReceiver.cs         # Listener UDP NetFlow v5/v9, IPFIX, sFlow v5
│   ├── NetworkDeviceScanner.cs    # ARP + DNS inverso + SNMP v1 fingerprinting
│   ├── TcpServerHost.cs           # TcpListener modalità server → broadcast metriche LZ4
│   ├── TcpClientConnector.cs      # TcpClient persistente con riconnessione automatica
│   ├── PacketCodec.cs             # Serializzazione + compressione LZ4/Brotli
│   ├── SqliteLogger.cs            # Scrittura asincrona su SQLite (modalità permanente)
│   ├── MetricsBuffer.cs           # Buffer circolare in-memory (modalità standard)
│   ├── ScanService.cs             # Timer scan + distribuzione eventi
│   ├── ConfigManager.cs           # Lettura/scrittura netmancat.json + eventi cambio config
│   ├── NicManager.cs              # Enumerazione interfacce di rete
│   └── WatchlistManager.cs        # Gestione connessioni in analisi approfondita
│
├── UI/
│   ├── MainForm.cs                # Finestra principale, status bar, tray icon
│   ├── SplashForm.cs              # Splash screen con progress bar (thread STA separato)
│   ├── CrashReporterForm.cs       # Dialogo di segnalazione eccezioni non gestite
│   └── Panels/
│       ├── MonitorPanel.cs        # Tab Monitor: griglia connessioni TCP real-time
│       ├── AnalysisPanel.cs       # Tab Analisi: grafici + traceroute Watchlist
│       ├── FlowAnalysisPanel.cs   # Tab Flusso: NetFlow/sFlow + discovery LAN
│       ├── ServerPanel.cs         # Tab Server: gestione server TCP + griglia client
│       └── ConfigPanel.cs         # Tab Configurazione: soglie, log, avvio automatico
│
└── Models/
    ├── AppConfig.cs               # Schema netmancat.json
    ├── MetricRecord.cs            # Record metriche per buffer/SQLite
    ├── RemoteWatchSnapshot.cs     # Snapshot Watchlist inviato dal server
    └── ServerEntry.cs             # Voce server configurato
```

---

## 🔧 Stack tecnico

| Tecnologia | Utilizzo |
|-----------|----------|
| C# .NET 8 · WinForms | Linguaggio, UI, target framework |
| `GetExtendedTcpTable` (IP Helper API) | Enumerazione connessioni TCP per PID |
| `GetPerTcpConnectionEStats` / `SetPerTcpConnectionEStats` | Banda reale per connessione (dati kernel) |
| `IcmpSendEcho2` (ICMP.dll P/Invoke) | Latenza RTT e packet loss asincroni |
| `GetIpNetTable2` | Tabella ARP per LAN discovery |
| SNMP v1 BER/ASN.1 (implementazione manuale) | Fingerprinting dispositivi di rete |
| UDP listener multi-protocollo | NetFlow v5, NetFlow v9, IPFIX, sFlow v5 |
| `System.Net.Sockets` `TcpListener`/`TcpClient` | Comunicazione server/client |
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | Compressione pacchetti TCP (velocità massima) |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) | Persistenza metriche, nessun ORM |
| `System.Text.Json` | Serializzazione payload e configurazione |
| Authenticode + signtool.exe | Firma digitale dell'EXE pubblicato |

---

## 🛠️ Build dal sorgente

```powershell
# Prerequisiti: .NET 8 SDK, Visual Studio 2022 o VS Code con C# Dev Kit

git clone https://github.com/fabiodirauso/NetManCat.git
cd NetManCat

# Debug
dotnet build NetManCat\NetManCat.csproj -c Debug

# Publish single-file (richiede certificato per la firma)
.\publish.ps1
```

> Il file `publish.ps1` esegue `dotnet publish` con `PublishSingleFile=true`, `SelfContained=true`,
> `RuntimeIdentifier=win-x64` e firma il risultato con `signtool.exe`.
> Per build non firmate rimuovere la sezione signtool dallo script.

---

## 📐 Formato dati UI

- Tempi sempre in **millisecondi** con un decimale → `12.4 ms`
- Per ogni connessione: `PID | Processo | IP:Porta | Latenza | ↑ KB/s | ↓ KB/s | Loss%`
- Traceroute: `Hop | IP | Hostname | RTT min/avg/max ms | Stato`
- Colori soglia configurabili: 🔴 oltre soglia · 🟡 entro 80% soglia · 🟢 normale

---

## 📄 Licenza

Distribuito sotto licenza **MIT**. Vedi [`LICENSE`](LICENSE) per i dettagli.

---

## 👤 Autore

**Fabio Di Rauso** — [fabiodirauso.it](https://www.fabiodirauso.it)

[![Blog](https://img.shields.io/badge/Blog-fabiodirauso.it-e65100)](https://www.fabiodirauso.it)
[![Facebook](https://img.shields.io/badge/Facebook-FabioDiRauso-1877f2?logo=facebook&logoColor=white)](https://www.facebook.com/profile.php?id=61552800346424)
[![Instagram](https://img.shields.io/badge/Instagram-fabiodirauso.it-e1306c?logo=instagram&logoColor=white)](https://www.instagram.com/fabiodirauso.it/)
[![YouTube](https://img.shields.io/badge/YouTube-FabioDiRausoItalia-ff0000?logo=youtube&logoColor=white)](https://www.youtube.com/@FabioDiRausoItalia)

---

<div align="center">
  <sub>NetManCat — Network Connection Monitor · © 2026 FabioDiRauso.it</sub>
</div>
