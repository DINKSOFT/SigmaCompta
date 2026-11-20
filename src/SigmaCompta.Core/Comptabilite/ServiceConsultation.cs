using System;
using System.Collections.Generic;
using System.Data;
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
    /// Consultation d'un compte général ou d'un compte tiers : report à nouveau, lignes de la période
    /// avec solde progressif, filtre de lettrage. Lecture seule : aucune écriture dans la base.
    /// </summary>
    public static class ServiceConsultation
    {
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

        /// <summary>Comptes proposés : comptes généraux imputables (hors comptes de total) ou tous les tiers.</summary>
        public static List<ElementCompte> ComptesConsultables(Referentiel r, NatureCompte nature)
        {
            if (r == null) return new List<ElementCompte>();
            IEnumerable<ElementCompte> liste = nature == NatureCompte.General
                ? r.Comptes.Values.Where(c => c.EstImputable).Select(c => new ElementCompte(nature, c.Numero, c.Intitule))
                : r.Tiers.Values.Select(t => new ElementCompte(nature, t.Numero, t.Intitule));
            return liste.OrderBy(e => e.Numero, StringComparer.OrdinalIgnoreCase).ToList();
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

        /// <summary>Lignes de la période. Paramètres : @compte, @du, @auExclu.</summary>
        public static string RequeteLignes(NatureCompte nature, FiltreLettrage lettrage, bool colonneLettre, bool colonneLettrage)
        {
            var sb = new StringBuilder();
            sb.Append("SELECT e.EC_No, e.JO_Num, e.EC_Date, e.EC_Piece, e.EC_RefPiece, e.CG_Num, e.CT_Num, e.EC_Intitule, ")
              .Append("e.EC_Sens, e.EC_Montant, e.EC_Echeance, ")
              .Append(colonneLettrage ? "e.EC_Lettrage" : "CAST(NULL AS varchar(3)) AS EC_Lettrage").Append(", ")
              .Append(colonneLettre ? "e.EC_Lettre" : "CAST(NULL AS smallint) AS EC_Lettre")
              .Append(" FROM dbo.F_ECRITUREC e WHERE e.").Append(Colonne(nature))
              .Append(" = @compte AND e.EC_Date >= @du AND e.EC_Date < @auExclu");
            var condition = ConditionLettrage(lettrage, colonneLettre, colonneLettrage);
            if (condition != null) sb.Append(" AND ").Append(condition);
            sb.Append(" ORDER BY e.EC_Date, e.JO_Num, e.EC_Piece, e.EC_No");
            return sb.ToString();
        }

        /// <summary>Cumuls du début d'exercice jusqu'à la veille de la période. Paramètres : @compte, @debut, @du.</summary>
        public static string RequeteReport(NatureCompte nature)
        {
            return "SELECT SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit " +
                   "FROM dbo.F_ECRITUREC e WHERE e." + Colonne(nature) + " = @compte AND e.EC_Date >= @debut AND e.EC_Date < @du";
        }

        /// <summary>
        /// Lettrée = EC_Lettre différent de 0 ; à défaut de cette colonne, code EC_Lettrage renseigné.
        /// Sans colonne de lettrage, toutes les lignes sont considérées comme non lettrées.
        /// </summary>
        public static string ConditionLettrage(FiltreLettrage filtre, bool colonneLettre, bool colonneLettrage)
        {
            if (filtre == FiltreLettrage.Toutes) return null;
            string estLettree = colonneLettre ? "ISNULL(e.EC_Lettre, 0) <> 0"
                              : colonneLettrage ? "ISNULL(e.EC_Lettrage, '') <> ''"
                              : null;
            if (estLettree == null) return filtre == FiltreLettrage.Lettrees ? "1 = 0" : null;
            return filtre == FiltreLettrage.Lettrees ? estLettree : "NOT (" + estLettree + ")";
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

            bool colonneLettre = await ColonneExisteAsync(c, "EC_Lettre", ct).ConfigureAwait(false);
            bool colonneLettrage = await ColonneExisteAsync(c, "EC_Lettrage", ct).ConfigureAwait(false);
            r.LettrageDisponible = colonneLettre || colonneLettrage;

            if (f.AvecReport)
            {
                var debut = DebutReport(r.Du, referentiel == null ? null : referentiel.Exercices);
                if (debut.HasValue)
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
            }

            using (var cmd = SqlOutils.Commande(c, RequeteLignes(f.Nature, f.Lettrage, colonneLettre, colonneLettrage), 120))
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
                        var lettrage = (SqlOutils.Texte(rd["EC_Lettrage"]) ?? "").Trim();
                        var cg = (SqlOutils.Texte(rd["CG_Num"]) ?? "").Trim();
                        var tiers = (SqlOutils.Texte(rd["CT_Num"]) ?? "").Trim();
                        r.Lignes.Add(new LigneConsultation
                        {
                            EcNo = SqlOutils.Entier(rd["EC_No"]),
                            Journal = (SqlOutils.Texte(rd["JO_Num"]) ?? "").Trim(),
                            Date = SqlOutils.Date(rd["EC_Date"]) ?? r.Du,
                            Piece = (SqlOutils.Texte(rd["EC_Piece"]) ?? "").Trim(),
                            RefPiece = (SqlOutils.Texte(rd["EC_RefPiece"]) ?? "").Trim(),
                            CompteGeneral = cg,
                            CompteTiers = tiers,
                            CompteAssocie = f.Nature == NatureCompte.General ? tiers : cg,
                            Libelle = (SqlOutils.Texte(rd["EC_Intitule"]) ?? "").Trim(),
                            Debit = credit ? 0m : montant,
                            Credit = credit ? montant : 0m,
                            Echeance = SqlOutils.Date(rd["EC_Echeance"]),
                            Lettrage = lettrage,
                            EstLettree = colonneLettre ? SqlOutils.Entier(rd["EC_Lettre"]) != 0 : lettrage.Length > 0
                        });
                    }
                }
            }

            Calculer(r);
            return r;
        }

        private static SqlParameter ParametreCompte(string compte)
        {
            return new SqlParameter("@compte", SqlDbType.VarChar, 17) { Value = compte };
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
