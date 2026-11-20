using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.App.ViewModels
{
    public sealed class BasesViewModel : PageViewModel
    {
        private List<InfoBase> _toutes = new List<InfoBase>();
        private List<InfoTable> _toutesTables = new List<InfoTable>();
        private string _serveurCharge;

        public BasesViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            BasesAffichees = new ObservableCollection<InfoBase>();
            TablesAffichees = new ObservableCollection<InfoTable>();
            Structure = new ObservableCollection<ColonneStructure>();

            ActualiserCommand = new CommandeAsync(ChargerBasesAsync, () => Session.EstConnecte);
            // Une structure non conforme n'empêche pas de continuer : l'écran Vérification détaille le problème et bloque l'import.
            UtiliserCommand = new Commande(Utiliser, () => BaseSelectionnee != null && BaseSelectionnee.EstComptabilite && TableCible != null);
        }

        public override string Titre { get { return "Base de données & table cible"; } }
        public override string SousTitre { get { return "Sélectionne le dossier comptable où importer les écritures."; } }

        public ObservableCollection<InfoBase> BasesAffichees { get; private set; }
        public ObservableCollection<InfoTable> TablesAffichees { get; private set; }
        public ObservableCollection<ColonneStructure> Structure { get; private set; }

        public CommandeAsync ActualiserCommand { get; private set; }
        public Commande UtiliserCommand { get; private set; }

        private string _recherche = "";
        private bool _seulementComptables = true, _toutesLesTables, _chargementTables;
        private InfoBase _selection;
        private InfoTable _cible;

        public string Recherche { get { return _recherche; } set { if (Set(ref _recherche, value)) Filtrer(); } }
        public bool SeulementComptables { get { return _seulementComptables; } set { if (Set(ref _seulementComptables, value)) Filtrer(); } }
        public bool ToutesLesTables { get { return _toutesLesTables; } set { if (Set(ref _toutesLesTables, value)) FiltrerTables(); } }
        public bool ChargementTables { get { return _chargementTables; } private set { Set(ref _chargementTables, value); } }

        public int NombreBases { get { return _toutes.Count; } }
        public int NombreBasesComptables { get { return _toutes.Count(b => b.EstComptabilite); } }

        public InfoBase BaseSelectionnee
        {
            get { return _selection; }
            set
            {
                if (Set(ref _selection, value))
                {
                    Notifier("ABaseSelectionnee");
                    Lancer(ChargerTablesAsync);
                }
            }
        }

        public bool ABaseSelectionnee { get { return _selection != null; } }

        public InfoTable TableCible
        {
            get { return _cible; }
            set
            {
                if (Set(ref _cible, value))
                {
                    Notifier("AvertissementTable");
                    Lancer(ChargerStructureAsync);
                }
            }
        }

        public bool StructureConforme { get { return Structure.Count > 0 && Structure.All(c => c.Conforme); } }

        public string AvertissementTable
        {
            get
            {
                if (_cible == null || _cible.EstTableEcrituresComptables) return null;
                return "La procédure d'import écrit toujours dans dbo.F_ECRITUREC (avec F_JMOUV, F_COMPTEG…). " +
                       _cible.NomComplet + " est affichée pour contrôle de structure uniquement.";
            }
        }

        public override async Task ActiverAsync()
        {
            if (!Session.EstConnecte) return;
            if (_serveurCharge != Session.Fabrique.Parametres.Serveur || _toutes.Count == 0)
                await ChargerBasesAsync();
        }

        private async Task ChargerBasesAsync()
        {
            await OccuperAsync("Recherche des bases comptables sur le serveur…", async () =>
            {
                List<InfoBase> bases;
                var progression = new Progress<string>(m => MessageOccupe = m);
                using (var c = await Session.Fabrique.OuvrirAsync())
                    bases = await ExplorateurBases.ListerBasesAsync(c, progression);
                _toutes = bases;
                _serveurCharge = Session.Fabrique.Parametres.Serveur;
                NotifierPlusieurs("NombreBases", "NombreBasesComptables");
                Filtrer();

                var derniere = Session.Base != null ? Session.Base.Nom : Session.Parametres.DerniereBase;
                var aSelectionner = BasesAffichees.FirstOrDefault(b => string.Equals(b.Nom, derniere, StringComparison.OrdinalIgnoreCase))
                                    ?? BasesAffichees.FirstOrDefault(b => b.EstComptabilite);
                if (aSelectionner != null) BaseSelectionnee = aSelectionner;
            });
        }

        private void Filtrer()
        {
            var r = (Recherche ?? "").Trim();
            var liste = _toutes.Where(b => (!SeulementComptables || b.EstComptabilite)
                                           && (r.Length == 0
                                               || b.Nom.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0
                                               || (b.RaisonSociale ?? "").IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0))
                               .OrderByDescending(b => b.EstComptabilite).ThenBy(b => b.Nom, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            var selection = BaseSelectionnee;
            BasesAffichees.Clear();
            foreach (var b in liste) BasesAffichees.Add(b);
            if (selection != null && !liste.Contains(selection)) BaseSelectionnee = null;
        }

        private async Task ChargerTablesAsync()
        {
            _toutesTables = new List<InfoTable>();
            TablesAffichees.Clear();
            Structure.Clear();
            TableCible = null;
            var b = BaseSelectionnee;
            if (b == null || !b.Accessible) return;

            ChargementTables = true;
            try
            {
                List<InfoTable> tables;
                using (var c = await Session.Fabrique.OuvrirAsync(b.Nom))
                    tables = await ExplorateurBases.ListerTablesAsync(c);
                if (BaseSelectionnee != b) return; // sélection changée entre-temps
                _toutesTables = tables;
                FiltrerTables();
                TableCible = TablesAffichees.FirstOrDefault(t => t.EstTableEcrituresComptables) ?? TablesAffichees.FirstOrDefault(t => t.EstCompatible);
            }
            finally
            {
                ChargementTables = false;
            }
        }

        private void FiltrerTables()
        {
            var cible = TableCible;
            TablesAffichees.Clear();
            foreach (var t in _toutesTables.Where(t => ToutesLesTables || t.EstCompatible)) TablesAffichees.Add(t);
            if (cible != null && TablesAffichees.Contains(cible)) TableCible = cible;
        }

        private async Task ChargerStructureAsync()
        {
            Structure.Clear();
            Notifier("StructureConforme");
            var t = TableCible;
            var b = BaseSelectionnee;
            if (t == null || b == null) return;
            List<ColonneStructure> colonnes;
            using (var c = await Session.Fabrique.OuvrirAsync(b.Nom))
                colonnes = await ExplorateurBases.StructureAsync(c, t.Schema, t.Nom);
            if (TableCible != t) return;
            foreach (var col in colonnes) Structure.Add(col);
            Notifier("StructureConforme");
            CommandeBase.ReevaluerTout();
        }

        private void Utiliser()
        {
            var cible = TableCible.EstTableEcrituresComptables
                ? TableCible
                : _toutesTables.FirstOrDefault(t => t.EstTableEcrituresComptables) ?? TableCible;
            Session.DefinirBase(BaseSelectionnee, cible);
            Navigation.Notifier("Base « " + BaseSelectionnee.Nom + " » sélectionnée" +
                                (string.IsNullOrEmpty(BaseSelectionnee.RaisonSociale) ? "." : " — " + BaseSelectionnee.RaisonSociale + "."),
                                TypeNotification.Succes);
            Navigation.AllerA(3);
        }
    }
}
