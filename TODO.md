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
- [ ] gestion de l'affichage des fps et du mode game meme avec un jeu qui est en fenetre ex : League of legends
- [ ] Mode jeu automatique : quand `ForegroundGame` détecte un jeu plein écran, vue dédiée sur l'écran du boîtier
  - FPS en grand, temps de frame et 1 % low (PresentMon via HWiNFO, à vérifier dans `--dump`), température / charge
    GPU, VRAM, température CPU ; widgets secondaires (disques, Wi-Fi, dock…) masqués
  - Retour à la vue normale à la sortie du jeu (avec un délai pour ne pas basculer à chaque Alt+Tab)
  - Capteurs choisis comme les autres (assistant + appui long), seuils existants réutilisés
- [ ] Historique des températures : toucher une jauge (CPU / GPU, température ou puissance) ouvre une courbe des
  10 à 30 dernières minutes
  - Tampon circulaire côté C# ou JS (1 point / s), canvas redessiné une fois par seconde au plus, rien dans la
    boucle 24 i/s quand le panneau est fermé
  - Distinguer du réglage par appui long (toucher court = courbe, appui long = panneau `#sheet`)
- [ ] Refroidissement : température du liquide de l'AIO TITAN 360, vitesse de la pompe et des ventilateurs RX,
  lus par HWiNFO (capteurs du hub iCUE LINK, sans le SDK iCUE)
  - Vérifier d'abord que HWiNFO les expose (`--dump`), puis nouvelles entrées `HwInfo` + étape de l'assistant
  - Jauge « liquide » dans le style des autres, seuils réglables par appui long
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
