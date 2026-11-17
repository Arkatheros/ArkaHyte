# Étude — page Home Assistant

Tâche du [TODO](../TODO.md) : « intégration de Home Assistant dans une page, via API ». Décisions du propriétaire
(2026-11-17) en fin de document. **Réalisé** (2026-11-17) : étapes 1 et 2 du découpage (connexion, page, réglages,
commandes au toucher et − / + du chauffage) ; restent les étapes 3 et 4 (voir le TODO).

## Objectif

Une troisième page sur l'écran du boîtier, à côté des vues normale et jeu, pour **voir et piloter la maison**
depuis le PC : lumières, prises, volets, chauffage, capteurs, scènes. On y accède par balayage, comme les deux
vues existantes.

## Contraintes du projet qui pèsent

- **Performance** : l'écran tourne en permanence (cible ~16 % d'un cœur). Pas de sondage : uniquement des mises à
  jour poussées par Home Assistant, rien de dessiné quand la page est cachée (comme `GAME_HIDDEN`), boucle à
  24 fps unique, glides pour les valeurs.
- **Mémoire de l'hôte** : allocations minimes, pas de bibliothèque lourde (pas de client HA tiers).
- **Thèmes et palettes** : toutes les couleurs par variables ; les états « allumé » prennent la lame.
- **Fenêtre non activante** : tout se pilote au toucher, sans clavier sur l'écran du boîtier (saisie de l'adresse
  et du jeton dans les Paramètres, sur l'écran principal).

## Quelle API

| | REST (`/api/states`, `/api/services/…`) | WebSocket (`/api/websocket`) |
|---|---|---|
| Mises à jour | sondage obligatoire | poussées en temps réel |
| Coût au repos | une requête toutes les N s | rien tant que rien ne change |
| Commandes | `POST /api/services/<domaine>/<service>` | `call_service` sur la même connexion |
| Listes (pièces, appareils) | non | `config/area_registry/list`, `config/entity_registry/list`, `config/device_registry/list` |

**Retenu : WebSocket.** Abonnement `subscribe_entities` limité aux entités affichées (`entity_ids`) : instantané
compressé au départ, puis seulement les différences. Ce message n'est pas documenté officiellement, mais c'est
celui de l'interface de HA depuis 2022.4 (client officiel `home-assistant-js-websocket`). Repli documenté si un
jour il disparaît : `subscribe_trigger` avec un déclencheur `state` sur la même liste. Authentification par
**jeton d'accès longue durée** (profil HA › Sécurité), envoyé dans le premier message `auth`.

## Architecture

Même découpage que les autres sources : le C# parle à HA, la page affiche.

```
Home Assistant ──WebSocket──> HomeAssistantClient (C#, thread de fond)
                                 │ cache des entités suivies, regroupement 150 ms
                                 ▼
                       DisplayForm ──message `home`──> console.js (page Maison)
                       DisplayForm <──message `homeCall`── toucher sur une tuile
```

- **`HomeAssistantClient`** (`Sensors/` ou `Home/`) : `ClientWebSocket` natif .NET, reconnexion avec attente
  croissante (2 s → 60 s), ping toutes les 30 s. Garde l'état des seules entités choisies ; regroupe les
  changements (150 ms) avant de poster, comme le message `audio` (60 ms).
- **Pourquoi pas directement depuis la page** : le jeton resterait dans la page (lisible par F12), HA refuse les
  origines web non déclarées (`cors_allowed_origins`), et la connexion suivrait les rechargements de la page.
- **Pause** : écrans éteints ou session verrouillée (`SensorHub.Paused`) → connexion gardée mais plus rien posté ;
  au retour, l'état complet est renvoyé d'un coup.
- **Messages** :
  - `home` → page : `{ status: connected | connecting | offline | unauthorized, entities: { id: { state, name, icon, unit, attrs… } } }`
    (complet au départ, différences ensuite).
  - `homeCall` → hôte : `{ domain, service, entityId, data }`, ex. `light.turn_on { brightness_pct: 40 }`.
  - Retour d'échec de commande → `toast` (« Commande refusée par Home Assistant »).
- **Pas d'effet optimiste trompeur** : au toucher, la tuile passe en état « en cours » ; la vraie valeur arrive par
  l'abonnement (quelques dizaines de ms en local). Au bout de 3 s sans réponse, retour à l'état réel et toast.

## Sécurité du jeton

- Chiffré par **DPAPI** (`ProtectedData`, utilisateur Windows courant) dans `%LOCALAPPDATA%\ArkaHyte\home.json`,
  jamais en clair dans `appsettings.json`.
- **Jamais exporté** par `ConfigBackup` (illisible sur une autre machine de toute façon) : à ressaisir après une
  réinstallation, comme la configuration de HWiNFO.
- Jamais envoyé à la page ni écrit dans les journaux.
- HTTPS accepté (certificat valide requis) ; HTTP seulement pour une adresse locale (réseau privé).

## Page « Maison » sur l'écran

- **Place** : troisième page du balayage, à gauche : *Maison · Normale · Jeu* (balayage vers la droite depuis la vue
  normale), trois points en bas. Page absente tant que HA n'est pas configuré.
- **Contenu** : une grille de tuiles regroupées par pièce (zones HA), titre de section comme les en-têtes CPU / GPU.
  Tuile selon le domaine de l'entité :

  | Domaine | Tuile | Toucher | Appui long |
  |---|---|---|---|
  | `light` | icône, nom, % | allumer / éteindre | panneau : luminosité (glissière), couleur si gérée |
  | `switch`, `fan`, `input_boolean` | icône, nom, état | basculer | — |
  | `cover` (volets) | icône, position % | ouvrir / fermer | panneau : position, stop |
  | `climate` | température actuelle, consigne et boutons −/+ sur la tuile | −/+ : consigne ±0,5 °C | panneau : mode |
  | `sensor`, `binary_sensor` | valeur + unité (ou ouvert / fermé) | courbe 24 h (`history/history_during_period`) | — |
  | `scene`, `script` | icône, nom | lancer | — |
  | `lock`, `alarm_control_panel` | état | demande une confirmation (second toucher, comme la veille) | — |
  | `media_player` | titre, volume | lecture / pause | — (déjà couvert par « Lecture en cours » pour le PC) |
  | `camera` | **hors première version** : images JPEG à rafraîchir, coûteux en CPU et en mémoire | | |

- **Tailles** : tuiles 1 × 1 (161 × 161 px, 4 par ligne) et 2 × 1 pour les capteurs à courbe ou le chauffage.
  La page défile verticalement si les tuiles dépassent l'écran (2 560 px de haut laissent de la place).
- **Icônes** : HA donne des noms Material Design Icons (`mdi:lightbulb`). Une correspondance vers les icônes
  Lucide déjà embarquées (`js/icons.js`) suffit pour les domaines courants ; icône par défaut du domaine sinon.
  Pas de police MDI complète (7 000 icônes, ~1 Mo).
- **États** : HA hors ligne → tuiles grisées (`.stale`, comme la vue jeu) et bandeau « Home Assistant injoignable ».
- **Bascule automatique** : aucune. Option possible plus tard : afficher la page quand une entité choisie change
  (sonnette, mouvement).

## Paramètres (nouvelle étape « Maison »)

1. **Connexion** : adresse (`http://homeassistant.local:8123` proposé), jeton, bouton « Tester » (version de HA
   et nombre d'entités affichés).
2. **Entités** : liste fournie par HA (registre des entités + zones), recherche, filtres par domaine et par pièce ;
   cases à cocher. Ordre des pièces et des tuiles par glisser-déposer (code de l'étape Disposition réutilisé).
3. **Aperçu en direct** sur l'écran du boîtier, comme l'Apparence et la Disposition.

Réglage `Home` dans `appsettings.json` : `{ Enabled, Url, Sections: [{ Area, Entities: [ids] }] }` ; jeton à part
(voir Sécurité). Ajout de la page à l'étape Disposition : la page Maison n'est pas une pile de bandes, elle a sa
propre grille, donc elle reste hors de ce mécanisme.

## Coût estimé à l'exécution

- Réseau : quelques octets par changement d'état ; zéro au repos.
- Hôte : une connexion WebSocket, un dictionnaire de quelques dizaines d'entités. Négligeable en mémoire.
- Page : rien tant qu'elle est cachée ; affichée, uniquement des écritures DOM au changement (`setText` /
  `setStyle`). À mesurer avec `scripts\Measure-ArkaHyte.ps1` page affichée et cachée.

## Découpage en étapes

1. **Connexion et lecture** : client WebSocket, jeton DPAPI, étape Paramètres (connexion + choix des entités),
   page Maison en lecture seule (tuiles, états, hors ligne). Mode design avec données simulées.
2. **Commandes** : toucher (basculer, scènes), retour d'état, confirmations, toasts d'échec.
3. **Panneaux** : luminosité / couleur, volets, chauffage, courbe 24 h des capteurs.
4. **Options** : bascule automatique sur événement, regroupement par pièce, tuiles 2 × 1 réglables.

Chaque étape validée avec `scripts\Test-Dev.ps1` avant la suivante.

## Risques

- `subscribe_entities` non documenté : repli `subscribe_trigger` prévu dès l'étape 1, choisi si le premier échoue.
- Certificat auto-signé sur une instance HTTPS locale : refusé par défaut ; à traiter seulement si le cas se pose
  (empreinte du certificat épinglée dans les réglages).
- Grand nombre d'entités (500+) dans la liste des Paramètres : la liste est filtrée et paginée côté page, l'écran
  du boîtier ne suit que les entités choisies.

## Décisions (2026-11-17)

1. **Instance** : Home Assistant sur le réseau local, en HTTP (`http://homeassistant.local:8123` ou IP locale).
2. **Place** : page à gauche de la vue normale (*Maison · Normale · Jeu*).
3. **Entités de la première version** : lumières et prises, capteurs, chauffage et volets, scènes et scripts
   (verrous, alarme et lecteurs multimédia plus tard, caméras hors champ).
4. **Choix des entités** : à la main dans l'étape « Maison » des Paramètres (recherche, filtres, glisser-déposer).
5. **Bascule automatique** : non, pour l'instant.
7. **Groupes libres** (2026-11-17, remplace le regroupement par pièce) : groupes créés, renommés, supprimés et
   déplacés par l'utilisateur, « Maison » créé au premier ajout ; 1 à 3 tuiles par ligne par groupe, largeur 1 à 3
   par tuile ; entités déplacées dans un groupe ou vers un autre. Volets : boutons monter / stop / descendre. Scènes :
   pastilles rondes comme le dock (style C), icône d'après le nom.
6. **Maquette validée** (2026-11-17) : tuiles 3 par ligne (194 × 170, doubles pour chauffage et capteurs à courbe),
   boutons − / + de consigne directement sur la tuile du chauffage (plus de panneau pour la consigne), scènes en
   haut sous l'en-tête, en-tête de pièce avec sa température / humidité, coins biseautés bordés jusque dans le biseau.
