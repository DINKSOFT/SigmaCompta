using System.Linq;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Comptabilite
{
    /// <summary>Export Excel d'une consultation de compte (report, lignes, solde progressif, totaux).</summary>
    public static class ExportConsultation
    {
        public static readonly string[] Entetes =
        {
            "Date", "Journal", "Pièce", "N° facture", "Référence", "Compte général", "Compte tiers", "Libellé",
            "Débit", "Crédit", "Solde", "Lettrage", "Pointage", "Échéance", "EC_No"
        };

        public static object[] Ligne(LigneConsultation l)
        {
            return new object[]
            {
                l.EstReport ? null : (object)l.Date, l.Journal, l.Piece, l.RefPiece, l.Reference, l.CompteGeneral, l.CompteTiers, l.Libelle,
                l.DebitAffiche, l.CreditAffiche, l.Solde, l.Lettrage, l.Pointage, (object)l.Echeance, l.EstReport ? null : (object)l.EcNo
            };
        }

        public static void Ecrire(string chemin, ResultatConsultation r)
        {
            var lignes = r.LignesAvecReport.Select(Ligne).ToList();
            lignes.Add(new object[] { null, null, null, null, null, null, null, "Total des mouvements", r.MouvementsDebit, r.MouvementsCredit, null, null, null, null, null });
            lignes.Add(new object[] { null, null, null, null, null, null, null, "Solde au " + r.Au.ToString("dd/MM/yyyy"), null, null, r.SoldeFinal, Montants.AvecSens(r.SoldeFinal), null, null, null });
            var feuille = (r.Nature == NatureCompte.General ? "Compte " : "Tiers ") + r.Compte;
            EcrivainXlsx.Ecrire(chemin, feuille, Entetes, lignes,
                new double[] { 12, 9, 12, 14, 14, 14, 12, 42, 15, 15, 16, 9, 9, 12, 9 });
        }
    }
}
