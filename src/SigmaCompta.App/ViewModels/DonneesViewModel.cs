using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.App.ViewModels
{
    public sealed class ProfilItem
    {
        public string Chemin { get; set; }
        public ProfilImport Profil { get; set; }
        public string Nom { get { return Profil.Nom; } }
        public string Fichier { get { return Path.GetFileName(Chemin); } }
    }

    public sealed class OptionChoix
    {
        public OptionChoix(string valeur, string libelle) { Valeur = valeur; Libelle = libelle; }
        public string Valeur { get; private set; }
        public string Libelle { get; private set; }
        public override string ToString() { return Libelle; }
    }

    /// <summary>Une ligne de la table de correspondance des colonnes (éditable).</summary>
    public sealed class ChampVM : ObservableObject
    {
        private readonly DonneesViewModel _parent;
        public ChampVM(DonneesViewModel parent, string champ, string libelle)
        {
            _parent = parent;
            Champ = champ;
            Libelle = libelle;
        }

        public string Champ { get; private set; }
        public string Libelle { get; private set; }

        public string Candidats
        {
            get { return _parent.Profil == null ? "" : ChampsImport.Lire(_parent.Profil.Colonnes, Champ); }
            set
            {
                if (_parent.Profil == null) return;
                ChampsImport.Ecrire(_parent.Profil.Colonnes, Champ, value ?? "");
                Notifier();
                _parent.ProfilModifie();
            }
        }

        private string _trouvee;
        private bool _obligatoire, _estTrouvee;
        public string ColonneTrouvee { get { return _trouvee; } set { Set(ref _trouvee, value); } }
        public bool EstTrouvee { get { return _estTrouvee; } set { Set(ref _estTrouvee, value); } }
        public bool Obligatoire { get { return _obligatoire; } set { Set(ref _obligatoire, value); } }
        public bool Manquante { get { return Obligatoire && !EstTrouvee; } }
        public void Rafraichir() { NotifierTout(); }
    }

    public sealed class DonneesViewModel : PageViewModel
    {
        public DonneesViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            Profils = new ObservableCollection<ProfilItem>();
            Champs = new ObservableCollection<ChampVM>(ChampsImport.Definitions.Select(d => new ChampVM(this, d[0], d[1])));
            Feuilles = new ObservableCollection<string>();
            Journaux = new ObservableCollection<Journal>();

            ModesRegroupementDisponibles = ModesRegroupement.Tous.Select(m => new OptionChoix(m, ModesRegroupement.Libelle(m))).ToList();
            TypesSource = new List<OptionChoix> { new OptionChoix("Auto", "Automatique (extension)"), new OptionChoix("Excel", "Excel (.xlsx)"), new OptionChoix("Csv", "CSV / texte") };
            Separateurs = new List<OptionChoix> { new OptionChoix("auto", "Automatique"), new OptionChoix(";", "Point-virgule ;"), new OptionChoix(",", "Virgule ,"), new OptionChoix("tab", "Tabulation"), new OptionChoix("|", "Barre verticale |") };
            Encodages = new List<OptionChoix> { new OptionChoix("windows-1252", "ANSI (Windows-1252)"), new OptionChoix("utf-8", "UTF-8") };
            SeparateursDecimaux = new List<OptionChoix> { new OptionChoix("auto", "Automatique"), new OptionChoix(",", "Virgule"), new OptionChoix(".", "Point") };

            ParcourirCommand = new Commande(Parcourir);
            RechargerCommand = new CommandeAsync(() => ChargerFichierAsync(CheminFichier), () => !string.IsNullOrEmpty(CheminFichier));
            NouveauProfilCommand = new Commande(NouveauProfil);
            OuvrirProfilCommand = new Commande(OuvrirProfil);
            EnregistrerProfilCommand = new Commande(() => EnregistrerProfil(false), () => Profil != null);
            EnregistrerProfilSousCommand = new Commande(() => EnregistrerProfil(true), () => Profil != null);
            ModeleExcelCommand = new Commande(() => GenererModele(".xlsx"), () => Profil != null);
            ModeleCsvCommand = new Commande(() => GenererModele(".csv"), () => Profil != null);
            AnalyserCommand = new CommandeAsync(AnalyserAsync, () => Session.Source != null && Profil != null && !ColonnesManquantes);
            OuvrirDossierProfilsCommand = new Commande(() => Dialogues.OuvrirDansExplorateur(Chemins.Profils));
        }

        public override string Titre { get { return "Données à importer"; } }
        public override string SousTitre { get { return "Fichier de paramètres (correspondance des colonnes) et fichier de données Excel ou CSV."; } }

        // ---------------- Listes de choix ----------------
        public ObservableCollection<ProfilItem> Profils { get; private set; }
        public ObservableCollection<ChampVM> Champs { get; private set; }
        public ObservableCollection<string> Feuilles { get; private set; }
        public ObservableCollection<Journal> Journaux { get; private set; }
        public List<OptionChoix> ModesRegroupementDisponibles { get; private set; }
        public List<OptionChoix> TypesSource { get; private set; }
        public List<OptionChoix> Separateurs { get; private set; }
        public List<OptionChoix> Encodages { get; private set; }
        public List<OptionChoix> SeparateursDecimaux { get; private set; }

        // ---------------- Commandes ----------------
        public Commande ParcourirCommand { get; private set; }
        public CommandeAsync RechargerCommand { get; private set; }
        public Commande NouveauProfilCommand { get; private set; }
        public Commande OuvrirProfilCommand { get; private set; }
        public Commande EnregistrerProfilCommand { get; private set; }
        public Commande EnregistrerProfilSousCommand { get; private set; }
        public Commande ModeleExcelCommand { get; private set; }
        public Commande ModeleCsvCommand { get; private set; }
        public CommandeAsync AnalyserCommand { get; private set; }
        public Commande OuvrirDossierProfilsCommand { get; private set; }

        // ---------------- Profil ----------------
        private ProfilItem _profilItem;
        private bool _profilModifie, _chargementProfil;

        public ProfilImport Profil { get { return Session.Profil; } }
        public string CheminProfil { get { return Session.CheminProfil; } }
        public bool ProfilEstModifie { get { return _profilModifie; } private set { Set(ref _profilModifie, value); } }

        public ProfilItem ProfilSelectionne
        {
            get { return _profilItem; }
            set
            {
                if (_profilItem == value) return;
                if (value != null && ProfilEstModifie && _profilItem != null &&
                    !Dialogues.Confirmer("Changer de fichier de paramètres", "Les modifications du profil « " + Profil.Nom + " » ne sont pas enregistrées. Les abandonner ?", "Abandonner", true))
                {
                    Notifier();
                    return;
                }
                _profilItem = value;
                Notifier();
                if (value != null)
                {
                    // Recharge depuis le disque : abandonne les modifications non enregistrées.
                    ProfilImport p;
                    try { p = MagasinProfils.Charger(value.Chemin); } catch (Exception) { p = value.Profil; }
                    AppliquerProfil(p, value.Chemin);
                }
            }
        }

        public void AppliquerProfil(ProfilImport p, string chemin)
        {
            _chargementProfil = true;
            Session.Profil = p.Normaliser();
            Session.CheminProfil = chemin;
            Session.Parametres.DernierProfil = chemin;
            Session.EnregistrerParametres();
            NotifierTout();
            foreach (var c in Champs) c.Rafraichir();
            _chargementProfil = false;
            ProfilEstModifie = false;
            ActualiserCorrespondances();
        }

        /// <summary>Appelé à chaque modification d'un paramètre : met à jour l'aperçu de correspondance.</summary>
        public void ProfilModifie()
        {
            if (_chargementProfil) return;
            ProfilEstModifie = true;
            Session.InvaliderAnalyse();
            ActualiserCorrespondances();
        }

        // Propriétés éditables du profil (relayées pour notifier l'interface).
        public string NomProfil { get { return Profil == null ? "" : Profil.Nom; } set { if (Profil == null) return; Profil.Nom = value; Notifier(); ProfilModifie(); } }
        public string DescriptionProfil { get { return Profil == null ? "" : Profil.Description; } set { if (Profil == null) return; Profil.Description = value; Notifier(); ProfilModifie(); } }
        public string TypeSource { get { return Profil == null ? "" : Profil.Source.Type; } set { if (Profil == null) return; Profil.Source.Type = value; Notifier(); ProfilModifie(); } }
        public int LigneEntete
        {
            get { return Profil == null ? 1 : Profil.Source.LigneEntete; }
            set { if (Profil == null) return; Profil.Source.LigneEntete = Math.Max(1, value); Notifier(); ProfilModifie(); RelireSiPossible(); }
        }
        public string Separateur { get { return Profil == null ? "" : Profil.Source.Separateur; } set { if (Profil == null) return; Profil.Source.Separateur = value; Notifier(); ProfilModifie(); RelireSiPossible(); } }
        public string Encodage { get { return Profil == null ? "" : Profil.Source.Encodage; } set { if (Profil == null) return; Profil.Source.Encodage = value; Notifier(); ProfilModifie(); RelireSiPossible(); } }
        public string FormatsDate { get { return Profil == null ? "" : Profil.Source.FormatsDate; } set { if (Profil == null) return; Profil.Source.FormatsDate = value; Notifier(); ProfilModifie(); } }
        public string SeparateurDecimal { get { return Profil == null ? "" : Profil.Source.SeparateurDecimal; } set { if (Profil == null) return; Profil.Source.SeparateurDecimal = value; Notifier(); ProfilModifie(); } }
        public string Regroupement { get { return Profil == null ? "" : Profil.Regroupement; } set { if (Profil == null) return; Profil.Regroupement = value; Notifier(); ProfilModifie(); } }
        public string JournalDefaut { get { return Profil == null ? "" : Profil.Defauts.Journal; } set { if (Profil == null) return; Profil.Defauts.Journal = value ?? ""; Notifier(); ProfilModifie(); } }
        public string LibelleDefaut { get { return Profil == null ? "" : Profil.Defauts.Libelle; } set { if (Profil == null) return; Profil.Defauts.Libelle = value ?? ""; Notifier(); ProfilModifie(); } }
        public int LongueurComptes { get { return Profil == null ? 0 : Profil.Defauts.LongueurComptes; } set { if (Profil == null) return; Profil.Defauts.LongueurComptes = Math.Max(0, Math.Min(13, value)); Notifier(); ProfilModifie(); } }
        public bool NumerotationAutomatique { get { return Profil != null && Profil.Options.NumerotationAutomatique; } set { if (Profil == null) return; Profil.Options.NumerotationAutomatique = value; Notifier(); ProfilModifie(); } }
        public bool InverserMontantsNegatifs { get { return Profil != null && Profil.Options.InverserMontantsNegatifs; } set { if (Profil == null) return; Profil.Options.InverserMontantsNegatifs = value; Notifier(); ProfilModifie(); } }
        public bool IgnorerLignesSansMontant { get { return Profil != null && Profil.Options.IgnorerLignesSansMontant; } set { if (Profil == null) return; Profil.Options.IgnorerLignesSansMontant = value; Notifier(); ProfilModifie(); } }

        // ---------------- Fichier de données ----------------
        private string _cheminFichier, _feuille, _infoFichier;
        private DataView _apercu;
        private int _nombreLignes;
        private bool _changementFeuille;

        public string CheminFichier { get { return _cheminFichier; } private set { Set(ref _cheminFichier, value); NotifierPlusieurs("NomFichier", "AFichier"); } }
        public string NomFichier { get { return string.IsNullOrEmpty(_cheminFichier) ? "" : Path.GetFileName(_cheminFichier); } }
        public bool AFichier { get { return !string.IsNullOrEmpty(_cheminFichier); } }
        public string InfoFichier { get { return _infoFichier; } private set { Set(ref _infoFichier, value); } }
        public DataView Apercu { get { return _apercu; } private set { Set(ref _apercu, value); } }
        public int NombreLignes { get { return _nombreLignes; } private set { Set(ref _nombreLignes, value); } }
        public bool EstExcel { get { return Feuilles.Count > 0; } }

        public string FeuilleSelectionnee
        {
            get { return _feuille; }
            set
            {
                if (Set(ref _feuille, value) && !_changementFeuille && value != null && AFichier)
                    Lancer(() => ChargerFichierAsync(CheminFichier, value));
            }
        }

        public bool ColonnesManquantes { get { return Champs.Any(c => c.Manquante); } }
        public string ResumeCorrespondance
        {
            get
            {
                if (Session.Source == null) return "Charge un fichier pour vérifier la correspondance des colonnes.";
                var manquantes = Champs.Where(c => c.Manquante).Select(c => c.Libelle).ToList();
                var trouvees = Champs.Count(c => c.EstTrouvee);
                return manquantes.Count == 0
                    ? trouvees + " colonne(s) reconnue(s) — prêt pour l'analyse."
                    : "Colonne(s) obligatoire(s) introuvable(s) : " + string.Join(", ", manquantes) + ".";
            }
        }

        public override Task ActiverAsync()
        {
            if (Profils.Count == 0) ChargerProfils();
            Journaux.Clear();
            if (Session.Referentiel != null)
                foreach (var j in Session.Referentiel.Journaux.Values.OrderBy(j => j.Code)) Journaux.Add(j);
            return Task.FromResult(0);
        }

        private void ChargerProfils()
        {
            Profils.Clear();
            foreach (var kv in MagasinProfils.Lister()) Profils.Add(new ProfilItem { Chemin = kv.Key, Profil = kv.Value });

            var dernier = Session.Parametres.DernierProfil;
            var item = Profils.FirstOrDefault(p => string.Equals(p.Chemin, dernier, StringComparison.OrdinalIgnoreCase));
            if (item == null && !string.IsNullOrEmpty(dernier) && File.Exists(dernier))
            {
                try
                {
                    item = new ProfilItem { Chemin = dernier, Profil = MagasinProfils.Charger(dernier) };
                    Profils.Add(item);
                }
                catch (Exception) { item = null; }
            }
            ProfilSelectionne = item ?? Profils.FirstOrDefault();
        }

        private void NouveauProfil()
        {
            var p = ProfilImport.BrouillardStandard();
            p.Nom = "Nouveau profil " + DateTime.Now.ToString("dd-MM HHmm");
            var item = new ProfilItem { Chemin = MagasinProfils.CheminPourNom(p.Nom), Profil = p };
            MagasinProfils.Enregistrer(item.Chemin, p);
            Profils.Add(item);
            ProfilSelectionne = item;
            Navigation.Notifier("Profil créé à partir du modèle « Brouillard standard ».", TypeNotification.Succes);
        }

        private void OuvrirProfil()
        {
            var chemin = Dialogues.ChoisirFichier("Ouvrir un fichier de paramètres", MagasinProfils.FiltreDialogue, Chemins.Profils);
            if (chemin == null) return;
            var p = MagasinProfils.Charger(chemin);
            var item = Profils.FirstOrDefault(x => string.Equals(x.Chemin, chemin, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new ProfilItem { Chemin = chemin, Profil = p };
                Profils.Add(item);
            }
            ProfilSelectionne = item;
        }

        private void EnregistrerProfil(bool sous)
        {
            var chemin = CheminProfil;
            if (sous || string.IsNullOrEmpty(chemin))
            {
                chemin = Dialogues.ChoisirEmplacement("Enregistrer le fichier de paramètres", MagasinProfils.FiltreDialogue,
                    Path.GetFileName(MagasinProfils.CheminPourNom(Profil.Nom)), Chemins.Profils);
                if (chemin == null) return;
            }
            MagasinProfils.Enregistrer(chemin, Profil);
            Session.CheminProfil = chemin;
            var item = Profils.FirstOrDefault(x => string.Equals(x.Chemin, chemin, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                item = new ProfilItem { Chemin = chemin, Profil = Profil };
                Profils.Add(item);
            }
            else item.Profil = Profil;
            _profilItem = item;
            Notifier("ProfilSelectionne");
            ProfilEstModifie = false;
            Navigation.Notifier("Paramètres enregistrés : " + Path.GetFileName(chemin), TypeNotification.Succes);
        }

        private void GenererModele(string extension)
        {
            var chemin = Dialogues.ChoisirEmplacement("Créer un modèle de fichier de données",
                extension == ".csv" ? "CSV (*.csv)|*.csv" : "Classeur Excel (*.xlsx)|*.xlsx",
                "Modele_ecritures_" + (Profil.Nom ?? "import").Replace(' ', '_') + extension);
            if (chemin == null) return;
            ExportRapport.GenererModele(chemin, Profil);
            Navigation.Notifier("Modèle créé : " + Path.GetFileName(chemin), TypeNotification.Succes);
            Dialogues.OuvrirDansExplorateur(chemin);
        }

        private void Parcourir()
        {
            var dossier = Session.Parametres.DernierDossierDonnees;
            var chemin = Dialogues.ChoisirFichier("Choisir le fichier de données", LecteurSource.FiltreDialogue, dossier);
            if (chemin != null) Lancer(() => ChargerFichierAsync(chemin));
        }

        /// <summary>Appelé par l'écran (bouton Parcourir ou glisser-déposer).</summary>
        public Task DeposerFichierAsync(string chemin)
        {
            return ChargerFichierAsync(chemin);
        }

        private void RelireSiPossible()
        {
            if (AFichier && !_chargementProfil) Lancer(() => ChargerFichierAsync(CheminFichier, FeuilleSelectionnee));
        }

        private async Task ChargerFichierAsync(string chemin, string feuille = null)
        {
            if (string.IsNullOrEmpty(chemin) || Profil == null) return;
            await OccuperAsync("Lecture de " + Path.GetFileName(chemin) + "…", async () =>
            {
                var source = Profil.Source;
                var table = await Task.Run(() => LecteurSource.Lire(chemin, source, feuille));
                Session.Source = table;
                Session.InvaliderAnalyse();
                CheminFichier = chemin;
                Session.Parametres.DernierDossierDonnees = Path.GetDirectoryName(chemin);
                Session.EnregistrerParametres();

                _changementFeuille = true;
                Feuilles.Clear();
                foreach (var f in table.FeuillesDisponibles) Feuilles.Add(f);
                FeuilleSelectionnee = table.Feuille;
                _changementFeuille = false;
                Notifier("EstExcel");

                NombreLignes = table.Lignes.Count;
                var info = new FileInfo(chemin);
                InfoFichier = (table.Feuille != null ? "Feuille « " + table.Feuille + " » · " : "") + table.Entetes.Count + " colonnes · " +
                              table.Lignes.Count + " lignes · " + (info.Length < 1024 * 1024 ? (info.Length / 1024.0).ToString("0.#") + " Ko" : (info.Length / 1048576.0).ToString("0.#") + " Mo") +
                              " · modifié le " + info.LastWriteTime.ToString("dd/MM/yyyy HH:mm");
                Apercu = ConstruireApercu(table, 500);
                ActualiserCorrespondances();
            });
        }

        private static DataView ConstruireApercu(TableSource t, int max)
        {
            var dt = new DataTable();
            dt.Columns.Add("ln", typeof(int)).Caption = "Ligne";
            for (int i = 0; i < t.Entetes.Count; i++)
                dt.Columns.Add("c" + i, typeof(string)).Caption = Colonnes.Lettre(i) + " · " + (string.IsNullOrEmpty(t.Entetes[i]) ? "(vide)" : t.Entetes[i]);
            for (int r = 0; r < Math.Min(max, t.Lignes.Count); r++)
            {
                var valeurs = new object[t.Entetes.Count + 1];
                valeurs[0] = t.NumerosLigne[r];
                for (int i = 0; i < t.Entetes.Count; i++) valeurs[i + 1] = t.Valeur(r, i);
                dt.Rows.Add(valeurs);
            }
            return dt.DefaultView;
        }

        private void ActualiserCorrespondances()
        {
            if (Profil == null) return;
            var entetes = Session.Source == null ? new List<string>() : Session.Source.Entetes;
            var res = ConstructeurPieces.ResoudreColonnes(Profil, entetes);
            foreach (var c in Champs)
            {
                var r = res.First(x => x.Champ == c.Champ);
                c.EstTrouvee = r.Trouvee;
                c.ColonneTrouvee = Session.Source == null ? "" : r.Affichage;
                c.Obligatoire = r.Obligatoire;
                c.Rafraichir();
            }
            NotifierPlusieurs("ColonnesManquantes", "ResumeCorrespondance");
            CommandeBase.ReevaluerTout();
        }

        private async Task AnalyserAsync()
        {
            ResultatAnalyse analyse = null;
            var ok = await OccuperAsync("Construction des pièces…", async () =>
            {
                var profil = Profil;
                var source = Session.Source;
                analyse = await Task.Run(() => ConstructeurPieces.Construire(source, profil));
                if (analyse.Bloquee) return;
                MessageOccupe = "Contrôle des comptes, tiers et journaux…";
                var referentiel = await Session.ObtenirReferentielAsync();
                await Task.Run(() => Validateur.Valider(analyse.Pieces, referentiel, profil));
            });
            if (!ok || analyse == null) return;
            if (analyse.Bloquee)
            {
                Dialogues.Informer("Analyse impossible", string.Join(Environment.NewLine, analyse.AnomaliesGlobales.Select(a => a.Message)), true);
                return;
            }
            if (analyse.Pieces.Count == 0)
            {
                Navigation.Notifier("Aucune ligne avec montant trouvée dans le fichier.", TypeNotification.Avertissement);
                return;
            }
            Session.Analyse = analyse;
            Navigation.AllerA(5);
        }
    }
}
