using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    public sealed class HistoriqueViewModel : PageViewModel
    {
        public HistoriqueViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Executions = new ObservableCollection<ExecutionImport>();
            ActualiserCommand = new Commande(Charger);
            ExporterCommand = new Commande(Exporter, () => Selection != null);
            ViderCommand = new Commande(Vider, () => Executions.Count > 0);
            OuvrirRapportsCommand = new Commande(() => Dialogues.OuvrirDansExplorateur(Chemins.Rapports));
        }

        public override string Titre { get { return "Historique des imports"; } }
        public override string SousTitre { get { return "Imports réalisés depuis ce poste, avec le détail des pièces."; } }

        public ObservableCollection<ExecutionImport> Executions { get; private set; }
        public Commande ActualiserCommand { get; private set; }
        public Commande ExporterCommand { get; private set; }
        public Commande ViderCommand { get; private set; }
        public Commande OuvrirRapportsCommand { get; private set; }

        private ExecutionImport _selection;
        public ExecutionImport Selection { get { return _selection; } set { Set(ref _selection, value); Notifier("ASelection"); CommandeBase.ReevaluerTout(); } }
        public bool ASelection { get { return _selection != null; } }

        public int NombreImports { get { return Executions.Count; } }
        public int TotalPiecesImportees { get { return Executions.Where(e => !e.Simulation).Sum(e => e.NombreReussies); } }
        public decimal TotalMontant { get { return Executions.Where(e => !e.Simulation).Sum(e => e.TotalDebit); } }

        public override Task ActiverAsync()
        {
            Charger();
            return Task.FromResult(0);
        }

        private void Charger()
        {
            var id = Selection != null ? Selection.Id : null;
            Executions.Clear();
            foreach (var e in MagasinHistorique.Charger()) Executions.Add(e);
            Selection = Executions.FirstOrDefault(e => e.Id == id) ?? Executions.FirstOrDefault();
            NotifierPlusieurs("NombreImports", "TotalPiecesImportees", "TotalMontant");
        }

        private void Exporter()
        {
            var e = Selection;
            var chemin = Dialogues.ChoisirEmplacement("Exporter le détail de l'import", "Classeur Excel (*.xlsx)|*.xlsx",
                "Historique_" + e.Base + "_" + e.Date.ToString("yyyyMMdd_HHmm") + ".xlsx");
            if (chemin == null) return;
            EcrivainXlsx.Ecrire(chemin, "Pièces",
                new[] { "Statut", "Journal", "Date", "Pièce", "EC_No", "Montant", "Message" },
                e.Pieces.Select(p => new object[] { p.Statut, p.Journal, (object)p.Date, p.Piece, p.EcNo, p.Montant, p.Message }),
                new double[] { 14, 9, 12, 14, 16, 16, 90 });
            Dialogues.OuvrirDansExplorateur(chemin);
        }

        private void Vider()
        {
            if (!Dialogues.Confirmer("Vider l'historique", "Supprimer l'historique local des " + Executions.Count + " import(s) ? Les écritures déjà importées en comptabilité ne sont pas touchées.", "Vider", true)) return;
            MagasinHistorique.Vider();
            Charger();
        }
    }
}
