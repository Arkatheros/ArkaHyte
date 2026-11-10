# Arka Hyte – écran du Hyte Y70 Touch

Remplace HYTE Nexus sur l'écran du boîtier (682×2560, portrait) par une application maison :
**Arka Hyte** (nom technique `ArkaHyte` : exécutable, dossiers, journaux), une fenêtre WebView2 qui affiche l'interface web de `src/ArkaHyte/wwwroot`
alimentée par des données réelles lues en C#.

## Structure

| Dossier | Contenu |
|---|---|
| `src/ArkaHyte` | Application C# .NET 10 (WinForms + WebView2) |
| `src/ArkaHyte/wwwroot` | Interface : `index.html`, `css/console.css`, `js/console.js`, polices, fond |
| `scripts/` | Lanceur au démarrage, installation, release, génération de l'icône |
| `installer/` | Textes et images de l'assistant d'installation MSI |
| `.github/workflows` | Release GitHub automatique sur tag `v1.x.y` |
| `bin/` | Binaires publiés (non versionnés) |

Itération sur le design : ouvrir `src/ArkaHyte/wwwroot/index.html` dans un navigateur, l'interface
passe en mode design avec des données simulées.

## Sources de données

| Donnée | Source |
|---|---|
| Charge CPU, RAM, disques, uptime | API Windows |
| Températures, puissances, charge GPU, VRAM | HWiNFO → Gadget (registre `HKCU\Software\HWiNFO64\VSB`) |
| FPS en jeu (affichés seulement pendant un jeu) | Capteur PresentMon de HWiNFO 64 bits, valeur « Displayed » (ou RTSS si RivaTuner tourne) → Gadget (`HwInfo.GpuFps`) ; seulement quand la fenêtre au premier plan est en plein écran |
| Wi-Fi (SSID, signal, RSSI, canal, bande, norme, sécurité, débit du lien) | API native `wlanapi` |
| Débits réseau, ping | .NET `NetworkInterface` / `Ping` |
| Lecture en cours (Spotify, YouTube, Twitch…) | API média Windows (`GlobalSystemMediaTransportControls`) |

Les capteurs HWiNFO sont retrouvés **par nom** (voir `HwInfo` dans `appsettings.json`), l'ordre
dans Gadget n'a donc pas d'importance. Jokers `*` acceptés.

### Assistant de configuration

Au premier lancement, une fenêtre s'ouvre sur l'écran principal (ensuite : menu de l'icône ›
**Assistant de configuration**) :

1. **Écran du boîtier** : choix sur la carte des écrans (bouton pour afficher leurs numéros).
2. **HWiNFO** : vérifie qu'il tourne et transmet des valeurs à Gadget, guide sa configuration.
3. **Capteurs** : une valeur HWiNFO par widget, avec suggestion quand le réglage ne trouve rien.
4. **Matériel** : noms CPU / GPU et mémoire GPU, détectés depuis le registre.
5. **Terminer** : écrit `%LOCALAPPDATA%\ArkaHyte\appsettings.json` (`SetupCompleted: true`) et redémarre l'affichage.

« Passer l'assistant » ne change rien d'autre que `SetupCompleted` ; fermer la fenêtre le repropose
au prochain démarrage.

### Températures des disques

1. Dans HWiNFO → onglet *HWiNFO Gadget*, cocher la température de chaque disque (capteurs S.M.A.R.T.).
2. Lancer `bin\ArkaHyte\ArkaHyte.exe --dump dump.json` et repérer le nom exact dans `hwInfoEntries`.
3. Renseigner `Drives[].Temp` dans `bin\ArkaHyte\appsettings.json`, par exemple :
	```json
	{ "Name": "C:", "Temp": { "Sensor": "S.M.A.R.T.: Samsung*", "Label": "Température du disque" } }
	```

## Installer

**Avant tout, désinstaller ou désactiver HYTE Nexus** (sinon les deux se disputent l'écran du
boîtier) : Arka Hyte n'y touche pas.

Télécharger depuis les [releases GitHub](https://github.com/Arkatheros/ArkaHyte/releases) (pas de
droits admin) :

- **`ArkaHyte-Setup.msi`** : assistant avec confirmation avant d'installer (boutons Windows Installer en anglais)
- `ArkaHyte-Setup.exe` : installation directe en un clic, sans question

Les deux font la même installation :

- installe dans `%LOCALAPPDATA%\ArkaHyte.App` (le runtime .NET 10 Desktop est installé s'il manque)
- lance Arka Hyte à l'ouverture de session (clé `Run` de l'utilisateur) et crée le raccourci du menu Démarrer
- copie `appsettings.json` dans `%LOCALAPPDATA%\ArkaHyte\` : c'est cette copie qui fait foi, les
  mises à jour ne l'écrasent pas.

Désinstaller : Paramètres Windows › Applications. Les données (`%LOCALAPPDATA%\ArkaHyte` :
raccourcis, journaux, réglages) sont conservées ; réactiver HYTE Nexus soi-même si besoin.

### Mises à jour

Arka Hyte vérifie les releases GitHub 1 min après le démarrage puis toutes les 6 h. Une nouvelle
version est annoncée sur l'écran et dans le menu de l'icône (**Installer la version X**) ; elle ne
s'installe que sur ce clic (téléchargement, puis redémarrage de l'affichage). L'entrée sert aussi à
vérifier tout de suite. Rien de tout cela pour une copie lancée depuis `bin\` (développement).

### Publier une version

Le dépôt doit être **public** (les copies installées lisent les releases sans identifiant).

1. Monter `<Version>` dans `src\ArkaHyte\ArkaHyte.csproj`, commiter, pousser.
2. Poser le tag correspondant et le pousser :

```powershell
git tag v1.0.5
git push origin v1.0.5
```

Le workflow [Release](.github/workflows/release.yml) (GitHub Actions, Windows) vérifie que le tag
correspond à la version du csproj puis lance `scripts\New-Release.ps1 -Publish` avec le jeton du
workflow : aucun jeton personnel n'est nécessaire. Suivi dans l'onglet **Actions** du dépôt ; les
installeurs restent aussi attachés à l'exécution. En cas d'échec, supprimer le tag
(`git push origin :refs/tags/v1.0.5`, `git tag -d v1.0.5`), corriger, recommencer.

Construction locale, sans publication (ou publication manuelle avec `$env:GITHUB_TOKEN` et `-Publish`) :

```powershell
scripts\New-Release.ps1 -Version 1.0.5             # installeurs dans artifacts\releases
```

Le script publie l'appli, récupère la release précédente (paquet delta : quelques dizaines de Ko pour
un petit changement), empaquette avec [Velopack](https://velopack.io) (`vpk`, outil local du dépôt :
`dotnet-tools.json`), renomme les installeurs (`ArkaHyte-Setup.*`) et envoie la release. Une version
déjà publiée ne peut pas être reconstruite : toujours monter le numéro.

Textes et images de l'assistant MSI : `installer\` (`welcome.txt`, `conclusion.txt`, `banner.bmp`,
`dialog.bmp`) ; images régénérées depuis l'icône par `powershell -File scripts\New-InstallerArt.ps1`.

### Développement

Tester une modification sans publier de version : `scripts\Test-Dev.ps1` compile vers `bin\ArkaHyte`,
arrête la copie installée, lance la version de dev (« dev » à côté du numéro dans le menu de l'icône)
et attend. Quitter la version de dev (menu de l'icône › Quitter, ou Ctrl+C dans le terminal) relance
la copie installée. Les deux partagent `%LOCALAPPDATA%\ArkaHyte` (réglages, raccourcis, journaux).

```powershell
scripts\Test-Dev.ps1                          # compile puis lance la version de dev
scripts\Test-Dev.ps1 -Arguments '--setup'     # avec l'assistant ouvert
scripts\Test-Dev.ps1 -Arguments '--demo','stress' -NoBuild
```

Ancienne installation de dev (tâche planifiée, sans la version installée) :

```powershell
dotnet publish src\ArkaHyte -c Release -o bin\ArkaHyte
scripts\Install.ps1              # (admin) tâche planifiée vers bin\ArkaHyte
scripts\Install.ps1 -Uninstall   # retire la tâche et le raccourci
```

Ne pas cumuler avec la version installée : `scripts\Install.ps1 -Uninstall` avant d'installer
(sinon la tâche planifiée lance la copie de dev, et l'instance unique ferme la version installée).

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
  Le logo est redessiné en blanc comme les icônes intégrées : plaque de fond retirée, détails
  conservés en découpe quand c'est possible (`monochromeIcon` dans `console.js`).
  Si le résultat ne convient pas, ~160 icônes intégrées sont proposées par catégorie (grille défilante) :
  les 12 d'origine et une sélection de [Lucide](https://lucide.dev) (licence ISC), dans `js/icons.js`.
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
**recharger l'affichage**, **assistant de configuration**, **ouvrir le dossier des journaux**,
**mises à jour**, **quitter**.

## Diagnostic

| Commande | Effet |
|---|---|
| `ArkaHyte.exe --dump out.json` | Lit tous les capteurs une fois et écrit le résultat |
| `ArkaHyte.exe --screenshot out.png` | Affiche l'interface, enregistre une capture et quitte |
| `ArkaHyte.exe --demo stress` | Démarre en mode démo (CPU/GPU 100 %, sans internet) |
| `ArkaHyte.exe --open-editor 0` | Ouvre l'éditeur du 1er raccourci au démarrage |
| `ArkaHyte.exe --setup` | Ouvre l'assistant de configuration au démarrage |
| `ArkaHyte.exe --setup-preview out.png [--setup-step 2]` | Rend l'assistant seul (sans rien enregistrer), capture et quitte ; fonctionne à côté d'une instance lancée |
| `ArkaHyte.exe --preview-speedtest [done]` | Affiche le compteur (ou le résumé) du test de débit avec des valeurs d'exemple |
| `ArkaHyte.exe --speedtest out.txt` | Lance un vrai test de débit et écrit chaque étape |
| `ArkaHyte.exe --list-apps out.json` | Liste les applications proposées par l'éditeur |
| `ArkaHyte.exe --menu-preview out.png` | Rend le menu de l'icône de notification en image |
| `ArkaHyte.exe --devtools` | Active F12 (outils de développement) |
| `ArkaHyte.exe --memory-log` | Note la mémoire de l'hôte (tas .NET, mémoire privée, handles) toutes les 10 s dans `display.log` (sinon toutes les heures) |

Logs : `%LOCALAPPDATA%\ArkaHyte\` (`display.log`, `launcher.log`).

### Mesurer la consommation

```powershell
scripts\Measure-ArkaHyte.ps1                                # 60 s sur l'instance en cours
scripts\Measure-ArkaHyte.ps1 -Seconds 300 -OutFile bench.json
```

Échantillonne `ArkaHyte.exe` et tous ses processus WebView2 (ceux des autres applis sont ignorés),
puis affiche une ligne par processus et le total :

- **CPU % core** : moyenne en % d'un cœur logique (16 % visés) ; le résumé donne aussi le % de la
  machine (chiffre du Gestionnaire des tâches) et le pic.
- **GPU %** : moteur le plus chargé (3D, Copy…), comme le Gestionnaire des tâches.
- **Private WS MB** : mémoire de la colonne « Mémoire » du Gestionnaire des tâches ;
  **Peak WS MB** : plus haut working set vu (pages partagées comprises) ; **Private MB** : mémoire privée allouée.

Pour le scénario chargé, activer le **mode démo** depuis l'icône de notification pendant la mesure.

## Modifier l'interface

Ouvrir `src/ArkaHyte/wwwroot/index.html` dans un navigateur : sans l'application, la page
passe en **mode design** avec des données simulées. Republier ensuite avec `dotnet publish`.

Performances mesurées (`Measure-ArkaHyte.ps1` sur 120 s, 10/11/2026, affichage normal) : ~7 % d'un
cœur (≈ 0,4 % du CPU total), ~1 % de GPU, ~150 Mo de RAM stables (hôte .NET ~20-45 Mo, le reste
pour WebView2). Écrans éteints ou session verrouillée : rendu et capteurs en pause.
