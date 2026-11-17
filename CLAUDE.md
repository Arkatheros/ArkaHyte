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
- `--demo stress|idle|game|desktop`, `--preview-speedtest [done]`, `--open-editor 0` : états à combiner avec `--screenshot`.
- `--setup-preview out.png [--setup-step 0-11]` : capture de l'assistant, autonome (pas de mutex) et sans écriture.
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
`saveMetric` (réglages à l'écran : appui long sur une mesure → panneau `#sheet`, `SHEETS` dans `console.js` ;
toucher le compteur CPU → `#cpuDetail` (fréquence, tension, PPT, cœurs actifs : `HwInfoSettings.Cpu*` ou leurs
`*Defaults` nommés comme HWiNFO ; fréquences par cœur `HwInfoReader.CoreClocks` ; charge par fil
`SystemMetrics.ReadThreadLoads` / `NtQuerySystemInformation`), option `CpuDetails` (`saveMetric { cpuDetails }`) pour
fréquence et tension à côté du compteur ;
éditeur du dock : onglets applications / actions, cibles `action:lock|sleep|screenshot` (`SystemCommands`,
`ms-screenclip:`) et `custom:{id}` (`AppSettings.CustomActions` : url ou script .ps1, résolus au lancement par
`ShortcutLauncher`, créés à l'étape « Raccourcis » des Paramètres, fichier choisi par `pickScript`) ; veille confirmée
par un second toucher ; bouton vidé (`clearShortcut`, cible vide) affiché « + », ouvre l'éditeur ;
barre de progression de la lecture en cours (`MediaData.CanSeek`, message `media` `{ action: seek, position }`
→ `MediaMonitor.SeekAsync`, exclue du balayage par `data-no-swipe`) ;
pastille audio de la lecture en cours → `#audioPanel` (`Audio/AudioService` : Core Audio par COM, `Native/CoreAudio.cs`,
`IPolicyConfig` non documenté pour la sortie par défaut ; notifications `IMMNotificationClient` /
`IAudioEndpointVolumeCallback`, appels hors thread UI, message `audio` regroupé sur 60 ms ; retour `audio`
`{ action: volume | output | mic }`) ;
toucher court sur une tuile température / consommation → `#history`, 30 min en tampon circulaire côté page,
`HISTORY_METRICS`, courbe redessinée à chaque échantillon seulement panneau ouvert).
Choix de capteurs et seuils écrits par `SensorSettings` (partagé avec l'assistant), appliqués à chaud
(`SensorHub.UpdateSettings`, `config.thresholds` → `limits` côté page ; plus aucun seuil en dur).
Ajouter une fonctionnalité interactive = un type de message de chaque côté.

**Configuration de HWiNFO** (`HwInfoConfigurator`, `ArkaHyte.exe --configure-hwinfo` lancé élevé par l'assistant :
HWiNFO tourne en admin). Réglage par mesure dans `HKCU\Software\HWiNFO64\Sensors\{capteur}\{Type}{n}` (`InVSB`,
`VSBidx`, `Color`), lu au démarrage de HWiNFO et réécrit à sa sortie : modifié HWiNFO arrêté. Découverte : toutes
les mesures possibles signalées avec `VSBidx` ≥ 100000 (Gadget numérote ses emplacements d'après `VSBidx`, trous
compris) et une couleur unique, puis seules `Wanted` (noms anglais / français exacts) gardées ; échec = tout
restauré. Ajouts suivis dans `hwinfodded.json` (retirés s'ils ne sont plus voulus), emplacements de découverte
effacés de `VSB` (HWiNFO laisse les anciennes valeurs). Lecteurs : parcourir les emplacements présents
(`HwInfoReader.Slots`), jamais s'arrêter au premier trou.
**Sources** (`Sensors/`) : HWiNFO Gadget via registre `HKCU\Software\HWiNFO64\VSB` (capteurs
retrouvés par nom/libellé avec jokers, configurés dans `appsettings.json` → `HwInfo`, `Drives[].Temp` ;
relu seulement après une écriture de HWiNFO, signalée par `RegNotifyChangeKeyValue`, donc à son rythme),
Disques : `DiskData.Disk` = numéro de disque physique (IOCTL, `HardwareDetector.ReadDiskNumber`, mis en
cache par `SensorHub`) ; l'UI regroupe les lecteurs consécutifs d'un même disque sous une température.
FPS : capteur PresentMon de HWiNFO, nommé d'après le processus au premier plan (`PresentMon [x.exe]`,
suffixe neutralisé par `HwInfoReader`) et ne comptant que si la fenêtre au premier plan couvre son
écran ou appartient à un jeu reconnu par Windows (liste de la Game Bar, `HKCU\System\GameConfigStore\Children`
→ `MatchedExeFullPath`, relue au plus une fois par minute ; `ForegroundGame`), ce qui couvre les jeux en fenêtre.
**Vue jeu.** Un jeu passé au premier plan est suivi jusqu'à la fin de son processus. Jeu = liste de la Game Bar,
`GameView.ExtraGames` (nom d'exécutable, ajouté dans les Paramètres parmi les applis ouvertes : message
`listProcesses` → `processes`) ou, avec `DetectEngines` (vrai par défaut, hors interface), classe de fenêtre `UnrealWindow` / `UnityWndClass`
(lanceur Epic et éditeurs exclus) ; règles poussées par `ForegroundGame.Configure` (`SensorHub`).
Le jeu suivi
(`ForegroundGame` → `Snapshot.Game` : nom, fréquence de l'écran). `console.js` (`renderGame`) passe alors
`.frame` en `game` (+ `has-game` tant qu'un jeu tourne) : mêmes widgets GPU / CPU / réseau déplacés, dock gardé (lecture en cours masquée)
par CSS, éléments `.game-only` (en-tête, cadran FPS en SVG, trio, courbe 60 s), retour 10 s après le jeu.
Deux pages : balayage horizontal sur `.frame` (`touch-action: pan-y`, clic de fin de geste avalé ; doigt suivi par les
événements `touch*`, pas `pointer*` : un défilement commencé, comme sur les tuiles Maison, annule le pointeur ; après un geste rapide Chromium
peut avaler le clic de l'appui suivant : appui court sans clic → clic produit par la page, `tapRepair`) ou points
`#pageDots` → `viewChoice` (gardé jusqu'à la fin du jeu, ou jusqu'au prochain jeu sans partie en cours) ;
vue jeu sans jeu possible (`renderNoGame`). Jeu en arrière-plan
(`GameData.Foreground` faux) ou sans FPS : valeurs et courbe figées en gris (`.stale`) ; sans choix manuel,
vue jeu seule au lancement d'un jeu si `GameView.AutoGame` (sinon seulement au balayage), vue normale après
`GameView.AutoNormalDelaySec` si `AutoNormal` (étape « Vue jeu » de l'assistant, `config.gameView`). Valeurs lissées comme ailleurs,
courbe défilée par `transform` (`scrollGraph`), anneau CPU (compteur de charge) à gauche de température / consommation. 1 % low / temps de frame : `HwInfo.GpuFpsLow`,
`GpuFrameTime` facultatifs, sinon calculés côté page.
`wlanapi` natif lu à offsets fixes (`WifiReader`), `NetworkInterface` + `Ping` (statut
online/no-internet/offline), SMTC pour la lecture en cours (`MediaMonitor`), API Windows pour CPU/RAM/disques.
Météo : `WeatherService` (Open-Meteo, HTTP toutes les 15 min, ville géocodée dans l'assistant) → message
`weather` ; activée / désactivée par appui long sur l'horloge (`saveMetric` `{ weather }`).
Coût électrique : `EnergyMeter` (dans `SensorHub`, y compris en pause : seule la lecture HWiNFO tourne alors)
intègre (CPU × `CpuPercent` + GPU × `GpuPercent` + `Energy.OtherWatts`, somme de `Energy.Components` quand détaillé,
toujours réécrit pour les anciennes versions) ÷ `PsuEfficiency` en kWh par jour (`energy.json`, écart > 5 s entre
échantillons ignoré = veille) et par session de jeu (`Snapshot.Game`) → `Snapshot.Energy` ; le coût est calculé
côté page au prix courant. Bloc `#energyCorner` (`.energy-side`) à droite du cadran CPU, `.frame.energy-on` range alors
fréquence et tension à gauche ; panneau `#energy` (toucher sur `#energyCorner` ou `#gameEnergy`), jours passés
demandés à l'ouverture (`energyHistory` → `energy`, 365 jours ; barres semaine / mois / an côté page),
« Réinitialiser » confirmé par un second toucher (`energyReset` → `EnergyMeter.Reset`), « Régler » → `openSettings { step: 'energy' }`.
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
pour garder les jokers. Deux modes selon `SetupCompleted` : assistant (étapes, résumé, enregistrer et
redémarrer) ou **Paramètres** (`settings-mode` : navigation libre, Fermer / Enregistrer, fenêtre gardée ouverte,
message `saved`). `Saved(restart)` : redémarrage seulement au premier setup ou si l'écran du boîtier change
(`DisplayForm.RestartRequested` → `Program` relance l'exe avec `--restarted`, qui attend le mutex de l'instance
sortante) ; sinon `DisplayForm.ReloadSettings` applique à chaud (capteurs, matériel, météo, vue jeu).

**Raccourcis du dock.** Défauts dans `appsettings.json` → `Shortcuts`, surchargés par
`%LOCALAPPDATA%\ArkaHyte\shortcuts.json` (`ShortcutStore`). L'éditeur (appui long) liste les applis via
`shell:AppsFolder` (`AppCatalog`, icônes `Native/ShellIcons.cs`). Logos d'applis recolorés en blanc côté web
(`monochromeIcon`) ; icônes intégrées = `BUILTIN_ICONS` (`console.js`, noms déjà enregistrés : ne pas renommer)
+ sélection Lucide générée dans `js/icons.js` (`LUCIDE_ICONS`, `LUCIDE_GROUPS`).

**Données utilisateur** : `%LOCALAPPDATA%\ArkaHyte\` (`DataFolder`) : réglages, shortcuts et `energy.json` à la racine, `logs\`
(`Log` : un fichier par jour `display-AAAA-MM-JJ.log`, 7 jours gardés, 10 Mo max par jour ; `launcher-*` du
lanceur de dev), `hwinfo\` (`backup-*.reg` : la dernière seulement, `catalog.json`, `added.json`), `ui\` (profil
WebView2, renommé depuis `WebView2\` quand aucun navigateur ne le tient), `tools\`. `DataFolder.MoveLegacyFiles`
range les fichiers des versions précédentes au démarrage. Export / import (`ConfigBackup`, liens du pied de
l'assistant, messages `exportConfig` / `importConfig`) : un seul JSON (`format` `ArkaHyte.Config`) avec appsettings,
shortcuts et energy ; l'import copie d'abord les fichiers remplacés dans `backup\before-import-*`, passe l'historique
par `EnergyMeter.Replace` (sinon réécrit à l'arrêt) puis redémarre l'affichage. Fichiers HWiNFO exclus (propres à Windows).

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

**Disposition** (étape « Disposition », `data-page="9"`, puis « Maison » = 10, « Terminer » = 11). Chaque vue = pile de bandes pleine largeur
(`LAYOUT_VIEWS` dans `console.js` : haut d'origine, widgets de la bande ; hauteur = jusqu'au haut de la suivante), dock fixe.
Réglage `Layout` (`LayoutSettings` : `Normal` / `Game` = `[{ Id, Show }]`, ids aussi dans `BANDS` de `setup.js` ;
`EffectiveLayout` normalisé) → `config.layout` → `placeBands` : décalage par `margin-top` (`--dy` / `--dy-game`, posés
sur les widgets, jamais `top` ni `transform`, laissés aux animations), classes `band-off` / `band-off-game`
(`display: none`), place des bandes masquées répartie entre les autres. Nouveau widget pleine largeur : l'ajouter à
une bande ou créer une bande (les trois listes). Aperçu en direct : `previewLayout { layout, view }` → `SetupForm.LayoutPreviewChanged`
→ message `layoutPreview` (`view` force la vue affichée, `applyView` ; null en quittant l'étape ; `{ end }` à la fermeture),
déplacements animés (`arrangeBands`, FLIP).
**Maison** (Home Assistant, [étude](docs/etude-home-assistant.md)) : troisième page à gauche de la vue normale
(`VIEW_ORDER` home · normal · game dans `console.js`, `currentView`, classes `.frame.home` / `.game` posées par
`setViewClasses` ; balayage et points `#pageDots` sur les trois, Maison absente tant que `config.home.enabled` est faux).
`Home/HomeAssistantClient` (C#) : WebSocket `/api/websocket` (`HomeSocket` : auth par jeton, requêtes numérotées,
abonnements), `subscribe_entities` limité aux entités choisies (format compressé `a` / `c` / `r`, non documenté, celui
du frontend HA), attributs filtrés (`KeptAttributes`), changements regroupés 150 ms → message `home` (JSON texte)
`{ status, version, areas, full, entities: { id: { state, attrs, changed } }, removed }` ; reconnexion 2 → 60 s, arrêt
sur `auth_invalid` ; muet en pause (`Paused`, tout renvoyé à la reprise) ; buffer de réception réutilisé (mémoire de l'hôte).
Commandes `homeCall { domain, service, entityId, data }` limitées à `AllowedServices` et aux entités suivies, échec →
`homeCallFailed` + toast. Jeton : `HomeToken` (DPAPI utilisateur, `home-token.bin`, jamais dans appsettings ni
exporté ni renvoyé aux pages ; connecté (jeton enregistré ou catalogue lu) : adresse + « Se déconnecter » seuls
(`#homeLinked`, formulaire `#homeConnectForm` masqué, `renderHomeDisconnect`) ; `homeSetup.forget` → aperçu `forget` (jeton vide, le
jeton enregistré ignoré aussi par `homeRead`) puis `homeForget` à l'enregistrement → `HomeToken.Delete`). Réglage `Home` `{ Enabled, Url, Groups: [{ Name, Columns 1-3, Tiles: [{ Id, Span 1-3 }] }] }`,
séparateur = entrée de `Groups` avec `Separator` (hauteur 8-600 px) et `Line` (`.home-separator`, jamais cible des ajouts),
(groupes de l'utilisateur, pas les pièces de HA ; `EffectiveGroups` borne et dédoublonne, l'ancien `Entities` plat
lu comme un groupe « Maison »), `config.home` (`HomeConfig`). Page : `buildHome` (un groupe = titre + grille
`--cols`, tuile `grid-column: span`, mise en page large dès 2/3 de la ligne ; scènes en pastilles rondes, icône
d'après le nom `sceneIcon` ; icône choisie par tuile `Tiles[].Icon` = nom de `js/home-icons.js` (sélection Lucide
générée par familles d'équipement, `HOME_ICONS` / `HOME_ICON_GROUPS`, `chosenIcon`, sinon automatique selon le type
et l'état) ; volets : boutons haut / stop / bas ; chauffage : − / +, compact si étroit ; toucher = `turn_on` /
`turn_off` d'après l'état affiché, jamais `toggle` ; appui long `attachLongPress` → `#homePanel` (`openHomePanel` :
lumière / volet / chauffage, curseurs à la `audio-volume` envoyés au lâcher) ; toucher un capteur → courbe 24 h
(`homeHistory` → `HomeAssistantClient.HistoryAsync`, `history/history_during_period` réduit à un point par 5 min)),
`renderHomeTile` seulement page affichée (`home.dirty` sinon), anneau CPU non dessiné sur cette page. Étape
« Maison » des Paramètres : groupes créés / renommés / supprimés (second clic) / déplacés (`startRowDrag` sur
l'en-tête), entités déplacées entre groupes (`startTileDrag`), ajout par « + Ajouter » au pied d'un groupe
(`openHomeAdd` → `#homeAddPicker` : cases cochées dans `homeSetup.adding.picked`, `confirmHomeAdd` ; entités déjà
placées grisées, une seule fois sur la page ; sans groupe, « + Ajouter des éléments » crée « Maison ») ; glisser-déposer suivi au niveau de la fenêtre (`followPointer` : une ligne
déplacée dans le DOM perd sa capture du pointeur). `homeRead` → `HomeCatalog.ReadAsync` (`get_states` + registres
zones / entités / appareils, pièce affichée dans la liste seulement) → `homeCatalog` ; aperçu
`previewHome { home, token, focus }` → `SetupForm.HomePreviewChanged` → `homePreview` (`focus` affiche la page Maison).
Mode design : maison simulée (`startHomeDemo`, `?view=home`), réponses aux commandes simulées.
**Fond personnel** (étape « Apparence » des Paramètres, `data-page="8"`). `BackgroundStore` :
image choisie (`pickBackground` → `OpenFileDialog`, copiée aussitôt en `source-*` dans `DataFolder.Backgrounds`),
cadre au ratio de l'écran dessiné dans `setup.js` (fractions de l'image redressée), rendu une seule fois en C#
(GDI+, orientation EXIF appliquée, 682 × 2560, `background-*.jpg` nouveau nom à chaque rendu) dans `Save`, seulement
quand une image est en cours de recadrage. Réglage `Background` = `{ Enabled, Image, Shade }` : seul le rendu est gardé
(choix du propriétaire), la copie de la photo effacée après enregistrement ; recadrer = la choisir à nouveau (l'étape
montre le rendu seul, sans cadre, `.crop-stage.saved`). Fond du thème enregistré : dossier vidé. Vidéo (MP4 / WebM,
1080p au plus vérifié par `GetVideoPropertiesAsync`, `ImportAsync`) : jamais rendue, gardée comme `Image` avec son
`Crop` (recadrable sans la re-choisir), jouée par `#bgVideo` sous le canvas `#bg` (alors effacé, ombrage + grain seuls),
cadre par `object-view-box`, pause si `document.hidden` ou jeu en cours (`updateVideoPlayback`), jamais exportée
(choix du propriétaire). Les deux pages lisent ce dossier par l'hôte virtuel `hyte-user.local` (`Allow`, voir la
lisibilité) ; `config.background` `{ url, shade, crop }` → `applyBackground` (`console.js`),
jamais de particules sur un fond personnel (`drawFx` ne dessine plus rien ; option retirée, choix du propriétaire). Aperçu en direct sur l'écran du boîtier dès qu'on
touche à l'étape : `previewAppearance` (thème, palette, fond ; 40 ms au plus) → `SetupForm.AppearancePreviewChanged` → message `appearancePreview`
`{ url, crop, shade }`, cadre découpé dans l'original (`drawImage` source rect, image gardée chargée) ou rendu enregistré
tel quel, `{ end }` à la fermeture des Paramètres (retour au fond enregistré, avant le `CleanUp`). Tout sauf le rendu
enregistré est effacé après enregistrement et à la fermeture des Paramètres (`CleanUp`) ; rendu exporté en base64 (`ConfigBackup`).
**Thèmes, palettes, lisibilité** : trois attributs indépendants posés par `console.js` sur la racine.
- `data-theme` (`Themes`, `sith` / `jedi` / `coruscant` / `dagobah`, affichés Mustafar / Tatooine / Coruscant / Dagobah (identifiants `sith` / `jedi` gardés pour les réglages existants), `Theme` dans `appsettings.json`, étape Apparence seulement, appliqué par `ReloadSettings`) :
  le monde. `THEMES` (`console.js`) : fond, particules, ombrage du ciel (`skyShade`), palette propre, clarté ;
  CSS : fourreau `.hilt-sith` en Sith, `.hilt-jedi` ailleurs ; voiles de fumée et taches de lave en Sith seulement
  (masqués aussi sur un fond personnel, animation `animateBackground` arrêtée). Particules `none` (Coruscant, Dagobah) : `drawFx`
  ne dessine rien. Ajouter un thème : `Themes.All` (C#, avec sa palette), `THEMES` (`console.js`), `THEME_CARDS`
  (`setup.js`), fond 682 × 2560 dans `assets/` (recadré au centre, JPEG ~250 Ko ; un point très clair derrière un texte, comme
  la lune de Dagobah, voilé à la couleur des nuages autour au moment du recadrage).
- `data-palette` (`Palettes`, 7 couleurs de sabres ; pas de « couleurs du thème » (choix du propriétaire) : choisir un
  thème pose sa palette (`Themes.PaletteOf`, rouge / bleu), qu'on change ensuite ; `Palette` absent ou inconnu =
  celle du thème, `EffectivePalette` ; étape Apparence seulement (le menu de l'icône en prend les couleurs) → `config` `{ palette }`,
  sans recharger) : toutes les couleurs sont des
  variables (`--blade-rgb`, `--line-rgb`, `--panel-rgb`…, triplets pour `rgba()`), redéfinies par
  `:root[data-palette="…"]` ; ne jamais réintroduire de couleur en dur. `console.js` les lit dans `palette`
  (`readPalette`) pour le canvas. Alertes : `--ember-rgb` par palette, teinte qui tranche avec la lame (orange sur
  rouge / vert / violet, violet sur bleu / orange, rouge sur jaune / blanc), rien de plus qu'un changement de couleur
  (une plaque sombre derrière les valeurs a été refusée). `--upload-rgb` : phase d'envoi du test de débit. Le menu
  suit la palette affichée (`TrayIcon.SetPalette` → `SithMenuRenderer.SetPalette`). Catalogue C# (`Palettes.All`) = noms
  et couleurs du menu et des pastilles ; jeux complets de variables dans `console.css` seulement.
- `data-bright` : fond clair → ne pas assombrir l'image (choix du propriétaire), rendre l'écran lisible (`--halo`
  hérité depuis `.frame`, `-webkit-text-stroke` sur les chiffres, `--track` foncé), dans le ton le plus sombre de
  la palette. Thème : `THEMES.bright` (Jedi, Coruscant, Dagobah) ; image personnelle : luminance moyenne mesurée sur une copie 34 × 128
  (`isBrightPicture`, seuil 0,28, d'où `Allow` sur `hyte-user.local` et `crossOrigin`).

## Règles de performance de l'UI (impératives)

L'écran tourne en permanence : cible ~16 % d'un cœur.
- Boucle d'animation unique à 24 fps via `setTimeout` dans `console.js` ; tout nouvel effet s'y branche.
- Valeurs affichées lissées entre deux échantillons : `glide(clé, cible, draw)` (`console.js`) les fait glisser
  à vitesse constante sur la durée du changement précédent, `stepGlides` dans la boucle ; toute nouvelle valeur
  passe par là. Jauges sur la valeur brute ; chiffres sur leur propre glide avec `NUMBER` (moyenne des
  3 derniers échantillons, réécrits 4 fois par seconde au plus). Écritures DOM par `setText` / `setStyle`,
  seulement si le contenu change.
- Vue cachée = rien de dessiné : glides de la vue jeu avec `hidden: GAME_HIDDEN` (valeur tenue à jour, aucun
  dessin), courbe marquée `graph.dirty`, tout repeint d'un coup au retour (`paintGameView`).
- Aucune `transition` CSS ni animation CSS infinie, pas de `backdrop-filter`.
- Seule exception à la boucle : effets ponctuels en Web Animations sur `transform` / `opacity` (compositeur),
  ex. changement de vue normale ↔ jeu (`switchView`, `GameView.Transition` : `morph` = FLIP des widgets
  communs + fondu des autres, `slide`, `fade`, `none` ; puis `refillGauges` remonte jauges et cadrans depuis 0).
- Fond vidéo : hors boucle, décodé par le GPU et composité par Chromium ; mesuré à +5 % d'un cœur en 720p, d'où la
  limite à 1080p et la pause en jeu.
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
