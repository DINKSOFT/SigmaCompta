using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Import
{
    /// <summary>Exports Excel/CSV : rapport par pièce, détail des lignes, modèle de fichier de données.</summary>
    public static class ExportRapport
    {
        private static void Ecrire(string chemin, string feuille, IList<string> entetes, IEnumerable<object[]> lignes, IList<double> largeurs = null)
        {
            if (string.Equals(Path.GetExtension(chemin), ".csv", StringComparison.OrdinalIgnoreCase))
                LecteurCsv.Ecrire(chemin, entetes, lignes);
            else
                EcrivainXlsx.Ecrire(chemin, feuille, entetes, lignes, largeurs);
        }

        public static void ExporterPieces(string chemin, IEnumerable<PieceImport> pieces)
        {
            var entetes = new[] { "Statut", "N°", "Journal", "Date", "Pièce (fichier)", "Pièce attribuée", "EC_No", "Lignes", "Débit", "Crédit", "Écart", "Message" };
            var lignes = pieces.Select(p => new object[]
            {
                ServiceImport.LibelleStatut(p.Statut), p.Numero, p.Journal, (object)p.Date, p.PieceFournie, p.NumeroAttribue, p.PlageEcNo,
                p.NombreLignes, p.TotalDebit, p.TotalCredit, p.Ecart, p.Resume
            });
            Ecrire(chemin, "Rapport", entetes, lignes, new double[] { 14, 6, 9, 12, 16, 16, 16, 8, 16, 16, 12, 90 });
        }

        public static void ExporterLignes(string chemin, IEnumerable<PieceImport> pieces, bool seulementAnomalies)
        {
            var entetes = new[] { "Pièce n°", "Statut pièce", "Ligne fichier", "Journal", "Date", "N° pièce", "Compte", "Tiers", "Libellé", "Débit", "Crédit", "EC_No", "Anomalies" };
            var lignes = new List<object[]>();
            foreach (var p in pieces)
            {
                foreach (var l in p.Lignes)
                {
                    var anomalies = p.Anomalies.Concat(l.Anomalies).Select(a => a.Message).ToList();
                    if (!string.IsNullOrEmpty(p.MessageServeur) && (p.Statut == StatutPiece.Echec)) anomalies.Add(p.MessageServeur);
                    if (seulementAnomalies && anomalies.Count == 0) continue;
                    lignes.Add(new object[]
                    {
                        p.Numero, ServiceImport.LibelleStatut(p.Statut), l.LigneFichier, l.Journal, (object)l.Date, l.Piece, l.CompteGeneral, l.CompteTiers,
                        l.Libelle, l.Debit, l.Credit, l.EcNo, string.Join(" · ", anomalies)
                    });
                }
            }
            Ecrire(chemin, seulementAnomalies ? "Anomalies" : "Lignes", entetes, lignes, new double[] { 9, 14, 11, 9, 12, 14, 12, 12, 40, 15, 15, 9, 90 });
        }

        /// <summary>Génère un fichier de données vierge conforme au profil (en-têtes + 2 lignes d'exemple).</summary>
        public static void GenererModele(string chemin, ProfilImport profil)
        {
            var c = profil.Colonnes;
            var champs = new[]
            {
                ChampsImport.Journal, ChampsImport.Date, ChampsImport.Piece, ChampsImport.Reference, ChampsImport.CompteGeneral,
                ChampsImport.CompteTiers, ChampsImport.Libelle, ChampsImport.Debit, ChampsImport.Credit
            };
            var entetes = champs.Select(ch => ChampsImport.NomPrincipal(c, ch)).ToList();
            var date = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var exemples = new List<object[]>
            {
                new object[] { "OD", date, "", "PAIE" + date.ToString("yyMM"), "6611000", null, "Salaires " + date.ToString("MMMM yyyy"), 1500000m, null },
                new object[] { "OD", date, "", "PAIE" + date.ToString("yyMM"), "4220000", null, "Salaires " + date.ToString("MMMM yyyy"), null, 1500000m },
            };
            Ecrire(chemin, "Ecritures", entetes, exemples, new double[] { 10, 12, 12, 16, 14, 14, 40, 16, 16 });
        }
    }
}
