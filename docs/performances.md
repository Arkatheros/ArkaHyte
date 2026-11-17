# Consommation d'Arka Hyte

Banc d'essai de la version 1.7.2, le 17 novembre 2026. L'écran du boîtier tourne en permanence : voici ce qu'il
coûte au PC.

| Processeur | Carte graphique | Mémoire | Réseau |
|---|---|---|---|
| **8,9 %** d'un cœur (0,56 % du processeur entier) | **~1 %** | **219 Mo** | **~8 Mo par jour** (~0,7 kbit/s) |

Chiffres en usage réel : vue normale, thème Mustafar, Home Assistant connecté. Écran éteint ou session
verrouillée, l'affichage se met en pause et la consommation tombe presque à zéro.

## Processeur selon l'usage

Moyenne sur la durée de la mesure, en % d'un cœur logique (100 % = un cœur occupé en continu). Objectif de
l'application : 16 % d'un cœur.

| Scénario | Durée | CPU (% d'un cœur) | CPU (% du total) | Pic sur 1 s | GPU | Mémoire | Mémoire réservée |
|---|---|---:|---:|---:|---:|---:|---:|
| Usage réel | 5 min | 8,9 % | 0,56 % | 43 % | ~1 % | 219 Mo | 390 Mo |
| Vue jeu (`--demo game`) | 2 min | 11,6 % | 0,72 % | 46 % | ~1 % | 224 Mo | 398 Mo |
| Charge maximale (`--demo stress`) | 2 min | 12,7 % | 0,80 % | 56 % | ~1 % | 221 Mo | 389 Mo |

Les trois scénarios restent sous l'objectif de 16 %.

## Qui consomme quoi

L'application, c'est un petit programme .NET qui lit les capteurs, plus un moteur WebView2 (celui d'Edge) qui
dessine l'écran à 24 images par seconde. Presque tout le coût vient de l'affichage : la lecture des capteurs ne
prend que 0,3 % d'un cœur.

| Partie | Processus | CPU (% d'un cœur) | Mémoire |
|---|---|---:|---:|
| Rendu graphique | processus GPU de WebView2 | 4,8 % | 79 Mo |
| Page affichée | renderer de WebView2 | 3,8 % | 55 Mo |
| Capteurs, Home Assistant, météo | `ArkaHyte.exe` (.NET) | 0,3 % | 42 Mo |
| Navigateur | processus principal de WebView2 | < 0,1 % | 32 Mo |
| Utilitaires | réseau 6 Mo, stockage 3 Mo, rapport de plantage 2 Mo | < 0,1 % | 11 Mo |
| **Total** | | **8,9 %** | **219 Mo** |

Usage réel. Mémoire = colonne « Mémoire » du Gestionnaire des tâches (working set privé).

## Réseau

L'application ne télécharge presque rien. Le plus gros poste est le petit ping qui sert à afficher la latence et
l'état de la connexion.

| Source | Fréquence | Par jour |
|---|---|---:|
| Ping vers 1.1.1.1 (latence, état de la connexion) | toutes les 2 s | ~6,4 Mo |
| Météo Open-Meteo (~1 Ko + connexion chiffrée) | toutes les 15 min | ~0,8 Mo |
| Recherche de mise à jour sur GitHub (~165 Ko) | toutes les 6 h | ~0,7 Mo |
| WebView2 au démarrage (DNS, configuration Microsoft) | au lancement | quelques Ko |
| **Total vers Internet** | ~0,7 kbit/s en continu | **~8 Mo** |
| Home Assistant (réseau local seulement) | à chaque changement d'état | quelques Ko par heure |

## Comment c'est mesuré

- **Machine** : AMD Ryzen 7 9800X3D (16 cœurs logiques), NVIDIA GeForce RTX 4090, Windows 11.
- **Usage réel** : copie installée 1.7.2, 5 minutes. Les démos tournent sur la build de dev publiée depuis le
  même code, 2 minutes chacune après 25 s de chauffe.
- **Processeur, carte graphique, mémoire** : `scripts\Measure-ArkaHyte.ps1`, un relevé par seconde sur
  `ArkaHyte.exe` et tous ses processus WebView2 (ceux des autres applications ne sont pas comptés). GPU = moteur
  le plus occupé, comme le Gestionnaire des tâches.
- **Réseau** : calculé à partir de la taille réelle de chaque requête et de sa fréquence, recoupé avec les
  connexions ouvertes et les compteurs d'échanges des processus. Ce n'est pas une capture du trafic (elle demande
  les droits administrateur).
- **Pas encore mesuré** : la stabilité de la mémoire sur plusieurs jours. Les chiffres viennent d'une instance
  lancée depuis environ 25 minutes ; pour la vérifier, lancer avec `--memory-log` quelques heures puis
  `Measure-ArkaHyte.ps1`.

Refaire la mesure :

```powershell
scripts\Measure-ArkaHyte.ps1 -Seconds 300 -OutFile bench.json   # instance en cours
scripts\Test-Dev.ps1 -Arguments '--demo', 'stress' -NoBuild     # puis Measure-ArkaHyte.ps1 dans une autre console
```
