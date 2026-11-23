using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>Compte général (CG_Num) ou compte tiers (CT_Num).</summary>
    public enum NatureCompte { General, Tiers }

    /// <summary>
    /// Type d'écriture affiché : toutes, ou selon l'état de lettrage, ou selon l'état de pointage
    /// (panneau « Type d'écriture » de l'interrogation de compte).
    /// </summary>
    public enum FiltreLettrage { Toutes, NonLettrees, Lettrees, NonPointees, Pointees }

    /// <summary>Critères d'une consultation de compte.</summary>
    public sealed class FiltreConsultation
    {
        public NatureCompte Nature { get; set; }
        public string Compte { get; set; }
        public DateTime Du { get; set; }
        public DateTime Au { get; set; }
        public FiltreLettrage Lettrage { get; set; }

        /// <summary>Le report à nouveau n'a de sens que pour la vue complète du compte.</summary>
        public bool AvecReport { get { return Lettrage == FiltreLettrage.Toutes; } }
    }

    /// <summary>Une ligne d'écriture du compte consulté (ou la ligne de report en tête).</summary>
    public sealed class LigneConsultation
    {
        public int EcNo { get; set; }
        public string Journal { get; set; }
        public DateTime Date { get; set; }
        public string Piece { get; set; }
        public string RefPiece { get; set; }
        /// <summary>Référence de la ligne (EC_Reference), vide si la base n'a pas cette colonne.</summary>
        public string Reference { get; set; }
        public string CompteGeneral { get; set; }
        public string CompteTiers { get; set; }
        /// <summary>L'autre compte de la ligne : le tiers pour un compte général, le compte collectif pour un tiers.</summary>
        public string CompteAssocie { get; set; }
        public string Libelle { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public DateTime? Echeance { get; set; }
        public string Lettrage { get; set; }
        public bool EstLettree { get; set; }
        /// <summary>Code de pointage (EC_Pointage).</summary>
        public string Pointage { get; set; }
        public bool EstPointee { get; set; }
        /// <summary>Ligne de report à nouveau calculée (pas une écriture de la base).</summary>
        public bool EstReport { get; set; }

        /// <summary>Solde cumulé après cette ligne (positif = débiteur).</summary>
        public decimal Solde { get; set; }

        public decimal? DebitAffiche { get { return Debit == 0 ? (decimal?)null : Debit; } }
        public decimal? CreditAffiche { get { return Credit == 0 ? (decimal?)null : Credit; } }
        public string SoldeLibelle { get { return Montants.AvecSens(Solde); } }
    }

    /// <summary>Résultat d'une consultation : report, lignes avec solde progressif et totaux.</summary>
    public sealed class ResultatConsultation
    {
        public NatureCompte Nature { get; set; }
        public string Compte { get; set; }
        public string Intitule { get; set; }
        public DateTime Du { get; set; }
        public DateTime Au { get; set; }
        public FiltreLettrage Lettrage { get; set; }

        /// <summary>Début de la période de report (début d'exercice), null si pas de report.</summary>
        public DateTime? DebutReport { get; set; }
        public decimal ReportDebit { get; set; }
        public decimal ReportCredit { get; set; }
        public decimal ReportSolde { get { return ReportDebit - ReportCredit; } }

        /// <summary>Lignes de la période, sans la ligne de report.</summary>
        public List<LigneConsultation> Lignes { get; } = new List<LigneConsultation>();

        public decimal MouvementsDebit { get; set; }
        public decimal MouvementsCredit { get; set; }
        public decimal SoldeFinal { get; set; }
        public int NombreNonLettrees { get; set; }

        /// <summary>Colonnes de lettrage présentes dans la base.</summary>
        public bool LettrageDisponible { get; set; }
        /// <summary>Colonnes de pointage présentes dans la base.</summary>
        public bool PointageDisponible { get; set; }

        /// <summary>
        /// Mouvements du compte mois par mois, du début de l'exercice (ou de la période) jusqu'à la fin de la période,
        /// toutes écritures confondues : ils ne dépendent pas du type d'écriture choisi.
        /// </summary>
        public List<CumulMensuel> Cumuls { get; } = new List<CumulMensuel>();
        public decimal CumulsDebit { get; set; }
        public decimal CumulsCredit { get; set; }
        /// <summary>Solde réel du compte à la fin de la période, quel que soit le type d'écriture affiché.</summary>
        public decimal SoldeCompte { get { return CumulsDebit - CumulsCredit; } }

        /// <summary>Première lettre libre sur ce compte (A, B… Z, AA…), vide si le lettrage est absent de la base.</summary>
        public string ProchaineLettre { get; set; }

        /// <summary>Lignes à afficher ou exporter : report en tête (s'il existe), puis les écritures.</summary>
        public IEnumerable<LigneConsultation> LignesAvecReport
        {
            get
            {
                if (DebutReport.HasValue)
                    yield return new LigneConsultation
                    {
                        EstReport = true,
                        Date = Du,
                        Libelle = "Report au " + Du.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                                  + " (depuis le " + DebutReport.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ")",
                        Debit = ReportDebit,
                        Credit = ReportCredit,
                        Solde = ReportSolde
                    };
                foreach (var l in Lignes) yield return l;
            }
        }
    }

    /// <summary>Mouvements d'un mois sur le compte consulté, avec le solde cumulé depuis le début.</summary>
    public sealed class CumulMensuel
    {
        /// <summary>Premier jour du mois.</summary>
        public DateTime Mois { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public decimal Solde { get { return Debit - Credit; } }
        public decimal SoldeCumule { get; set; }

        public string Libelle
        {
            get
            {
                var t = Mois.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"));
                return t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t.Substring(1);
            }
        }

        public decimal? DebitAffiche { get { return Debit == 0 ? (decimal?)null : Debit; } }
        public decimal? CreditAffiche { get { return Credit == 0 ? (decimal?)null : Credit; } }
        public string SoldeLibelle { get { return Montants.AvecSens(Solde); } }
        public string SoldeCumuleLibelle { get { return Montants.AvecSens(SoldeCumule); } }
    }

    /// <summary>Total des lignes cochées pour le lettrage.</summary>
    public sealed class SelectionLettrage
    {
        public int Nombre { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public decimal Solde { get { return Debit - Credit; } }
        /// <summary>Au moins deux lignes et débit = crédit : la sélection peut être lettrée.</summary>
        public bool EstEquilibree { get { return Nombre >= 2 && Debit == Credit; } }
    }

    public static class Montants
    {
        /// <summary>« 1 250 000 D », « 300 C » ou « 0 ».</summary>
        public static string AvecSens(decimal solde)
        {
            if (solde == 0) return "0";
            return Math.Abs(solde).ToString("#,##0.##", CultureInfo.CurrentCulture) + (solde > 0 ? " D" : " C");
        }
    }
}

namespace SigmaCompta.Core.Modeles
{
    /// <summary>Entrée de la liste des comptes à consulter.</summary>
    public sealed class ElementCompte
    {
        public ElementCompte(NatureCompte nature, string numero, string intitule)
        {
            Nature = nature;
            Numero = numero ?? "";
            Intitule = intitule ?? "";
        }

        public NatureCompte Nature { get; private set; }
        public string Numero { get; private set; }
        public string Intitule { get; private set; }

        /// <summary>Recherche sur le début du n° de compte ou n'importe où dans l'intitulé.</summary>
        public bool Correspond(string recherche)
        {
            if (string.IsNullOrWhiteSpace(recherche)) return true;
            var r = recherche.Trim();
            return Numero.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                || Intitule.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public override string ToString() { return Numero + " — " + Intitule; }
    }
}
