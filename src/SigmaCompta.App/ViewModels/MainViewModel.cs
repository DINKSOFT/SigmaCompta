using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.App.ViewModels
{
    public sealed class EtapeViewModel : ObservableObject
    {
        public EtapeViewModel(int numero, string titre, string description, string icone, PageViewModel page, bool estEtape = true)
        {
            Numero = numero;
            Titre = titre;
            Description = description;
            Icone = icone;
            Page = page;
            EstEtape = estEtape;
        }

        public int Numero { get; private set; }
        /// <summary>Rubrique de la barre latérale (Dossier, Comptabilité, Import), affichée dans la barre de titre.</summary>
        public string Rubrique { get; set; } = "Import";
        public string Titre { get; private set; }
        public string Description { get; private set; }
        public string Icone { get; private set; }
        public PageViewModel Page { get; private set; }
        /// <summary>false pour les entrées hors parcours (Historique).</summary>
        public bool EstEtape { get; private set; }

        private bool _active, _terminee, _accessible = true;
        public bool EstActive { get { return _active; } set { Set(ref _active, value); } }
        public bool EstTerminee { get { return _terminee; } set { Set(ref _terminee, value); } }
        public bool EstAccessible { get { return _accessible; } set { Set(ref _accessible, value); } }
    }

    public sealed class MainViewModel : ObservableObject, INavigation
    {
        private readonly Func<int, Task> _delai;

        public MainViewModel(IDialogues dialogues, Func<int, Task> delai = null)
        {
            _delai = delai ?? (ms => Task.Delay(ms));
            Session = new Session();
            Session.PropertyChanged += (s, e) => ActualiserEtapes();
            CommandeAsync.GestionnaireErreur = ex => Notifier(SqlOutils.MessageConvivial(ex), TypeNotification.Erreur);

            Connexion = new ConnexionViewModel(Session, this, dialogues);
            Bases = new BasesViewModel(Session, this, dialogues);
            Verification = new VerificationViewModel(Session, this, dialogues);
            Donnees = new DonneesViewModel(Session, this, dialogues);
            Controle = new ControleViewModel(Session, this, dialogues);
            Import = new ImportViewModel(Session, this, dialogues);
            Historique = new HistoriqueViewModel(Session, this, dialogues);
            Consultation = new ConsultationViewModel(Session, this, dialogues);
            Etats = new EtatsViewModel(Session, this, dialogues);
            Saisie = new SaisieViewModel(Session, this, dialogues);
            Accueil = new AccueilViewModel(Session, this, dialogues);

            Etapes = new ObservableCollection<EtapeViewModel>
            {
                new EtapeViewModel(1, "Connexion", "Serveur SQL", "\uE703", Connexion),
                new EtapeViewModel(2, "Base & table", "Dossier comptable cible", "\uE8F1", Bases),
                new EtapeViewModel(3, "Vérification", "Schéma & procédure", "\uE9D9", Verification),
                new EtapeViewModel(4, "Données", "Fichier & paramètres", "\uE8E5", Donnees),
                new EtapeViewModel(5, "Contrôle", "Pièces & simulation", "\uE73E", Controle),
                new EtapeViewModel(6, "Import", "Écriture en comptabilité", "\uE898", Import),
            };
            EtapeHistorique = new EtapeViewModel(7, "Historique", "Imports passés", "\uE81C", Historique, false);
            Etapes[0].Rubrique = "Dossier";
            Etapes[1].Rubrique = "Dossier";

            // Barre latérale : Dossier (connexion, base), Comptabilité (modules), Import d'écritures (parcours existant).
            EtapesDossier = new ObservableCollection<EtapeViewModel>(Etapes.Take(2));
            EtapesImport = new ObservableCollection<EtapeViewModel>(Etapes.Skip(2));
            Modules = new ObservableCollection<EtapeViewModel>
            {
                new EtapeViewModel(12, "Saisie", "Pièces au clavier", "\uE70F", Saisie, false) { Rubrique = "Comptabilité" },
                new EtapeViewModel(10, "Consultation", "Comptes & solde progressif", "\uE721", Consultation, false) { Rubrique = "Comptabilité" },
                new EtapeViewModel(11, "États comptables", "Balance, grand livre, journaux", "\uE8A5", Etats, false) { Rubrique = "Comptabilité" },
            };

            EtapeAccueil = new EtapeViewModel(0, "Accueil", "Tous les modules", "\uE80F", Accueil, false) { Rubrique = "Sigma Compta" };
            Accueil.Construire(this, Naviguer);

            NaviguerCommand = new Commande(p => Naviguer(p as EtapeViewModel), p => p is EtapeViewModel && ((EtapeViewModel)p).EstAccessible);
            FermerNotificationCommand = new Commande(() => NotificationVisible = false);

            ActualiserEtapes();
            Naviguer(EtapeAccueil);
        }

        public Session Session { get; private set; }
        public ConnexionViewModel Connexion { get; private set; }
        public BasesViewModel Bases { get; private set; }
        public VerificationViewModel Verification { get; private set; }
        public DonneesViewModel Donnees { get; private set; }
        public ControleViewModel Controle { get; private set; }
        public ImportViewModel Import { get; private set; }
        public HistoriqueViewModel Historique { get; private set; }
        public ConsultationViewModel Consultation { get; private set; }
        public EtatsViewModel Etats { get; private set; }
        public SaisieViewModel Saisie { get; private set; }
        public AccueilViewModel Accueil { get; private set; }

        /// <summary>Les six étapes historiques (connexion → import) : leurs index restent ceux d'origine.</summary>
        public ObservableCollection<EtapeViewModel> Etapes { get; private set; }
        public ObservableCollection<EtapeViewModel> EtapesDossier { get; private set; }
        public ObservableCollection<EtapeViewModel> EtapesImport { get; private set; }
        public ObservableCollection<EtapeViewModel> Modules { get; private set; }
        public EtapeViewModel EtapeHistorique { get; private set; }
        public EtapeViewModel EtapeAccueil { get; private set; }

        public Commande NaviguerCommand { get; private set; }
        public Commande FermerNotificationCommand { get; private set; }

        private EtapeViewModel _etapeCourante;
        public EtapeViewModel EtapeCourante { get { return _etapeCourante; } private set { Set(ref _etapeCourante, value); } }

        private PageViewModel _page;
        public PageViewModel PageCourante { get { return _page; } private set { Set(ref _page, value); } }

        public string Version { get { return "v" + typeof(MainViewModel).Assembly.GetName().Version.ToString(3); } }

        private async void Naviguer(EtapeViewModel etape)
        {
            if (etape == null || !etape.EstAccessible) return;
            foreach (var e in Etapes) e.EstActive = e == etape;
            foreach (var m in Modules) m.EstActive = m == etape;
            EtapeHistorique.EstActive = etape == EtapeHistorique;
            EtapeAccueil.EstActive = etape == EtapeAccueil;
            EtapeCourante = etape;
            PageCourante = etape.Page;
            try
            {
                await etape.Page.ActiverAsync();
            }
            catch (Exception ex)
            {
                Notifier(SqlOutils.MessageConvivial(ex), TypeNotification.Erreur);
            }
        }

        public void AllerA(int numeroEtape)
        {
            var e = Etapes.Concat(Modules).Concat(new[] { EtapeHistorique, EtapeAccueil }).FirstOrDefault(x => x.Numero == numeroEtape);
            ActualiserEtapes();
            if (e != null) Naviguer(e);
        }

        public void ActualiserEtapes()
        {
            if (Etapes == null) return;
            var s = Session;
            Etapes[0].EstTerminee = s.EstConnecte;
            Etapes[1].EstAccessible = s.EstConnecte;
            Etapes[1].EstTerminee = s.BaseChoisie;
            Etapes[2].EstAccessible = s.BaseChoisie;
            Etapes[2].EstTerminee = s.ProcedurePrete;
            Etapes[3].EstAccessible = s.BaseChoisie;
            Etapes[3].EstTerminee = s.AnalysePrete;
            Etapes[4].EstAccessible = s.AnalysePrete;
            Etapes[4].EstTerminee = s.SimulationFaite;
            Etapes[5].EstAccessible = s.AnalysePrete && s.ProcedurePrete;
            Etapes[5].EstTerminee = s.ImportTermine;
            if (Modules != null)
                foreach (var m in Modules) m.EstAccessible = s.BaseChoisie;
            CommandeBase.ReevaluerTout();
        }

        // ---------------- Notifications (bandeau éphémère) ----------------
        private string _notification;
        private TypeNotification _typeNotification;
        private bool _notificationVisible;
        private int _jetonNotification;

        public string Notification { get { return _notification; } private set { Set(ref _notification, value); } }
        public TypeNotification TypeNotification { get { return _typeNotification; } private set { Set(ref _typeNotification, value); } }
        public bool NotificationVisible { get { return _notificationVisible; } set { Set(ref _notificationVisible, value); } }

        public async void Notifier(string message, TypeNotification type = TypeNotification.Info)
        {
            Notification = message;
            TypeNotification = type;
            NotificationVisible = true;
            int jeton = ++_jetonNotification;
            // Les erreurs restent affichées plus longtemps.
            await _delai(type == TypeNotification.Erreur ? 12000 : 5000);
            if (jeton == _jetonNotification) NotificationVisible = false;
        }
    }
}
