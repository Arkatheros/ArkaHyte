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
- [ ] Palettes de couleurs prédéfinies, choisies indépendamment du fond (au-delà de Sith / Jedi)
  - Une palette = le jeu de variables CSS d'un thème (`--blade-rgb`, `--line-rgb`, `--ember-rgb`…), aucune couleur à régler une à une
  - Choix dans l'étape « Apparence » avec aperçu, toujours accessible par le menu de l'icône
  - Lisibilité sur fond clair (halo, contour des chiffres) selon la palette
- [ ] Fond animé : vidéo ou image animée (GIF / WebP animé)
  - Mesurer d'abord le coût (`Measure-ArkaHyte.ps1`, cible ~16 % d'un cœur), décodage matériel de la vidéo
  - En pause écrans éteints ou session verrouillée, comme le reste ; rognage au même cadre que l'image
- [ ] Positionnement des widgets
  - Définir le modèle (emplacements prédéfinis, ordre des blocs, ou placement libre) avant de coder : les vues normale et jeu
    et le morph entre elles en dépendent
 - [ ] intégration de home assistant dans un page, via api
 

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
