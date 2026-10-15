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
- Fenêtre transparente (fenêtre superposée) avec coins arrondis
- Toujours visible (top-most), déplaçable à la souris
- Icône dans la barre d'état (notification) avec menu contextuel
- Menu contextuel du widget : minimiser, à propos, fermer

## Compilation

Prérequis : le compilateur C# de .NET Framework 4.0
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

```bat
build.cmd
```

Le script génère `PerfWidget.exe`.

## Utilisation

Lancer `PerfWidget.exe` :

- **Clic gauche maintenu** sur le widget : déplacer la fenêtre.
- **Clic droit** sur le widget : menu contextuel.
- **Clic gauche** sur l'icône de la barre d'état : afficher / masquer le widget.

## À propos

Grégory Hargous — [gregland.net](https://gregland.net)
