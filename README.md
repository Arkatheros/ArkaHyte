# Arka Hyte – écran du Hyte Y70 Touch

Remplace HYTE Nexus sur l'écran du boîtier (682×2560, portrait) par une application maison :
**Arka Hyte** (nom technique `ArkaHyte` : exécutable, dossiers, journaux), une fenêtre WebView2 qui affiche l'interface web de `src/ArkaHyte/wwwroot`
alimentée par des données réelles lues en C#.

## Structure

| Dossier | Contenu |
|---|---|
| `src/ArkaHyte` | Application C# .NET 10 (WinForms + WebView2) |
| `src/ArkaHyte/wwwroot` | Interface : `index.html`, `css/console.css`, `js/console.js`, polices, fond |
| `mockup/sith-console.html` | Maquette de design validée (données simulées) |
| `scripts/` | Lanceur au démarrage et installation |
| `bin/` | Binaires publiés |
| `Skins/` | Ancien skin Rainmeter (obsolète) |

## Sources de données

| Donnée | Source |
|---|---|
| Charge CPU, RAM, disques, uptime | API Windows |
| Températures, puissances, charge GPU, VRAM | HWiNFO → Gadget (registre `HKCU\Software\HWiNFO64\VSB`) |
| Wi-Fi (SSID, signal, RSSI, canal, bande, norme, sécurité, débit du lien) | API native `wlanapi` |
| Débits réseau, ping | .NET `NetworkInterface` / `Ping` |
| Lecture en cours (Spotify, YouTube, Twitch…) | API média Windows (`GlobalSystemMediaTransportControls`) |

Les capteurs HWiNFO sont retrouvés **par nom** (voir `HwInfo` dans `appsettings.json`), l'ordre
dans Gadget n'a donc pas d'importance. Jokers `*` acceptés.

### Températures des disques

1. Dans HWiNFO → onglet *HWiNFO Gadget*, cocher la température de chaque disque (capteurs S.M.A.R.T.).
2. Lancer `bin\ArkaHyte\ArkaHyte.exe --dump dump.json` et repérer le nom exact dans `hwInfoEntries`.
3. Renseigner `Drives[].Temp` dans `bin\ArkaHyte\appsettings.json`, par exemple :
	```json
	{ "Name": "C:", "Temp": { "Sensor": "S.M.A.R.T.: Samsung*", "Label": "Température du disque" } }
	```

## Construire

```powershell
dotnet publish src\ArkaHyte -c Release -o bin\ArkaHyte
```

## Installer (PowerShell administrateur)

```powershell
scripts\Install.ps1
```

- désactive la tâche planifiée `HYTE Nexus` et arrête Nexus
- crée la tâche `Arka Hyte` à l'ouverture de session → `Start-ArkaHyte.ps1`
  (et supprime les anciennes tâches `HYTE Display (Rainmeter)` / `HyteDisplay`, l'ancien raccourci et le relais `Start-HyteDisplay.ps1`)
- crée le raccourci **Arka Hyte** dans le menu Démarrer (pour relancer après « Quitter »)

Retour à Nexus : `scripts\Install.ps1 -Uninstall`

## Test de débit

Bouton **Test débit** du panneau Réseau : le test s'affiche dans le panneau, sans en changer la taille.
Compteur de vitesse façon Speedtest à gauche et vitesse en cours à droite. L'aiguille est rouge en
réception, puis redescend à zéro et remonte en ambre pour l'envoi. Le résultat (↓ / ↑ / ping) reste
affiché 30 s, puis le panneau Réseau revient seul.

Le test passe par l'outil officiel **Speedtest by Ookla** (CLI), téléchargé depuis
`install.speedtest.net` au premier test dans `%LOCALAPPDATA%\ArkaHyte\tools\speedtest.exe` (sa
licence n'autorise pas de l'inclure dans le dépôt ; elle est acceptée via `--accept-license
--accept-gdpr`). Il choisit le serveur Ookla le plus proche. Le test sature réellement la connexion
(~1 à 2 Go à 1 Gbit/s) : il ne se lance que sur appui.

Les serveurs de test de Cloudflare ont été abandonnés : ils bloquaient l'adresse 1 h après deux tests.

## Tactile et curseur

Windows place le curseur de la souris à l'endroit de chaque toucher. Arka Hyte intègre une
protection (`TouchGuard.cs`, hook souris sur son propre thread) qui renvoie le curseur à sa
dernière position sur l'écran principal après chaque toucher sur l'écran du boîtier, plus un
filet de sécurité qui le ramène s'il reste immobile sur cet écran. `--touch-log` journalise
chaque clic vu par cette protection.

## Raccourcis du bas

- **Appui court** : lance l'application.
- **Appui long** (~0,65 s) : ouvre l'éditeur à l'écran — liste des applications du menu Démarrer
  (bureau + Store) avec index A–Z, choix de l'icône (logo de l'appli ou icône intégrée).
- Les choix faits à l'écran sont stockés dans `%LOCALAPPDATA%\ArkaHyte\shortcuts.json` et priment
  sur `Shortcuts` de `appsettings.json` (supprimer ce fichier pour revenir aux valeurs par défaut).

## Icône de l'application

`src/ArkaHyte/Assets/app.ico` (exe + zone de notification), générée depuis le logo découpé
`src/ArkaHyte/Assets/empire-logo.png` en rouge sabre sur fond sombre :

```powershell
powershell -File scripts\New-AppIcon.ps1 -SourceImage src\ArkaHyte\Assets\empire-logo.png -Background dark -Recolor '#ff1f33' -LumaMask
```

## Icône de notification

Clic droit sur le sabre rouge dans la zone de notification : **mode démo** (CPU/GPU 100 %, sans internet),
**recharger l'affichage**, **ouvrir le dossier des journaux**, **quitter**.

## Diagnostic

| Commande | Effet |
|---|---|
| `ArkaHyte.exe --dump out.json` | Lit tous les capteurs une fois et écrit le résultat |
| `ArkaHyte.exe --screenshot out.png` | Affiche l'interface, enregistre une capture et quitte |
| `ArkaHyte.exe --demo stress` | Démarre en mode démo (CPU/GPU 100 %, sans internet) |
| `ArkaHyte.exe --open-editor 0` | Ouvre l'éditeur du 1er raccourci au démarrage |
| `ArkaHyte.exe --preview-speedtest [done]` | Affiche le compteur (ou le résumé) du test de débit avec des valeurs d'exemple |
| `ArkaHyte.exe --speedtest out.txt` | Lance un vrai test de débit et écrit chaque étape |
| `ArkaHyte.exe --list-apps out.json` | Liste les applications proposées par l'éditeur |
| `ArkaHyte.exe --menu-preview out.png` | Rend le menu de l'icône de notification en image |
| `ArkaHyte.exe --devtools` | Active F12 (outils de développement) |

Logs : `%LOCALAPPDATA%\ArkaHyte\` (`display.log`, `launcher.log`).

## Modifier l'interface

Ouvrir `src/ArkaHyte/wwwroot/index.html` dans un navigateur : sans l'application, la page
passe en **mode design** avec des données simulées. Republier ensuite avec `dotnet publish`.

Performances mesurées : ~16 % d'un cœur (≈ 1 % du CPU total), ~380 Mo de RAM (WebView2).
