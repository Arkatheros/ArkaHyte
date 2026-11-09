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
- [ ] Revoir l'animation du test de debit car, quand on passe du test de reception vers le test de reception, ca va super vite, du coup l'animation
 au lieu de partir de 0 et de monté, il change de couleur et par directement de 900Mo par exemple. il faut plus de fluidité.
 la barre de progression en bas ne sert pas enleve la. et je pense que c'est pas la peine d'agrandir le widget au moment du test.
 il doit rester de la meme taille, un compteur de vitesse à droite le vitesse actuelle, puis il passe a l'envoi et enfin il affiche le resultat:
 reception envoi et ping
- [ ] bench le programme, pouvoir voir combien il prends de processeur et combien de mémoire
- [ ] Faire un installeur