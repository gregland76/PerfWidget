# PerfWidget

Petit widget Windows de surveillance des performances : CPU, mémoire et
utilisation du disque `C:`, affichés dans une fenêtre transparente toujours
au premier plan.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET](https://img.shields.io/badge/.NET%20Framework-4.0-purple)

## Fonctionnalités

- **CPU** : charge processeur totale (`% Processor Time`)
- **Mémoire** : `% Committed Bytes In Use`
- **Disque** : espace utilisé sur `C:`
- **Batterie** : temps restant estimé (ou `AC` sur secteur)
- Fenêtre transparente (fenêtre superposée) avec coins arrondis
- Toujours visible (top-most), déplaçable avec **Ctrl + clic gauche**
- Le widget s'efface dès que la souris le survole, afin de laisser passer les
  clics vers la fenêtre située en dessous ; il réapparaît dès que le pointeur
  s'en écarte (ou dès que **Ctrl** est enfoncé, pour pouvoir le déplacer)
- Ne peut pas sortir de l'écran : le widget reste toujours entièrement dans la
  zone de travail de l'écran, même après un changement de résolution ou de
  disposition des moniteurs
- Deux dispositions au choix (données **sur une même ligne** ou **en colonne**,
  largeur réduite dans ce dernier cas) ; une seule entrée de menu bascule de
  l'une à l'autre (« Passer en disposition en ligne » / « Passer en disposition
  en colonne »)
- La dernière disposition choisie est mémorisée dans `settings.ini`, à côté de
  l'exécutable, et restaurée au démarrage
- Icône dans la barre d'état (notification) avec menu contextuel : afficher,
  minimiser, disposition, démarrage avec Windows, à propos, fermer
- Démarrage automatique avec Windows, via un raccourci créé dans le dossier
  de démarrage (`shell:startup`), activable depuis le menu de l'icône

## Compilation

Prérequis : le compilateur C# de .NET Framework 4.0
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

```bat
build.cmd
```

Le script génère `PerfWidget.exe`.

## Utilisation

Lancer `PerfWidget.exe` :

- **Survol du widget** : il s'efface aussitôt, ce qui permet de cliquer sur la
  fenêtre située en dessous. Il réapparaît dès que la souris s'en écarte.
- **Ctrl enfoncé** : le widget reste affiché et peut être déplacé par un
  **clic gauche maintenu**.
- Le widget n'a pas de menu contextuel : toutes les commandes sont dans le menu
  de l'icône de la barre d'état (clic droit sur l'icône, près de l'horloge).
- **Menu « Passer en disposition en ligne / en colonne »** (icône) :
  bascule entre l'affichage sur une même ligne et l'affichage en colonne.
- **Menu « Démarrer avec Windows »** (icône) : cocher pour créer un
  raccourci `PerfWidget.lnk` dans le dossier de démarrage de Windows
  (`shell:startup`), décocher pour le supprimer.
- **Clic gauche** sur l'icône de la barre d'état : afficher / masquer le widget.

## À propos

Grégory Hargous — [gregland.net](https://gregland.net)
