using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.Core.Comptabilite
{
    /// <summary>
    /// Interrogation d'un compte général ou d'un compte tiers : report à nouveau, lignes de la période
    /// avec solde progressif, filtre de lettrage ou de pointage, cumuls mensuels, préparation du lettrage.
    /// Lecture seule : aucune écriture dans la base.
    /// </summary>
    public static class ServiceConsultation
    {
        /// <summary>Colonnes facultatives de F_ECRITUREC présentes dans la base.</summary>
        public sealed class ColonnesEcriture
        {
            public bool Lettre { get; set; }
            public bool Lettrage { get; set; }
            public bool Point { get; set; }
            public bool Pointage { get; set; }
            public bool Reference { get; set; }

            public bool LettrageDisponible { get { return Lettre || Lettrage; } }
            public bool PointageDisponible { get { return Point || Pointage; } }
        }

        /// <summary>
        /// Début de la période de report : le premier jour de l'exercice qui contient <paramref name="du"/>.
        /// Les à-nouveaux étant datés du premier jour de l'exercice, on ne remonte jamais au-delà
        /// (sinon l'exercice précédent serait compté deux fois). Null si <paramref name="du"/> est
        /// le premier jour de l'exercice ou n'appartient à aucun exercice.
        /// </summary>
        public static DateTime? DebutReport(DateTime du, IEnumerable<Exercice> exercices)
        {
            if (exercices == null) return null;
            var exo = exercices.Where(e => e.Contient(du)).OrderByDescending(e => e.Debut).FirstOrDefault();
            if (exo == null || exo.Debut.Date >= du.Date) return null;
            return exo.Debut.Date;
        }

        /// <summary>Solde progressif (départ = report), totaux de la période et nombre de lignes non lettrées.</summary>
        public static void Calculer(ResultatConsultation r)
        {
            decimal solde = r.DebutReport.HasValue ? r.ReportSolde : 0m;
            decimal debit = 0m, credit = 0m;
            int nonLettrees = 0;
            foreach (var l in r.Lignes)
            {
                solde += l.Debit - l.Credit;
                l.Solde = solde;
                debit += l.Debit;
                credit += l.Credit;
                if (!l.EstLettree) nonLettrees++;
            }
            r.MouvementsDebit = debit;
            r.MouvementsCredit = credit;
            r.SoldeFinal = solde;
            r.NombreNonLettrees = nonLettrees;
        }

        /// <summary>
        /// Cumuls mois par mois de <paramref name="debut"/> à <paramref name="au"/> : chaque mois figure,
        /// même sans mouvement, avec le solde cumulé. <paramref name="mouvements"/> : (année, mois) → débit, crédit.
        /// </summary>
        public static void CalculerCumuls(ResultatConsultation r, DateTime debut, DateTime au,
                                          IDictionary<Tuple<int, int>, Tuple<decimal, decimal>> mouvements)
        {
            r.Cumuls.Clear();
            decimal cumul = 0m, totalDebit = 0m, totalCredit = 0m;
            var mois = new DateTime(debut.Year, debut.Month, 1);
            var dernier = new DateTime(au.Year, au.Month, 1);
            while (mois <= dernier)
            {
                Tuple<decimal, decimal> m;
                decimal d = 0m, c = 0m;
                if (mouvements != null && mouvements.TryGetValue(Tuple.Create(mois.Year, mois.Month), out m))
                {
                    d = m.Item1;
                    c = m.Item2;
                }
                cumul += d - c;
                totalDebit += d;
                totalCredit += c;
                r.Cumuls.Add(new CumulMensuel { Mois = mois, Debit = d, Credit = c, SoldeCumule = cumul });
                mois = mois.AddMonths(1);
            }
            r.CumulsDebit = totalDebit;
            r.CumulsCredit = totalCredit;
        }

        /// <summary>Total des lignes cochées (la ligne de report n'est jamais comptée).</summary>
        public static SelectionLettrage TotaliserSelection(IEnumerable<LigneConsultation> lignes)
        {
            var s = new SelectionLettrage();
            if (lignes == null) return s;
            foreach (var l in lignes)
            {
                if (l == null || l.EstReport) continue;
                s.Nombre++;
                s.Debit += l.Debit;
                s.Credit += l.Credit;
            }
            return s;
        }

        /// <summary>
        /// Lettre qui suit la plus grande déjà utilisée : A, B… Z, AA, AB… ZZ, AAA… ZZZ (trois caractères au plus).
        /// Les codes sont comparés sans tenir compte de la casse ; ceux qui ne sont pas faits de lettres sont ignorés.
        /// Renvoie "" quand ZZZ est atteint.
        /// </summary>
        public static string LettreSuivante(IEnumerable<string> codes)
        {
            string max = null;
            if (codes != null)
                foreach (var brut in codes)
                {
                    var code = (brut ?? "").Trim().ToUpperInvariant();
                    if (code.Length == 0 || code.Length > 3 || !code.All(ch => ch >= 'A' && ch <= 'Z')) continue;
                    if (max == null || code.Length > max.Length || (code.Length == max.Length && string.CompareOrdinal(code, max) > 0))
                        max = code;
                }
            if (max == null) return "A";

            var lettres = max.ToCharArray();
            int i = lettres.Length - 1;
            while (i >= 0 && lettres[i] == 'Z')
            {
                lettres[i] = 'A';
                i--;
            }
            if (i >= 0)
            {
                lettres[i]++;
                return new string(lettres);
            }
            return max.Length >= 3 ? "" : new string('A', max.Length + 1);
        }

        /// <summary>
        /// Recherche « par mots » dans la liste : chaque mot doit se trouver dans l'un des textes de la ligne
        /// (journal, pièce, facture, référence, comptes, libellé, lettrage, pointage, dates) ou dans ses montants.
        /// </summary>
        public static bool Correspond(LigneConsultation l, string recherche)
        {
            if (string.IsNullOrWhiteSpace(recherche)) return true;
            if (l == null || l.EstReport) return false;
            var inv = CultureInfo.InvariantCulture;
            var textes = new[]
            {
                l.Journal, l.Piece, l.RefPiece, l.Reference, l.CompteGeneral, l.CompteTiers, l.Libelle, l.Lettrage, l.Pointage,
                l.Date.ToString("dd/MM/yyyy", inv), l.Date.ToString("ddMMyy", inv),
                l.Echeance.HasValue ? l.Echeance.Value.ToString("dd/MM/yyyy", inv) : null,
                l.Debit != 0 ? l.Debit.ToString("0.##", inv) : null,
                l.Credit != 0 ? l.Credit.ToString("0.##", inv) : null
            };
            foreach (var brut in recherche.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Un montant tapé avec une virgule décimale (« 4500,5 ») se compare au montant stocké (« 4500.5 »).
                var mot = brut.Trim();
                var montant = mot.Replace(" ", "").Replace(" ", "").Replace(',', '.');
                bool trouve = textes.Any(t => !string.IsNullOrEmpty(t) &&
                                              (t.IndexOf(mot, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                               t.IndexOf(montant, StringComparison.OrdinalIgnoreCase) >= 0));
                if (!trouve) return false;
            }
            return true;
        }

        /// <summary>Libellé du type d'écriture (critères affichés, titres d'impression).</summary>
        public static string LibelleFiltre(FiltreLettrage f)
        {
            switch (f)
            {
                case FiltreLettrage.NonLettrees: return "Écritures non lettrées";
                case FiltreLettrage.Lettrees: return "Écritures lettrées";
                case FiltreLettrage.NonPointees: return "Écritures non pointées";
                case FiltreLettrage.Pointees: return "Écritures pointées";
                default: return "Toutes les écritures";
            }
        }

        /// <summary>Comptes proposés : comptes généraux imputables (hors comptes de total) ou tous les tiers.</summary>
        public static List<ElementCompte> ComptesConsultables(Referentiel r, NatureCompte nature)
        {
            if (r == null) return new List<ElementCompte>();
            IEnumerable<ElementCompte> liste = nature == NatureCompte.General
                ? r.Comptes.Values.Where(c => c.EstImputable).Select(c => new ElementCompte(nature, c.Numero, c.Intitule))
                : r.Tiers.Values.Select(t => new ElementCompte(nature, t.Numero, t.Intitule));
            return liste.OrderBy(e => e.Numero, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Compte désigné par ce qui est tapé : n° exact, sinon premier n° qui commence ainsi,
        /// sinon premier compte dont l'intitulé contient le texte. Null si rien ne correspond.
        /// </summary>
        public static ElementCompte Trouver(IList<ElementCompte> comptes, string texte)
        {
            if (comptes == null || string.IsNullOrWhiteSpace(texte)) return null;
            var t = texte.Trim();
            // Le texte peut être l'affichage complet « 571100 — Caisse » repris d'une liste.
            int tiret = t.IndexOf(" — ", StringComparison.Ordinal);
            if (tiret > 0) t = t.Substring(0, tiret).Trim();
            return comptes.FirstOrDefault(c => string.Equals(c.Numero, t, StringComparison.OrdinalIgnoreCase))
                ?? comptes.FirstOrDefault(c => c.Numero.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                ?? comptes.FirstOrDefault(c => c.Intitule.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string Intitule(Referentiel r, NatureCompte nature, string compte)
        {
            if (r == null || string.IsNullOrWhiteSpace(compte)) return "";
            if (nature == NatureCompte.General)
            {
                CompteGeneral g;
                return r.Comptes.TryGetValue(compte.Trim(), out g) ? g.Intitule : "";
            }
            Tiers t;
            return r.Tiers.TryGetValue(compte.Trim(), out t) ? t.Intitule : "";
        }

        private static string Colonne(NatureCompte nature)
        {
            return nature == NatureCompte.General ? "CG_Num" : "CT_Num";
        }

        /// <summary>Lignes de la période (lettrage seul). Paramètres : @compte, @du, @auExclu.</summary>
        public static string RequeteLignes(NatureCompte nature, FiltreLettrage lettrage, bool colonneLettre, bool colonneLettrage)
        {
            return RequeteLignes(nature, lettrage, new ColonnesEcriture { Lettre = colonneLettre, Lettrage = colonneLettrage });
        }

        /// <summary>Lignes de la période. Paramètres : @compte, @du, @auExclu.</summary>
        public static string RequeteLignes(NatureCompte nature, FiltreLettrage filtre, ColonnesEcriture col)
        {
            col = col ?? new ColonnesEcriture();
            var sb = new StringBuilder();
            sb.Append("SELECT e.EC_No, e.JO_Num, " + SqlOutils.DateEcriture + " AS EC_Date, e.EC_Piece, e.EC_RefPiece, e.CG_Num, e.CT_Num, e.EC_Intitule, ")
              .Append("e.EC_Sens, e.EC_Montant, e.EC_Echeance, ")
              .Append(col.Reference ? "e.EC_Reference" : "CAST(NULL AS varchar(17)) AS EC_Reference").Append(", ")
              .Append(col.Lettrage ? "e.EC_Lettrage" : "CAST(NULL AS varchar(3)) AS EC_Lettrage").Append(", ")
              .Append(col.Lettre ? "e.EC_Lettre" : "CAST(NULL AS smallint) AS EC_Lettre").Append(", ")
              .Append(col.Pointage ? "e.EC_Pointage" : "CAST(NULL AS varchar(3)) AS EC_Pointage").Append(", ")
              .Append(col.Point ? "e.EC_Point" : "CAST(NULL AS smallint) AS EC_Point")
              .Append(" FROM dbo.F_ECRITUREC e WHERE e.").Append(Colonne(nature))
              .Append(" = @compte AND " + SqlOutils.DateEcriture + " >= @du AND " + SqlOutils.DateEcriture + " < @auExclu");
            var condition = Condition(filtre, col);
            if (condition != null) sb.Append(" AND ").Append(condition);
            sb.Append(" ORDER BY " + SqlOutils.DateEcriture + ", e.JO_Num, e.EC_Piece, e.EC_No");
            return sb.ToString();
        }

        /// <summary>Cumuls du début d'exercice jusqu'à la veille de la période. Paramètres : @compte, @debut, @du.</summary>
        public static string RequeteReport(NatureCompte nature)
        {
            return "SELECT SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit " +
                   "FROM dbo.F_ECRITUREC e WHERE e." + Colonne(nature) + " = @compte AND " + SqlOutils.DateEcriture + " >= @debut AND " + SqlOutils.DateEcriture + " < @du";
        }

        /// <summary>Mouvements mois par mois, toutes écritures. Paramètres : @compte, @debut, @auExclu.</summary>
        public static string RequeteCumuls(NatureCompte nature)
        {
            var d = SqlOutils.DateEcriture;
            return "SELECT YEAR(" + d + ") AS Annee, MONTH(" + d + ") AS Mois, " +
                   "SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit " +
                   "FROM dbo.F_ECRITUREC e WHERE e." + Colonne(nature) + " = @compte AND " + d + " >= @debut AND " + d + " < @auExclu " +
                   "GROUP BY YEAR(" + d + "), MONTH(" + d + ") ORDER BY Annee, Mois";
        }

        /// <summary>Codes de lettrage déjà utilisés sur le compte, toutes dates. Paramètre : @compte.</summary>
        public static string RequeteLettres(NatureCompte nature)
        {
            return "SELECT DISTINCT e.EC_Lettrage FROM dbo.F_ECRITUREC e WHERE e." + Colonne(nature) +
                   " = @compte AND ISNULL(e.EC_Lettrage, '') <> ''";
        }

        /// <summary>Condition SQL sur l'état de lettrage (forme historique, conservée pour les appels existants).</summary>
        public static string ConditionLettrage(FiltreLettrage filtre, bool colonneLettre, bool colonneLettrage)
        {
            return Condition(filtre, new ColonnesEcriture { Lettre = colonneLettre, Lettrage = colonneLettrage });
        }

        /// <summary>
        /// Lettrée = EC_Lettre différent de 0, à défaut code EC_Lettrage renseigné ; pointée = EC_Point différent de 0,
        /// à défaut code EC_Pointage renseigné. Sans la colonne correspondante, aucune ligne n'est lettrée (ou pointée).
        /// </summary>
        public static string Condition(FiltreLettrage filtre, ColonnesEcriture col)
        {
            if (filtre == FiltreLettrage.Toutes) return null;
            col = col ?? new ColonnesEcriture();
            bool pointage = filtre == FiltreLettrage.Pointees || filtre == FiltreLettrage.NonPointees;
            bool positif = filtre == FiltreLettrage.Lettrees || filtre == FiltreLettrage.Pointees;
            string estVrai = pointage
                ? (col.Point ? "ISNULL(e.EC_Point, 0) <> 0" : col.Pointage ? "ISNULL(e.EC_Pointage, '') <> ''" : null)
                : (col.Lettre ? "ISNULL(e.EC_Lettre, 0) <> 0" : col.Lettrage ? "ISNULL(e.EC_Lettrage, '') <> ''" : null);
            if (estVrai == null) return positif ? "1 = 0" : null;
            return positif ? estVrai : "NOT (" + estVrai + ")";
        }

        public static async Task<ColonnesEcriture> ColonnesAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            const string sql = "SELECT COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Lettre'), COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Lettrage'), " +
                               "COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Point'), COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Pointage'), " +
                               "COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Reference')";
            var col = new ColonnesEcriture();
            using (var cmd = SqlOutils.Commande(c, sql, 60))
            using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await rd.ReadAsync(ct).ConfigureAwait(false))
                {
                    col.Lettre = !rd.IsDBNull(0);
                    col.Lettrage = !rd.IsDBNull(1);
                    col.Point = !rd.IsDBNull(2);
                    col.Pointage = !rd.IsDBNull(3);
                    col.Reference = !rd.IsDBNull(4);
                }
            }
            return col;
        }

        public static async Task<ResultatConsultation> ConsulterAsync(SqlConnection c, FiltreConsultation f, Referentiel referentiel,
                                                                      CancellationToken ct = default(CancellationToken))
        {
            if (f == null) throw new ArgumentNullException("f");
            var compte = (f.Compte ?? "").Trim();
            if (compte.Length == 0) throw new ArgumentException("Aucun compte choisi.");
            if (f.Au.Date < f.Du.Date) throw new ArgumentException("La date de fin précède la date de début.");

            var r = new ResultatConsultation
            {
                Nature = f.Nature,
                Compte = compte,
                Intitule = Intitule(referentiel, f.Nature, compte),
                Du = f.Du.Date,
                Au = f.Au.Date,
                Lettrage = f.Lettrage
            };

            var col = await ColonnesAsync(c, ct).ConfigureAwait(false);
            r.LettrageDisponible = col.LettrageDisponible;
            r.PointageDisponible = col.PointageDisponible;

            var debut = DebutReport(r.Du, referentiel == null ? null : referentiel.Exercices);
            if (f.AvecReport && debut.HasValue)
            {
                using (var cmd = SqlOutils.Commande(c, RequeteReport(f.Nature), 120))
                {
                    cmd.Parameters.Add(ParametreCompte(compte));
                    cmd.Parameters.Add(new SqlParameter("@debut", SqlDbType.DateTime) { Value = debut.Value });
                    cmd.Parameters.Add(new SqlParameter("@du", SqlDbType.DateTime) { Value = r.Du });
                    using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        if (await rd.ReadAsync(ct).ConfigureAwait(false))
                        {
                            r.ReportDebit = Montant(rd["Debit"]);
                            r.ReportCredit = Montant(rd["Credit"]);
                        }
                    }
                }
                r.DebutReport = debut;
            }

            using (var cmd = SqlOutils.Commande(c, RequeteLignes(f.Nature, f.Lettrage, col), 120))
            {
                cmd.Parameters.Add(ParametreCompte(compte));
                cmd.Parameters.Add(new SqlParameter("@du", SqlDbType.DateTime) { Value = r.Du });
                cmd.Parameters.Add(new SqlParameter("@auExclu", SqlDbType.DateTime) { Value = r.Au.AddDays(1) });
                using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await rd.ReadAsync(ct).ConfigureAwait(false))
                    {
                        bool credit = SqlOutils.Entier(rd["EC_Sens"]) == 1;
                        decimal montant = Montant(rd["EC_Montant"]);
                        var lettrage = Texte(rd["EC_Lettrage"]);
                        var pointage = Texte(rd["EC_Pointage"]);
                        var cg = Texte(rd["CG_Num"]);
                        var tiers = Texte(rd["CT_Num"]);
                        r.Lignes.Add(new LigneConsultation
                        {
                            EcNo = SqlOutils.Entier(rd["EC_No"]),
                            Journal = Texte(rd["JO_Num"]),
                            Date = SqlOutils.Date(rd["EC_Date"]) ?? r.Du,
                            Piece = Texte(rd["EC_Piece"]),
                            RefPiece = Texte(rd["EC_RefPiece"]),
                            Reference = Texte(rd["EC_Reference"]),
                            CompteGeneral = cg,
                            CompteTiers = tiers,
                            CompteAssocie = f.Nature == NatureCompte.General ? tiers : cg,
                            Libelle = Texte(rd["EC_Intitule"]),
                            Debit = credit ? 0m : montant,
                            Credit = credit ? montant : 0m,
                            Echeance = SqlOutils.Date(rd["EC_Echeance"]),
                            Lettrage = lettrage,
                            EstLettree = col.Lettre ? SqlOutils.Entier(rd["EC_Lettre"]) != 0 : lettrage.Length > 0,
                            Pointage = pointage,
                            EstPointee = col.Point ? SqlOutils.Entier(rd["EC_Point"]) != 0 : pointage.Length > 0
                        });
                    }
                }
            }
            Calculer(r);

            // Cumuls mensuels et solde réel du compte : toutes écritures, depuis le début de l'exercice.
            var debutCumuls = debut ?? r.Du;
            var mouvements = new Dictionary<Tuple<int, int>, Tuple<decimal, decimal>>();
            using (var cmd = SqlOutils.Commande(c, RequeteCumuls(f.Nature), 120))
            {
                cmd.Parameters.Add(ParametreCompte(compte));
                cmd.Parameters.Add(new SqlParameter("@debut", SqlDbType.DateTime) { Value = debutCumuls });
                cmd.Parameters.Add(new SqlParameter("@auExclu", SqlDbType.DateTime) { Value = r.Au.AddDays(1) });
                using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await rd.ReadAsync(ct).ConfigureAwait(false))
                        mouvements[Tuple.Create(SqlOutils.Entier(rd["Annee"]), SqlOutils.Entier(rd["Mois"]))] =
                            Tuple.Create(Montant(rd["Debit"]), Montant(rd["Credit"]));
                }
            }
            CalculerCumuls(r, debutCumuls, r.Au, mouvements);

            r.ProchaineLettre = "";
            if (col.Lettrage)
            {
                var codes = new List<string>();
                using (var cmd = SqlOutils.Commande(c, RequeteLettres(f.Nature), 120))
                {
                    cmd.Parameters.Add(ParametreCompte(compte));
                    using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        while (await rd.ReadAsync(ct).ConfigureAwait(false))
                            codes.Add(Texte(rd[0]));
                }
                r.ProchaineLettre = LettreSuivante(codes);
            }
            return r;
        }

        private static SqlParameter ParametreCompte(string compte)
        {
            return new SqlParameter("@compte", SqlDbType.VarChar, 17) { Value = compte };
        }

        private static string Texte(object o)
        {
            return (SqlOutils.Texte(o) ?? "").Trim();
        }

        private static decimal Montant(object o)
        {
            return o == null || o == DBNull.Value ? 0m : Convert.ToDecimal(o);
        }

        internal static async Task<bool> ColonneExisteAsync(SqlConnection c, string colonne, CancellationToken ct)
        {
            var r = await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_ECRITUREC', N'" + colonne.Replace("'", "''") + "')", ct)
                                   .ConfigureAwait(false);
            return r != null;
        }
    }
}
