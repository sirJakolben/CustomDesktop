# CustomDesktop — Claude Code Instructions

## Stack
- WinUI 3 (Windows App SDK 2.x) + C# / .NET 9
- Windows 11 only (min version: 10.0.22621.0)
- CsWin32 für alle P/Invoke-Deklarationen

## Architektur-Entscheid
Desktop-Overlay via Ebene-A: rahmenloses WinUI 3-Fenster (kein WorkerW-Hacking).
Begründung: Update-sicher, vollständig dokumentiert, ausreichend für alle Features.

## P/Invoke Regeln (ZWINGEND)
- NIEMALS P/Invoke-Signaturen manuell schreiben
- Immer `NativeMethods.txt` erweitern und CsWin32 generieren lassen
- Alle generierten Aufrufe in `Infrastructure/NativeMethods.cs` kapseln — nie direkt in UI-Code

## Spike-Protokoll
Jede unbekannte Windows-API wird als isolierter Spike in `Spikes/` validiert:
```
Spike-Datei:  Spikes/Spike_XX_<ApiName>.cs (Console App oder minimaler Test)
Ziel:         Eine API, ein Nachweis, ~50-100 Zeilen
Pass-Kriterium: Im Code-Kommentar dokumentieren
Nach Abschluss: Spike bleibt als Referenz, wird NICHT in Hauptcode übernommen
```

## Ordnerstruktur (Zielzustand)
```
CustomDesktop/
  Infrastructure/
    NativeMethods.cs     — Wrapper um CsWin32-Aufrufe
    ShellWindow.cs       — Fenster-Konfiguration (Z-Order, Styles)
    SystemEventBridge.cs — WM_SETTINGCHANGE, WM_DISPLAYCHANGE etc.
  Grid/
    GridConfiguration.cs
    GridLayoutManager.cs
    GridCanvas.cs
  Desktop/
    DesktopItemRepository.cs
    DesktopItemControl.xaml
  Folder/
    FolderModel.cs
    FolderControl.xaml
  Widgets/
    IWidget.cs
    ClockWidget/
  Spikes/              — Wegwerf-Validierungsprototypen
  Settings/
```

## Build-Befehle
```powershell
$Platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'AMD64') { 'x64' } else { $env:PROCESSOR_ARCHITECTURE }
dotnet build -c Debug -p:Platform=$Platform
dotnet run   -c Debug -p:Platform=$Platform
```

## Wichtige Constraints
- Fenster darf NICHT in Alt+Tab oder Taskbar erscheinen (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)
- DPI-Awareness: PerMonitorV2 (bereits in app.manifest)
- Kein Autoload/Singleton-Pattern für Windows-API-Wrapper — explizite Dependency Injection
- Keine Magic Strings für Window-Class-Namen — Konstanten in `Infrastructure/WindowConstants.cs`

## Debugging-Protokoll (ZWINGEND nach jeder Phase)
Dieses Protokoll MUSS nach Abschluss jeder Phase vollständig durchlaufen werden.
Keine Änderungen vor Schritt 4.

```
1. Alle relevanten Dateien lesen — niemals raten
2. 3 Hypothesen formulieren, nach Wahrscheinlichkeit gerankt
3. Jede Hypothese durch Ausführen des Codes / der Tests verifizieren
4. NUR die bestätigte Ursache beheben — nichts anderes
5. Danach vollständige Test-Suite ausführen
6. Befund und Änderungen dokumentieren
```

## Phasen-Übersicht
- Phase 0: Toolchain          ✅ abgeschlossen
- Phase 1: Shell-Fenster-Fundament (Spikes 01-03) ✅ abgeschlossen
- Phase 2: Grid-Engine ✅ abgeschlossen
- Phase 3: Desktop-Icon-Integration (Spikes 05-06)
- Phase 4: App-Folder-System
- Phase 5: Resilienz (System-Events)
- Phase 6: Widget-System & Settings-UI
