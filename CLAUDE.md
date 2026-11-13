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

# Tester sans publier : copie installée arrêtée, bin\ArkaHyte lancé, copie installée relancée en sortant
# (menu Quitter ou Ctrl+C). Moyen normal de faire valider une modification par l'utilisateur.
scripts\Test-Dev.ps1 [-Arguments '--setup'] [-NoBuild]

# Installation de dev au démarrage (PowerShell admin, tâche planifiée vers bin) / retrait
scripts\Install.ps1
scripts\Install.ps1 -Uninstall

# Installeurs Velopack en local (artifacts\releases\ArkaHyte-Setup.msi/.exe), sans publier
# Version toujours > dernière release publiée ; packId ArkaHyte.App à ne jamais changer (mises à jour)
scripts\New-Release.ps1 -Version 1.0.5

# Release GitHub : <Version> du csproj montée et poussée, puis tag identique poussé
# → .github/workflows/release.yml lance New-Release.ps1 -Publish avec le jeton du workflow
git tag v1.0.5; git push origin v1.0.5
```

YAML : seule exception aux tabulations (interdites pour l'indentation), espaces dans `.github/workflows`.

Pas de tests automatisés ni de linter. Vérification par les options one-shot de `ArkaHyte.exe`
(liste complète dans `Program.cs` et le README) :

- `--screenshot out.png` : rend l'interface, capture, quitte. Moyen principal de valider un changement visuel.
- `--dump out.json` : un échantillon de tous les capteurs + entrées HWiNFO disponibles.
- `--demo stress|idle|game`, `--preview-speedtest [done]`, `--open-editor 0` : états à combiner avec `--screenshot`.
- `--setup-preview out.png [--setup-step 0-4]` : capture de l'assistant, autonome (pas de mutex) et sans écriture.
- `--speedtest out.txt` : vrai test de débit (sature la connexion, ne pas lancer sans raison).
- `--devtools` : F12.

Consommation : `scripts\Measure-ArkaHyte.ps1 [-Seconds 60] [-OutFile x.json]` sur l'instance en cours
(CPU par processus en % d'un cœur, GPU, mémoire ; hôte + arbre WebView2 uniquement).

Itération UI rapide : ouvrir `src/ArkaHyte/wwwroot/index.html` (ou `setup.html`) dans un navigateur. Sans hôte
WebView2 (`window.chrome.webview` absent), `console.js` / `setup.js` passent en **mode design** avec données simulées.

## Architecture

**Flux de données C# → web.** `SensorHub` échantillonne toutes les sources chaque seconde sur un
thread de fond et publie un `Snapshot` (`Sensors/Snapshot.cs`, records sérialisés en camelCase,
`null` → `--` côté UI). Chaque source est isolée : une panne ne vide que ses widgets.
`DisplayForm` relaie via `PostWebMessageAsJson` ; `console.js` dispatche sur `message.type`
(`snapshot`, `cover`, `config`, `toast`, `speedtest`, `apps`). Les événements hors thread UI passent
par `PostFromAnyThread` (BeginInvoke).

**Flux web → C#.** `host.postMessage({ type, ... })` traité dans le `switch` de `DisplayForm` :
`ready`, `launch`, `media`, `listApps`, `saveShortcut`, `speedtest` (start/cancel), `hwinfoList` /
`saveMetric` (réglages à l'écran : appui long sur une mesure → panneau `#sheet`, `SHEETS` dans `console.js`).
Choix de capteurs et seuils écrits par `SensorSettings` (partagé avec l'assistant), appliqués à chaud
(`SensorHub.UpdateSettings`, `config.thresholds` → `limits` côté page ; plus aucun seuil en dur).
Ajouter une fonctionnalité interactive = un type de message de chaque côté.

**Sources** (`Sensors/`) : HWiNFO Gadget via registre `HKCU\Software\HWiNFO64\VSB` (capteurs
retrouvés par nom/libellé avec jokers, configurés dans `appsettings.json` → `HwInfo`, `Drives[].Temp` ;
relu seulement après une écriture de HWiNFO, signalée par `RegNotifyChangeKeyValue`, donc à son rythme),
Disques : `DiskData.Disk` = numéro de disque physique (IOCTL, `HardwareDetector.ReadDiskNumber`, mis en
cache par `SensorHub`) ; l'UI regroupe les lecteurs consécutifs d'un même disque sous une température.
FPS : capteur PresentMon de HWiNFO, nommé d'après le processus au premier plan (`PresentMon [x.exe]`,
suffixe neutralisé par `HwInfoReader`) et ne comptant que si la fenêtre au premier plan couvre son
écran ou appartient à un jeu reconnu par Windows (liste de la Game Bar, `HKCU\System\GameConfigStore\Children`
→ `MatchedExeFullPath`, relue au plus une fois par minute ; `ForegroundGame`), ce qui couvre les jeux en fenêtre.
**Vue jeu.** Un jeu reconnu par Windows passé au premier plan est suivi jusqu'à la fin de son processus
(`ForegroundGame` → `Snapshot.Game` : nom, fréquence de l'écran). `console.js` (`renderGame`) passe alors
`.frame` en `game` (+ `has-game` tant qu'un jeu tourne) : mêmes widgets GPU / CPU / réseau déplacés, dock gardé (lecture en cours masquée)
par CSS, éléments `.game-only` (en-tête, cadran FPS en SVG, trio, courbe 60 s), retour 10 s après le jeu.
Bouton `#viewSwitch` (vue normale ↔ jeu, choix gardé jusqu'à la fin du jeu). Valeurs lissées comme ailleurs,
courbe défilée par `transform` (`scrollGraph`), anneau CPU (compteur de charge) à gauche de température / consommation. 1 % low / temps de frame : `HwInfo.GpuFpsLow`,
`GpuFrameTime` facultatifs, sinon calculés côté page.
`wlanapi` natif lu à offsets fixes (`WifiReader`), `NetworkInterface` + `Ping` (statut
online/no-internet/offline), SMTC pour la lecture en cours (`MediaMonitor`), API Windows pour CPU/RAM/disques.
Météo : `WeatherService` (Open-Meteo, HTTP toutes les 15 min, ville géocodée dans l'assistant) → message
`weather` ; activée / désactivée par appui long sur l'horloge (`saveMetric` `{ weather }`).
`SpeedTest` pilote le CLI Ookla (`--format=jsonl --progress=yes`), téléchargé au premier usage dans
`%LOCALAPPDATA%\ArkaHyte\tools` (licence : ne pas l'inclure au dépôt). Cloudflare abandonné (blocage IP).

**Fenêtre.** Écran du boîtier trouvé par sa taille (`Display` dans `appsettings.json`), fenêtre sans bordure
non activante (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `MA_NOACTIVATE`) pour ne jamais voler le focus.
Hôte virtuel `https://hyte.local/` → `wwwroot`. Instance unique (mutex `Local\ArkaHyte`).
`TouchGuard` : hook souris bas niveau sur thread dédié qui renvoie le curseur sur l'écran principal
après un toucher sur le boîtier (+ watchdog). `TrayIcon` + `SithMenuRenderer` : menu de notification thématisé.

**Assistant de configuration.** `SetupForm` : fenêtre normale sur l'écran principal, WebView2 sur `setup.html`
(même dossier de données, donc même processus navigateur ; libéré à la fermeture). Ouvert depuis
`DisplayForm.OnHandleCreated` tant que `SetupCompleted` est faux (pas `Load` : la fenêtre reste cachée
sans écran du boîtier), ou par le menu. Messages : `state` (écrans, matériel détecté par
`HardwareDetector` via le registre), `hwinfo` toutes les 2 s ; retour `save`/`skip`/`identify`/`startHwInfo`.
`AppSettings.SaveUser` modifie l'arbre JSON (le reste du fichier est conservé) ; capteurs inchangés omis
pour garder les jokers. Après `save`, `DisplayForm.RestartRequested` → `Program` relance l'exe avec
`--restarted`, qui attend le mutex de l'instance sortante.

**Raccourcis du dock.** Défauts dans `appsettings.json` → `Shortcuts`, surchargés par
`%LOCALAPPDATA%\ArkaHyte\shortcuts.json` (`ShortcutStore`). L'éditeur (appui long) liste les applis via
`shell:AppsFolder` (`AppCatalog`, icônes `Native/ShellIcons.cs`). Logos d'applis recolorés en blanc côté web
(`monochromeIcon`) ; icônes intégrées = `BUILTIN_ICONS` (`console.js`, noms déjà enregistrés : ne pas renommer)
+ sélection Lucide générée dans `js/icons.js` (`LUCIDE_ICONS`, `LUCIDE_GROUPS`).

**Données utilisateur** : `%LOCALAPPDATA%\ArkaHyte\` (logs `display.log`, `launcher.log`, shortcuts, tools).

**Installation et mises à jour (Velopack).** `VelopackApp.Build()…Run()` en tête de `Main` (hooks
d'install / mise à jour / désinstallation → `Installation.cs`, 30 s max puis exit). Installé dans
`%LOCALAPPDATA%\ArkaHyte.App` : packId distinct du dossier de données, que la désinstallation supprimerait.
Démarrage : tâche planifiée « Arka Hyte » à l'ouverture de session (sans admin, priorité 4, sans limite de durée), la clé HKCU `Run` passait ~35 s après le logon (file d'Explorer) et ne sert plus que de secours. HYTE Nexus n'est pas géré par l'appli ni l'installeur : c'est à
l'utilisateur de le désactiver ou le désinstaller (choix du propriétaire). `AppUpdater` lit les releases de github.com/Arkatheros/ArkaHyte (dépôt public requis) et
n'installe que sur clic du menu. `AppSettings.Load()` préfère `%LOCALAPPDATA%\ArkaHyte\appsettings.json`
(copié à l'installation). Version = `<Version>` du csproj, passée par `New-Release.ps1`. Garder le paquet
`Velopack` et l'outil `vpk` (`dotnet-tools.json`) à la même version.

**Démarrage (dev)** : tâche planifiée `Arka Hyte` (AtLogOn) → `scripts/Start-ArkaHyte.ps1` → `bin\ArkaHyte\ArkaHyte.exe`.
Sous PowerShell 5.1, ne pas utiliser `$PSScriptRoot` dans les valeurs par défaut de `param()` (vide).

**Thèmes.** `Themes` (`AppSettings.cs`) : `sith` / `jedi`, `Theme` dans `appsettings.json`, choisi par le menu
de l'icône (`DisplayForm.SetTheme` → message `config` `{ theme }`, sans recharger). Dans `console.css`, toute
couleur liée au thème passe par une variable (`--blade-rgb`, `--line-rgb`, `--panel-rgb`…, triplets pour
`rgba()`), redéfinie par `:root[data-theme="jedi"]` ; ne jamais réintroduire de rouge en dur. `console.js` lit
ces triplets dans `palette` (`readPalette`) pour le canvas ; `THEMES` y donne le fond, les particules et
l'ombrage du ciel (`skyShade`). Fond clair (Jedi) : ne pas assombrir l'image (choix du propriétaire), rendre
le thème lisible (`--halo` hérité depuis `.frame`, `-webkit-text-stroke` sur les chiffres, `--track` foncé).
Alertes : `--ember-rgb` par thème (orange Sith, violet Jedi), rien de plus qu'un changement de couleur
(une plaque sombre derrière les valeurs a été refusée). Fourreaux `.hilt-sith` / `.hilt-jedi` dans le SVG du sabre VRAM. Le menu de
l'icône suit le thème (`SithMenuRenderer.SetTheme`).

## Règles de performance de l'UI (impératives)

L'écran tourne en permanence : cible ~16 % d'un cœur.
- Boucle d'animation unique à 24 fps via `setTimeout` dans `console.js` ; tout nouvel effet s'y branche.
- Valeurs affichées lissées entre deux échantillons : `glide(clé, cible, draw)` (`console.js`) les fait glisser
  à vitesse constante sur la durée du changement précédent, `stepGlides` dans la boucle ; toute nouvelle valeur
  passe par là. Jauges sur la valeur brute ; chiffres sur leur propre glide avec `NUMBER` (moyenne des
  3 derniers échantillons, réécrits 4 fois par seconde au plus). Écritures DOM par `setText` / `setStyle`,
  seulement si le contenu change.
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
