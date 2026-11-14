# TODO — file d'attente

**Source de vérité pour « on fait quoi maintenant ? ».** La liste est
**ordonnée** : le prochain point à traiter est le premier non coché.

Les rubriques ne sont **pas numérotées** — leur ordre est leur position dans le
fichier, et rien d'autre. Un numéro dirait la même chose deux fois, et il
faudrait renuméroter toute la suite chaque fois qu'une rubrique est terminée.

Une rubrique terminée **quitte ce fichier** : ce qu'elle laisse de durable part
dans le [README](README.md), le [CLAUDE.md](CLAUDE.md) ou dans [docs/](docs/), le
récit de sa réalisation reste dans l'historique git. Ce fichier ne contient que
du non fait.

---

# Liste des tâches
- [ ] Audio au toucher : volume général et par application, bouton micro coupé / actif avec voyant bien visible,
  bascule de la sortie par défaut (casque ↔ enceintes)
  - API Core Audio (`IMMDeviceEnumerator`, `IAudioEndpointVolume`, sessions audio) dans l'hôte : pas de shell
    chargé ; changement de sortie par défaut via l'interface non documentée `IPolicyConfig` (à valider)
  - Notification des changements (volume, mute, périphériques) plutôt que sondage
  - Mesurer la mémoire de l'hôte avant / après (`--memory-log`)
- [ ] Actions dans le dock : en plus des applis, raccourcis verrouiller le PC, mettre en veille, capture d'écran,
  lancer un script PowerShell, ouvrir une URL
  - Nouveaux types dans `ShortcutStore` / éditeur, icônes Lucide ; confirmation au toucher pour la veille
- [ ] Lecture en cours enrichie : barre de progression touchable pour avancer / reculer, volume de l'appli qui joue
  - SMTC : `GetTimelineProperties` + `TryChangePlaybackPositionAsync` ; volume via la session audio de l'appli
    (dépend du point Audio)
- [ ] Coût électrique du PC : compteur kWh et € (jour, session de jeu) à partir des puissances CPU + GPU (+ forfait
  pour le reste, réglable), prix du kWh dans les réglages
  - Cumul côté C# (persisté dans `%LOCALAPPDATA%\ArkaHyte` pour survivre au redémarrage), remise à zéro quotidienne

# En attente — uniquement sur demande explicite du propriétaire

Hors file d'attente : « next » ne les prend jamais.

- [ ] Redémarrage automatique d'iCUE quand il dérive (à faire seulement si le problème revient et qu'il le demande)
  - Constat du 2026-11-14 : après des heures, la charge totale montait à ~22 % (dont ~12 % dus à iCUE, surtout en
    temps noyau imputé au processus « System ») ; iCUE relancé, retour à ~10 %. Arka Hyte n'y est pour rien (+1 à 3 %)
  - La surcharge n'apparaît pas sur `iCUE.exe` : la détecter par la charge noyau totale sur plusieurs minutes, ou
    redémarrer à intervalle fixe
  - Fermer iCUE proprement : forcer l'arrêt d'`iCUE.exe` et d'`iCUEDevicePluginHost` laisse les services Corsair
    incohérents (plugins qui ne s'installent plus, assistant du hub bloqué) ; leur redémarrage demande l'admin
  - Le tapis, le support casque et le hub LINK n'ont pas de mode mémoire : iCUE doit rester lancé
