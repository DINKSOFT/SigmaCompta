using System;
using System.Collections.Generic;
using System.Globalization;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>Mois de saisie : on saisit dans un journal et une période, comme dans le logiciel comptable.</summary>
    public sealed class PeriodeSaisie
    {
        private static readonly CultureInfo Fr = new CultureInfo("fr-FR");

        public PeriodeSaisie(DateTime mois)
        {
            Mois = new DateTime(mois.Year, mois.Month, 1);
        }

        /// <summary>Premier jour du mois.</summary>
        public DateTime Mois { get; private set; }
        public DateTime Fin { get { return Mois.AddMonths(1).AddDays(-1); } }
        public int NombreJours { get { return DateTime.DaysInMonth(Mois.Year, Mois.Month); } }

        public string Libelle
        {
            get
            {
                var s = Mois.ToString("MMMM yyyy", Fr);
                return s.Length == 0 ? s : char.ToUpper(s[0], Fr) + s.Substring(1);
            }
        }

        public bool Contient(DateTime d) { return d.Date >= Mois && d.Date <= Fin; }

        public override string ToString() { return Libelle; }
        public override bool Equals(object obj) { var p = obj as PeriodeSaisie; return p != null && p.Mois == Mois; }
        public override int GetHashCode() { return Mois.GetHashCode(); }
    }

    /// <summary>En-tête de la pièce saisie.</summary>
    public sealed class EntetePiece
    {
        public string Journal { get; set; }
        public DateTime? Date { get; set; }
        /// <summary>Vide = numérotation automatique selon le journal.</summary>
        public string Piece { get; set; }
        /// <summary>Référence de la pièce (n° de facture…), reprise sur chaque ligne.</summary>
        public string RefPiece { get; set; }
        /// <summary>Libellé proposé pour les lignes sans libellé.</summary>
        public string Libelle { get; set; }
    }

    /// <summary>Texte brut de la ligne de saisie, tel que tapé à l'écran.</summary>
    public sealed class SaisieLigne
    {
        public string Compte { get; set; }
        public string Tiers { get; set; }
        public string Libelle { get; set; }
        public string Echeance { get; set; }
        public string Debit { get; set; }
        public string Credit { get; set; }
    }

    /// <summary>Une ligne de la pièce en cours, comptes déjà reconnus dans la base.</summary>
    public sealed class LigneSaisie
    {
        public int Numero { get; set; }
        public string CompteGeneral { get; set; }
        public string IntituleCompte { get; set; }
        public string CompteTiers { get; set; }
        public string IntituleTiers { get; set; }
        public string Libelle { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public DateTime? Echeance { get; set; }

        public decimal? DebitAffiche { get { return Debit == 0 ? (decimal?)null : Debit; } }
        public decimal? CreditAffiche { get { return Credit == 0 ? (decimal?)null : Credit; } }

        /// <summary>Texte brut équivalent, pour remettre la ligne dans la zone de saisie (modification).</summary>
        public SaisieLigne VersSaisie()
        {
            return new SaisieLigne
            {
                Compte = CompteGeneral,
                Tiers = CompteTiers,
                Libelle = Libelle,
                Echeance = Echeance.HasValue ? Echeance.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "",
                Debit = Debit == 0 ? "" : Debit.ToString("0.##", CultureInfo.CurrentCulture),
                Credit = Credit == 0 ? "" : Credit.ToString("0.##", CultureInfo.CurrentCulture)
            };
        }
    }

    /// <summary>Écritures déjà enregistrées dans le journal et la période (brouillard).</summary>
    public sealed class BrouillardPeriode
    {
        public List<LigneEtat> Lignes { get; } = new List<LigneEtat>();
        public decimal TotalDebit { get; set; }
        public decimal TotalCredit { get; set; }
        public int NombreEcritures { get; set; }
        public int NombrePieces { get; set; }
    }
}
