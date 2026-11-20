using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SigmaCompta.App.Mvvm;
using SigmaCompta.App.Services;
using SigmaCompta.Core.Comptabilite;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.App.ViewModels
{
    /// <summary>
    /// Comptabilité › Saisie : pièces tapées au clavier dans un journal et un mois, contrôlées localement,
    /// puis enregistrées par imp.PS_ImportPiece (mêmes contrôles et même numérotation que l'import).
    /// </summary>
    public sealed class SaisieViewModel : PageViewModel
    {
        private Referentiel _referentiel;
        private List<ElementCompte> _comptes = new List<ElementCompte>();
        private List<ElementCompte> _tiers = new List<ElementCompte>();
        private LigneSaisie _ligneEnModification;
        private string _baseConfirmee;

        public SaisieViewModel(Session session, INavigation navigation, IDialogues dialogues)
            : base(session, navigation, dialogues)
        {
            ValiderLigneCommand = new Commande(ValiderLigne, () => _referentiel != null);
            AnnulerLigneCommand = new Commande(AnnulerLigne);
            ModifierLigneCommand = new Commande(ModifierLigne, () => _ligneSelectionnee != null);
            SupprimerLigneCommand = new Commande(SupprimerLigne, () => _ligneSelectionnee != null);
            NouvellePieceCommand = new Commande(NouvellePiece, () => _lignes.Count > 0 || _ligneEnModification != null);
            ControlerCommand = new CommandeAsync(() => EnvoyerAsync(true), () => PeutEnvoyer);
            EnregistrerCommand = new CommandeAsync(() => EnvoyerAsync(false), () => PeutEnvoyer);
            ActualiserCommand = new CommandeAsync(ActualiserAsync, () => _journal != null && _periode != null);
            UtiliserSuggestionCommand = new Commande(p => UtiliserSuggestion(p as ElementCompte), p => p is ElementCompte);
            VerificationCommand = new Commande(() => Navigation.AllerA(3));
        }

        public override string Titre { get { return "Saisie des écritures"; } }
        public override string SousTitre { get { return "Pièces saisies au clavier dans un journal et un mois, contrôlées puis enregistrées dans la base comptable."; } }

        public Commande ValiderLigneCommand { get; private set; }
        public Commande AnnulerLigneCommand { get; private set; }
        public Commande ModifierLigneCommand { get; private set; }
        public Commande SupprimerLigneCommand { get; private set; }
        public Commande NouvellePieceCommand { get; private set; }
        public CommandeAsync ControlerCommand { get; private set; }
        public CommandeAsync EnregistrerCommand { get; private set; }
        public CommandeAsync ActualiserCommand { get; private set; }
        public Commande UtiliserSuggestionCommand { get; private set; }
        public Commande VerificationCommand { get; private set; }

        // ---------------------------------------------------------------- Chargement

        public override async Task ActiverAsync()
        {
            if (!Session.BaseChoisie) return;
            if (_referentiel != null && ReferenceEquals(_referentiel, Session.Referentiel))
            {
                // Retour sur l'écran : la pièce en cours est gardée, le brouillard est relu (un import a pu avoir lieu).
                Lancer(ActualiserAsync);
                return;
            }

            await OccuperAsync("Chargement des journaux, comptes et tiers…", async () =>
            {
                _referentiel = await Session.ObtenirReferentielAsync();
                _comptes = ServiceSaisie.ComptesSaisissables(_referentiel, NatureCompte.General);
                _tiers = ServiceSaisie.ComptesSaisissables(_referentiel, NatureCompte.Tiers);
                Journaux = ServiceSaisie.JournauxSaisissables(_referentiel);
                Periodes = ServiceSaisie.Periodes(_referentiel);
                _journal = Journaux.FirstOrDefault();
                _periode = ServiceSaisie.PeriodeParDefaut(Periodes, DateTime.Today);
                _lignes = new ObservableCollection<LigneSaisie>();
                _ligneEnModification = null;
                ViderChampsPiece();
                ViderChampsLigne("");
                ProposerSuggestions(NatureCompte.General, "");

                using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                {
                    MessageProcedure = await ServiceSaisie.VerifierProcedureAsync(c);
                    ChainageActif = await ServiceSaisie.ChainageActifAsync(c);
                    await LireBrouillardAsync(c);
                }
                Revalider();
                NotifierTout();
            });
        }

        // ---------------------------------------------------------------- Journal et période

        private IList<Journal> _journaux = new List<Journal>();
        public IList<Journal> Journaux { get { return _journaux; } private set { Set(ref _journaux, value); } }

        private Journal _journal;
        public Journal JournalChoisi
        {
            get { return _journal; }
            set { if (Set(ref _journal, value)) ContexteModifie(); }
        }

        private IList<PeriodeSaisie> _periodes = new List<PeriodeSaisie>();
        public IList<PeriodeSaisie> Periodes { get { return _periodes; } private set { Set(ref _periodes, value); } }

        private PeriodeSaisie _periode;
        public PeriodeSaisie PeriodeChoisie
        {
            get { return _periode; }
            set { if (Set(ref _periode, value)) ContexteModifie(); }
        }

        /// <summary>Bornes de la période : le calendrier du champ « Jour » ne propose que ces dates.</summary>
        public DateTime? DebutPeriode { get { return _periode == null ? (DateTime?)null : _periode.Mois; } }
        public DateTime? FinPeriode { get { return _periode == null ? (DateTime?)null : _periode.Fin; } }

        private void ContexteModifie()
        {
            NotifierPlusieurs("InfoJournal", "PeriodeCloturee", "MessageCloture", "TitreBrouillard", "DebutPeriode", "FinPeriode");
            Revalider();
            Lancer(ActualiserAsync);
        }

        public string InfoJournal
        {
            get
            {
                var j = _journal;
                if (j == null) return "";
                var txt = j.TypeLibelle + " · numérotation " + j.NumerotationLibelle.ToLower(CultureInfo.CurrentCulture);
                if (!string.IsNullOrEmpty(j.CompteTresorerie)) txt += " · trésorerie " + j.CompteTresorerie;
                return txt;
            }
        }

        public bool PeriodeCloturee { get { return _journal != null && ServiceSaisie.EstCloturee(_referentiel, _journal.Code, _periode); } }

        public string MessageCloture
        {
            get { return PeriodeCloturee ? "La période " + _periode.Libelle + " du journal " + _journal.Code + " est clôturée : aucune pièce ne peut y être enregistrée." : null; }
        }

        private string _messageProcedure;
        /// <summary>Null si la procédure d'enregistrement est installée et à jour.</summary>
        public string MessageProcedure
        {
            get { return _messageProcedure; }
            private set { if (Set(ref _messageProcedure, value)) Notifier("ProcedureAbsente"); }
        }
        public bool ProcedureAbsente { get { return _messageProcedure != null; } }

        private bool _chainageActif;
        /// <summary>La base chaîne ses écritures (cbHash) : l'enregistrement direct exige une acceptation explicite.</summary>
        public bool ChainageActif { get { return _chainageActif; } private set { Set(ref _chainageActif, value); } }

        private bool _ignorerHash;
        public bool IgnorerHash { get { return _ignorerHash; } set { if (Set(ref _ignorerHash, value)) CommandeBase.ReevaluerTout(); } }

        // ---------------------------------------------------------------- En-tête de la pièce

        private string _jour = "";
        public string JourTexte { get { return _jour; } set { if (Set(ref _jour, value ?? "")) Revalider(); } }

        private string _piece = "";
        public string PieceTexte { get { return _piece; } set { if (Set(ref _piece, value ?? "")) Revalider(); } }

        private string _refPiece = "";
        public string RefPieceTexte { get { return _refPiece; } set { if (Set(ref _refPiece, value ?? "")) Revalider(); } }

        private string _libellePiece = "";
        /// <summary>Libellé proposé pour les lignes (repris sur la ligne de saisie s'il est vide).</summary>
        public string LibellePiece
        {
            get { return _libellePiece; }
            set
            {
                var ancien = _libellePiece;
                if (!Set(ref _libellePiece, value ?? "")) return;
                if (string.IsNullOrEmpty(_libelleLigne) || _libelleLigne == ancien) LibelleLigne = _libellePiece;
                Revalider();
            }
        }

        public DateTime? DatePiece { get { return ServiceSaisie.Date(_periode, _jour); } }

        public string ApercuDate
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_jour)) return "jour du mois";
                var d = DatePiece;
                return d.HasValue ? d.Value.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture) : "hors période";
            }
        }

        private void ViderChampsPiece()
        {
            _jour = "";
            _piece = "";
            _refPiece = "";
            _libellePiece = "";
            NotifierPlusieurs("JourTexte", "PieceTexte", "RefPieceTexte", "LibellePiece", "ApercuDate");
        }

        // ---------------------------------------------------------------- Ligne de saisie

        private string _compte = "";
        public string CompteTexte
        {
            get { return _compte; }
            set
            {
                if (!Set(ref _compte, value ?? "")) return;
                ProposerSuggestions(NatureCompte.General, _compte);
                Notifier("ApercuLigne");
            }
        }

        private string _tiersTexte = "";
        public string TiersTexte
        {
            get { return _tiersTexte; }
            set
            {
                if (!Set(ref _tiersTexte, value ?? "")) return;
                ProposerSuggestions(NatureCompte.Tiers, _tiersTexte);
                Notifier("ApercuLigne");
            }
        }

        private string _libelleLigne = "";
        public string LibelleLigne { get { return _libelleLigne; } set { Set(ref _libelleLigne, value ?? ""); } }

        private string _echeance = "";
        public string EcheanceTexte { get { return _echeance; } set { Set(ref _echeance, value ?? ""); } }

        private string _debit = "";
        public string DebitTexte { get { return _debit; } set { Set(ref _debit, value ?? ""); } }

        private string _credit = "";
        public string CreditTexte { get { return _credit; } set { Set(ref _credit, value ?? ""); } }

        private string _messageLigne;
        /// <summary>Raison pour laquelle la ligne tapée n'a pas pu être ajoutée (null = aucune).</summary>
        public string MessageLigne { get { return _messageLigne; } private set { Set(ref _messageLigne, value); } }

        /// <summary>Comptes reconnus pendant la frappe : « 4010000 Fournisseurs · F100003 SONABEL ».</summary>
        public string ApercuLigne
        {
            get
            {
                if (_referentiel == null) return "";
                var morceaux = new List<string>();
                var tiers = ServiceSaisie.ResoudreTiers(_referentiel, _tiersTexte);
                var compte = string.IsNullOrWhiteSpace(_compte) ? ServiceSaisie.CollectifDuTiers(_referentiel, tiers)
                                                                : ServiceSaisie.ResoudreCompte(_referentiel, _compte);
                if (compte != null) morceaux.Add(compte.Numero + " " + compte.Intitule + (compte.EstImputable ? "" : " (compte de total)"));
                else if (!string.IsNullOrWhiteSpace(_compte)) morceaux.Add("compte « " + _compte.Trim() + " » à préciser");
                if (tiers != null) morceaux.Add(tiers.Numero + " " + tiers.Intitule);
                else if (!string.IsNullOrWhiteSpace(_tiersTexte)) morceaux.Add("tiers « " + _tiersTexte.Trim() + " » à préciser");
                return string.Join("  ·  ", morceaux);
            }
        }

        public bool EnModification { get { return _ligneEnModification != null; } }
        public string LibelleBoutonLigne { get { return _ligneEnModification == null ? "Ajouter" : "Mettre à jour"; } }

        private void ViderChampsLigne(string libelle)
        {
            _compte = "";
            _tiersTexte = "";
            _libelleLigne = libelle ?? "";
            _echeance = "";
            _debit = "";
            _credit = "";
            _messageLigne = null;
            NotifierPlusieurs("CompteTexte", "TiersTexte", "LibelleLigne", "EcheanceTexte", "DebitTexte", "CreditTexte", "MessageLigne",
                              "ApercuLigne", "EnModification", "LibelleBoutonLigne");
        }

        /// <summary>Ligne suivante : comptes vides, même libellé, montant qui équilibre la pièce.</summary>
        private void PreparerLigneSuivante(string libelle)
        {
            ViderChampsLigne(libelle);
            decimal d, c;
            ServiceSaisie.SoldeAProposer(_lignes, out d, out c);
            _debit = d == 0 ? "" : d.ToString("0.##", CultureInfo.CurrentCulture);
            _credit = c == 0 ? "" : c.ToString("0.##", CultureInfo.CurrentCulture);
            NotifierPlusieurs("DebitTexte", "CreditTexte");
            ProposerSuggestions(NatureCompte.General, "");
        }

        private void ValiderLigne()
        {
            if (_referentiel == null) return;
            var saisie = new SaisieLigne
            {
                Compte = _compte, Tiers = _tiersTexte, Libelle = _libelleLigne,
                Echeance = _echeance, Debit = _debit, Credit = _credit
            };
            string erreur;
            var ligne = ServiceSaisie.InterpreterLigne(_referentiel, saisie, _libellePiece, out erreur);
            if (ligne == null)
            {
                MessageLigne = erreur;
                return;
            }
            var liste = _lignes.ToList();
            int i = _ligneEnModification == null ? -1 : liste.IndexOf(_ligneEnModification);
            if (i >= 0) liste[i] = ligne;
            else liste.Add(ligne);
            _ligneEnModification = null;
            DefinirLignes(liste);
            PreparerLigneSuivante(ligne.Libelle);
        }

        private void AnnulerLigne()
        {
            _ligneEnModification = null;
            PreparerLigneSuivante(string.IsNullOrEmpty(_libelleLigne) ? _libellePiece : _libelleLigne);
        }

        private void ModifierLigne()
        {
            var l = _ligneSelectionnee;
            if (l == null) return;
            _ligneEnModification = l;
            var s = l.VersSaisie();
            _compte = s.Compte ?? "";
            _tiersTexte = s.Tiers ?? "";
            _libelleLigne = s.Libelle ?? "";
            _echeance = s.Echeance ?? "";
            _debit = s.Debit ?? "";
            _credit = s.Credit ?? "";
            _messageLigne = null;
            NotifierPlusieurs("CompteTexte", "TiersTexte", "LibelleLigne", "EcheanceTexte", "DebitTexte", "CreditTexte", "MessageLigne",
                              "ApercuLigne", "EnModification", "LibelleBoutonLigne");
            CommandeBase.ReevaluerTout();
        }

        private void SupprimerLigne()
        {
            var l = _ligneSelectionnee;
            if (l == null) return;
            if (ReferenceEquals(l, _ligneEnModification)) _ligneEnModification = null;
            DefinirLignes(_lignes.Where(x => !ReferenceEquals(x, l)).ToList());
            if (_ligneEnModification == null) PreparerLigneSuivante(_libelleLigne);
        }

        // ---------------------------------------------------------------- Suggestions

        private IList<ElementCompte> _suggestions = new List<ElementCompte>();
        public IList<ElementCompte> Suggestions { get { return _suggestions; } private set { Set(ref _suggestions, value); } }

        private string _titreSuggestions = "Comptes généraux";
        public string TitreSuggestions { get { return _titreSuggestions; } private set { Set(ref _titreSuggestions, value); } }

        private void ProposerSuggestions(NatureCompte nature, string texte)
        {
            TitreSuggestions = nature == NatureCompte.General ? "Comptes généraux" : "Tiers";
            Suggestions = ServiceSaisie.Filtrer(nature == NatureCompte.General ? _comptes : _tiers, texte, 60);
        }

        private void UtiliserSuggestion(ElementCompte e)
        {
            if (e == null) return;
            if (e.Nature == NatureCompte.General) CompteTexte = e.Numero;
            else TiersTexte = e.Numero;
        }

        // ---------------------------------------------------------------- Lignes de la pièce

        private ObservableCollection<LigneSaisie> _lignes = new ObservableCollection<LigneSaisie>();
        public ObservableCollection<LigneSaisie> Lignes { get { return _lignes; } }

        private LigneSaisie _ligneSelectionnee;
        public LigneSaisie LigneSelectionnee
        {
            get { return _ligneSelectionnee; }
            set { if (Set(ref _ligneSelectionnee, value)) CommandeBase.ReevaluerTout(); }
        }

        private void DefinirLignes(IList<LigneSaisie> liste)
        {
            for (int i = 0; i < liste.Count; i++) liste[i].Numero = i + 1;
            _lignes = new ObservableCollection<LigneSaisie>(liste);
            _ligneSelectionnee = null;
            NotifierPlusieurs("Lignes", "LigneSelectionnee", "TotalDebit", "TotalCredit", "EcartLibelle", "EstEquilibree", "AucuneLigne");
            Revalider();
        }

        public bool AucuneLigne { get { return _lignes.Count == 0; } }
        public decimal TotalDebit { get { return _lignes.Sum(l => l.Debit); } }
        public decimal TotalCredit { get { return _lignes.Sum(l => l.Credit); } }
        public bool EstEquilibree { get { return _lignes.Count > 0 && ServiceSaisie.Ecart(_lignes) == 0; } }

        public string EcartLibelle
        {
            get
            {
                if (_lignes.Count == 0) return "—";
                var e = ServiceSaisie.Ecart(_lignes);
                return e == 0 ? "Équilibrée" : "Écart " + Montants.AvecSens(e);
            }
        }

        // ---------------------------------------------------------------- Contrôles

        private IList<string> _anomalies = new List<string>();
        public IList<string> Anomalies { get { return _anomalies; } private set { Set(ref _anomalies, value); } }

        private bool _pieceValide;
        public bool PieceValide { get { return _pieceValide; } private set { Set(ref _pieceValide, value); } }

        public bool PeutEnvoyer
        {
            get { return _pieceValide && !ProcedureAbsente && !PeriodeCloturee && (!_chainageActif || _ignorerHash); }
        }

        private EntetePiece Entete()
        {
            return new EntetePiece
            {
                Journal = _journal == null ? "" : _journal.Code,
                Date = DatePiece,
                Piece = _piece,
                RefPiece = _refPiece,
                Libelle = _libellePiece
            };
        }

        private void Revalider()
        {
            NotifierPlusieurs("ApercuDate", "DatePiece");
            if (_referentiel == null || _lignes.Count == 0)
            {
                Anomalies = new List<string>();
                PieceValide = false;
            }
            else
            {
                var p = ServiceSaisie.Construire(Entete(), _lignes);
                var liste = ServiceSaisie.Controler(p, _referentiel, _periode);
                Anomalies = liste.Select(a => a.ToString()).ToList();
                PieceValide = !liste.Any(a => a.EstErreur);
            }
            Notifier("AAnomalies");
            CommandeBase.ReevaluerTout();
        }

        public bool AAnomalies { get { return _anomalies.Count > 0; } }

        private string _messageServeur;
        /// <summary>Résultat du dernier contrôle ou enregistrement.</summary>
        public string MessageServeur { get { return _messageServeur; } private set { Set(ref _messageServeur, value); } }

        private bool _messageServeurErreur;
        public bool MessageServeurErreur { get { return _messageServeurErreur; } private set { Set(ref _messageServeurErreur, value); } }

        // ---------------------------------------------------------------- Contrôle serveur et enregistrement

        private async Task EnvoyerAsync(bool simulation)
        {
            if (_referentiel == null || !Session.BaseChoisie) return;
            var piece = ServiceSaisie.Construire(Entete(), _lignes);
            if (ServiceSaisie.Controler(piece, _referentiel, _periode).Any(a => a.EstErreur))
            {
                Revalider();
                Navigation.Notifier("La pièce contient des erreurs : corrigez-les avant de l'envoyer.", TypeNotification.Avertissement);
                return;
            }

            var nomBase = Session.Base.Nom;
            if (!simulation && _baseConfirmee != nomBase)
            {
                if (!Dialogues.Confirmer("Enregistrer dans la base comptable",
                    "Les pièces saisies ici sont écrites directement dans la base « " + nomBase + " », comme une saisie dans le logiciel comptable.\n\n" +
                    "Une fois enregistrée, une pièce se corrige ou se supprime dans le logiciel comptable : Sigma Compta ne modifie pas encore les écritures existantes.\n\n" +
                    "Cette confirmation n'est demandée qu'une fois par base.", "Enregistrer")) return;
                _baseConfirmee = nomBase;
            }

            string erreur = null;
            var termine = await OccuperAsync(simulation ? "Contrôle de la pièce par le serveur…" : "Enregistrement de la pièce…", async () =>
            {
                using (var c = await Session.Fabrique.OuvrirAsync(nomBase))
                {
                    try
                    {
                        await ServiceImport.AppelerAsync(c, null, piece, ServiceSaisie.Options(_ignorerHash), simulation, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        erreur = ServiceSaisie.ExpliquerErreur(ex);
                        return;
                    }
                    if (!simulation) await LireBrouillardAsync(c);
                }
            });

            if (erreur != null)
            {
                MessageServeurErreur = true;
                MessageServeur = (simulation ? "Pièce refusée par le serveur : " : "Pièce non enregistrée : ") + erreur;
                if (erreur.IndexOf("chaînage", StringComparison.OrdinalIgnoreCase) >= 0) ChainageActif = true;
                Navigation.Notifier(MessageServeur, TypeNotification.Erreur);
                return;
            }
            if (!termine || piece.NumeroAttribue == null) return;   // erreur de connexion : déjà notifiée par OccuperAsync

            MessageServeurErreur = false;
            var date = piece.Date.HasValue ? piece.Date.Value.ToString("dd/MM/yyyy") : "";
            if (simulation)
            {
                MessageServeur = "Pièce acceptée par le serveur (n° provisoire " + piece.NumeroAttribue + "). Rien n'a été enregistré.";
                Navigation.Notifier(MessageServeur, TypeNotification.Succes);
                return;
            }
            MessageServeur = "Pièce " + piece.NumeroAttribue + " du " + date + " enregistrée dans " + piece.Journal +
                             " (écritures " + piece.PlageEcNo + ", " + piece.TotalDebit.ToString("#,##0.##") + ").";
            Navigation.Notifier(MessageServeur, TypeNotification.Succes);
            // Pièce suivante : même journal, même période, même jour.
            _ligneEnModification = null;
            _piece = "";
            _refPiece = "";
            _libellePiece = "";
            NotifierPlusieurs("PieceTexte", "RefPieceTexte", "LibellePiece");
            DefinirLignes(new List<LigneSaisie>());
            PreparerLigneSuivante("");
        }

        private void NouvellePiece()
        {
            if (_lignes.Count > 0 && !Dialogues.Confirmer("Nouvelle pièce",
                    "Abandonner la pièce en cours (" + _lignes.Count + " ligne(s)) ? Elle n'a pas été enregistrée.", "Abandonner", true))
                return;
            _ligneEnModification = null;
            ViderChampsPiece();
            DefinirLignes(new List<LigneSaisie>());
            PreparerLigneSuivante("");
            MessageServeur = null;
        }

        // ---------------------------------------------------------------- Brouillard de la période

        private IList<LigneEtat> _brouillard = new List<LigneEtat>();
        public IList<LigneEtat> LignesBrouillard { get { return _brouillard; } private set { Set(ref _brouillard, value); } }

        private string _resumeBrouillard = "";
        public string ResumeBrouillard { get { return _resumeBrouillard; } private set { Set(ref _resumeBrouillard, value); } }

        public string TitreBrouillard
        {
            get
            {
                return _journal == null || _periode == null ? "Brouillard"
                    : "Déjà enregistré · " + _journal.Code + " · " + _periode.Libelle;
            }
        }

        private async Task ActualiserAsync()
        {
            if (!Session.BaseChoisie || _journal == null || _periode == null) return;
            using (var c = await Session.Fabrique.OuvrirAsync(Session.Base.Nom))
                await LireBrouillardAsync(c);
        }

        private async Task LireBrouillardAsync(SqlConnection c)
        {
            if (_journal == null || _periode == null)
            {
                LignesBrouillard = new List<LigneEtat>();
                ResumeBrouillard = "";
                return;
            }
            var b = ServiceSaisie.Brouillard(await ServiceSaisie.LireBrouillardAsync(c, _journal.Code, _periode));
            LignesBrouillard = b.Lignes;
            ResumeBrouillard = b.NombreEcritures == 0
                ? "Aucune écriture dans ce journal pour ce mois."
                : b.NombrePieces.ToString("N0") + " pièce(s) · " + b.NombreEcritures.ToString("N0") + " écriture(s) · débit " +
                  b.TotalDebit.ToString("#,##0.##") + " · crédit " + b.TotalCredit.ToString("#,##0.##");
        }
    }
}
