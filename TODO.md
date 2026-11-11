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
- [ ] Pilotage RGB (Corsair iCUE + carte mère MSI MPG X870E), par étapes, testé sur le PC du propriétaire :
  - Corsair : iCUE SDK (iCUE ouvert, pas d'admin), via la bibliothèque RGB.NET ; prise de main puis restitution à iCUE
  - Carte mère MSI : de préférence via le serveur OpenRGB (lancé en admin au démarrage, Arka Hyte s'y connecte en local
    sans droits) ; à défaut SDK Mystic Light (exige l'admin, support X870E incertain). Vérifier d'abord la détection de
    la carte dans OpenRGB ; OpenRGB limité à la carte mère pour ne pas disputer les LED Corsair à iCUE
  - Pilotage dans un processus enfant (comme le catalogue d'applis) pour garder l'hôte léger
  - Usages : couleur qui suit le thème (rouge Sith, bleu Jedi), alertes lumineuses (CPU/GPU chaud, pas d'internet),
    réglage à l'écran (activer, couleur)
  - Écarté : Windows Dynamic Lighting (seule l'appli au premier plan pilote les LED ; Arka Hyte ne l'est jamais)
