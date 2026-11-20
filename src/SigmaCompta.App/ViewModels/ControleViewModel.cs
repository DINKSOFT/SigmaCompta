using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.App.ViewModels
{
    public sealed class ControleViewModel : PageViewModel
    {
        private List<PieceVM> _toutes = new List<PieceVM>();
        private ResultatAnalyse _analyseAffichee;
        private CancellationTokenSource _annulation;

        public ControleViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Pieces = new ObservableCollection<PieceVM>();
            RevaliderCommand = new CommandeAsync(RevaliderAsync, () => Session.AnalysePrete && !EnCours);
            SimulerCommand = new CommandeAsync(SimulerAsync, () => Session.AnalysePrete && Session.ProcedurePrete && !EnCours && NombreValides > 0);
            AnnulerCommand = new Commande(() => { if (_annulation != null) _annulation.Cancel(); }, () => EnCours);
            ExporterAnomaliesCommand = new Commande(ExporterAnomalies, () => Session.AnalysePrete);
            ExporterRapportCommand = new Commande(ExporterRapport, () => Session.AnalysePrete);
            ContinuerCommand = new Commande(() => Navigation.AllerA(6), () => Session.AnalysePrete && Session.ProcedurePrete && NombreValides > 0 && !EnCours);
            RetourDonneesCommand = new Commande(() => Navigation.AllerA(4));
            FiltreCommand = new Commande(p => Filtre = p as string ?? "Toutes");
        }

        public override string Titre { get { return "Contrôle des pièces"; } }
        public override string SousTitre { get { return "Vérifie l'équilibre, les comptes et les journaux, puis simule l'import sur le serveur sans rien enregistrer."; } }

        public ObservableCollection<PieceVM> Pieces { get; private set; }

        public CommandeAsync RevaliderCommand { get; private set; }
        public CommandeAsync SimulerCommand { get; private set; }
        public Commande AnnulerCommand { get; private set; }
        public Commande ExporterAnomaliesCommand { get; private set; }
        public Commande ExporterRapportCommand { get; private set; }
        public Commande ContinuerCommand { get; private set; }
        public Commande RetourDonneesCommand { get; private set; }
        public Commande FiltreCommand { get; private set; }

        private string _filtre = "Toutes", _recherche = "", _messageProgression;
        private PieceVM _selection;
        private bool _enCours;
        private double _progression;

        public string Filtre { get { return _filtre; } set { if (Set(ref _filtre, value)) Filtrer(); } }
        public string Recherche { get { return _recherche; } set { if (Set(ref _recherche, value)) Filtrer(); } }
        public PieceVM PieceSelectionnee { get { return _selection; } set { Set(ref _selection, value); Notifier("APieceSelectionnee"); } }
        public bool APieceSelectionnee { get { return _selection != null; } }
        public bool EnCours { get { return _enCours; } private set { Set(ref _enCours, value); CommandeBase.ReevaluerTout(); } }
        public double Progression { get { return _progression; } private set { Set(ref _progression, value); } }
        public string MessageProgression { get { return _messageProgression; } private set { Set(ref _messageProgression, value); } }

        // ---------------- Indicateurs ----------------
        public int NombrePieces { get { return _toutes.Count; } }
        public int NombreLignes { get { return _toutes.Sum(p => p.NombreLignes); } }
        public decimal TotalDebit { get { return _toutes.Sum(p => p.TotalDebit); } }
        public decimal TotalCredit { get { return _toutes.Sum(p => p.TotalCredit); } }
        public int NombreErreurs { get { return _toutes.Count(p => p.Piece.AErreur); } }
        public int NombreAvertissements { get { return _toutes.Count(p => !p.Piece.AErreur && p.Piece.AAvertissement); } }
        public int NombreValides { get { return _toutes.Count(p => !p.Piece.AErreur); } }
        public int NombreSimulees { get { return _toutes.Count(p => p.Statut == StatutPiece.Simulee); } }
        public int NombreRefusees { get { return _toutes.Count(p => p.Statut == StatutPiece.Echec); } }
        public int LignesIgnorees { get { return Session.Analyse == null ? 0 : Session.Analyse.LignesIgnorees; } }
        public bool SimulationFaite { get { return Session.SimulationFaite; } }

        private string _diagnosticHash;
        /// <summary>Après simulation, sur une base où le chaînage cbHash est actif : la base le renseigne-t-elle à l'insertion ?</summary>
        public string DiagnosticHash { get { return _diagnosticHash; } private set { Set(ref _diagnosticHash, value); } }

        private bool ChainageActif
        {
            get
            {
                return Session.Controles != null && Session.Controles.Any(c =>
                    c.Titre.StartsWith("Chaînage", StringComparison.Ordinal) && c.Statut == StatutControle.Avertissement);
            }
        }

        private void DiagnostiquerHash()
        {
            var simulees = _toutes.Where(p => p.Statut == StatutPiece.Simulee && p.Piece.LignesHashees.HasValue).ToList();
            if (!ChainageActif || simulees.Count == 0) { DiagnosticHash = null; return; }
            DiagnosticHash = simulees.All(p => p.Piece.LignesHashees == p.NombreLignes)
                ? "Chaînage d'inaltérabilité : la base a renseigné cbHash sur les lignes simulées. L'import direct reste dans la chaîne."
                : "Chaînage d'inaltérabilité : cbHash n'est PAS renseigné à l'insertion. Un import direct créerait des écritures hors chaîne (option à cocher volontairement à l'étape Import).";
        }

        public string Synthese
        {
            get
            {
                if (NombrePieces == 0) return "Aucune pièce analysée.";
                if (SimulationFaite)
                    return NombreSimulees + " pièce(s) acceptée(s) par le serveur en simulation" + (NombreRefusees > 0 ? ", " + NombreRefusees + " refusée(s)." : ".") +
                           (NombreErreurs > 0 ? " " + NombreErreurs + " pièce(s) en erreur ne seront pas importées." : "");
                if (NombreErreurs == 0) return "Toutes les pièces sont valides. Lance la simulation pour faire valider par les déclencheurs de la base.";
                return NombreErreurs + " pièce(s) en erreur sur " + NombrePieces + ". Corrige le fichier puis recharge-le, ou importe seulement les pièces valides.";
            }
        }

        public override Task ActiverAsync()
        {
            if (Session.Analyse != _analyseAffichee) Recharger();
            return Task.FromResult(0);
        }

        private void Recharger()
        {
            _analyseAffichee = Session.Analyse;
            _toutes = Session.Pieces.Select(p => new PieceVM(p)).ToList();
            DiagnosticHash = null;
            _filtre = NombreErreurs > 0 ? "Erreurs" : "Toutes";
            Notifier("Filtre");
            Filtrer();
            ActualiserIndicateurs();
        }

        private void Filtrer()
        {
            IEnumerable<PieceVM> q = _toutes;
            switch (Filtre)
            {
                case "Erreurs": q = q.Where(p => p.Piece.AErreur || p.Statut == StatutPiece.Echec); break;
                case "Avertissements": q = q.Where(p => !p.Piece.AErreur && p.Piece.AAvertissement); break;
                case "Valides": q = q.Where(p => !p.Piece.AErreur && p.Statut != StatutPiece.Echec); break;
            }
            q = q.Where(p => p.Correspond(Recherche));
            var selection = PieceSelectionnee;
            Pieces.Clear();
            foreach (var p in q) Pieces.Add(p);
            PieceSelectionnee = Pieces.Contains(selection) ? selection : Pieces.FirstOrDefault();
        }

        private void ActualiserIndicateurs()
        {
            foreach (var p in _toutes) p.Actualiser();
            NotifierPlusieurs("NombrePieces", "NombreLignes", "TotalDebit", "TotalCredit", "NombreErreurs", "NombreAvertissements",
                              "NombreValides", "NombreSimulees", "NombreRefusees", "LignesIgnorees", "SimulationFaite", "Synthese");
            var s = PieceSelectionnee;
            PieceSelectionnee = null;
            PieceSelectionnee = s;
            CommandeBase.ReevaluerTout();
        }

        private async Task RevaliderAsync()
        {
            await OccuperAsync("Rechargement du référentiel comptable et revalidation…", async () =>
            {
                var r = await Session.ObtenirReferentielAsync(true);
                var pieces = Session.Pieces;
                var profil = Session.Profil;
                await Task.Run(() => Validateur.Valider(pieces, r, profil));
                Session.SimulationFaite = false;
            });
            Filtrer();
            ActualiserIndicateurs();
        }

        private async Task SimulerAsync()
        {
            _annulation = new CancellationTokenSource();
            EnCours = true;
            Progression = 0;
            try
            {
                var progression = new Progress<ProgressionImport>(p =>
                {
                    Progression = p.Pourcentage;
                    MessageProgression = p.Message;
                    if (p.Piece != null)
                    {
                        var vm = _toutes.FirstOrDefault(x => x.Piece == p.Piece);
                        if (vm != null) vm.Actualiser();
                    }
                });
                var service = new ServiceImport(Session.Fabrique, Session.Base.Nom);
                await service.SimulerAsync(Session.Pieces, Session.Profil.Options, progression, _annulation.Token);
                Session.SimulationFaite = true;
                DiagnostiquerHash();
                Navigation.Notifier(NombreRefusees == 0
                        ? "Simulation réussie : " + NombreSimulees + " pièce(s) acceptée(s) par le serveur."
                        : "Simulation terminée : " + NombreRefusees + " pièce(s) refusée(s) par le serveur.",
                    NombreRefusees == 0 ? TypeNotification.Succes : TypeNotification.Avertissement);
                if (NombreRefusees > 0) Filtre = "Erreurs";
            }
            catch (OperationCanceledException)
            {
                Navigation.Notifier("Simulation interrompue.", TypeNotification.Avertissement);
            }
            finally
            {
                EnCours = false;
                MessageProgression = null;
                Filtrer();
                ActualiserIndicateurs();
            }
        }

        private void ExporterAnomalies()
        {
            var chemin = Dialogues.ChoisirEmplacement("Exporter les lignes en anomalie", "Classeur Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                "Anomalies_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
            if (chemin == null) return;
            ExportRapport.ExporterLignes(chemin, Session.Pieces, true);
            Navigation.Notifier("Anomalies exportées.", TypeNotification.Succes);
            Dialogues.OuvrirDansExplorateur(chemin);
        }

        private void ExporterRapport()
        {
            var chemin = Dialogues.ChoisirEmplacement("Exporter le contrôle des pièces", "Classeur Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                "Controle_pieces_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
            if (chemin == null) return;
            ExportRapport.ExporterPieces(chemin, Session.Pieces);
            Navigation.Notifier("Rapport exporté.", TypeNotification.Succes);
            Dialogues.OuvrirDansExplorateur(chemin);
        }
    }
}
