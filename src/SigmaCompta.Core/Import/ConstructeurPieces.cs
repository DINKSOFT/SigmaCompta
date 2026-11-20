using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Import
{
    public static class ChampsImport
    {
        public const string Journal = "Journal";
        public const string Date = "Date";
        public const string Piece = "Piece";
        public const string Reference = "Reference";
        public const string RefPiece = "RefPiece";
        public const string CompteGeneral = "CompteGeneral";
        public const string CompteTiers = "CompteTiers";
        public const string Libelle = "Libelle";
        public const string Debit = "Debit";
        public const string Credit = "Credit";
        public const string Montant = "Montant";
        public const string Sens = "Sens";
        public const string Echeance = "Echeance";

        /// <summary>Champ, libellé affiché, accès au paramètre du profil.</summary>
        public static readonly string[][] Definitions =
        {
            new[] { Journal, "Journal" },
            new[] { Date, "Date" },
            new[] { Piece, "N° de pièce" },
            new[] { Reference, "Référence" },
            new[] { RefPiece, "N° facture (EC_RefPiece)" },
            new[] { CompteGeneral, "Compte général" },
            new[] { CompteTiers, "Compte tiers" },
            new[] { Libelle, "Libellé" },
            new[] { Debit, "Débit" },
            new[] { Credit, "Crédit" },
            new[] { Montant, "Montant (alternative)" },
            new[] { Sens, "Sens D/C (alternative)" },
            new[] { Echeance, "Échéance" },
        };

        public static string Lire(ColonnesProfil c, string champ)
        {
            switch (champ)
            {
                case Journal: return c.Journal;
                case Date: return c.Date;
                case Piece: return c.Piece;
                case Reference: return c.Reference;
                case RefPiece: return c.RefPiece;
                case CompteGeneral: return c.CompteGeneral;
                case CompteTiers: return c.CompteTiers;
                case Libelle: return c.Libelle;
                case Debit: return c.Debit;
                case Credit: return c.Credit;
                case Montant: return c.Montant;
                case Sens: return c.Sens;
                case Echeance: return c.Echeance;
                default: return "";
            }
        }

        public static void Ecrire(ColonnesProfil c, string champ, string valeur)
        {
            switch (champ)
            {
                case Journal: c.Journal = valeur; break;
                case Date: c.Date = valeur; break;
                case Piece: c.Piece = valeur; break;
                case Reference: c.Reference = valeur; break;
                case RefPiece: c.RefPiece = valeur; break;
                case CompteGeneral: c.CompteGeneral = valeur; break;
                case CompteTiers: c.CompteTiers = valeur; break;
                case Libelle: c.Libelle = valeur; break;
                case Debit: c.Debit = valeur; break;
                case Credit: c.Credit = valeur; break;
                case Montant: c.Montant = valeur; break;
                case Sens: c.Sens = valeur; break;
                case Echeance: c.Echeance = valeur; break;
            }
        }

        /// <summary>Premier nom candidat d'un champ (utilisé pour générer les modèles).</summary>
        public static string NomPrincipal(ColonnesProfil c, string champ)
        {
            var v = Lire(c, champ) ?? "";
            var premier = v.Split('|').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith("["));
            return premier ?? champ;
        }
    }

    public sealed class ResultatAnalyse
    {
        public List<PieceImport> Pieces { get; } = new List<PieceImport>();
        public List<CorrespondanceColonne> Correspondances { get; } = new List<CorrespondanceColonne>();
        public List<Anomalie> AnomaliesGlobales { get; } = new List<Anomalie>();
        public int LignesLues { get; set; }
        public int LignesIgnorees { get; set; }
        public bool Bloquee { get { return AnomaliesGlobales.Any(a => a.EstErreur); } }
    }

    /// <summary>Transforme les lignes du fichier en pièces selon le fichier de paramètres.</summary>
    public static class ConstructeurPieces
    {
        private static readonly Regex LettreColonne = new Regex(@"^\[\s*([A-Za-z]{1,3})\s*\]$");

        public static List<CorrespondanceColonne> ResoudreColonnes(ProfilImport profil, IList<string> entetes)
        {
            var normalises = entetes.Select(Analyseurs.NormaliserEntete).ToList();
            var utilisees = new HashSet<int>();
            var res = new List<CorrespondanceColonne>();
            foreach (var def in ChampsImport.Definitions)
            {
                var candidats = ChampsImport.Lire(profil.Colonnes, def[0]) ?? "";
                var cc = new CorrespondanceColonne { Champ = def[0], Libelle = def[1], Candidats = candidats };
                foreach (var brut in candidats.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0))
                {
                    int idx = -1;
                    var m = LettreColonne.Match(brut);
                    if (m.Success)
                    {
                        idx = Colonnes.Index(m.Groups[1].Value);
                        if (idx >= entetes.Count) idx = -1;
                    }
                    else
                    {
                        var n = Analyseurs.NormaliserEntete(brut);
                        for (int i = 0; i < normalises.Count; i++)
                            if (normalises[i].Length > 0 && normalises[i] == n && !utilisees.Contains(i)) { idx = i; break; }
                    }
                    if (idx >= 0 && !utilisees.Contains(idx))
                    {
                        cc.IndexColonne = idx;
                        cc.EnteteTrouvee = string.IsNullOrEmpty(entetes[idx]) ? Colonnes.Lettre(idx) : entetes[idx];
                        utilisees.Add(idx);
                        break;
                    }
                }
                res.Add(cc);
            }

            Func<string, CorrespondanceColonne> c = ch => res.First(x => x.Champ == ch);
            c(ChampsImport.CompteGeneral).Obligatoire = true;
            c(ChampsImport.Date).Obligatoire = true;
            c(ChampsImport.Journal).Obligatoire = string.IsNullOrWhiteSpace(profil.Defauts.Journal);
            bool debitCredit = c(ChampsImport.Debit).Trouvee || c(ChampsImport.Credit).Trouvee;
            c(ChampsImport.Debit).Obligatoire = !c(ChampsImport.Montant).Trouvee;
            c(ChampsImport.Credit).Obligatoire = !c(ChampsImport.Montant).Trouvee;
            c(ChampsImport.Montant).Obligatoire = !debitCredit;
            return res;
        }

        public static ResultatAnalyse Construire(TableSource source, ProfilImport profil)
        {
            profil.Normaliser();
            var res = new ResultatAnalyse { LignesLues = source.Lignes.Count };
            res.Correspondances.AddRange(ResoudreColonnes(profil, source.Entetes));

            foreach (var cc in res.Correspondances.Where(x => x.Obligatoire && !x.Trouvee))
                res.AnomaliesGlobales.Add(new Anomalie
                {
                    Niveau = NiveauAnomalie.Erreur,
                    Message = "Colonne « " + cc.Libelle + " » introuvable (noms recherchés : " + cc.Candidats + ")."
                });
            if (res.Bloquee) return res;

            var col = res.Correspondances.ToDictionary(x => x.Champ, x => x.IndexColonne);
            var lignes = new List<LigneImport>();
            for (int i = 0; i < source.Lignes.Count; i++)
            {
                var l = LireLigne(source, i, col, profil);
                if (l == null) { res.LignesIgnorees++; continue; }
                lignes.Add(l);
            }

            Regrouper(lignes, profil, res.Pieces);

            int n = 1;
            foreach (var p in res.Pieces)
            {
                p.Numero = n++;
                int k = 1;
                foreach (var l in p.Lignes) l.NumLigne = k++;
                var premiere = p.Lignes[0];
                p.Journal = premiere.Journal;
                p.Date = premiere.Date;
                p.PieceFournie = profil.Options.NumerotationAutomatique ? "" : (premiere.Piece ?? "").Trim();
                p.RefPiece = premiere.RefPiece;

                var dates = p.Lignes.Where(x => x.Date.HasValue).Select(x => x.Date.Value.Date).Distinct().Count();
                if (dates > 1)
                    p.Anomalies.Add(new Anomalie { Niveau = NiveauAnomalie.Erreur, Message = "La pièce regroupe " + dates + " dates différentes : une pièce comptable n'a qu'une date." });
                var pieces = p.Lignes.Select(x => (x.Piece ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                if (pieces > 1 && !profil.Options.NumerotationAutomatique)
                    p.Anomalies.Add(new Anomalie { Niveau = NiveauAnomalie.Avertissement, Message = "Plusieurs n° de pièce dans le groupe : le premier (« " + p.PieceFournie + " ») sera utilisé." });
                p.ActualiserStatutLocal();
            }
            return res;
        }

        private static string Val(TableSource s, int ligne, Dictionary<string, int> col, string champ)
        {
            int idx;
            if (!col.TryGetValue(champ, out idx) || idx < 0) return null;
            var v = s.Valeur(ligne, idx);
            return v == null ? null : v.Trim();
        }

        private static LigneImport LireLigne(TableSource s, int i, Dictionary<string, int> col, ProfilImport profil)
        {
            var l = new LigneImport { LigneFichier = s.NumerosLigne.Count > i ? s.NumerosLigne[i] : i + 2 };
            Action<string> erreur = m => l.Anomalies.Add(new Anomalie { Niveau = NiveauAnomalie.Erreur, Message = m, LigneFichier = l.LigneFichier });
            Action<string> avert = m => l.Anomalies.Add(new Anomalie { Niveau = NiveauAnomalie.Avertissement, Message = m, LigneFichier = l.LigneFichier });

            // Montants d'abord : une ligne sans montant peut être ignorée (ligne de total, ligne vide…).
            decimal debit = 0, credit = 0;
            var sepDec = profil.Source.SeparateurDecimal;
            if (col[ChampsImport.Debit] >= 0 || col[ChampsImport.Credit] >= 0)
            {
                var td = Val(s, i, col, ChampsImport.Debit);
                var tc = Val(s, i, col, ChampsImport.Credit);
                if (!Analyseurs.EssayerMontant(td, sepDec, out debit)) erreur("Débit illisible : « " + td + " ».");
                if (!Analyseurs.EssayerMontant(tc, sepDec, out credit)) erreur("Crédit illisible : « " + tc + " ».");
            }
            else
            {
                var tm = Val(s, i, col, ChampsImport.Montant);
                decimal m;
                if (!Analyseurs.EssayerMontant(tm, sepDec, out m)) erreur("Montant illisible : « " + tm + " ».");
                var sens = (Val(s, i, col, ChampsImport.Sens) ?? "").Trim().ToUpperInvariant();
                if (sens.Length == 0) { if (m >= 0) debit = m; else credit = -m; }
                else if (sens == "D" || sens == "0" || sens.StartsWith("DEB") || sens.StartsWith("DÉB")) debit = m;
                else if (sens == "C" || sens == "1" || sens.StartsWith("CRE") || sens.StartsWith("CRÉ")) credit = m;
                else erreur("Sens « " + sens + " » non reconnu (attendu D/C ou 0/1).");
            }

            if (profil.Options.InverserMontantsNegatifs)
            {
                if (debit < 0) { credit += -debit; debit = 0; }
                if (credit < 0) { debit += -credit; credit = 0; }
            }
            l.Debit = debit;
            l.Credit = credit;

            l.CompteGeneral = Analyseurs.CompleterCompte(Val(s, i, col, ChampsImport.CompteGeneral), profil.Defauts.LongueurComptes);
            if (debit == 0 && credit == 0 && !l.Anomalies.Any() && profil.Options.IgnorerLignesSansMontant)
                return null;

            l.Journal = Val(s, i, col, ChampsImport.Journal);
            if (string.IsNullOrEmpty(l.Journal)) l.Journal = (profil.Defauts.Journal ?? "").Trim();

            var td2 = Val(s, i, col, ChampsImport.Date);
            DateTime d;
            if (string.IsNullOrEmpty(td2)) erreur("Date absente.");
            else if (Analyseurs.EssayerDate(td2, profil.Source.FormatsDate, out d)) l.Date = d;
            else erreur("Date illisible : « " + td2 + " ».");

            l.Piece = Val(s, i, col, ChampsImport.Piece);
            l.Reference = Val(s, i, col, ChampsImport.Reference);
            var refPiece = Val(s, i, col, ChampsImport.RefPiece);
            if (string.IsNullOrEmpty(refPiece)) refPiece = l.Reference;
            if (refPiece != null && refPiece.Length > 17) { avert("Référence tronquée à 17 caractères."); refPiece = refPiece.Substring(0, 17); }
            l.RefPiece = refPiece;

            var tiers = Val(s, i, col, ChampsImport.CompteTiers);
            l.CompteTiers = string.IsNullOrEmpty(tiers) ? null : tiers;

            var lib = Val(s, i, col, ChampsImport.Libelle);
            if (string.IsNullOrEmpty(lib)) lib = profil.Defauts.Libelle;
            if (lib != null && lib.Length > 69) { avert("Libellé tronqué à 69 caractères."); lib = lib.Substring(0, 69); }
            l.Libelle = lib ?? "";

            var te = Val(s, i, col, ChampsImport.Echeance);
            if (!string.IsNullOrEmpty(te))
            {
                DateTime e;
                if (Analyseurs.EssayerDate(te, profil.Source.FormatsDate, out e)) l.Echeance = e;
                else avert("Échéance illisible (« " + te + " ») : ignorée.");
            }
            return l;
        }

        private static string Cle(LigneImport l, string mode)
        {
            var j = (l.Journal ?? "").ToUpperInvariant();
            var d = l.Date.HasValue ? l.Date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "?";
            var p = (l.Piece ?? "").Trim().ToUpperInvariant();
            var r = (l.Reference ?? l.RefPiece ?? "").Trim().ToUpperInvariant();
            switch (mode)
            {
                case ModesRegroupement.JournalPiece: return j + "|" + p;
                case ModesRegroupement.JournalDateReference: return j + "|" + d + "|" + r;
                default: return j + "|" + d + "|" + p;
            }
        }

        private static void Regrouper(List<LigneImport> lignes, ProfilImport profil, List<PieceImport> pieces)
        {
            if (profil.Regroupement == ModesRegroupement.Equilibre)
            {
                PieceImport courante = null;
                decimal solde = 0;
                foreach (var l in lignes)
                {
                    bool nouvelle = courante == null
                        || !string.Equals(courante.Lignes[0].Journal, l.Journal, StringComparison.OrdinalIgnoreCase)
                        || courante.Lignes[0].Date != l.Date
                        || (solde == 0 && courante.Lignes.Count >= 2);
                    if (nouvelle)
                    {
                        courante = new PieceImport { Cle = "#" + (pieces.Count + 1) };
                        pieces.Add(courante);
                        solde = 0;
                    }
                    courante.Lignes.Add(l);
                    solde += l.Debit - l.Credit;
                }
                return;
            }

            var index = new Dictionary<string, PieceImport>(StringComparer.Ordinal);
            foreach (var l in lignes)
            {
                var cle = Cle(l, profil.Regroupement);
                PieceImport p;
                if (!index.TryGetValue(cle, out p))
                {
                    p = new PieceImport { Cle = cle };
                    index[cle] = p;
                    pieces.Add(p);
                }
                p.Lignes.Add(l);
            }
        }
    }
}
