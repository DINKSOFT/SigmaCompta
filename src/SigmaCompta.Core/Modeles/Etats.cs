using System;
using System.Collections.Generic;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>États comptables disponibles.</summary>
    public enum TypeEtat { Balance, GrandLivre, Journal, Centralisateur }

    /// <summary>Rôle d'une ligne dans un état.</summary>
    public enum GenreLigne
    {
        /// <summary>Écriture, compte de balance ou cumul journal/mois.</summary>
        Detail,
        /// <summary>Titre d'un groupe : compte du grand livre, journal et mois.</summary>
        Entete,
        /// <summary>Solde d'ouverture d'un compte du grand livre.</summary>
        Report,
        /// <summary>Total intermédiaire : classe, type de tiers, mois.</summary>
        SousTotal,
        /// <summary>Total d'un compte ou d'un journal.</summary>
        Total,
        TotalGeneral
    }

    /// <summary>Types de tiers de la base comptable (F_COMPTET.CT_Type).</summary>
    public static class TypesTiers
    {
        public const int Client = 0;
        public const int Fournisseur = 1;
        public const int Salarie = 2;
        public const int Autre = 3;

        public static string Libelle(int type)
        {
            switch (type)
            {
                case Client: return "Clients";
                case Fournisseur: return "Fournisseurs";
                case Salarie: return "Salariés";
                case Autre: return "Autres tiers";
                default: return "Type non renseigné";
            }
        }
    }

    /// <summary>
    /// Plage « du compte … au compte … ». La borne haute porte sur le début du numéro :
    /// de « 6 » à « 6 » couvre toute la classe 6, de « 401 » à « 409 » couvre 4011000 à 4099999.
    /// </summary>
    public sealed class PlageComptes
    {
        public PlageComptes(string de, string a)
        {
            De = (de ?? "").Trim();
            A = (a ?? "").Trim();
        }

        public string De { get; private set; }
        public string A { get; private set; }
        public bool EstVide { get { return De.Length == 0 && A.Length == 0; } }

        public bool Contient(string compte)
        {
            var c = (compte ?? "").Trim();
            if (De.Length > 0 && string.Compare(c, De, StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (A.Length > 0)
            {
                var debut = c.Length > A.Length ? c.Substring(0, A.Length) : c;
                if (string.Compare(debut, A, StringComparison.OrdinalIgnoreCase) > 0) return false;
            }
            return true;
        }

        public string Libelle
        {
            get
            {
                if (EstVide) return "tous les comptes";
                if (A.Length == 0) return "à partir du compte " + De;
                if (De.Length == 0) return "jusqu'au compte " + A;
                return De == A ? "comptes " + De + "…" : "du compte " + De + " au compte " + A;
            }
        }
    }

    /// <summary>Paramètres d'un état comptable.</summary>
    public sealed class ParametresEtat
    {
        public TypeEtat Etat { get; set; }
        /// <summary>Balance et grand livre : comptes généraux ou tiers.</summary>
        public NatureCompte Nature { get; set; }
        public DateTime Du { get; set; }
        public DateTime Au { get; set; }
        public string CompteDe { get; set; }
        public string CompteA { get; set; }
        /// <summary>Journaux et centralisateur : un seul journal (vide = tous).</summary>
        public string Journal { get; set; }
        /// <summary>
        /// Balance et grand livre : journal des à-nouveaux. Ses écritures de la période vont dans le solde
        /// d'ouverture au lieu des mouvements. Vide = aucun.
        /// </summary>
        public string JournalANouveaux { get; set; }
        /// <summary>Tiers : un seul type (<see cref="TypesTiers"/>), null = tous.</summary>
        public int? TypeTiers { get; set; }
        /// <summary>Balance : garder les comptes dont le solde final est nul.</summary>
        public bool InclureComptesSoldes { get; set; } = true;

        public PlageComptes Plage { get { return new PlageComptes(CompteDe, CompteA); } }
    }

    /// <summary>
    /// Une ligne d'état. Selon l'état, seules certaines colonnes sont remplies ; une valeur nulle
    /// s'affiche comme une cellule vide.
    /// </summary>
    public sealed class LigneEtat
    {
        public GenreLigne Genre { get; set; }
        public string Compte { get; set; }
        public string CompteTiers { get; set; }
        /// <summary>Grand livre : le tiers d'une ligne de compte général, le compte général d'une ligne de tiers.</summary>
        public string CompteAssocie { get; set; }
        public string Libelle { get; set; }
        public string Periode { get; set; }
        public DateTime? Date { get; set; }
        public string Journal { get; set; }
        public string Piece { get; set; }
        public string RefPiece { get; set; }
        /// <summary>Référence de la ligne (EC_Reference) ; lue par le brouillard de saisie.</summary>
        public string Reference { get; set; }
        public DateTime? Echeance { get; set; }
        public string Lettrage { get; set; }
        public int? EcNo { get; set; }

        public decimal? Debit { get; set; }
        public decimal? Credit { get; set; }
        /// <summary>Grand livre : solde cumulé (positif = débiteur).</summary>
        public decimal? Solde { get; set; }

        // Balance (six colonnes)
        public decimal? OuvertureDebit { get; set; }
        public decimal? OuvertureCredit { get; set; }
        public decimal? MouvementDebit { get; set; }
        public decimal? MouvementCredit { get; set; }
        public decimal? SoldeDebit { get; set; }
        public decimal? SoldeCredit { get; set; }

        public string SoldeLibelle { get { return Solde.HasValue ? Montants.AvecSens(Solde.Value) : ""; } }
        public bool EstDetail { get { return Genre == GenreLigne.Detail; } }
    }

    /// <summary>Résultat d'un état : lignes prêtes à afficher ou exporter, totaux et avertissements.</summary>
    public sealed class ResultatEtat
    {
        public ParametresEtat Parametres { get; set; }
        public TypeEtat Etat { get { return Parametres == null ? TypeEtat.Balance : Parametres.Etat; } }
        public string Titre { get; set; }
        public string SousTitre { get; set; }
        /// <summary>Début de la période d'ouverture (début d'exercice), null si la période commence avec l'exercice.</summary>
        public DateTime? DebutReport { get; set; }

        public List<LigneEtat> Lignes { get; } = new List<LigneEtat>();
        public List<string> Avertissements { get; } = new List<string>();

        /// <summary>Mouvements de la période (hors solde d'ouverture).</summary>
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
        public decimal Ecart { get { return TotalDebit - TotalCredit; } }

        public int NombreComptes { get; set; }
        public int NombreJournaux { get; set; }
        public int NombreEcritures { get; set; }
        public DateTime GenereLe { get; set; } = DateTime.Now;
    }

    /// <summary>Cumuls d'un compte lus dans la base : ouverture et mouvements de la période.</summary>
    public sealed class CumulCompte
    {
        public string Compte { get; set; }
        public decimal OuvertureDebit { get; set; }
        public decimal OuvertureCredit { get; set; }
        public decimal MouvementDebit { get; set; }
        public decimal MouvementCredit { get; set; }

        public decimal SoldeOuverture { get { return OuvertureDebit - OuvertureCredit; } }
        public decimal SoldeFinal { get { return SoldeOuverture + MouvementDebit - MouvementCredit; } }
    }

    /// <summary>Écriture lue pour le grand livre ou les journaux.</summary>
    public sealed class EcritureEtat
    {
        public int EcNo { get; set; }
        public string Journal { get; set; }
        public DateTime Date { get; set; }
        public string Piece { get; set; }
        public string RefPiece { get; set; }
        /// <summary>Référence de la ligne (EC_Reference), vide si la requête ne la lit pas.</summary>
        public string Reference { get; set; }
        public string CompteGeneral { get; set; }
        public string CompteTiers { get; set; }
        public string Libelle { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public DateTime? Echeance { get; set; }
        public string Lettrage { get; set; }
    }

    /// <summary>Totaux d'un journal pour un mois (journal centralisateur).</summary>
    public sealed class CumulJournal
    {
        public string Journal { get; set; }
        /// <summary>Premier jour du mois.</summary>
        public DateTime Mois { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public int Nombre { get; set; }
    }
}
