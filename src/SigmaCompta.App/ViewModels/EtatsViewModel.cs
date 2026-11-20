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
    /// <summary>Élément d'une liste de choix (journal, type de tiers…) : valeur technique et libellé affiché.</summary>
    public sealed class Choix
    {
        public Choix(string valeur, string libelle)
        {
            Valeur = valeur ?? "";
            Libelle = libelle ?? "";
        }

        public string Valeur { get; private set; }
        public string Libelle { get; private set; }
        public override string ToString() { return Libelle; }
    }

    /// <summary>
    /// Comptabilité › États comptables : balance générale ou des tiers, grand livre, journaux et journal
    /// centralisateur sur une période, avec export Excel. Lecture seule.
    /// </summary>
    public sealed class EtatsViewModel : PageViewModel
    {
        private const string FormatsDate = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|d/M/yy|ddMMyyyy|ddMMyy|yyyy-MM-dd";

        private Referentiel _referentiel;

        public EtatsViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            GenererCommand = new CommandeAsync(GenererAsync, () => _referentiel != null && PeriodeValide);
            ExporterCommand = new Commande(Exporter, () => _resultat != null);
            PdfCommand = new Commande(() => ImpressionPdf.Enregistrer(Dialogues, Navigation, Pdf, NomPdf()), () => _resultat != null);
            ImprimerCommand = new Commande(() => ImpressionPdf.Imprimer(Dialogues, Navigation, Pdf, NomPdf()), () => _resultat != null);
        }

        public override string Titre { get { return "États comptables"; } }
        public override string SousTitre { get { return "Balance, grand livre, journaux et centralisateur sur une période, avec export Excel. Lecture seule."; } }

        public CommandeAsync GenererCommand { get; private set; }
        public Commande ExporterCommand { get; private set; }
        public Commande PdfCommand { get; private set; }
        public Commande ImprimerCommand { get; private set; }

        private byte[] Pdf()
        {
            return ImpressionEtats.Generer(_resultat, ImpressionPdf.Societe(Session), ImpressionPdf.Dossier(Session));
        }

        private string NomPdf()
        {
            return Path.ChangeExtension(ExportEtats.NomFichier(_resultat), ".pdf");
        }

        // ---------------------------------------------------------------- Chargement

        public override async Task ActiverAsync()
        {
            if (!Session.BaseChoisie) return;
            if (_referentiel != null && ReferenceEquals(_referentiel, Session.Referentiel)) return;

            await OccuperAsync("Chargement du plan comptable, des tiers et des journaux…", async () =>
            {
                _referentiel = await Session.ObtenirReferentielAsync();
                Resultat = null;

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

                var journaux = _referentiel.Journaux.Values.OrderBy(j => j.Code, StringComparer.OrdinalIgnoreCase).ToList();
                JournauxChoix = new[] { new Choix("", "Tous les journaux") }
                    .Concat(journaux.Select(j => new Choix(j.Code, j.Code + " — " + j.Intitule))).ToList();
                _journal = JournauxChoix[0];
                JournauxANouveauxChoix = new[] { new Choix("", "Aucun") }
                    .Concat(journaux.Select(j => new Choix(j.Code, j.Code + " — " + j.Intitule))).ToList();
                var an = ServiceEtats.DetecterJournalANouveaux(_referentiel);
                _journalANouveaux = JournauxANouveauxChoix.FirstOrDefault(c => string.Equals(c.Valeur, an, StringComparison.OrdinalIgnoreCase))
                                    ?? JournauxANouveauxChoix[0];

                NotifierPlusieurs("Exercices", "ExerciceSelectionne", "DuTexte", "AuTexte", "PeriodeValide",
                                  "JournauxChoix", "JournalChoisi", "JournauxANouveauxChoix", "JournalANouveauxChoisi");
            });
        }

        // ---------------------------------------------------------------- État choisi

        private string _etat = "Balance";
        /// <summary>« Balance », « GrandLivre », « Journal » ou « Centralisateur » (segments de l'écran).</summary>
        public string EtatChoisi
        {
            get { return _etat; }
            set
            {
                if (!Set(ref _etat, value)) return;
                Resultat = null;
                NotifierPlusieurs("AvecComptes", "AvecJournal", "AvecTypeTiers", "EstBalance", "AideEtat");
                CommandeBase.ReevaluerTout();
            }
        }

        private TypeEtat Etat
        {
            get
            {
                TypeEtat e;
                return Enum.TryParse(_etat, out e) ? e : TypeEtat.Balance;
            }
        }

        /// <summary>Balance et grand livre : nature, plage de comptes, journal des à-nouveaux.</summary>
        public bool AvecComptes { get { return Etat == TypeEtat.Balance || Etat == TypeEtat.GrandLivre; } }
        /// <summary>Journaux et centralisateur : choix du journal.</summary>
        public bool AvecJournal { get { return !AvecComptes; } }
        public bool AvecTypeTiers { get { return AvecComptes && _nature == NatureCompte.Tiers; } }
        public bool EstBalance { get { return Etat == TypeEtat.Balance; } }

        public string AideEtat
        {
            get
            {
                switch (Etat)
                {
                    case TypeEtat.Balance: return "Soldes d'ouverture, mouvements de la période et soldes finaux de chaque compte, avec total par classe.";
                    case TypeEtat.GrandLivre: return "Écritures de chaque compte avec solde d'ouverture, solde progressif et total.";
                    case TypeEtat.Journal: return "Écritures par journal et par mois, avec total du mois et du journal.";
                    default: return "Total de chaque journal pour chaque mois de la période.";
                }
            }
        }

        // ---------------------------------------------------------------- Paramètres

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
            // Les bornes de comptes généraux n'ont pas de sens pour les tiers (et inversement).
            _compteDe = "";
            _compteA = "";
            NotifierPlusieurs("NatureGenerale", "NatureTiers", "AvecTypeTiers", "CompteDe", "CompteA");
            ParametreModifie();
        }

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
                ParametreModifie();
            }
        }

        private string _duTexte = "";
        public string DuTexte { get { return _duTexte; } set { if (Set(ref _duTexte, value)) { Notifier("PeriodeValide"); ParametreModifie(); } } }

        private string _auTexte = "";
        public string AuTexte { get { return _auTexte; } set { if (Set(ref _auTexte, value)) { Notifier("PeriodeValide"); ParametreModifie(); } } }

        public bool PeriodeValide
        {
            get
            {
                DateTime du, au;
                return Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) && Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) && au >= du;
            }
        }

        private string _compteDe = "";
        public string CompteDe { get { return _compteDe; } set { if (Set(ref _compteDe, value ?? "")) ParametreModifie(); } }

        private string _compteA = "";
        public string CompteA { get { return _compteA; } set { if (Set(ref _compteA, value ?? "")) ParametreModifie(); } }

        private IList<Choix> _journauxChoix = new List<Choix>();
        public IList<Choix> JournauxChoix { get { return _journauxChoix; } private set { Set(ref _journauxChoix, value); } }

        private Choix _journal;
        public Choix JournalChoisi { get { return _journal; } set { if (Set(ref _journal, value)) ParametreModifie(); } }

        private IList<Choix> _journauxANouveauxChoix = new List<Choix>();
        public IList<Choix> JournauxANouveauxChoix { get { return _journauxANouveauxChoix; } private set { Set(ref _journauxANouveauxChoix, value); } }

        private Choix _journalANouveaux;
        /// <summary>Ses écritures de la période vont dans le solde d'ouverture (balance, grand livre).</summary>
        public Choix JournalANouveauxChoisi { get { return _journalANouveaux; } set { if (Set(ref _journalANouveaux, value)) ParametreModifie(); } }

        private static readonly IList<Choix> _typesTiers = new List<Choix>
        {
            new Choix("", "Tous les tiers"),
            new Choix(TypesTiers.Client.ToString(CultureInfo.InvariantCulture), "Clients"),
            new Choix(TypesTiers.Fournisseur.ToString(CultureInfo.InvariantCulture), "Fournisseurs"),
            new Choix(TypesTiers.Salarie.ToString(CultureInfo.InvariantCulture), "Salariés"),
            new Choix(TypesTiers.Autre.ToString(CultureInfo.InvariantCulture), "Autres tiers")
        };
        public IList<Choix> TypesTiersChoix { get { return _typesTiers; } }

        private Choix _typeTiers = _typesTiers[0];
        public Choix TypeTiersChoisi { get { return _typeTiers; } set { if (Set(ref _typeTiers, value)) ParametreModifie(); } }

        private bool _inclureSoldes = true;
        public bool InclureComptesSoldes { get { return _inclureSoldes; } set { if (Set(ref _inclureSoldes, value)) ParametreModifie(); } }

        /// <summary>Un paramètre a changé depuis le dernier calcul : l'état affiché n'est plus à jour.</summary>
        private bool _perime;
        public bool ResultatPerime { get { return _perime; } private set { Set(ref _perime, value); } }

        private void ParametreModifie()
        {
            if (_resultat != null) ResultatPerime = true;
            CommandeBase.ReevaluerTout();
        }

        private ParametresEtat ParametresCourants()
        {
            DateTime du, au;
            if (!Analyseurs.EssayerDate(_duTexte, FormatsDate, out du) || !Analyseurs.EssayerDate(_auTexte, FormatsDate, out au) || au < du)
                return null;
            int type;
            return new ParametresEtat
            {
                Etat = Etat,
                Nature = _nature,
                Du = du,
                Au = au,
                CompteDe = AvecComptes ? _compteDe : "",
                CompteA = AvecComptes ? _compteA : "",
                Journal = AvecJournal && _journal != null ? _journal.Valeur : "",
                JournalANouveaux = AvecComptes && _journalANouveaux != null ? _journalANouveaux.Valeur : "",
                TypeTiers = AvecTypeTiers && _typeTiers != null && int.TryParse(_typeTiers.Valeur, NumberStyles.Integer, CultureInfo.InvariantCulture, out type)
                            ? type : (int?)null,
                InclureComptesSoldes = _inclureSoldes
            };
        }

        // ---------------------------------------------------------------- Calcul

        private async Task GenererAsync()
        {
            if (!Session.BaseChoisie || _referentiel == null) return;
            var p = ParametresCourants();
            if (p == null) return;
            await OccuperAsync("Calcul : " + ServiceEtats.Titre(p.Etat, p.Nature).ToLower(CultureInfo.CurrentCulture) + "…", async () =>
            {
                using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                    Resultat = await ServiceEtats.GenererAsync(c, p, _referentiel);
            });
        }

        private ResultatEtat _resultat;
        public ResultatEtat Resultat
        {
            get { return _resultat; }
            private set
            {
                _resultat = value;
                _perime = false;
                Lignes = value == null ? new List<LigneEtat>() : value.Lignes;
                NotifierTout();
                CommandeBase.ReevaluerTout();
            }
        }

        public IList<LigneEtat> Lignes { get; private set; } = new List<LigneEtat>();

        public bool AResultat { get { return _resultat != null; } }
        public bool AucuneLigne { get { return _resultat != null && _resultat.Lignes.All(l => l.Genre == GenreLigne.TotalGeneral); } }

        // Grille affichée : celle de l'état calculé (pas de l'état sélectionné avant un nouveau calcul).
        public bool AfficheBalance { get { return _resultat != null && _resultat.Etat == TypeEtat.Balance; } }
        public bool AfficheGrandLivre { get { return _resultat != null && _resultat.Etat == TypeEtat.GrandLivre; } }
        public bool AfficheJournal { get { return _resultat != null && _resultat.Etat == TypeEtat.Journal; } }
        public bool AfficheCentralisateur { get { return _resultat != null && _resultat.Etat == TypeEtat.Centralisateur; } }

        public string TitreResultat { get { return _resultat == null ? "Aucun état calculé" : _resultat.Titre; } }
        public string SousTitreResultat { get { return _resultat == null ? "Choisis l'état et la période, puis Générer." : _resultat.SousTitre; } }

        public decimal TotalDebit { get { return _resultat == null ? 0m : _resultat.TotalDebit; } }
        public decimal TotalCredit { get { return _resultat == null ? 0m : _resultat.TotalCredit; } }

        public string EcartLibelle
        {
            get
            {
                if (_resultat == null) return "—";
                return _resultat.Ecart == 0 ? "Équilibré" : Montants.AvecSens(_resultat.Ecart);
            }
        }

        public bool EcartNonNul { get { return _resultat != null && _resultat.Ecart != 0; } }

        public string CompteurTitre
        {
            get
            {
                if (_resultat == null) return "LIGNES";
                return _resultat.Etat == TypeEtat.Balance || _resultat.Etat == TypeEtat.GrandLivre ? "COMPTES" : "ÉCRITURES";
            }
        }

        public string CompteurLibelle
        {
            get
            {
                var r = _resultat;
                if (r == null) return "—";
                switch (r.Etat)
                {
                    case TypeEtat.Balance: return r.NombreComptes.ToString("N0");
                    case TypeEtat.GrandLivre: return r.NombreComptes.ToString("N0") + " · " + r.NombreEcritures.ToString("N0") + " écr.";
                    default: return r.NombreEcritures.ToString("N0") + " · " + r.NombreJournaux.ToString("N0") + " jnl";
                }
            }
        }

        public IList<string> Avertissements { get { return _resultat == null ? new List<string>() : _resultat.Avertissements; } }
        public bool AAvertissements { get { return _resultat != null && _resultat.Avertissements.Count > 0; } }

        // ---------------------------------------------------------------- Export

        private void Exporter()
        {
            var r = _resultat;
            if (r == null) return;
            var chemin = Dialogues.ChoisirEmplacement("Exporter l'état", "Classeur Excel (*.xlsx)|*.xlsx", ExportEtats.NomFichier(r));
            if (chemin == null) return;
            try
            {
                ExportEtats.Ecrire(chemin, r);
                Navigation.Notifier("État exporté : " + Path.GetFileName(chemin), TypeNotification.Succes);
                Dialogues.OuvrirDansExplorateur(chemin);
            }
            catch (IOException ex)
            {
                Navigation.Notifier("Export impossible : " + ex.Message + " (le fichier est peut-être ouvert dans Excel).", TypeNotification.Erreur);
            }
        }
    }
}
