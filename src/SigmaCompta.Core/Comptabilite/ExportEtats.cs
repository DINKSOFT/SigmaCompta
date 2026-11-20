using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Comptabilite
{
    /// <summary>Export Excel des états comptables : colonnes propres à chaque état, lignes de total en gras.</summary>
    public static class ExportEtats
    {
        public static string[] Entetes(TypeEtat etat)
        {
            switch (etat)
            {
                case TypeEtat.Balance:
                    return new[] { "Compte", "Intitulé", "Ouverture débit", "Ouverture crédit", "Mouvements débit", "Mouvements crédit",
                                   "Solde débiteur", "Solde créditeur" };
                case TypeEtat.GrandLivre:
                    return new[] { "Compte", "Date", "Journal", "Pièce", "Référence", "Libellé", "Compte associé", "Débit", "Crédit",
                                   "Solde", "Sens", "Lettrage", "Échéance" };
                case TypeEtat.Journal:
                    return new[] { "Journal", "Date", "Pièce", "Référence", "Compte général", "Compte tiers", "Libellé", "Débit", "Crédit", "Échéance" };
                default:
                    return new[] { "Période", "Journal", "Intitulé", "Débit", "Crédit" };
            }
        }

        public static double[] Largeurs(TypeEtat etat)
        {
            switch (etat)
            {
                case TypeEtat.Balance: return new double[] { 14, 44, 16, 16, 16, 16, 16, 16 };
                case TypeEtat.GrandLivre: return new double[] { 14, 11, 9, 11, 13, 46, 14, 15, 15, 16, 6, 9, 11 };
                case TypeEtat.Journal: return new double[] { 9, 11, 11, 13, 14, 12, 46, 15, 15, 11 };
                default: return new double[] { 18, 10, 40, 16, 16 };
            }
        }

        private static object Nombre(decimal? v)
        {
            return v.HasValue ? (object)v.Value : null;
        }

        private static object Date(DateTime? d)
        {
            return d.HasValue ? (object)d.Value : null;
        }

        public static object[] Ligne(TypeEtat etat, LigneEtat l)
        {
            bool detail = l.Genre == GenreLigne.Detail;
            switch (etat)
            {
                case TypeEtat.Balance:
                    return new object[]
                    {
                        detail ? l.Compte : null, l.Libelle,
                        Nombre(l.OuvertureDebit), Nombre(l.OuvertureCredit), Nombre(l.MouvementDebit), Nombre(l.MouvementCredit),
                        Nombre(l.SoldeDebit), Nombre(l.SoldeCredit)
                    };
                case TypeEtat.GrandLivre:
                    return new object[]
                    {
                        l.Compte, l.Genre == GenreLigne.Entete ? null : Date(l.Date), l.Journal, l.Piece, l.RefPiece, l.Libelle, l.CompteAssocie,
                        Nombre(l.Debit), Nombre(l.Credit), Nombre(l.Solde),
                        l.Solde.HasValue ? (l.Solde.Value > 0 ? "D" : l.Solde.Value < 0 ? "C" : "") : null,
                        l.Lettrage, Date(l.Echeance)
                    };
                case TypeEtat.Journal:
                    return new object[]
                    {
                        l.Journal, Date(l.Date), l.Piece, l.RefPiece, l.Compte, l.CompteTiers, l.Libelle, Nombre(l.Debit), Nombre(l.Credit), Date(l.Echeance)
                    };
                default:
                    return new object[] { l.Periode, l.Journal, l.Libelle, Nombre(l.Debit), Nombre(l.Credit) };
            }
        }

        /// <summary>« Balance_generale_20260101_20261231.xlsx ».</summary>
        public static string NomFichier(ResultatEtat r)
        {
            var sb = new StringBuilder();
            foreach (var ch in (r.Titre ?? "Etat").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }
            var p = r.Parametres;
            var nom = sb.ToString();
            while (nom.Contains("__")) nom = nom.Replace("__", "_");
            return nom.Trim('_') + "_" + p.Du.ToString("yyyyMMdd") + "_" + p.Au.ToString("yyyyMMdd") + ".xlsx";
        }

        public static void Ecrire(string chemin, ResultatEtat r)
        {
            var lignes = r.Lignes;
            var feuille = r.Etat == TypeEtat.Balance ? "Balance" : r.Etat == TypeEtat.GrandLivre ? "Grand livre"
                        : r.Etat == TypeEtat.Journal ? "Journaux" : "Centralisateur";
            if (r.Parametres != null && r.Parametres.Nature == NatureCompte.Tiers && (r.Etat == TypeEtat.Balance || r.Etat == TypeEtat.GrandLivre))
                feuille += " tiers";
            EcrivainXlsx.Ecrire(chemin, feuille, Entetes(r.Etat), lignes.Select(l => Ligne(r.Etat, l)), Largeurs(r.Etat),
                                i => i >= 0 && i < lignes.Count && lignes[i].Genre != GenreLigne.Detail);
        }
    }
}
