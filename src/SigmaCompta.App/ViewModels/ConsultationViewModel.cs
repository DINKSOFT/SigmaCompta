using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Comptabilite;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>
    /// Comptabilité › Consultation : écritures d'un compte général ou tiers sur une période,
    /// avec report à nouveau, solde progressif et filtre de lettrage. Lecture seule.
    /// </summary>
    public sealed class ConsultationViewModel : PageViewModel
    {
        private const string FormatsDate = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|d/M/yy|ddMMyyyy|ddMMyy|yyyy-MM-dd";

        private Referentiel _referentiel;
        private List<ElementCompte> _tousLesComptes = new List<ElementCompte>();
        private bool _aRelancer;

        public ConsultationViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            ConsulterCommand = new CommandeAsync(ConsulterAsync, () => CompteSelectionne != null && PeriodeValide);
            ExporterCommand = new Commande(Exporter, () => Resultat != null);
            PdfCommand = new Commande(() => ImpressionPdf.Enregistrer(Dialogues, Navigation, Pdf, NomPdf()), () => Resultat != null);
            ImprimerCommand = new Commande(() => ImpressionPdf.Imprimer(Dialogues, Navigation, Pdf, NomPdf()), () => Resultat != null);
            CompteSuivantCommand = new Commande(() => Deplacer(1), () => ComptesFiltres.Count > 0);
            ComptePrecedentCommand = new Commande(() => Deplacer(-1), () => ComptesFiltres.Count > 0);
            BasculerListeCommand = new Commande(() => ListeVisible = !ListeVisible);
        }

        public override string Titre { get { return "Consultation des comptes"; } }
        public override string SousTitre { get { return "Écritures d'un compte général ou tiers : report à nouveau, solde progressif et lettrage. Lecture seule."; } }

        public CommandeAsync ConsulterCommand { get; private set; }
        public Commande ExporterCommand { get; private set; }
        public Commande PdfCommand { get; private set; }
        public Commande ImprimerCommand { get; private set; }

        /// <summary>Extrait de compte imprimable (report, écritures, solde progressif, total).</summary>
        private byte[] Pdf()
        {
            return ImpressionEtats.Generer(ImpressionEtats.ExtraitDeCompte(_resultat), ImpressionPdf.Societe(Session), ImpressionPdf.Dossier(Session));
        }

        private string NomPdf()
        {
            var r = _resultat;
            return "Extrait_" + r.Compte + "_" + r.Du.ToString("yyyyMMdd") + "_" + r.Au.ToString("yyyyMMdd") + ".pdf";
        }
        public Commande CompteSuivantCommand { get; private set; }
        public Commande ComptePrecedentCommand { get; private set; }
        public Commande BasculerListeCommand { get; private set; }

        private bool _listeVisible = true;
        public bool ListeVisible { get { return _listeVisible; } set { Set(ref _listeVisible, value); } }

        // ---------------------------------------------------------------- Chargement

        public override async Task ActiverAsync()
        {
            if (!Session.BaseChoisie) return;
            if (_referentiel != null && ReferenceEquals(_referentiel, Session.Referentiel)) return;

            await OccuperAsync("Chargement du plan comptable et des tiers…", async () =>
            {
                _referentiel = await Session.ObtenirReferentielAsync();
                Resultat = null;
                _compte = null;
                Notifier("CompteSelectionne");
                Notifier("ACompte");

                Exercices = _referentiel.Exercices.OrderByDescending(e => e.Debut).ToList();
                var aujourdhui = DateTime.Today;
                _exercice = Exercices.FirstOrDefault(e => e.Contient(aujourdhui)) ?? Exercices.FirstOrDefault();
                if (_exercice != null)
                {
                    _duTexte = _exercice.Debut.ToString("dd/MM/yyyy");
                    _auTexte = _exercice.Fin.ToString("dd/MM/yyyy");
                }
                else
                {
                    _duTexte = new DateTime(aujourdhui.Year, 1, 1).ToString("dd/MM/yyyy");
                    _auTexte = new DateTime(aujourdhui.Year, 12, 31).ToString("dd/MM/yyyy");
                }
                NotifierPlusieurs("Exercices", "ExerciceSelectionne", "DuTexte", "AuTexte", "PeriodeValide");
                ChargerListe();
            });
        }

        // ---------------------------------------------------------------- Comptes

        private NatureCompte _nature = NatureCompte.General;

        public bool NatureGenerale
        {
            get { return _nature == NatureCompte.General; }
            set { if (value) ChangerNature(NatureCompte.General); }
        }

        public bool NatureTiers
        {
            get { return _nature == NatureCompte.Tiers; }
            set { if (value) ChangerNature(NatureCompte.Tiers); }
        }

        private void ChangerNature(NatureCompte nature)
        {
            if (_nature == nature) return;
            _nature = nature;
            NotifierPlusieurs("NatureGenerale", "NatureTiers");
            CompteSelectionne = null;
            ChargerListe();
        }

        private void ChargerListe()
        {
            _tousLesComptes = ServiceConsultation.ComptesConsultables(_referentiel, _nature);
            Filtrer();
        }

        private string _recherche = "";
        public string Recherche { get { return _recherche; } set { if (Set(ref _recherche, value)) Filtrer(); } }

        private IList<ElementCompte> _comptesFiltres = new List<ElementCompte>();
        public IList<ElementCompte> ComptesFiltres { get { return _comptesFiltres; } private set { Set(ref _comptesFiltres, value); } }
        public int NombreComptes { get { return _comptesFiltres.Count; } }

        private void Filtrer()
        {
            ComptesFiltres = _tousLesComptes.Where(c => c.Correspond(_recherche)).ToList();
            Notifier("NombreComptes");
            CommandeBase.ReevaluerTout();
        }

        private ElementCompte _compte;
        public ElementCompte CompteSelectionne
        {
            get { return _compte; }
            set
            {
                if (!Set(ref _compte, value)) return;
                Notifier("ACompte");
                if (value == null) Resultat = null;
                else Lancer(ConsulterAsync);
            }
        }

        public bool ACompte { get { return _compte != null; } }

        private void Deplacer(int sens)
        {
            var liste = ComptesFiltres;
            if (liste.Count == 0) return;
            int i = _compte == null ? -1 : liste.IndexOf(_compte);
            i = i < 0 ? (sens > 0 ? 0 : liste.Count - 1) : Math.Max(0, Math.Min(liste.Count - 1, i + sens));
            CompteSelectionne = liste[i];
        }

        // ---------------------------------------------------------------- Période et filtre

        private IList<Exercice> _exercices = new List<Exercice>();
        public IList<Exercice> Exercices { get { return _exercices; } private set { Set(ref _exercices, value); } }

        private Exercice _exercice;
        public Exercice ExerciceSelectionne
        {
            get { return _exercice; }
            set
            {
                if (!Set(ref _exercice, value) || value == null) return;
                _duTexte = value.Debut.ToString("dd/MM/yyyy");
                _auTexte = value.Fin.ToString("dd/MM/yyyy");
                NotifierPlusieurs("DuTexte", "AuTexte", "PeriodeValide");
                Relancer();
            }
        }

        private string _duTexte = "";
        public string DuTexte { get { return _duTexte; } set { if (Set(ref _duTexte, value)) { Notifier("PeriodeValide"); Relancer(); } } }

        private string _auTexte = "";
        public string AuTexte { get { return _auTexte; } set { if (Set(ref _auTexte, value)) { Notifier("PeriodeValide"); Relancer(); } } }

        public bool PeriodeValide
        {
            get
            {
                DateTime du, au;
                return Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) && Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) && au >= du;
            }
        }

        private string _filtreLettrage = "Toutes";
        /// <summary>« Toutes », « NonLettrees » ou « Lettrees » (puces de filtre).</summary>
        public string FiltreLettrage { get { return _filtreLettrage; } set { if (Set(ref _filtreLettrage, value)) Relancer(); } }

        private void Relancer()
        {
            CommandeBase.ReevaluerTout();
            if (_compte != null && PeriodeValide) Lancer(ConsulterAsync);
        }

        // ---------------------------------------------------------------- Consultation

        private FiltreConsultation FiltreCourant()
        {
            DateTime du, au;
            if (_compte == null || !Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) || !Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) || au < du)
                return null;
            Core.Modeles.FiltreLettrage lettrage;
            if (!Enum.TryParse(_filtreLettrage, out lettrage)) lettrage = Core.Modeles.FiltreLettrage.Toutes;
            return new FiltreConsultation { Nature = _compte.Nature, Compte = _compte.Numero, Du = du, Au = au, Lettrage = lettrage };
        }

        private async Task ConsulterAsync()
        {
            if (!Session.BaseChoisie || _referentiel == null) return;
            // Une consultation est déjà en cours : on relancera avec les derniers critères à la fin.
            if (EstOccupe) { _aRelancer = true; return; }
            do
            {
                _aRelancer = false;
                var filtre = FiltreCourant();
                if (filtre == null) return;
                await OccuperAsync("Lecture des écritures du compte " + filtre.Compte + "…", async () =>
                {
                    using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                        Resultat = await ServiceConsultation.ConsulterAsync(c, filtre, _referentiel);
                });
            }
            while (_aRelancer);
        }

        private ResultatConsultation _resultat;
        public ResultatConsultation Resultat
        {
            get { return _resultat; }
            private set
            {
                _resultat = value;
                Lignes = value == null ? new List<LigneConsultation>() : value.LignesAvecReport.ToList();
                NotifierTout();
                CommandeBase.ReevaluerTout();
            }
        }

        public IList<LigneConsultation> Lignes { get; private set; } = new List<LigneConsultation>();

        public bool AResultat { get { return _resultat != null; } }
        public bool AucuneLigne { get { return _resultat != null && _resultat.Lignes.Count == 0 && !_resultat.DebutReport.HasValue; } }

        public string CompteAffiche { get { return _compte == null ? "" : _compte.Numero; } }
        public string IntituleAffiche { get { return _compte == null ? "Aucun compte sélectionné" : _compte.Intitule; } }

        public string ReportTitre
        {
            get
            {
                if (_resultat == null) return "REPORT";
                if (_resultat.Lettrage != Core.Modeles.FiltreLettrage.Toutes) return "REPORT (NON CALCULÉ)";
                return "REPORT AU " + _resultat.Du.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
        }

        public string ReportLibelle
        {
            get { return _resultat == null || !_resultat.DebutReport.HasValue ? "—" : Montants.AvecSens(_resultat.ReportSolde); }
        }

        public decimal TotalDebit { get { return _resultat == null ? 0m : _resultat.MouvementsDebit; } }
        public decimal TotalCredit { get { return _resultat == null ? 0m : _resultat.MouvementsCredit; } }

        public string SoldeTitre
        {
            get
            {
                if (_resultat == null) return "SOLDE";
                if (_resultat.Lettrage != Core.Modeles.FiltreLettrage.Toutes) return "SOLDE DES LIGNES AFFICHÉES";
                return "SOLDE AU " + _resultat.Au.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            }
        }

        public string SoldeLibelle { get { return _resultat == null ? "—" : Montants.AvecSens(_resultat.SoldeFinal); } }

        public string Resume
        {
            get
            {
                if (_resultat == null) return "";
                var txt = _resultat.Lignes.Count.ToString("N0") + " écriture(s)";
                if (_resultat.LettrageDisponible && _resultat.Lettrage == Core.Modeles.FiltreLettrage.Toutes)
                    txt += " · " + _resultat.NombreNonLettrees.ToString("N0") + " non lettrée(s)";
                if (!_resultat.LettrageDisponible) txt += " · lettrage absent de cette base";
                return txt;
            }
        }

        // ---------------------------------------------------------------- Export

        private void Exporter()
        {
            var r = _resultat;
            if (r == null) return;
            var chemin = Dialogues.ChoisirEmplacement("Exporter la consultation", "Classeur Excel (*.xlsx)|*.xlsx",
                "Consultation_" + r.Compte + "_" + r.Du.ToString("yyyyMMdd") + "_" + r.Au.ToString("yyyyMMdd") + ".xlsx");
            if (chemin == null) return;
            try
            {
                ExportConsultation.Ecrire(chemin, r);
                Navigation.Notifier("Consultation exportée : " + Path.GetFileName(chemin), TypeNotification.Succes);
                Dialogues.OuvrirDansExplorateur(chemin);
            }
            catch (IOException ex)
            {
                Navigation.Notifier("Export impossible : " + ex.Message + " (le fichier est peut-être ouvert dans Excel).", TypeNotification.Erreur);
            }
        }
    }
}
