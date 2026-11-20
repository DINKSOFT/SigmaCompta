using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    public sealed class ImportViewModel : PageViewModel
    {
        private CancellationTokenSource _annulation;
        private List<PieceVM> _toutes = new List<PieceVM>();
        private ResultatAnalyse _analyseAffichee;

        public ImportViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Resultats = new ObservableCollection<PieceVM>();
            LancerCommand = new CommandeAsync(LancerAsync, () => PeutLancer);
            AnnulerCommand = new Commande(() => { if (_annulation != null) _annulation.Cancel(); }, () => EnCours);
            ExporterRapportCommand = new Commande(ExporterRapport, () => Termine);
            OuvrirRapportCommand = new Commande(() => Dialogues.OuvrirDansExplorateur(CheminRapport), () => !string.IsNullOrEmpty(CheminRapport));
            NouvelImportCommand = new Commande(NouvelImport, () => !EnCours);
            HistoriqueCommand = new Commande(() => Navigation.AllerA(7));
        }

        public override string Titre { get { return "Import en comptabilité"; } }
        public override string SousTitre { get { return "Écriture définitive des pièces dans F_ECRITUREC via imp.PS_ImportPiece."; } }

        public ObservableCollection<PieceVM> Resultats { get; private set; }

        public CommandeAsync LancerCommand { get; private set; }
        public Commande AnnulerCommand { get; private set; }
        public Commande ExporterRapportCommand { get; private set; }
        public Commande OuvrirRapportCommand { get; private set; }
        public Commande NouvelImportCommand { get; private set; }
        public Commande HistoriqueCommand { get; private set; }

        // ---------------- Options (enregistrées dans le profil courant) ----------------
        private OptionsProfil Options { get { return Session.Profil == null ? new OptionsProfil() : Session.Profil.Options; } }

        public bool ModeToutOuRien
        {
            get { return Options.ModeTransaction == ModesTransaction.ToutOuRien; }
            set
            {
                Options.ModeTransaction = value ? ModesTransaction.ToutOuRien : ModesTransaction.ParPiece;
                NotifierPlusieurs("ModeToutOuRien", "ModeParPiece", "PeutLancer", "MessageBlocage");
                CommandeBase.ReevaluerTout();
            }
        }
        public bool ModeParPiece { get { return !ModeToutOuRien; } set { ModeToutOuRien = !value; } }
        public bool ArreterPremiereErreur { get { return Options.ArreterPremiereErreur; } set { Options.ArreterPremiereErreur = value; Notifier(); } }
        // Renuméroter et autoriser le doublon s'excluent : cocher l'un décoche l'autre.
        public bool AutoriserDoublon
        {
            get { return Options.AutoriserDoublon; }
            set
            {
                Options.AutoriserDoublon = value;
                if (value) Options.RenumeroterDoublon = false;
                NotifierPlusieurs("AutoriserDoublon", "RenumeroterDoublon");
            }
        }
        public bool RenumeroterDoublon
        {
            get { return Options.RenumeroterDoublon; }
            set
            {
                Options.RenumeroterDoublon = value;
                if (value) Options.AutoriserDoublon = false;
                NotifierPlusieurs("AutoriserDoublon", "RenumeroterDoublon");
            }
        }
        public bool IgnorerHash { get { return Options.IgnorerHash; } set { Options.IgnorerHash = value; Notifier(); } }

        private bool _exclureRefusees = true;
        public bool ExclureRefusees { get { return _exclureRefusees; } set { if (Set(ref _exclureRefusees, value)) NotifierResume(); } }

        // ---------------- Résumé ----------------
        public string Serveur { get { return Session.EstConnecte ? Session.Fabrique.Parametres.Serveur : ""; } }
        public string NomBase { get { return Session.Base == null ? "" : Session.Base.Nom; } }
        public string RaisonSociale { get { return Session.Base == null ? "" : Session.Base.RaisonSociale; } }
        public string Fichier { get { return Session.NomFichier; } }
        public string NomProfil { get { return Session.Profil == null ? "" : Session.Profil.Nom; } }

        private List<PieceImport> PiecesAImporter
        {
            get
            {
                return Session.Pieces.Where(p => !p.AErreur && !(ExclureRefusees && p.Statut == StatutPiece.Echec)).ToList();
            }
        }

        public int NombreAImporter { get { return PiecesAImporter.Count; } }
        public int NombreLignesAImporter { get { return PiecesAImporter.Sum(p => p.NombreLignes); } }
        public decimal MontantAImporter { get { return PiecesAImporter.Sum(p => p.TotalDebit); } }
        public int NombreExclues { get { return Session.Pieces.Count - NombreAImporter; } }
        public bool SimulationFaite { get { return Session.SimulationFaite; } }

        public string MessageBlocage
        {
            get
            {
                if (!Session.ProcedurePrete) return "La base n'est pas prête : termine l'étape « Vérification ».";
                if (NombreAImporter == 0) return "Aucune pièce importable.";
                if (ModeToutOuRien && NombreExclues > 0) return "Mode « tout ou rien » : " + NombreExclues + " pièce(s) en erreur ou refusée(s) empêchent l'import. Passe en mode « pièce par pièce » ou corrige le fichier.";
                return null;
            }
        }

        public bool PeutLancer { get { return !EnCours && !Termine && MessageBlocage == null; } }

        // ---------------- Exécution ----------------
        private bool _enCours, _termine;
        private double _progression;
        private string _messageProgression, _bilan, _cheminRapport;
        private TypeNotification _typeBilan;

        public bool EnCours { get { return _enCours; } private set { Set(ref _enCours, value); Notifier("PeutLancer"); CommandeBase.ReevaluerTout(); } }
        public bool Termine { get { return _termine; } private set { Set(ref _termine, value); Notifier("PeutLancer"); } }
        public double Progression { get { return _progression; } private set { Set(ref _progression, value); } }
        public string MessageProgression { get { return _messageProgression; } private set { Set(ref _messageProgression, value); } }
        public string Bilan { get { return _bilan; } private set { Set(ref _bilan, value); } }
        public TypeNotification TypeBilan { get { return _typeBilan; } private set { Set(ref _typeBilan, value); } }
        public string CheminRapport { get { return _cheminRapport; } private set { Set(ref _cheminRapport, value); } }

        public int NombreImportees { get { return _toutes.Count(p => p.Statut == StatutPiece.Importee); } }
        public int NombreEchecs { get { return _toutes.Count(p => p.Statut == StatutPiece.Echec); } }
        public int NombreAnnulees { get { return _toutes.Count(p => p.Statut == StatutPiece.Annulee); } }

        public override Task ActiverAsync()
        {
            if (Session.Analyse != _analyseAffichee)
            {
                _analyseAffichee = Session.Analyse;
                _toutes = Session.Pieces.Select(p => new PieceVM(p)).ToList();
                Resultats.Clear();
                Termine = Session.ImportTermine;
                Bilan = null;
                CheminRapport = null;
                Progression = 0;
            }
            NotifierResume();
            return Task.FromResult(0);
        }

        private void NotifierResume()
        {
            NotifierPlusieurs("Serveur", "NomBase", "RaisonSociale", "Fichier", "NomProfil", "NombreAImporter", "NombreLignesAImporter",
                              "MontantAImporter", "NombreExclues", "SimulationFaite", "MessageBlocage", "PeutLancer",
                              "ModeToutOuRien", "ModeParPiece", "ArreterPremiereErreur", "AutoriserDoublon", "RenumeroterDoublon", "IgnorerHash");
            CommandeBase.ReevaluerTout();
        }

        private async Task LancerAsync()
        {
            var aImporter = PiecesAImporter;
            var message = "Tu vas écrire définitivement " + aImporter.Count + " pièce(s) (" + aImporter.Sum(p => p.NombreLignes) + " lignes, " +
                          aImporter.Sum(p => p.TotalDebit).ToString("#,##0.##") + " au débit) dans la base « " + NomBase + " »" +
                          (string.IsNullOrEmpty(RaisonSociale) ? "" : " (" + RaisonSociale + ")") + ".\n\n" +
                          (ModeToutOuRien ? "Mode tout ou rien : si une seule pièce est refusée, aucune n'est enregistrée."
                                          : "Mode pièce par pièce : chaque pièce acceptée est enregistrée immédiatement.") +
                          (SimulationFaite ? "" : "\n\nAttention : la simulation n'a pas été lancée.") +
                          "\n\nConseil : fais une sauvegarde de la base avant un premier import.";
            if (!Dialogues.Confirmer("Confirmer l'import", message, "Importer " + aImporter.Count + " pièce(s)", true)) return;

            _annulation = new CancellationTokenSource();
            Resultats.Clear();
            foreach (var p in _toutes.Where(x => aImporter.Contains(x.Piece))) Resultats.Add(p);
            EnCours = true;
            Bilan = null;
            Progression = 0;
            try
            {
                var progression = new Progress<ProgressionImport>(p =>
                {
                    Progression = p.Pourcentage;
                    MessageProgression = p.Message;
                    foreach (var vm in Resultats) vm.Actualiser();
                    NotifierPlusieurs("NombreImportees", "NombreEchecs", "NombreAnnulees");
                });
                var service = new ServiceImport(Session.Fabrique, Session.Base.Nom);
                await service.ImporterAsync(aImporter, Session.Profil.Options, progression, _annulation.Token);
            }
            finally
            {
                EnCours = false;
                MessageProgression = null;
                foreach (var vm in _toutes) vm.Actualiser();
                NotifierPlusieurs("NombreImportees", "NombreEchecs", "NombreAnnulees");
            }

            Termine = true;
            Session.ImportTermine = true;
            Progression = 100;

            var execution = ServiceImport.Bilan(aImporter, false, Session.Profil.Options.ModeTransaction);
            execution.Serveur = Serveur;
            execution.Base = NomBase;
            execution.Fichier = Session.Source != null ? Session.Source.Chemin : "";
            execution.Profil = NomProfil;
            try { MagasinHistorique.Ajouter(execution); } catch (Exception) { /* historique non critique */ }

            try
            {
                var chemin = Path.Combine(Chemins.Rapports, "Import_" + NomBase + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".xlsx");
                ExportRapport.ExporterPieces(chemin, Session.Pieces);
                CheminRapport = chemin;
            }
            catch (Exception) { CheminRapport = null; }

            if (NombreEchecs == 0 && NombreAnnulees == 0)
            {
                TypeBilan = TypeNotification.Succes;
                Bilan = NombreImportees + " pièce(s) importée(s) avec succès dans " + NomBase + ".";
            }
            else if (NombreImportees == 0)
            {
                TypeBilan = TypeNotification.Erreur;
                Bilan = "Aucune pièce enregistrée : " + NombreEchecs + " refusée(s), " + NombreAnnulees + " annulée(s).";
            }
            else
            {
                TypeBilan = TypeNotification.Avertissement;
                Bilan = NombreImportees + " pièce(s) importée(s), " + NombreEchecs + " refusée(s), " + NombreAnnulees + " non traitée(s).";
            }
            Navigation.Notifier(Bilan, TypeBilan);
            NotifierResume();
        }

        private void ExporterRapport()
        {
            var chemin = Dialogues.ChoisirEmplacement("Exporter le rapport d'import", "Classeur Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv",
                "Rapport_import_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx");
            if (chemin == null) return;
            ExportRapport.ExporterPieces(chemin, Session.Pieces);
            Dialogues.OuvrirDansExplorateur(chemin);
        }

        private void NouvelImport()
        {
            Session.InvaliderAnalyse();
            _analyseAffichee = null;
            Termine = false;
            Navigation.AllerA(4);
        }
    }
}
