# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Projet

**Arka Hyte** (nom technique `ArkaHyte`) remplace HYTE Nexus sur l'écran du boîtier Hyte Y70 Touch
(682×2560, portrait) : application WinForms + WebView2 (.NET 10) qui affiche l'interface web de
`src/ArkaHyte/wwwroot` (thème Sith, noir/rouge) alimentée par des capteurs lus en C#.
Le [README](README.md) décrit l'usage ; [TODO.md](TODO.md) est la file d'attente ordonnée
(prochaine tâche = premier point non coché ; une tâche finie quitte le fichier).

## Commandes

```powershell
# Build + publication (l'app installée tourne depuis bin\ArkaHyte : la quitter avant, fichiers verrouillés)
dotnet publish src\ArkaHyte -c Release -o bin\ArkaHyte

# Installation de dev au démarrage (PowerShell admin, tâche planifiée vers bin) / retrait
scripts\Install.ps1
scripts\Install.ps1 -Uninstall

# Installeurs Velopack (artifacts\releases\ArkaHyte-Setup.msi/.exe) ; -Publish = release GitHub (GITHUB_TOKEN)
# Version toujours > dernière release publiée ; packId ArkaHyte.App à ne jamais changer (mises à jour)
scripts\New-Release.ps1 -Version 1.0.1 [-Publish]
```

Pas de tests automatisés ni de linter. Vérification par les options one-shot de `ArkaHyte.exe`
(liste complète dans `Program.cs` et le README) :

- `--screenshot out.png` : rend l'interface, capture, quitte. Moyen principal de valider un changement visuel.
- `--dump out.json` : un échantillon de tous les capteurs + entrées HWiNFO disponibles.
- `--demo stress|idle`, `--preview-speedtest [done]`, `--open-editor 0` : états à combiner avec `--screenshot`.
- `--speedtest out.txt` : vrai test de débit (sature la connexion, ne pas lancer sans raison).
- `--devtools` : F12.

Consommation : `scripts\Measure-ArkaHyte.ps1 [-Seconds 60] [-OutFile x.json]` sur l'instance en cours
(CPU par processus en % d'un cœur, GPU, mémoire ; hôte + arbre WebView2 uniquement).

Itération UI rapide : ouvrir `src/ArkaHyte/wwwroot/index.html` dans un navigateur. Sans hôte WebView2
(`window.chrome.webview` absent), `console.js` passe en **mode design** avec données simulées.

## Architecture

**Flux de données C# → web.** `SensorHub` échantillonne toutes les sources chaque seconde sur un
thread de fond et publie un `Snapshot` (`Sensors/Snapshot.cs`, records sérialisés en camelCase,
`null` → `--` côté UI). Chaque source est isolée : une panne ne vide que ses widgets.
`DisplayForm` relaie via `PostWebMessageAsJson` ; `console.js` dispatche sur `message.type`
(`snapshot`, `cover`, `config`, `toast`, `speedtest`, `apps`). Les événements hors thread UI passent
par `PostFromAnyThread` (BeginInvoke).

**Flux web → C#.** `host.postMessage({ type, ... })` traité dans le `switch` de `DisplayForm` :
`ready`, `launch`, `media`, `listApps`, `saveShortcut`, `speedtest` (start/cancel).
Ajouter une fonctionnalité interactive = un type de message de chaque côté.

**Sources** (`Sensors/`) : HWiNFO Gadget via registre `HKCU\Software\HWiNFO64\VSB` (capteurs
retrouvés par nom/libellé avec jokers, configurés dans `appsettings.json` → `HwInfo`, `Drives[].Temp`),
`wlanapi` natif lu à offsets fixes (`WifiReader`), `NetworkInterface` + `Ping` (statut
online/no-internet/offline), SMTC pour la lecture en cours (`MediaMonitor`), API Windows pour CPU/RAM/disques.
`SpeedTest` pilote le CLI Ookla (`--format=jsonl --progress=yes`), téléchargé au premier usage dans
`%LOCALAPPDATA%\ArkaHyte\tools` (licence : ne pas l'inclure au dépôt). Cloudflare abandonné (blocage IP).

**Fenêtre.** Écran du boîtier trouvé par sa taille (`Display` dans `appsettings.json`), fenêtre sans bordure
non activante (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `MA_NOACTIVATE`) pour ne jamais voler le focus.
Hôte virtuel `https://hyte.local/` → `wwwroot`. Instance unique (mutex `Local\ArkaHyte`).
`TouchGuard` : hook souris bas niveau sur thread dédié qui renvoie le curseur sur l'écran principal
après un toucher sur le boîtier (+ watchdog). `TrayIcon` + `SithMenuRenderer` : menu de notification thématisé.

**Raccourcis du dock.** Défauts dans `appsettings.json` → `Shortcuts`, surchargés par
`%LOCALAPPDATA%\ArkaHyte\shortcuts.json` (`ShortcutStore`). L'éditeur (appui long) liste les applis via
`shell:AppsFolder` (`AppCatalog`, icônes `Native/ShellIcons.cs`). Logos d'applis recolorés en blanc côté web
(`monochromeIcon`) ; icônes intégrées = `BUILTIN_ICONS` (`console.js`, noms déjà enregistrés : ne pas renommer)
+ sélection Lucide générée dans `js/icons.js` (`LUCIDE_ICONS`, `LUCIDE_GROUPS`).

**Données utilisateur** : `%LOCALAPPDATA%\ArkaHyte\` (logs `display.log`, `launcher.log`, shortcuts, tools).

**Installation et mises à jour (Velopack).** `VelopackApp.Build()…Run()` en tête de `Main` (hooks
d'install / mise à jour / désinstallation → `Installation.cs`, 30 s max puis exit). Installé dans
`%LOCALAPPDATA%\ArkaHyte.App` : packId distinct du dossier de données, que la désinstallation supprimerait.
Démarrage par la clé HKCU `Run`. HYTE Nexus n'est pas géré par l'appli ni l'installeur : c'est à
l'utilisateur de le désactiver ou le désinstaller (choix du propriétaire). `AppUpdater` lit les releases de github.com/Arkatheros/ArkaHyte (dépôt public requis) et
n'installe que sur clic du menu. `AppSettings.Load()` préfère `%LOCALAPPDATA%\ArkaHyte\appsettings.json`
(copié à l'installation). Version = `<Version>` du csproj, passée par `New-Release.ps1`. Garder le paquet
`Velopack` et l'outil `vpk` (`dotnet-tools.json`) à la même version.

**Démarrage (dev)** : tâche planifiée `Arka Hyte` (AtLogOn) → `scripts/Start-ArkaHyte.ps1` → `bin\ArkaHyte\ArkaHyte.exe`.
Sous PowerShell 5.1, ne pas utiliser `$PSScriptRoot` dans les valeurs par défaut de `param()` (vide).

## Règles de performance de l'UI (impératives)

L'écran tourne en permanence : cible ~16 % d'un cœur.
- Boucle d'animation unique à 24 fps via `setTimeout` dans `console.js` ; tout nouvel effet s'y branche.
- Aucune `transition` CSS ni animation CSS infinie, pas de `backdrop-filter`.
- Effets pilotés par variables CSS mises à jour en JS ; `will-change` sur les éléments animés.
- Une variable animée se pose sur les éléments qui la lisent (`setVar(elements, …)`), jamais sur `.frame` :
  héritée par toute la page, elle forcerait un recalcul de style complet à chaque frame.
- Rotation / déplacement : `transform` CSS (compositeur) plutôt que redessiner un canvas (ex. pointes de l'anneau CPU).
- Canvas d'effets en demi-résolution ; ne redessiner que si le rendu change (lame de l'anneau CPU).
- Écrans éteints ou session verrouillée : WebView masquée et `SensorHub.Paused` (`DisplayForm.UpdateRendering`).

## Mémoire de l'hôte (impératif)

- Rien qui charge le shell Windows (extensions shell, `Shell.Application`) dans l'hôte : le catalogue
  d'applis est listé par un processus enfant `ArkaHyte.exe --export-apps` (+50 Mo restés à vie sinon).
- L'hôte alloue très peu en .NET, donc le GC ne passe presque jamais alors que les objets WinRT (SMTC)
  retiennent de la mémoire native et des handles jusqu'à finalisation : `SensorHub` force un GC toutes
  les 30 s. `HeapHardLimit` (csproj) réduit le budget gen0, calé sinon sur le cache L3 (96 Mo).
- Vérifier avec `--memory-log` + `scripts\Measure-ArkaHyte.ps1` : la mémoire privée doit rester stable.

## Conventions

- Tabulations partout (C#, JS, CSS, JSON, PowerShell).
- Code, commentaires, logs, messages d'erreur en anglais ; textes affichés à l'utilisateur en français.
- Branche de travail `dev` (remote `origin` = github.com/Arkatheros/ArkaHyte). Commit/push uniquement sur demande.
- Icône : régénérer `src/ArkaHyte/Assets/app.ico` avec `scripts/New-AppIcon.ps1` (commande dans le README).
