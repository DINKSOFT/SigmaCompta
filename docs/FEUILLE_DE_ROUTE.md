# Sigma Compta — feuille de route vers une comptabilité complète

Objectif : faire de Sigma Compta un logiciel de comptabilité générale complet (saisie, consultation, lettrage, états, plan comptable), qui travaille **directement sur la base comptable existante** du client (SQL Server), en cohabitation avec le logiciel comptable déjà en place.

Décisions prises le 17/10/2026 :

| Sujet | Choix |
|---|---|
| Données | Base comptable existante (`F_ECRITUREC`, `F_COMPTEG`, `F_COMPTET`, `F_JOURNAUX`, `F_JMOUV`, `P_DOSSIER`) |
| Plateforme | WPF sur **.NET 8** (`net8.0-windows`) |
| Premiers modules | Saisie, consultation et lettrage, états comptables, plan comptable et tiers |
| Démarche | Feuille de route courte, puis premier module dans le code |

---

## 1. Principes d'architecture

**Lecture directe, écriture par procédure.** Les écrans lisent les tables par des requêtes SQL paramétrées. Toute écriture passe par une procédure du schéma `imp` installée et versionnée par Sigma Compta (aujourd'hui `imp.PS_ImportPiece`). Aucune instruction `UPDATE`/`INSERT` libre n'est envoyée depuis l'application. Chaque procédure :

- reproduit la séquence d'une saisie manuelle du logiciel comptable (mêmes colonnes, mêmes clés `cb*`, déclencheurs de la base exécutés) ;
- travaille dans une transaction et se contrôle elle-même après écriture ;
- dispose d'un mode simulation (tout est exécuté puis annulé).

Une nouvelle écriture (lettrage, création de compte…) n'est développée qu'après avoir observé, avec les Extended Events, ce que fait le logiciel comptable pour la même opération.

**Un cœur métier sans interface.** Tout ce qui touche aux données et aux règles vit dans `SigmaCompta.Core`, testé par `SigmaCompta.Tests` sans SQL Server :

| Dossier du cœur | Rôle |
|---|---|
| `Modeles/` | Objets métier (pièces, référentiel, consultation…) |
| `Sql/` | Connexion, référentiel, inspection du schéma, scripts |
| `Import/` | Import de fichiers (existant) |
| `Comptabilite/` | Services des nouveaux modules : consultation, puis états, saisie, lettrage, plan comptable |

**Une application organisée par modules.** La barre latérale est découpée en rubriques : *Dossier* (connexion, base), *Comptabilité* (les modules), *Import d'écritures* (le parcours existant). Chaque module est un `PageViewModel` + une vue XAML, sur le même modèle que les écrans actuels.

**Aucune mention du logiciel comptable d'origine** dans les fenêtres ni dans le code : on parle de « base comptable » et de « logiciel comptable ».

## 2. Passage à .NET 8

| Élément | Avant (.NET Framework 4.8) | Après (.NET 8) |
|---|---|---|
| Cible | `net48` | `net8.0-windows` |
| Accès SQL | `System.Data.SqlClient` (intégré) | Paquet `Microsoft.Data.SqlClient` |
| Mot de passe mémorisé | `ProtectedData` (intégré) | Paquet `System.Security.Cryptography.ProtectedData` |
| Encodage Windows-1252 (CSV) | Intégré | Fournisseur `CodePagesEncodingProvider` enregistré au démarrage |
| Formats et comparaisons de texte | NLS (Windows) | NLS conservé (`System.Globalization.UseNls`) pour éviter les écarts d'ICU (séparateur de milliers, recherche dans les textes) |
| Chiffrement de la connexion | Désactivé par défaut | Activé par défaut dans le nouveau pilote : on le règle sur *Optional* + certificat serveur accepté, pour se comporter comme avant |
| Diffusion | .NET 4.8 présent sur Windows 10/11 | Runtime **.NET 8 Desktop** à installer, ou publication autonome (`dotnet publish -r win-x64 --self-contained`) |

Les deux paquets NuGet sont restaurés automatiquement par Visual Studio 2022 (version 17.8 ou plus récente).

Point d'attention : le serveur de SODEM est un **SQL Server 2012 RTM (11.0.2100)**, sans Service Pack. Il ne gère pas TLS 1.2. Si un poste Windows désactive TLS 1.0, la connexion échouera (avec l'ancien comme avec le nouveau pilote). Installer le SP4 de SQL Server 2012 règle ce point.

## 3. Modules et ordre de réalisation

| Lot | Contenu | Écrit dans la base ? | État |
|---|---|---|---|
| 0 | Migration .NET 8, navigation par modules | Non | **Livré** |
| 1 | **Consultation des comptes** : compte général ou tiers, période, report à nouveau, solde progressif, filtre lettré / non lettré, export Excel | Non | **Livré (v1)** |
| 2 | **États comptables** : balance générale et balance tiers (six colonnes), grand livre, journaux, journal centralisateur, export Excel | Non | **Livré** — export Excel, PDF et impression |
| 3 | **Saisie des écritures** : saisie par journal et par mois, comptes et tiers proposés à la frappe, contrôles en direct (`Validateur`), numérotation selon le journal, enregistrement par `imp.PS_ImportPiece` (déjà en place) | Oui, procédure existante | **Livré (v1)** — création de pièces ; modification et suppression de pièces existantes à venir |
| 4 | **Lettrage** : sélection de lignes équilibrées, lettrage et délettrage | Oui, procédure à créer (`imp.PS_Lettrer`) après observation du logiciel comptable | À faire |
| 5 | **Plan comptable et tiers** : création et modification des comptes généraux, des tiers et des journaux | Oui, procédures à créer après observation (`F_COMPTEG`, `F_COMPTET`, clés `cb*`, déclencheurs) | À faire |
| Ensuite | Clôture et à-nouveaux, états financiers SYSCOHADA, rapprochement bancaire, déclarations de TVA | — | — |

Les lots 1 et 2 ne font que lire : ils sont sans risque pour la base de production et valident la nouvelle plateforme. Les lots 3 à 5 écrivent : ils réutilisent la démarche éprouvée de l'import (procédure, simulation, contrôle après écriture).

## 4. Risques et questions ouvertes

- **Travail simultané avec le logiciel comptable.** La base verrouille un journal/mois en cours de saisie (erreur 80003). Les écrans d'écriture devront afficher ce cas clairement.
- **Chaînage d'inaltérabilité (`cbHash`).** Une écriture insérée hors du logiciel comptable peut ne pas être chaînée ; l'écran Vérification le signale déjà. À valider avant toute mise en production des lots 3 à 5.
- **Norme IFRS (`EC_Norme`).** La consultation prend toutes les écritures, sans filtre de norme. À revoir si un dossier utilise la gestion IFRS.
- **Saisie.** Une pièce enregistrée se corrige pour l'instant dans le logiciel comptable : la modification et la suppression demandent de nouvelles procédures, à écrire après observation du logiciel comptable (comme le lettrage). Pas de ventilation analytique ni de contrepartie automatique sur les journaux de trésorerie : la ligne de banque ou de caisse se saisit comme les autres.
- **Journal des à-nouveaux.** La balance et le grand livre classent en solde d'ouverture les écritures du journal choisi comme journal des à-nouveaux (proposé d'après son code RAN/AN/ANO ou son intitulé). Sans ce choix, les à-nouveaux d'une période qui commence avec l'exercice sont comptés dans les mouvements. À confirmer sur la base de SODEM.
- **Lettrage.** La v1 de la consultation lit `EC_Lettre` (lettré oui/non) et `EC_Lettrage` (code). Le cas du lettrage partiel reste à confirmer sur une base réelle.
- **Licence.** Travail d'interopérabilité : vérifier les conditions de licence du logiciel comptable avant un usage commercial.

## 5. Compiler après la migration

1. Ouvrir `SigmaCompta.sln` dans Visual Studio 2022 (17.8 ou plus récent, charge de travail « Développement .NET Desktop »).
2. Laisser Visual Studio restaurer les paquets NuGet, puis générer la solution.
3. Lancer `SigmaCompta.Tests` : code de sortie 0 = tous les contrôles passent.
4. Lancer `SigmaCompta.App`, se connecter, choisir la base, puis ouvrir **Comptabilité › Consultation** ou **Comptabilité › États comptables**. Pour **Comptabilité › Saisie**, la procédure `imp.PS_ImportPiece` doit être installée (écran Vérification).

L'archive `livraisons/SigmaCompta_net48_avant_comptabilite_20261017.zip` contient le projet tel qu'il était avant ces changements.
