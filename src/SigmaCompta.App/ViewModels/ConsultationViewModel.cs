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
    /// <summary>Ligne de la grille d'interrogation : l'écriture et sa case de sélection pour le lettrage.</summary>
    public sealed class LigneInterrogation : ObservableObject
    {
        private readonly Action _surCoche;
        private bool _cochee;

        public LigneInterrogation(LigneConsultation ligne, Action surCoche)
        {
            Ligne = ligne;
            _surCoche = surCoche;
        }

        public LigneConsultation Ligne { get; private set; }

        /// <summary>La ligne de report n'est pas une écriture : elle ne se coche pas.</summary>
        public bool EstCochable { get { return !Ligne.EstReport; } }

        public bool Cochee
        {
            get { return _cochee; }
            set
            {
                if (!EstCochable) value = false;
                if (Set(ref _cochee, value) && _surCoche != null) _surCoche();
            }
        }

        /// <summary>Change la case sans prévenir l'écran (cocher / décocher tout, puis un seul recalcul).</summary>
        internal void Forcer(bool valeur)
        {
            if (!EstCochable) valeur = false;
            Set(ref _cochee, valeur, "Cochee");
        }
    }

    /// <summary>
    /// Comptabilité › Consultation, sur le modèle de l'« Interrogation et lettrage » des logiciels comptables :
    /// compte général ou tiers, type d'écriture (lettrées, pointées…), recherche dans la liste, cumuls mensuels,
    /// sélection des lignes à lettrer avec leur solde. Lecture seule : le lettrage en base viendra avec sa procédure.
    /// </summary>
    public sealed class ConsultationViewModel : PageViewModel
    {
        private const string FormatsDate = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|d/M/yy|ddMMyyyy|ddMMyy|yyyy-MM-dd";

        private Referentiel _referentiel;
        private bool _aRelancer;

        public ConsultationViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            AfficherCommand = new CommandeAsync(AfficherAsync, () => Session.BaseChoisie && !string.IsNullOrWhiteSpace(_compteTexte) && PeriodeValide);
            ExporterCommand = new Commande(Exporter, () => Resultat != null);
            PdfCommand = new Commande(() => ImpressionPdf.Enregistrer(Dialogues, Navigation, Pdf, NomPdf()), () => Resultat != null);
            ImprimerCommand = new Commande(() => ImpressionPdf.Imprimer(Dialogues, Navigation, Pdf, NomPdf()), () => Resultat != null);
            CompteSuivantCommand = new Commande(() => Deplacer(1), () => Comptes.Count > 0);
            ComptePrecedentCommand = new Commande(() => Deplacer(-1), () => Comptes.Count > 0);
            EffacerCriteresCommand = new Commande(EffacerCriteres);
            ToutCocherCommand = new Commande(() => Cocher(true), () => _lignesAffichees.Any(l => l.EstCochable));
            ToutDecocherCommand = new Commande(() => Cocher(false), () => _selection.Nombre > 0);
            // Le lettrage écrit dans la base : il attend sa procédure (lot 4 de la feuille de route).
            LettrerCommand = new Commande(() => { }, () => false);
        }

        public override string Titre
        {
            get
            {
                if (_resultat == null) return "Interrogation et lettrage";
                return "Interrogation du " + (_resultat.Nature == NatureCompte.General ? "compte général" : "compte tiers") + " : "
                       + _resultat.Compte + " " + (_resultat.Intitule ?? "");
            }
        }

        public override string SousTitre
        {
            get { return "Écritures d'un compte général ou tiers, cumuls mensuels et préparation du lettrage. Lecture seule."; }
        }

        public CommandeAsync AfficherCommand { get; private set; }
        public Commande ExporterCommand { get; private set; }
        public Commande PdfCommand { get; private set; }
        public Commande ImprimerCommand { get; private set; }
        public Commande CompteSuivantCommand { get; private set; }
        public Commande ComptePrecedentCommand { get; private set; }
        public Commande EffacerCriteresCommand { get; private set; }
        public Commande ToutCocherCommand { get; private set; }
        public Commande ToutDecocherCommand { get; private set; }
        public Commande LettrerCommand { get; private set; }

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

        // ---------------------------------------------------------------- Chargement

        public override async Task ActiverAsync()
        {
            if (!Session.BaseChoisie) return;
            if (_referentiel != null && ReferenceEquals(_referentiel, Session.Referentiel)) return;

            await OccuperAsync("Chargement du plan comptable et des tiers…", async () =>
            {
                _referentiel = await Session.ObtenirReferentielAsync();
                Resultat = null;
                _compteTexte = "";

                Exercices = _referentiel.Exercices.OrderByDescending(e => e.Debut).ToList();
                var aujourdhui = DateTime.Today;
                _exercice = Exercices.FirstOrDefault(e => e.Contient(aujourdhui)) ?? Exercices.FirstOrDefault();
                PeriodeExercice();
                NotifierPlusieurs("Exercices", "ExerciceSelectionne", "CompteTexte");
                ChargerComptes();
            });
        }

        private void PeriodeExercice()
        {
            var aujourdhui = DateTime.Today;
            _duTexte = (_exercice != null ? _exercice.Debut : new DateTime(aujourdhui.Year, 1, 1)).ToString("dd/MM/yyyy");
            _auTexte = (_exercice != null ? _exercice.Fin : new DateTime(aujourdhui.Year, 12, 31)).ToString("dd/MM/yyyy");
            NotifierPlusieurs("DuTexte", "AuTexte", "PeriodeValide", "Criteres");
        }

        // ---------------------------------------------------------------- Compte

        private NatureCompte _nature = NatureCompte.General;

        /// <summary>0 = compte général, 1 = compte tiers (liste « Compte général / Compte tiers »).</summary>
        public int NatureIndex
        {
            get { return _nature == NatureCompte.General ? 0 : 1; }
            set
            {
                var nature = value == 1 ? NatureCompte.Tiers : NatureCompte.General;
                if (_nature == nature) return;
                _nature = nature;
                Notifier();
                _compteTexte = "";
                Notifier("CompteTexte");
                Resultat = null;
                ChargerComptes();
            }
        }

        public IList<string> Natures { get; } = new List<string> { "Compte général", "Compte tiers" };

        private IList<ElementCompte> _comptes = new List<ElementCompte>();
        public IList<ElementCompte> Comptes { get { return _comptes; } private set { Set(ref _comptes, value); } }

        private void ChargerComptes()
        {
            Comptes = ServiceConsultation.ComptesConsultables(_referentiel, _nature);
            CommandeBase.ReevaluerTout();
        }

        private string _compteTexte = "";
        /// <summary>Ce qui est tapé ou choisi dans la liste : n°, début de n° ou partie de l'intitulé.</summary>
        public string CompteTexte
        {
            get { return _compteTexte; }
            set { if (Set(ref _compteTexte, value ?? "")) CommandeBase.ReevaluerTout(); }
        }

        /// <summary>Compte actuellement affiché.</summary>
        private ElementCompte _compte;

        private async Task AfficherAsync()
        {
            var compte = ServiceConsultation.Trouver(Comptes, _compteTexte);
            if (compte == null)
            {
                Navigation.Notifier("Aucun " + (_nature == NatureCompte.General ? "compte général" : "compte tiers") + " ne correspond à « "
                                    + _compteTexte.Trim() + " ».", TypeNotification.Erreur);
                return;
            }
            Choisir(compte);
            await ConsulterAsync();
        }

        private void Choisir(ElementCompte compte)
        {
            _compte = compte;
            _compteTexte = compte.Numero;
            Notifier("CompteTexte");
        }

        private void Deplacer(int sens)
        {
            var liste = Comptes;
            if (liste.Count == 0) return;
            int i = _compte == null ? -1 : liste.IndexOf(_compte);
            i = i < 0 ? (sens > 0 ? 0 : liste.Count - 1) : Math.Max(0, Math.Min(liste.Count - 1, i + sens));
            Choisir(liste[i]);
            Lancer(ConsulterAsync);
        }

        // ---------------------------------------------------------------- Vue, période, type d'écriture

        private string _vue = "Interrogation";
        /// <summary>« Interrogation » (écritures) ou « Cumuls » (cumuls généraux mois par mois).</summary>
        public string Vue
        {
            get { return _vue; }
            set { if (Set(ref _vue, value)) NotifierPlusieurs("VueInterrogation", "VueCumuls"); }
        }

        public bool VueInterrogation { get { return _vue != "Cumuls"; } }
        public bool VueCumuls { get { return _vue == "Cumuls"; } }

        private IList<Exercice> _exercices = new List<Exercice>();
        public IList<Exercice> Exercices { get { return _exercices; } private set { Set(ref _exercices, value); } }

        private Exercice _exercice;
        public Exercice ExerciceSelectionne
        {
            get { return _exercice; }
            set
            {
                if (!Set(ref _exercice, value) || value == null) return;
                PeriodeExercice();
                Relancer();
            }
        }

        private string _duTexte = "";
        public string DuTexte { get { return _duTexte; } set { if (Set(ref _duTexte, value)) { NotifierPlusieurs("PeriodeValide", "Criteres"); Relancer(); } } }

        private string _auTexte = "";
        public string AuTexte { get { return _auTexte; } set { if (Set(ref _auTexte, value)) { NotifierPlusieurs("PeriodeValide", "Criteres"); Relancer(); } } }

        public bool PeriodeValide
        {
            get
            {
                DateTime du, au;
                return Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) && Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) && au >= du;
            }
        }

        private string _typeEcriture = "Toutes";
        /// <summary>« Toutes », « NonLettrees », « Lettrees », « NonPointees » ou « Pointees » (panneau « Type d'écriture »).</summary>
        public string TypeEcriture
        {
            get { return _typeEcriture; }
            set { if (Set(ref _typeEcriture, value)) { Notifier("Criteres"); Relancer(); } }
        }

        private string _recherche = "";
        /// <summary>Mots recherchés dans la liste affichée (sans relire la base).</summary>
        public string Recherche
        {
            get { return _recherche; }
            set { if (Set(ref _recherche, value ?? "")) { Notifier("Criteres"); Filtrer(); } }
        }

        private void EffacerCriteres()
        {
            _typeEcriture = "Toutes";
            _recherche = "";
            var aujourdhui = DateTime.Today;
            var exo = Exercices.FirstOrDefault(e => e.Contient(aujourdhui)) ?? Exercices.FirstOrDefault();
            _exercice = exo;
            PeriodeExercice();
            NotifierPlusieurs("TypeEcriture", "Recherche", "ExerciceSelectionne");
            Relancer();
            Filtrer();
        }

        private void Relancer()
        {
            CommandeBase.ReevaluerTout();
            if (_compte != null && PeriodeValide) Lancer(ConsulterAsync);
        }

        /// <summary>Critères actifs, comme dans le cadre en bas à gauche de l'interrogation.</summary>
        public string Criteres
        {
            get
            {
                var lignes = new List<string>();
                DateTime du, au;
                if (Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) && Analyseurs.EssayerDate(_auTexte, FormatsDate, out au))
                    lignes.Add("Date d'écriture : du " + du.ToString("dd/MM/yyyy") + " au " + au.ToString("dd/MM/yyyy"));
                else
                    lignes.Add("Date d'écriture : période à corriger");
                FiltreLettrage f;
                if (Enum.TryParse(_typeEcriture, out f) && f != FiltreLettrage.Toutes)
                    lignes.Add("Type d'écriture : " + ServiceConsultation.LibelleFiltre(f));
                if (!string.IsNullOrWhiteSpace(_recherche))
                    lignes.Add("Recherche : « " + _recherche.Trim() + " »");
                return string.Join(Environment.NewLine, lignes);
            }
        }

        // ---------------------------------------------------------------- Consultation

        private FiltreConsultation FiltreCourant()
        {
            DateTime du, au;
            if (_compte == null || !Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) || !Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) || au < du)
                return null;
            FiltreLettrage filtre;
            if (!Enum.TryParse(_typeEcriture, out filtre)) filtre = FiltreLettrage.Toutes;
            return new FiltreConsultation { Nature = _compte.Nature, Compte = _compte.Numero, Du = du, Au = au, Lettrage = filtre };
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
                _toutesLesLignes = value == null
                    ? new List<LigneInterrogation>()
                    : value.LignesAvecReport.Select(l => new LigneInterrogation(l, SelectionChangee)).ToList();
                Cumuls = value == null ? new List<CumulMensuel>() : value.Cumuls.ToList();
                Filtrer();
                NotifierTout();
                CommandeBase.ReevaluerTout();
            }
        }

        private List<LigneInterrogation> _toutesLesLignes = new List<LigneInterrogation>();

        private IList<LigneInterrogation> _lignesAffichees = new List<LigneInterrogation>();
        /// <summary>Lignes de la grille après la recherche par mots (la ligne de report disparaît pendant une recherche).</summary>
        public IList<LigneInterrogation> Lignes { get { return _lignesAffichees; } }

        public IList<CumulMensuel> Cumuls { get; private set; } = new List<CumulMensuel>();

        private void Filtrer()
        {
            _lignesAffichees = _toutesLesLignes.Where(l => ServiceConsultation.Correspond(l.Ligne, _recherche)).ToList();
            // Une ligne masquée par la recherche ne reste pas cochée à l'insu de l'utilisateur.
            foreach (var l in _toutesLesLignes.Except(_lignesAffichees)) l.Forcer(false);
            NotifierPlusieurs("Lignes", "NombreLignes", "AucuneLigne", "TotalDebit", "TotalCredit");
            SelectionChangee();
        }

        public int NombreLignes { get { return _lignesAffichees.Count(l => !l.Ligne.EstReport); } }

        public bool AResultat { get { return _resultat != null; } }
        public bool SansResultat { get { return _resultat == null; } }
        public bool AucuneLigne { get { return _resultat != null && _lignesAffichees.Count == 0; } }

        /// <summary>Totaux des lignes affichées (report compris).</summary>
        public decimal TotalDebit { get { return _lignesAffichees.Sum(l => l.Ligne.Debit); } }
        public decimal TotalCredit { get { return _lignesAffichees.Sum(l => l.Ligne.Credit); } }

        /// <summary>Solde réel du compte à la fin de la période, quels que soient le type d'écriture et la recherche.</summary>
        public decimal? SoldeCompteDebit { get { return _resultat != null && _resultat.SoldeCompte > 0 ? _resultat.SoldeCompte : (decimal?)null; } }
        public decimal? SoldeCompteCredit { get { return _resultat != null && _resultat.SoldeCompte < 0 ? -_resultat.SoldeCompte : (decimal?)null; } }

        public string SoldeCompteTitre
        {
            get { return _resultat == null ? "Solde du compte" : "Solde du compte au " + _resultat.Au.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture); }
        }

        public decimal CumulsDebit { get { return _resultat == null ? 0m : _resultat.CumulsDebit; } }
        public decimal CumulsCredit { get { return _resultat == null ? 0m : _resultat.CumulsCredit; } }

        public string Resume
        {
            get
            {
                if (_resultat == null) return "Choisissez un compte puis « Afficher ».";
                var txt = NombreLignes.ToString("N0") + " écriture(s) affichée(s)";
                if (_resultat.LettrageDisponible && _resultat.Lettrage == FiltreLettrage.Toutes)
                    txt += " · " + _resultat.NombreNonLettrees.ToString("N0") + " non lettrée(s) sur la période";
                if (!_resultat.LettrageDisponible) txt += " · lettrage absent de cette base";
                if (!_resultat.PointageDisponible) txt += " · pointage absent de cette base";
                return txt;
            }
        }

        // ---------------------------------------------------------------- Sélection pour le lettrage

        private SelectionLettrage _selection = new SelectionLettrage();

        private void SelectionChangee()
        {
            _selection = ServiceConsultation.TotaliserSelection(_toutesLesLignes.Where(l => l.Cochee).Select(l => l.Ligne));
            NotifierPlusieurs("SelectionNombre", "SelectionDebit", "SelectionCredit", "SelectionSolde", "SelectionEquilibree", "ASelection");
            CommandeBase.ReevaluerTout();
        }

        private void Cocher(bool valeur)
        {
            foreach (var l in _lignesAffichees) l.Forcer(valeur);
            SelectionChangee();
        }

        public bool ASelection { get { return _selection.Nombre > 0; } }
        public int SelectionNombre { get { return _selection.Nombre; } }
        public decimal SelectionDebit { get { return _selection.Debit; } }
        public decimal SelectionCredit { get { return _selection.Credit; } }
        public string SelectionSolde { get { return Montants.AvecSens(_selection.Solde); } }
        public bool SelectionEquilibree { get { return _selection.EstEquilibree; } }

        /// <summary>Lettre que prendrait le prochain lettrage de ce compte.</summary>
        public string ProchaineLettre { get { return _resultat == null ? "" : _resultat.ProchaineLettre; } }

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
