# Sigma Compta — Comptabilité générale

Application WPF (.NET 8) de comptabilité générale qui travaille directement sur une base **comptable** SQL Server existante. Elle évolue vers une comptabilité complète : la feuille de route est dans [`docs/FEUILLE_DE_ROUTE.md`](docs/FEUILLE_DE_ROUTE.md).

**Modules disponibles**

- **Accueil** (écran de démarrage) : tous les modules en tuiles, regroupés par rubrique (gestion quotidienne, états comptables, dossier, import d'écritures, outils). Les tuiles restent grisées tant que le dossier nécessaire n'est pas ouvert ; un bandeau indique l'état de la connexion et propose l'action suivante (se connecter, choisir le dossier, saisir une pièce).
- **Comptabilité › Saisie** : écran disposé comme la fenêtre « Journal » des logiciels comptables. En tête, le solde du compte de trésorerie du journal (ancien solde, mouvements, nouveau solde). Une ligne de saisie alignée sur les colonnes (jour, n° pièce, n° facture, référence, compte général, compte tiers, libellé, échéance, débit, crédit) : comptes et tiers reconnus à la frappe dans une liste déroulante, montant d'équilibre proposé, bouton « Équilibrer ». Dessous, une seule grille : les écritures du journal pour le mois, suivies des lignes de la pièce en cours. En bas, les totaux du journal et le solde à équilibrer. Les pièces sont enregistrées par `imp.PS_ImportPiece` avec numérotation automatique.
- **Comptabilité › Consultation** : écritures d'un compte général ou tiers sur une période, report à nouveau, solde progressif, filtre lettré / non lettré, export Excel, extrait de compte en PDF ou à imprimer. Lecture seule.
- **Comptabilité › États comptables** : balance générale ou des tiers (six colonnes, total par classe ou par type de tiers), grand livre général ou des tiers, journaux et journal centralisateur, export Excel, PDF et impression (A4 paysage, en-tête société, pagination). Lecture seule.
- **Import d'écritures** : import de fichiers Excel ou CSV via la procédure stockée `imp.PS_ImportPiece` (détail ci-dessous).

Tous les champs de date acceptent la frappe directe (17, 17/10, 17/10/2026) et proposent un calendrier : bouton à droite du champ, F4 ou Alt+↓.

**Import d'écritures**

- Détection des instances SQL Server (registre local + réseau) et des bases comptables du serveur.
- Choix de la base et de la table cible : `F_ECRITUREC` est détectée et sa structure est vérifiée.
- Contrôle du schéma (version du script 01) et installation de la procédure en un clic.
- Fichier de paramètres `.json` réutilisable et fichier de données Excel (`.xlsx`) ou CSV.
- Contrôle local : équilibre, comptes, tiers, journaux, exercices et périodes clôturées.
- Simulation sur le serveur : les déclencheurs de la base s'exécutent, puis tout est annulé.
- Import pièce par pièce ou « tout ou rien », avec rapport Excel et historique.

Deux paquets NuGet seulement : `Microsoft.Data.SqlClient` (accès SQL Server) et `System.Security.Cryptography.ProtectedData` (mot de passe mémorisé). La lecture et l'écriture `.xlsx` et le JSON reposent sur .NET.

---

## 1. Compiler et lancer

**Prérequis :** Windows 10 ou 11 et Visual Studio 2022 (17.8 ou plus récent) avec la charge de travail « Développement .NET Desktop », qui installe le SDK .NET 8. Les paquets NuGet sont restaurés à la première génération.

1. Ouvrir `SigmaCompta.sln`.
2. Vérifier que **SigmaCompta.App** est le projet de démarrage (il l'est par défaut), puis lancer avec F5.
3. Pour diffuser l'application, générer en `Release` et copier le dossier `src\SigmaCompta.App\bin\Release\net8.0-windows\`. Le poste doit avoir le **runtime .NET 8 Desktop**. Pour s'en passer, publier en mode autonome : `dotnet publish src\SigmaCompta.App -c Release -r win-x64 --self-contained`.

Le projet **SigmaCompta.Tests** est une console qui exécute 155 contrôles du cœur métier, sans SQL Server. Un code de sortie 0 signifie que tous les contrôles passent.

## 2. Parcours dans l'application

| Étape | Ce qui se passe |
|---|---|
| 1. Connexion | Choix de l'instance (bouton *Détecter*) et de l'authentification Windows ou SQL. Le mot de passe peut être mémorisé : il est chiffré par DPAPI et lisible uniquement par ce compte Windows sur ce poste. |
| 2. Base & table | Liste des bases avec le badge « Comptabilité », la raison sociale, le nombre d'écritures, l'exercice et la procédure installée. Choix de la table cible et contrôle des 19 colonnes utilisées. |
| 3. Vérification | Contrôles structurés : tables, colonnes, colonnes obligatoires sans défaut, `CB_FntStr`, encodage `cb*`, déclencheurs, chaînage `cbHash`, droits et version de la procédure. Le bouton **Installer la procédure** exécute le script 02 dans une transaction. Le bouton **Script de pré-vérification** exécute le script 01 et affiche ses résultats. Le référentiel (journaux, comptes, tiers) est chargé à cette étape. |
| 4. Données | Choix ou édition du fichier de paramètres, puis du fichier de données (glisser-déposer accepté). La correspondance des colonnes est vérifiée en direct et un aperçu affiche les 500 premières lignes. |
| 5. Contrôle | Regroupement des lignes en pièces et validation locale, avec indicateurs, filtres et détail ligne par ligne. **Simuler sur le serveur** appelle la procédure avec `@Simulation = 1`. Les anomalies et le rapport s'exportent en Excel. |
| 6. Import | Mode pièce par pièce ou tout ou rien, options, puis confirmation. La progression s'affiche en direct. À la fin, un rapport Excel est enregistré automatiquement et l'import est ajouté à l'historique. |

## 3. Fichier de paramètres (`.json`)

Les profils sont rangés dans `%AppData%\SigmaCompta\Profils\` ; tout fichier `.json` peut aussi être ouvert depuis l'écran Données. Deux exemples se trouvent dans `modeles\`.

```json
{
  "Nom": "Brouillard standard",
  "Source":   { "Type": "Auto", "Feuille": "", "LigneEntete": 1, "Separateur": "auto",
                "Encodage": "windows-1252", "FormatsDate": "dd/MM/yyyy|yyyy-MM-dd|ddMMyy", "SeparateurDecimal": "auto" },
  "Colonnes": { "Journal": "JO_Num|Journal", "Date": "Date|EC_Date", "Piece": "EC_Piece|N° pièce",
                "Reference": "EC_Reference|Référence", "RefPiece": "EC_RefPiece|N° facture",
                "CompteGeneral": "CG_Num|Compte", "CompteTiers": "CT_Num|Tiers", "Libelle": "EC_Intitule|Libellé",
                "Debit": "Débit|Debit", "Credit": "Crédit|Credit", "Montant": "Montant", "Sens": "Sens",
                "Echeance": "EC_Echeance|Échéance" },
  "Regroupement": "JournalDatePiece",
  "Defauts":  { "Journal": "", "Libelle": "", "LongueurComptes": 0 },
  "Options":  { "NumerotationAutomatique": false, "RenumeroterDoublon": true, "AutoriserDoublon": false, "IgnorerHash": false,
                "ModeTransaction": "ParPiece", "ArreterPremiereErreur": false,
                "InverserMontantsNegatifs": true, "IgnorerLignesSansMontant": true }
}
```

- **Colonnes** : plusieurs noms d'en-tête possibles, séparés par `|`. La casse, les accents et les espaces sont ignorés. `[C]` désigne directement la colonne C. On utilise soit *Débit/Crédit*, soit *Montant* (signé ou associé à *Sens* D/C ou 0/1).
- **Regroupement** :
  - `JournalDatePiece` (par défaut) ;
  - `JournalPiece` ;
  - `JournalDateReference` ;
  - `Equilibre` : les lignes consécutives forment une pièce jusqu'à ce que débit et crédit soient égaux.
- **N° de pièce** : s'il est vide, ou si `NumerotationAutomatique` est activé, la procédure attribue le numéro suivant selon `F_JOURNAUX.JO_NumPiece`.
- **RenumeroterDoublon** (activé par défaut) : si le n° du fichier existe déjà dans le journal, la pièce prend le numéro qui suit la dernière pièce de la même séquence (même préfixe, même largeur) et l'import continue. Par exemple, si les pièces 1 à 50 existent, la pièce 2 du fichier devient 51. Désactivé, la pièce est refusée (erreur 50005). `AutoriserDoublon` garde au contraire le n° du fichier tel quel.
- **Contrôle des doublons dans le fichier** : avant l'import, un n° de pièce répété dans le fichier n'est signalé que dans le périmètre où la procédure le refuserait ou le renuméroterait, selon `F_JOURNAUX.JO_NumPiece` : même journal et même mois en numérotation mensuelle, même journal en numérotation continue (ou manuelle), toute la base en numérotation continue pour la base. Des pièces 1, 2, 3… qui repartent chaque mois ne sont donc pas signalées dans un journal à numérotation mensuelle.
- **RefPiece** : si la colonne est absente, la colonne *Référence* est utilisée à sa place (tronquée à 17 caractères).
- **LongueurComptes** : complète à droite les comptes numériques. Par exemple, avec 7, `422` devient `4220000`.
- **ModeTransaction** : `ParPiece` enregistre chaque pièce acceptée ; `ToutOuRien` utilise une seule transaction, et une pièce refusée annule tout.
- Une propriété absente du fichier garde sa valeur par défaut.

## 4. Fichier de données

Un fichier Excel (`.xlsx` ou `.xlsm`, la feuille se choisit à l'écran) ou un fichier CSV/TXT (séparateur détecté automatiquement, encodage ANSI ou UTF-8). Le bouton **Modèle Excel** génère un fichier vierge conforme au profil.

Les codes sont remplacés par leur forme exacte dans la base avant l'import. Par exemple, `od` devient `OD` et `c100003` devient `C100003`. C'est indispensable : les clés `cb*` sont binaires et sensibles à la casse.

## 5. Sécurité et bonnes pratiques

- **Sauvegarde** : faire une sauvegarde de la base avant le premier import réel.
- **Simulation d'abord** : la simulation fait valider chaque pièce par les vrais déclencheurs de la base (`TG_INS_F_ECRITUREC`), sans rien conserver.
- **Utilisateurs du logiciel comptable** : si quelqu'un saisit sur le même journal et le même mois, le logiciel verrouille la période (erreur 80003) et la pièce est refusée. L'application n'enregistre alors rien de partiel.
- **Chaînage cbHash** : une insertion SQL ne le calcule pas. Si le chaînage est actif sur la base, l'import est bloqué, sauf si l'option est explicitement cochée.
- **Hors périmètre** : la ventilation analytique (`F_ECRITUREA`) et les contreparties automatiques des journaux de trésorerie (la ligne de banque doit figurer dans le fichier).
- **Licence** : ce travail relève de l'interopérabilité. Vérifier les conditions de licence du logiciel comptable avant un usage en production.

## 6. Fichiers locaux

Tout est rangé dans `%AppData%\SigmaCompta\` :

- `Profils\*.json` : fichiers de paramètres ;
- `Rapports\Import_<base>_<date>.xlsx` : rapport enregistré après chaque import ;
- `historique.json` : les 300 derniers imports ;
- `parametres.json` : dernier serveur, dernière base, dernier profil ;
- `erreurs.log` : erreurs inattendues.

## 7. Architecture

```
SigmaCompta.sln
├─ src/SigmaCompta.Core      bibliothèque métier, sans interface
│  ├─ Modeles/               connexion, profil d'import, pièces, référentiel, historique
│  ├─ Sql/                   connexions, détection, inspection du schéma, scripts GO, référentiel
│  ├─ Fichiers/              lecteur/écrivain .xlsx (Open XML natif), écrivain PDF (polices standard), CSV, analyse des montants et dates
│  ├─ Import/                regroupement en pièces, validation, appel de imp.PS_ImportPiece, exports
│  ├─ Comptabilite/          saisie, consultation de compte, états comptables (balance, grand livre, journaux) et leurs exports
│  ├─ Stockage/              JSON, profils, préférences (DPAPI), historique
│  └─ Scripts/               01 pré-vérification · 02 procédure (embarqué) · 03 tests SQL
├─ src/SigmaCompta.App       WPF MVVM (ViewModels sans dépendance WPF, Views, thème)
└─ tests/SigmaCompta.Tests   contrôles du cœur métier (console)
```

**Vers la saisie directe.** `ServiceImport.AppelerAsync(connexion, transaction, piece, options, simulation)` enregistre une pièce construite par n'importe quelle source. Un futur écran de saisie n'aura qu'à créer un `PieceImport` à partir de ce qui est saisi. Il réutilisera aussi le `Referentiel` (journaux, comptes, tiers, avec leurs intitulés) pour l'autocomplétion, ainsi que `Validateur` pour les contrôles en direct.

## 8. Points à confirmer sur un vrai dossier

- **Version de SQL Server** : la procédure utilise `THROW`, `TRY_CAST` et `DATEFROMPARTS`, qui exigent un serveur SQL Server 2012 ou plus récent (version 11.x). Le niveau de compatibilité de la base n'intervient pas : une base au niveau 100 sur un serveur 11.0 a été testée avec succès. L'écran Vérification ne bloque donc qu'un serveur antérieur à 2012 ; une base sous le niveau 110 y est simplement signalée.
- **Codes `JO_NumPiece`** : l'hypothèse retenue est 0 = manuelle, 1 = continue par journal, 2 = continue pour la base, 3 = mensuelle. À comparer avec l'écran du journal dans le logiciel comptable ; l'écran Vérification affiche leur répartition.
- **Encodage de `cbEC_Piece` et `cbEC_RefPiece`** : le contrôle « Encodage des clés cb* » de l'écran Vérification le confirme.
- **Colonnes obligatoires** : si le contrôle « Colonnes obligatoires sans valeur par défaut » signale une colonne, il faut l'ajouter à l'INSERT de la procédure (script 02).
