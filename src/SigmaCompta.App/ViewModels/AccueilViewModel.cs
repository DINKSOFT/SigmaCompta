using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>Tuile de l'écran d'accueil : ouvre un écran (étape ou module) ou lance une action.</summary>
    public sealed class TuileAccueil : ObservableObject
    {
        private readonly Action _action;

        /// <summary>Tuile liée à un écran : accessible quand l'écran l'est ; <paramref name="avant"/> prépare l'écran (état à afficher…).</summary>
        public TuileAccueil(string libelle, string icone, string aide, EtapeViewModel etape, Action<EtapeViewModel> naviguer, Action avant = null)
        {
            Libelle = libelle;
            Icone = icone;
            Aide = aide;
            Etape = etape;
            _action = () =>
            {
                if (avant != null) avant();
                naviguer(etape);
            };
            etape.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "EstAccessible") Notifier("EstAccessible");
            };
            OuvrirCommand = new Commande(_action, () => EstAccessible);
        }

        /// <summary>Tuile d'action (dossier à ouvrir…), toujours accessible.</summary>
        public TuileAccueil(string libelle, string icone, string aide, Action action)
        {
            Libelle = libelle;
            Icone = icone;
            Aide = aide;
            _action = action;
            OuvrirCommand = new Commande(_action, () => EstAccessible);
        }

        public string Libelle { get; private set; }
        public string Icone { get; private set; }
        public string Aide { get; private set; }
        public EtapeViewModel Etape { get; private set; }
        public bool EstAccessible { get { return Etape == null || Etape.EstAccessible; } }
        public Commande OuvrirCommand { get; private set; }
    }

    public sealed class SectionAccueil
    {
        public SectionAccueil(string titre, params TuileAccueil[] tuiles)
        {
            Titre = titre;
            Tuiles = new List<TuileAccueil>(tuiles);
        }

        public string Titre { get; private set; }
        public IList<TuileAccueil> Tuiles { get; private set; }
    }

    /// <summary>Écran d'accueil : les modules de l'application regroupés en tuiles, et l'action conseillée.</summary>
    public sealed class AccueilViewModel : PageViewModel
    {
        private Action<EtapeViewModel> _naviguer;
        private EtapeViewModel _connexion, _bases, _saisie;

        public AccueilViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Sections = new ObservableCollection<SectionAccueil>();
            ActionConseilleeCommand = new Commande(ExecuterActionConseillee);
            session.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "EstConnecte" || e.PropertyName == "BaseChoisie" || e.PropertyName == "Base")
                    NotifierPlusieurs("SousTitre", "TitreBienvenue", "TexteBienvenue", "LibelleActionConseillee", "IconeActionConseillee");
            };
        }

        public override string Titre { get { return "Accueil"; } }

        public override string SousTitre
        {
            get
            {
                if (!Session.EstConnecte) return "Choisissez un module ou commencez par vous connecter à un serveur SQL.";
                if (!Session.BaseChoisie) return "Connecté au serveur. Choisissez le dossier comptable sur lequel travailler.";
                return "Dossier " + Session.Base.Nom + " ouvert : tous les modules sont disponibles.";
            }
        }

        public ObservableCollection<SectionAccueil> Sections { get; private set; }
        public Commande ActionConseilleeCommand { get; private set; }

        /// <summary>Appelée par la fenêtre principale une fois les étapes et modules créés.</summary>
        public void Construire(MainViewModel principal, Action<EtapeViewModel> naviguer)
        {
            _naviguer = naviguer;
            var etapes = principal.Etapes;
            var modules = principal.Modules;
            _connexion = etapes[0];
            _bases = etapes[1];
            _saisie = modules[0];
            var consultation = modules[1];
            var etats = modules[2];

            Action<string> etat = nom => principal.Etats.EtatChoisi = nom;
            const string aideDossier = "Disponible une fois un dossier comptable choisi.";

            Sections.Clear();
            Sections.Add(new SectionAccueil("Gestion quotidienne",
                new TuileAccueil("Saisie d'une pièce", "", "Saisir une pièce au clavier dans un journal. " + aideDossier, _saisie, naviguer),
                new TuileAccueil("Consultation des comptes", "", "Écritures d'un compte général ou tiers, solde progressif. " + aideDossier, consultation, naviguer),
                new TuileAccueil("Historique des imports", "", "Imports réalisés depuis ce poste.", principal.EtapeHistorique, naviguer)));

            Sections.Add(new SectionAccueil("États comptables",
                new TuileAccueil("Balance des comptes", "", "Balance générale ou des tiers, six colonnes. " + aideDossier, etats, naviguer, () => etat("Balance")),
                new TuileAccueil("Grand livre", "", "Grand livre général ou des tiers. " + aideDossier, etats, naviguer, () => etat("GrandLivre")),
                new TuileAccueil("Journaux", "", "Journal détaillé sur une période. " + aideDossier, etats, naviguer, () => etat("Journal")),
                new TuileAccueil("Journal centralisateur", "", "Totaux par journal et par mois. " + aideDossier, etats, naviguer, () => etat("Centralisateur"))));

            Sections.Add(new SectionAccueil("Dossier",
                new TuileAccueil("Connexion au serveur", "", "Instance SQL Server et authentification.", _connexion, naviguer),
                new TuileAccueil("Dossier comptable", "", "Choix de la base comptable et de la table cible.", _bases, naviguer),
                new TuileAccueil("Vérification du schéma", "", "Contrôle de la base et installation de la procédure d'import.", etapes[2], naviguer)));

            Sections.Add(new SectionAccueil("Import d'écritures",
                new TuileAccueil("Fichier à importer", "", "Fichier de paramètres et fichier Excel ou CSV. " + aideDossier, etapes[3], naviguer),
                new TuileAccueil("Contrôle et simulation", "", "Contrôle des pièces lues dans le fichier.", etapes[4], naviguer),
                new TuileAccueil("Import en comptabilité", "", "Écriture des pièces contrôlées dans le dossier.", etapes[5], naviguer)));

            Sections.Add(new SectionAccueil("Outils",
                new TuileAccueil("Profils d'import", "", "Ouvrir le dossier des fichiers de paramètres (.json).",
                    () => Dialogues.OuvrirDansExplorateur(Chemins.Profils)),
                new TuileAccueil("Rapports d'import", "", "Ouvrir le dossier des rapports Excel.",
                    () => Dialogues.OuvrirDansExplorateur(Chemins.Rapports)),
                new TuileAccueil("Journal des erreurs", "", "Erreurs inattendues enregistrées sur ce poste.", OuvrirJournalErreurs)));
        }

        // ---------------------------------------------------------------- Bandeau de bienvenue

        public string TitreBienvenue
        {
            get
            {
                if (Session.BaseChoisie)
                    return string.IsNullOrWhiteSpace(Session.Base.RaisonSociale) ? Session.Base.Nom : Session.Base.RaisonSociale;
                return "Bienvenue dans Sigma Compta";
            }
        }

        public string TexteBienvenue
        {
            get
            {
                if (!Session.EstConnecte) return "Pour travailler sur un dossier, connectez-vous d'abord au serveur SQL qui l'héberge.";
                if (!Session.BaseChoisie) return "Connexion établie avec " + Session.LibelleConnexion + ". Il reste à choisir le dossier comptable.";
                return Session.LibelleConnexion + " · saisie, consultation, états et import sont disponibles.";
            }
        }

        public string LibelleActionConseillee
        {
            get
            {
                if (!Session.EstConnecte) return "Se connecter";
                if (!Session.BaseChoisie) return "Choisir le dossier";
                return "Saisir une pièce";
            }
        }

        public string IconeActionConseillee
        {
            get
            {
                if (!Session.EstConnecte) return "";
                if (!Session.BaseChoisie) return "";
                return "";
            }
        }

        private void ExecuterActionConseillee()
        {
            if (_naviguer == null) return;
            _naviguer(!Session.EstConnecte ? _connexion : !Session.BaseChoisie ? _bases : _saisie);
        }

        private void OuvrirJournalErreurs()
        {
            var chemin = Path.Combine(Chemins.Racine, "erreurs.log");
            if (File.Exists(chemin)) Dialogues.OuvrirFichier(chemin);
            else Navigation.Notifier("Aucune erreur inattendue n'a été enregistrée sur ce poste.", TypeNotification.Succes);
        }
    }
}
