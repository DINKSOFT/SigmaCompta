using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>Résultat d'un lot de script, prêt pour l'affichage (colonnes renommées c0…cn, en-tête dans Caption).</summary>
    public sealed class LotVM
    {
        public LotVM(ResultatLot lot)
        {
            Numero = lot.Numero;
            Titre = lot.Titre;
            Erreur = lot.Erreur;
            Messages = string.Join(Environment.NewLine, lot.Messages);
            Vues = lot.Tables.Select(Preparer).ToList();
            Duree = lot.Duree.TotalMilliseconds < 1000 ? (int)lot.Duree.TotalMilliseconds + " ms" : lot.Duree.TotalSeconds.ToString("0.0") + " s";
        }

        public int Numero { get; private set; }
        public string Titre { get; private set; }
        public string Erreur { get; private set; }
        public bool EnErreur { get { return !string.IsNullOrEmpty(Erreur); } }
        public string Messages { get; private set; }
        public List<DataView> Vues { get; private set; }
        public string Duree { get; private set; }
        public int NombreLignes { get { return Vues.Sum(v => v.Count); } }

        public static DataView Preparer(DataTable t)
        {
            for (int i = 0; i < t.Columns.Count; i++)
            {
                var col = t.Columns[i];
                var entete = string.IsNullOrEmpty(col.ColumnName) || col.ColumnName.StartsWith("Column") ? "(sans nom)" : col.ColumnName;
                col.ColumnName = "c" + i;
                col.Caption = entete;
            }
            return t.DefaultView;
        }
    }

    public sealed class VerificationViewModel : PageViewModel
    {
        private string _baseControlee;

        public VerificationViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Controles = new ObservableCollection<Controle>();
            Lots = new ObservableCollection<LotVM>();
            ControlerCommand = new CommandeAsync(ControlerAsync, () => Session.BaseChoisie);
            InstallerCommand = new CommandeAsync(InstallerAsync, () => Session.BaseChoisie);
            ExecuterScriptCommand = new CommandeAsync(ExecuterScriptAsync, () => Session.BaseChoisie);
            ExporterScriptsCommand = new CommandeAsync(ExporterScriptsAsync);
            ContinuerCommand = new Commande(() => Navigation.AllerA(4), () => Session.BaseChoisie);
            MasquerScriptCommand = new Commande(() => AfficherScript = false);
        }

        public override string Titre { get { return "Vérification de la base"; } }
        public override string SousTitre { get { return "Contrôle du schéma de la base et installation de la procédure d'import."; } }

        public ObservableCollection<Controle> Controles { get; private set; }
        public ObservableCollection<LotVM> Lots { get; private set; }

        public CommandeAsync ControlerCommand { get; private set; }
        public CommandeAsync InstallerCommand { get; private set; }
        public CommandeAsync ExecuterScriptCommand { get; private set; }
        public CommandeAsync ExporterScriptsCommand { get; private set; }
        public Commande ContinuerCommand { get; private set; }
        public Commande MasquerScriptCommand { get; private set; }

        private LotVM _lot;
        private bool _afficherScript;
        public LotVM LotSelectionne { get { return _lot; } set { Set(ref _lot, value); } }
        public bool AfficherScript { get { return _afficherScript; } set { Set(ref _afficherScript, value); } }

        public int NombreOk { get { return Controles.Count(c => c.Statut == StatutControle.Ok); } }
        public int NombreAvertissements { get { return Controles.Count(c => c.Statut == StatutControle.Avertissement); } }
        public int NombreErreurs { get { return Controles.Count(c => c.Statut == StatutControle.Erreur); } }
        public bool Bloque { get { return Controles.Any(c => c.Statut == StatutControle.Erreur && c.Bloquant); } }
        public bool ProcedureAbsenteOuAncienne
        {
            get
            {
                var p = Controles.FirstOrDefault(c => c.Titre.StartsWith("Procédure"));
                return p != null && p.Statut != StatutControle.Ok;
            }
        }
        public string VersionApplication { get { return ScriptsEmbarques.VersionProcedureEmbarquee; } }
        public string NomBase { get { return Session.Base == null ? "" : Session.Base.Nom; } }

        public string Synthese
        {
            get
            {
                if (Controles.Count == 0) return "Contrôles non lancés.";
                if (Bloque) return "Import impossible en l'état : corrige les points bloquants ci-dessous.";
                if (NombreAvertissements > 0) return "Base prête pour l'import — " + NombreAvertissements + " point(s) d'attention.";
                return "Base prête pour l'import.";
            }
        }

        public override async Task ActiverAsync()
        {
            Notifier("NomBase");
            if (Session.BaseChoisie && _baseControlee != Session.Base.Nom)
                await ControlerAsync();
        }

        private async Task ControlerAsync()
        {
            await OccuperAsync("Contrôle de la base " + Session.Base.Nom + "…", async () =>
            {
                List<Controle> liste;
                using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                    liste = await InspecteurSchema.ControlerAsync(c);

                bool bloque = liste.Any(x => x.Statut == StatutControle.Erreur && x.Bloquant);
                if (!liste.Any(x => x.Titre.StartsWith("Tables comptables") && x.Statut == StatutControle.Erreur))
                {
                    MessageOccupe = "Chargement des journaux, comptes et tiers…";
                    try
                    {
                        var r = await Session.ObtenirReferentielAsync(true);
                        liste.Add(Controle.Creer("Référentiel comptable chargé", StatutControle.Ok,
                            r.Journaux.Count + " journaux, " + r.Comptes.Count + " comptes généraux, " + r.Tiers.Count + " tiers, " +
                            r.Exercices.Count + " exercice(s)" + (r.PeriodesCloturees.Count > 0 ? ", " + r.PeriodesCloturees.Count + " période(s) clôturée(s)." : ".")));
                    }
                    catch (Exception ex)
                    {
                        liste.Add(Controle.Creer("Référentiel comptable", StatutControle.Erreur, "Lecture impossible : " + ex.Message, true));
                        bloque = true;
                    }
                }

                Controles.Clear();
                foreach (var x in liste.OrderBy(x => x.Statut == StatutControle.Erreur ? 0 : x.Statut == StatutControle.Avertissement ? 1 : 2))
                    Controles.Add(x);
                _baseControlee = Session.Base.Nom;
                Session.Controles = liste;
                Session.ProcedurePrete = !bloque;
                NotifierPlusieurs("NombreOk", "NombreAvertissements", "NombreErreurs", "Bloque", "Synthese", "ProcedureAbsenteOuAncienne");
            });
        }

        private async Task InstallerAsync()
        {
            var b = Session.Base;
            if (!Dialogues.Confirmer("Installer la procédure d'import",
                "La procédure imp.PS_ImportPiece (version " + VersionApplication + ") et le type imp.TT_LigneEcriture vont être créés ou remplacés dans la base « " + b.Nom + " ».\n\n" +
                "Aucune écriture n'est modifiée. L'opération est faite dans une transaction : en cas d'échec, rien n'est changé.",
                "Installer")) return;

            var ok = await OccuperAsync("Installation de la procédure…", async () =>
            {
                var progression = new Progress<string>(m => MessageOccupe = m);
                using (var c = await Session.Fabrique.OuvrirAsync(b.Nom))
                    await InstallateurProcedure.InstallerAsync(c, progression);
            });
            if (!ok) return;
            b.ProcedureInstallee = true;
            b.VersionProcedure = VersionApplication;
            Navigation.Notifier("Procédure installée dans " + b.Nom + ".", TypeNotification.Succes);
            _baseControlee = null;
            await ControlerAsync();
        }

        private async Task ExecuterScriptAsync()
        {
            await OccuperAsync("Exécution du script de pré-vérification (lecture seule)…", async () =>
            {
                List<ResultatLot> res;
                var progression = new Progress<string>(m => MessageOccupe = m);
                using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                    res = await ExecuteurScript.ExecuterAsync(c, ScriptsEmbarques.Lire(ScriptsEmbarques.PreVerification), null, false, progression);
                Lots.Clear();
                foreach (var l in res.Where(x => x.Tables.Count > 0 || x.EnErreur)) Lots.Add(new LotVM(l));
                LotSelectionne = Lots.FirstOrDefault();
                AfficherScript = true;
            });
        }

        private async Task ExporterScriptsAsync()
        {
            var chemin = Dialogues.ChoisirEmplacement("Exporter les scripts SQL (le dossier choisi recevra les 3 scripts)",
                "Script SQL (*.sql)|*.sql", ScriptsEmbarques.Procedure);
            if (chemin == null) return;
            var dossier = Path.GetDirectoryName(chemin);
            ScriptsEmbarques.Exporter(dossier);
            var message = "3 scripts exportés dans " + dossier + ".";
            if (Session.BaseChoisie)
            {
                // Le script de la procédure est adapté à la base sélectionnée (clés cb* calculées retirées).
                ScriptAdapte adapte = null;
                await OccuperAsync("Adaptation du script à " + Session.Base.Nom + "…", async () =>
                {
                    using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                        adapte = await InstallateurProcedure.PreparerAsync(c);
                });
                if (adapte != null)
                {
                    File.WriteAllText(Path.Combine(dossier, ScriptsEmbarques.Procedure), adapte.Script, new System.Text.UTF8Encoding(true));
                    message += " Procédure adaptée à " + Session.Base.Nom +
                               (adapte.ColonnesRetirees.Count > 0 ? " (lignes retirées : " + string.Join(", ", adapte.ColonnesRetirees) + ")." : ".");
                }
            }
            Navigation.Notifier(message, TypeNotification.Succes);
            Dialogues.OuvrirDansExplorateur(dossier);
        }
    }
}
