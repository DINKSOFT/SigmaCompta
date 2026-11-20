using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>État partagé entre les écrans : connexion, base, profil, fichier et pièces.</summary>
    public sealed class Session : ObservableObject
    {
        public Session()
        {
            Parametres = MagasinParametres.Charger();
        }

        public ParametresApplication Parametres { get; private set; }

        public void EnregistrerParametres()
        {
            MagasinParametres.Enregistrer(Parametres);
        }

        // ---------------- Connexion ----------------
        private FabriqueConnexion _fabrique;
        private InfoServeur _serveur;

        public FabriqueConnexion Fabrique { get { return _fabrique; } }
        public InfoServeur Serveur { get { return _serveur; } }
        public bool EstConnecte { get { return _fabrique != null; } }

        public void DefinirConnexion(FabriqueConnexion fabrique, InfoServeur serveur)
        {
            _fabrique = fabrique;
            _serveur = serveur;
            DefinirBase(null, null);
            NotifierPlusieurs("Fabrique", "Serveur", "EstConnecte", "LibelleConnexion");
        }

        // ---------------- Base & table cible ----------------
        private InfoBase _base;
        private InfoTable _tableCible;

        public InfoBase Base { get { return _base; } }
        public InfoTable TableCible { get { return _tableCible; } }
        public bool BaseChoisie { get { return _base != null; } }

        public void DefinirBase(InfoBase b, InfoTable cible)
        {
            bool change = _base == null || b == null || _base.Nom != b.Nom;
            _base = b;
            _tableCible = cible;
            if (change)
            {
                Referentiel = null;
                ProcedurePrete = false;
                Controles = null;
                InvaliderAnalyse();
            }
            if (b != null)
            {
                Parametres.DerniereBase = b.Nom;
                EnregistrerParametres();
            }
            NotifierPlusieurs("Base", "TableCible", "BaseChoisie", "LibelleConnexion");
        }

        public string LibelleConnexion
        {
            get
            {
                if (!EstConnecte) return "Non connecté";
                return _base == null ? _fabrique.Parametres.Serveur : _fabrique.Parametres.Serveur + " · " + _base.Nom;
            }
        }

        // ---------------- Vérification ----------------
        private bool _procedurePrete;
        private List<Controle> _controles;
        private Referentiel _referentiel;

        public bool ProcedurePrete { get { return _procedurePrete; } set { Set(ref _procedurePrete, value); } }
        public List<Controle> Controles { get { return _controles; } set { Set(ref _controles, value); } }
        public Referentiel Referentiel { get { return _referentiel; } set { Set(ref _referentiel, value); } }

        public async Task<Referentiel> ObtenirReferentielAsync(bool recharger = false)
        {
            if (!BaseChoisie) throw new InvalidOperationException("Aucune base sélectionnée.");
            if (_referentiel == null || recharger)
            {
                using (var c = await _fabrique.OuvrirAsync(_base.Nom))
                    Referentiel = await ChargeurReferentiel.ChargerAsync(c);
            }
            return _referentiel;
        }

        // ---------------- Données ----------------
        private ProfilImport _profil;
        private string _cheminProfil;
        private TableSource _source;
        private ResultatAnalyse _analyse;

        public ProfilImport Profil { get { return _profil; } set { Set(ref _profil, value); } }
        public string CheminProfil { get { return _cheminProfil; } set { Set(ref _cheminProfil, value); } }
        public TableSource Source { get { return _source; } set { Set(ref _source, value); } }

        public ResultatAnalyse Analyse
        {
            get { return _analyse; }
            set
            {
                if (Set(ref _analyse, value)) NotifierPlusieurs("Pieces", "AnalysePrete", "ImportTermine");
            }
        }

        public IList<PieceImport> Pieces { get { return _analyse == null ? (IList<PieceImport>)new PieceImport[0] : _analyse.Pieces; } }
        public bool AnalysePrete { get { return _analyse != null && _analyse.Pieces.Count > 0; } }

        private bool _importTermine;
        public bool ImportTermine { get { return _importTermine; } set { Set(ref _importTermine, value); } }

        private bool _simulationFaite;
        public bool SimulationFaite { get { return _simulationFaite; } set { Set(ref _simulationFaite, value); } }

        public void InvaliderAnalyse()
        {
            Analyse = null;
            ImportTermine = false;
            SimulationFaite = false;
        }

        public string NomFichier
        {
            get { return _source == null ? "" : System.IO.Path.GetFileName(_source.Chemin); }
        }
    }

    /// <summary>Base des écrans : titre, état « occupé », exécution protégée.</summary>
    public abstract class PageViewModel : ObservableObject
    {
        protected PageViewModel(Session session, INavigation navigation, IDialogues dialogues)
        {
            Session = session;
            Navigation = navigation;
            Dialogues = dialogues;
        }

        public Session Session { get; private set; }
        protected INavigation Navigation { get; private set; }
        protected IDialogues Dialogues { get; private set; }

        public abstract string Titre { get; }
        public abstract string SousTitre { get; }

        private bool _occupe;
        private string _messageOccupe;

        public bool EstOccupe { get { return _occupe; } private set { Set(ref _occupe, value); } }
        public string MessageOccupe { get { return _messageOccupe; } set { Set(ref _messageOccupe, value); } }

        /// <summary>Appelée à chaque affichage de l'écran.</summary>
        public virtual Task ActiverAsync() { return Task.FromResult(0); }

        /// <summary>Exécute une action en affichant le voile d'attente ; les erreurs sont notifiées.</summary>
        protected async Task<bool> OccuperAsync(string message, Func<Task> action)
        {
            if (EstOccupe) return false;
            MessageOccupe = message;
            EstOccupe = true;
            try
            {
                await action();
                return true;
            }
            catch (Exception ex)
            {
                Navigation.Notifier(SqlOutils.MessageConvivial(ex), TypeNotification.Erreur);
                return false;
            }
            finally
            {
                EstOccupe = false;
                CommandeBase.ReevaluerTout();
            }
        }

        /// <summary>Lance une tâche sans l'attendre (changement de sélection…), erreurs notifiées.</summary>
        protected async void Lancer(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { Navigation.Notifier(SqlOutils.MessageConvivial(ex), TypeNotification.Erreur); }
        }
    }

    /// <summary>Enveloppe observable d'une pièce (statut rafraîchi pendant la simulation / l'import).</summary>
    public sealed class PieceVM : ObservableObject
    {
        public PieceVM(PieceImport piece) { Piece = piece; }

        public PieceImport Piece { get; private set; }
        public int Numero { get { return Piece.Numero; } }
        public string Journal { get { return Piece.Journal; } }
        public DateTime? Date { get { return Piece.Date; } }
        public string PieceAffichee { get { return Piece.PieceAffichee; } }
        public string PieceFournie { get { return Piece.PieceFournie; } }
        public int NombreLignes { get { return Piece.NombreLignes; } }
        public decimal TotalDebit { get { return Piece.TotalDebit; } }
        public decimal TotalCredit { get { return Piece.TotalCredit; } }
        public decimal Ecart { get { return Piece.Ecart; } }
        public StatutPiece Statut { get { return Piece.Statut; } }
        public string StatutLibelle { get { return ServiceImport.LibelleStatut(Piece.Statut); } }
        public string Resume { get { return Piece.Resume; } }
        public string PlageEcNo { get { return Piece.PlageEcNo; } }
        public int NombreErreurs { get { return Piece.NombreErreurs; } }
        public string Libelle
        {
            get
            {
                var l = Piece.Lignes.Select(x => x.Libelle).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                return l ?? "";
            }
        }
        public IEnumerable<Anomalie> Anomalies { get { return Piece.ToutesAnomalies.OrderBy(a => a.EstErreur ? 0 : 1).ThenBy(a => a.LigneFichier); } }
        public IList<LigneImport> Lignes { get { return Piece.Lignes; } }

        public bool Correspond(string recherche)
        {
            if (string.IsNullOrWhiteSpace(recherche)) return true;
            var r = recherche.Trim();
            Func<string, bool> c = s => !string.IsNullOrEmpty(s) && s.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0;
            return c(Piece.Journal) || c(Piece.PieceFournie) || c(Piece.NumeroAttribue) || c(Piece.Resume)
                || Piece.Lignes.Any(l => c(l.CompteGeneral) || c(l.CompteTiers) || c(l.Libelle) || c(l.Reference));
        }

        public void Actualiser() { NotifierTout(); }
    }
}
