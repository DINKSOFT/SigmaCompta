using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;

namespace SigmaCompta.Core.Comptabilite
{
    /// <summary>
    /// Saisie des écritures : reconnaissance des comptes et des tiers tapés au clavier, solde proposé,
    /// contrôles et construction de la pièce. L'enregistrement passe par imp.PS_ImportPiece
    /// (<see cref="ServiceImport.AppelerAsync"/>), comme l'import : mêmes contrôles, même numérotation.
    /// </summary>
    public static class ServiceSaisie
    {
        public const int LongueurLibelle = 69;
        public const int LongueurRefPiece = 17;
        public const int LongueurReference = 17;
        public const int LongueurPiece = 13;
        private const string FormatsDate = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|d/M/yy|ddMMyyyy|ddMMyy|yyyy-MM-dd";

        // ================================================================ Journal et période

        /// <summary>Tous les mois des exercices du dossier, du plus ancien au plus récent.</summary>
        public static List<PeriodeSaisie> Periodes(Referentiel r)
        {
            var mois = new SortedSet<DateTime>();
            if (r != null)
                foreach (var e in r.Exercices)
                    for (var m = new DateTime(e.Debut.Year, e.Debut.Month, 1); m <= e.Fin.Date; m = m.AddMonths(1))
                        mois.Add(m);
            return mois.Select(m => new PeriodeSaisie(m)).ToList();
        }

        /// <summary>Le mois en cours s'il fait partie des exercices, sinon le dernier mois.</summary>
        public static PeriodeSaisie PeriodeParDefaut(IList<PeriodeSaisie> periodes, DateTime aujourdhui)
        {
            if (periodes == null || periodes.Count == 0) return null;
            var mois = new DateTime(aujourdhui.Year, aujourdhui.Month, 1);
            return periodes.FirstOrDefault(p => p.Mois == mois) ?? periodes[periodes.Count - 1];
        }

        public static List<Journal> JournauxSaisissables(Referentiel r)
        {
            if (r == null) return new List<Journal>();
            return r.Journaux.Values.Where(j => !j.EnSommeil).OrderBy(j => j.Code, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool EstCloturee(Referentiel r, string journal, PeriodeSaisie periode)
        {
            return r != null && periode != null && !string.IsNullOrWhiteSpace(journal)
                   && r.PeriodesCloturees.Contains(Referentiel.ClePeriode(journal.Trim(), periode.Mois));
        }

        /// <summary>
        /// Date de la pièce : « 17 » = le 17 du mois de la période ; « 17/10 » ou « 17/10/2026 » sont acceptés
        /// s'ils tombent dans la période. Null si le texte est vide, illisible ou hors période.
        /// </summary>
        public static DateTime? Date(PeriodeSaisie periode, string texte)
        {
            if (periode == null || string.IsNullOrWhiteSpace(texte)) return null;
            var t = texte.Trim();
            int jour, mois;
            if (t.Length <= 2 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out jour))
                return jour >= 1 && jour <= periode.NombreJours ? new DateTime(periode.Mois.Year, periode.Mois.Month, jour) : (DateTime?)null;
            var parts = t.Split('/', '.', '-');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out jour)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out mois))
                return mois == periode.Mois.Month && jour >= 1 && jour <= periode.NombreJours
                    ? new DateTime(periode.Mois.Year, mois, jour) : (DateTime?)null;
            DateTime d;
            if (Analyseurs.EssayerDate(t, FormatsDate, out d) && periode.Contient(d)) return d.Date;
            return null;
        }

        // ================================================================ Comptes et tiers

        /// <summary>Comptes proposés à la saisie : comptes généraux imputables, ou tiers qui ne sont pas des prospects.</summary>
        public static List<ElementCompte> ComptesSaisissables(Referentiel r, NatureCompte nature)
        {
            if (r == null) return new List<ElementCompte>();
            IEnumerable<ElementCompte> liste = nature == NatureCompte.General
                ? r.Comptes.Values.Where(c => c.EstImputable).Select(c => new ElementCompte(nature, c.Numero, c.Intitule))
                : r.Tiers.Values.Where(t => !t.EstProspect).Select(t => new ElementCompte(nature, t.Numero, t.Intitule));
            return liste.OrderBy(e => e.Numero, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Suggestions pendant la frappe : d'abord les n° qui commencent par le texte, puis les intitulés qui le contiennent.</summary>
        public static List<ElementCompte> Filtrer(IEnumerable<ElementCompte> liste, string texte, int max = 40)
        {
            var t = (texte ?? "").Trim();
            var tous = (liste ?? Enumerable.Empty<ElementCompte>()).ToList();
            if (t.Length == 0) return tous.Take(max).ToList();
            var parNumero = tous.Where(e => e.Numero.StartsWith(t, StringComparison.OrdinalIgnoreCase));
            var parIntitule = tous.Where(e => !e.Numero.StartsWith(t, StringComparison.OrdinalIgnoreCase)
                                              && e.Intitule.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            return parNumero.Concat(parIntitule).Take(max).ToList();
        }

        /// <summary>
        /// Compte général désigné par le texte tapé : n° exact ; sinon n° complété par des zéros (« 401 » → 4010000) ;
        /// sinon seul compte imputable dont le n° commence par le texte ; sinon seul intitulé qui le contient.
        /// Null si introuvable ou ambigu.
        /// </summary>
        public static CompteGeneral ResoudreCompte(Referentiel r, string texte)
        {
            var t = (texte ?? "").Trim();
            if (r == null || t.Length == 0) return null;
            // Un n° exact de compte de total (« 401 ») ne bloque pas la complétion vers ses comptes imputables.
            CompteGeneral exact;
            r.Comptes.TryGetValue(t, out exact);
            if (exact != null && exact.EstImputable) return exact;
            var imputables = r.Comptes.Values.Where(c => c.EstImputable && !string.IsNullOrEmpty(c.Numero)).ToList();
            var prefixe = imputables.Where(c => c.Numero.StartsWith(t, StringComparison.OrdinalIgnoreCase)).ToList();
            var completes = prefixe.Where(c => c.Numero.Substring(t.Length).All(ch => ch == '0')).ToList();
            if (completes.Count == 1) return completes[0];
            if (prefixe.Count == 1) return prefixe[0];
            if (prefixe.Count > 1) return null;
            if (exact != null) return exact;   // compte de total sans compte imputable dessous : l'appelant le signale
            var parIntitule = imputables.Where(c => (c.Intitule ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList();
            return parIntitule.Count == 1 ? parIntitule[0] : null;
        }

        /// <summary>Tiers désigné par le texte tapé : n° exact, sinon seul n° qui commence par le texte, sinon seul intitulé qui le contient.</summary>
        public static Tiers ResoudreTiers(Referentiel r, string texte)
        {
            var t = (texte ?? "").Trim();
            if (r == null || t.Length == 0) return null;
            Tiers x;
            if (r.Tiers.TryGetValue(t, out x)) return x;
            var actifs = r.Tiers.Values.Where(v => !v.EstProspect && !string.IsNullOrEmpty(v.Numero)).ToList();
            var prefixe = actifs.Where(v => v.Numero.StartsWith(t, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (prefixe.Count == 1) return prefixe[0];
            if (prefixe.Count > 1) return null;
            var parIntitule = actifs.Where(v => (v.Intitule ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0).Take(2).ToList();
            return parIntitule.Count == 1 ? parIntitule[0] : null;
        }

        /// <summary>Compte collectif du tiers, s'il existe dans le plan comptable.</summary>
        public static CompteGeneral CollectifDuTiers(Referentiel r, Tiers t)
        {
            CompteGeneral g;
            return r != null && t != null && !string.IsNullOrWhiteSpace(t.Collectif) && r.Comptes.TryGetValue(t.Collectif.Trim(), out g) ? g : null;
        }

        // ================================================================ Lignes

        /// <summary>
        /// Interprète la ligne tapée. Renvoie null et un message si elle ne peut pas être ajoutée.
        /// Tiers sans compte : le compte collectif du tiers est pris. Libellé vide : <paramref name="libelleParDefaut"/>.
        /// </summary>
        public static LigneSaisie InterpreterLigne(Referentiel r, SaisieLigne s, string libelleParDefaut, out string erreur)
        {
            erreur = null;
            if (s == null) { erreur = "Ligne vide."; return null; }

            Tiers tiers = null;
            var texteTiers = (s.Tiers ?? "").Trim();
            if (texteTiers.Length > 0)
            {
                tiers = ResoudreTiers(r, texteTiers);
                if (tiers == null) { erreur = "Tiers « " + texteTiers + " » introuvable ou ambigu : choisissez-le dans la liste."; return null; }
                if (tiers.EstProspect) { erreur = "Le tiers « " + tiers.Numero + " » est un prospect : écriture impossible."; return null; }
            }

            CompteGeneral compte;
            var texteCompte = (s.Compte ?? "").Trim();
            if (texteCompte.Length == 0)
            {
                compte = CollectifDuTiers(r, tiers);
                if (compte == null)
                {
                    erreur = tiers == null ? "Compte général manquant." : "Compte général manquant (le tiers « " + tiers.Numero + " » n'a pas de compte collectif).";
                    return null;
                }
            }
            else
            {
                compte = ResoudreCompte(r, texteCompte);
                if (compte == null) { erreur = "Compte « " + texteCompte + " » introuvable ou ambigu : choisissez-le dans la liste."; return null; }
            }
            if (!compte.EstImputable) { erreur = "Le compte « " + compte.Numero + " » est un compte de total : il n'est pas imputable."; return null; }

            decimal debit, credit;
            if (!Analyseurs.EssayerMontant(s.Debit, "auto", out debit)) { erreur = "Montant au débit illisible : « " + s.Debit + " »."; return null; }
            if (!Analyseurs.EssayerMontant(s.Credit, "auto", out credit)) { erreur = "Montant au crédit illisible : « " + s.Credit + " »."; return null; }
            debit = Math.Round(debit, 2);
            credit = Math.Round(credit, 2);
            if (debit < 0 || credit < 0) { erreur = "Montant négatif : saisissez-le dans l'autre colonne."; return null; }
            if (debit != 0 && credit != 0) { erreur = "Renseignez soit le débit, soit le crédit."; return null; }
            if (debit == 0 && credit == 0) { erreur = "Montant manquant."; return null; }

            DateTime? echeance = null;
            if (!string.IsNullOrWhiteSpace(s.Echeance))
            {
                DateTime d;
                if (!Analyseurs.EssayerDate(s.Echeance, FormatsDate, out d)) { erreur = "Échéance illisible : « " + s.Echeance + " » (jj/mm/aaaa)."; return null; }
                echeance = d.Date;
            }

            var reference = (s.Reference ?? "").Trim();
            if (reference.Length > LongueurReference) { erreur = "Référence trop longue : " + reference.Length + " caractères, " + LongueurReference + " au maximum."; return null; }

            var libelle = string.IsNullOrWhiteSpace(s.Libelle) ? (libelleParDefaut ?? "").Trim() : s.Libelle.Trim();
            if (libelle.Length > LongueurLibelle) { erreur = "Libellé trop long : " + libelle.Length + " caractères, " + LongueurLibelle + " au maximum."; return null; }

            return new LigneSaisie
            {
                CompteGeneral = compte.Numero,
                IntituleCompte = compte.Intitule,
                CompteTiers = tiers == null ? "" : tiers.Numero,
                IntituleTiers = tiers == null ? "" : tiers.Intitule,
                Reference = reference,
                Libelle = libelle,
                Debit = debit,
                Credit = credit,
                Echeance = echeance
            };
        }

        public static decimal Ecart(IEnumerable<LigneSaisie> lignes)
        {
            return (lignes ?? Enumerable.Empty<LigneSaisie>()).Sum(l => l.Debit - l.Credit);
        }

        /// <summary>Montant qui équilibre la pièce, à proposer sur la ligne suivante (au crédit si le débit l'emporte).</summary>
        public static void SoldeAProposer(IEnumerable<LigneSaisie> lignes, out decimal debit, out decimal credit)
        {
            var e = Ecart(lignes);
            debit = e < 0 ? -e : 0m;
            credit = e > 0 ? e : 0m;
        }

        // ================================================================ Pièce

        public static PieceImport Construire(EntetePiece e, IList<LigneSaisie> lignes)
        {
            var refPiece = (e.RefPiece ?? "").Trim();
            var p = new PieceImport
            {
                Numero = 1,
                Cle = "saisie",
                Journal = (e.Journal ?? "").Trim(),
                Date = e.Date,
                PieceFournie = (e.Piece ?? "").Trim(),
                RefPiece = refPiece
            };
            for (int i = 0; i < lignes.Count; i++)
            {
                var l = lignes[i];
                p.Lignes.Add(new LigneImport
                {
                    LigneFichier = i + 1,
                    NumLigne = i + 1,
                    Journal = p.Journal,
                    Date = e.Date,
                    Piece = p.PieceFournie,
                    RefPiece = refPiece,
                    Reference = (l.Reference ?? "").Trim(),
                    CompteGeneral = l.CompteGeneral,
                    CompteTiers = l.CompteTiers,
                    Libelle = string.IsNullOrWhiteSpace(l.Libelle) ? (e.Libelle ?? "").Trim() : l.Libelle.Trim(),
                    Debit = l.Debit,
                    Credit = l.Credit,
                    Echeance = l.Echeance,
                    IntituleCompte = l.IntituleCompte
                });
            }
            return p;
        }

        /// <summary>Contrôles avant enregistrement : ceux de l'import (Validateur), plus la période et la longueur des textes.</summary>
        public static List<Anomalie> Controler(PieceImport p, Referentiel r, PeriodeSaisie periode)
        {
            Validateur.Valider(new List<PieceImport> { p }, r, null);
            if (!p.Date.HasValue)
                p.Anomalies.Add(Erreur(0, "Jour manquant ou hors de la période" + (periode == null ? "" : " " + periode.Libelle) + "."));
            else if (periode != null && !periode.Contient(p.Date.Value))
                p.Anomalies.Add(Erreur(0, "Le " + p.Date.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " n'appartient pas à la période " + periode.Libelle + "."));
            if ((p.RefPiece ?? "").Length > LongueurRefPiece)
                p.Anomalies.Add(Erreur(0, "Référence trop longue : " + p.RefPiece.Length + " caractères, " + LongueurRefPiece + " au maximum."));
            foreach (var l in p.Lignes.Where(x => (x.Reference ?? "").Length > LongueurReference))
                l.Anomalies.Add(Erreur(l.LigneFichier, "Référence trop longue : " + l.Reference.Length + " caractères, " + LongueurReference + " au maximum."));
            foreach (var l in p.Lignes.Where(x => (x.Libelle ?? "").Length > LongueurLibelle))
                l.Anomalies.Add(Erreur(l.LigneFichier, "Libellé trop long : " + l.Libelle.Length + " caractères, " + LongueurLibelle + " au maximum."));
            p.ActualiserStatutLocal();
            return p.ToutesAnomalies.OrderBy(a => a.EstErreur ? 0 : 1).ToList();
        }

        private static Anomalie Erreur(int ligne, string message)
        {
            return new Anomalie { Niveau = NiveauAnomalie.Erreur, Message = message, LigneFichier = ligne, DeValidation = true };
        }

        /// <summary>Options d'appel de la procédure pour une pièce saisie : un n° déjà pris est signalé, jamais remplacé.</summary>
        public static OptionsProfil Options(bool ignorerHash)
        {
            return new OptionsProfil
            {
                AutoriserDoublon = false,
                RenumeroterDoublon = false,
                IgnorerHash = ignorerHash,
                ModeTransaction = ModesTransaction.ParPiece
            };
        }

        /// <summary>Message lisible pour un refus du serveur.</summary>
        public static string ExpliquerErreur(Exception ex)
        {
            var sql = ex as SqlException;
            var lisible = Expliquer(sql == null ? 0 : sql.Number, ex.Message);
            return lisible ?? ErreursComptables.Expliquer(ex);
        }

        /// <summary>Traductions propres à la saisie (null = message standard).</summary>
        public static string Expliquer(int numero, string message)
        {
            message = message ?? "";
            if (numero == 50005)
                return "Ce n° de pièce est déjà utilisé dans ce journal. Videz le champ « N° pièce » pour une numérotation automatique, ou saisissez un autre n°.";
            if (message.IndexOf("Chaînage d'inaltérabilité", StringComparison.OrdinalIgnoreCase) >= 0)
                return "La base utilise le chaînage d'inaltérabilité, que l'enregistrement direct ne calcule pas. " +
                       "Cochez « Enregistrer hors chaînage » pour enregistrer quand même, en connaissance de cause.";
            return null;
        }

        // ================================================================ Base

        /// <summary>Null si imp.PS_ImportPiece est installée dans la bonne version, sinon le message à afficher.</summary>
        public static string MessageProcedure(bool installee, string versionBase, string versionApplication)
        {
            if (!installee)
                return "La procédure d'enregistrement imp.PS_ImportPiece n'est pas installée dans cette base. " +
                       "Ouvrez « Vérification » (Import d'écritures) et cliquez sur « Installer la procédure ».";
            if (versionBase != versionApplication)
                return "La procédure imp.PS_ImportPiece installée (version " + (versionBase ?? "?") + ") n'est pas celle de l'application (" +
                       versionApplication + "). Ouvrez « Vérification » et cliquez sur « Installer la procédure ».";
            return null;
        }

        public static async Task<string> VerifierProcedureAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            var def = SqlOutils.Texte(await SqlOutils.ScalaireAsync(c, "SELECT OBJECT_DEFINITION(OBJECT_ID(N'imp.PS_ImportPiece'))", ct).ConfigureAwait(false));
            return MessageProcedure(def != null, def == null ? null : ExplorateurBases.ExtraireVersion(def), ScriptsEmbarques.VersionProcedureEmbarquee);
        }

        /// <summary>La dernière écriture de la base porte-t-elle un chaînage d'inaltérabilité (cbHashVersion) ?</summary>
        public static async Task<bool> ChainageActifAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            if (await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_ECRITUREC', N'cbHashVersion')", ct).ConfigureAwait(false) == null)
                return false;
            var v = await SqlOutils.ScalaireAsync(c,
                "SELECT TOP (1) CASE WHEN cbHashVersion IS NULL THEN 0 ELSE 1 END FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC", ct).ConfigureAwait(false);
            return v != null && Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1;
        }

        /// <summary>Écritures du journal sur la période, ordonnées comme le journal.</summary>
        public static async Task<List<EcritureEtat>> LireBrouillardAsync(SqlConnection c, string journal, PeriodeSaisie periode,
                                                                         CancellationToken ct = default(CancellationToken))
        {
            // EC_Reference (colonne « Référence ») n'est lue que si la base l'a.
            var avecReference = await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_ECRITUREC', N'EC_Reference')", ct).ConfigureAwait(false) != null;
            return await ServiceEtats.LireEcrituresAsync(c, ServiceEtats.RequeteJournal(true, avecReference), cmd =>
            {
                cmd.Parameters.Add(new SqlParameter("@du", SqlDbType.DateTime) { Value = periode.Mois });
                cmd.Parameters.Add(new SqlParameter("@auExclu", SqlDbType.DateTime) { Value = periode.Mois.AddMonths(1) });
                cmd.Parameters.Add(new SqlParameter("@journal", SqlDbType.VarChar, 13) { Value = (journal ?? "").Trim() });
            }, ct).ConfigureAwait(false);
        }

        // ================================================================ Compte de trésorerie du journal

        /// <summary>
        /// Ancien solde du compte de trésorerie, calculé comme le logiciel comptable : écritures du compte
        /// passées dans ce journal depuis le début de l'exercice (<c>@debut</c>, facultatif) jusqu'à la veille
        /// de la période (<c>@du</c>). Les écritures du même compte dans d'autres journaux n'y entrent pas.
        /// Paramètres : @compte, @journal, [@debut], @du.
        /// </summary>
        public static string RequeteSoldeAnterieur(bool avecDebut)
        {
            return "SELECT SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit " +
                   "FROM dbo.F_ECRITUREC e WHERE e.CG_Num = @compte AND e.JO_Num = @journal" +
                   (avecDebut ? " AND " + SqlOutils.DateEcriture + " >= @debut" : "") +
                   " AND " + SqlOutils.DateEcriture + " < @du";
        }

        /// <summary>Solde (débit − crédit) du compte de trésorerie <paramref name="compte"/> dans le journal, au début de la période.</summary>
        public static async Task<decimal> LireSoldeAnterieurAsync(SqlConnection c, string compte, string journal, PeriodeSaisie periode,
                                                                  Referentiel r, CancellationToken ct = default(CancellationToken))
        {
            var exo = r == null ? null : r.Exercices.Where(e => e.Contient(periode.Mois)).OrderByDescending(e => e.Debut).FirstOrDefault();
            using (var cmd = SqlOutils.Commande(c, RequeteSoldeAnterieur(exo != null), 120))
            {
                cmd.Parameters.Add(new SqlParameter("@compte", SqlDbType.VarChar, 13) { Value = (compte ?? "").Trim() });
                cmd.Parameters.Add(new SqlParameter("@journal", SqlDbType.VarChar, 13) { Value = (journal ?? "").Trim() });
                if (exo != null) cmd.Parameters.Add(new SqlParameter("@debut", SqlDbType.DateTime) { Value = exo.Debut.Date });
                cmd.Parameters.Add(new SqlParameter("@du", SqlDbType.DateTime) { Value = periode.Mois });
                using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await rd.ReadAsync(ct).ConfigureAwait(false)) return 0m;
                    return MontantSql(rd["Debit"]) - MontantSql(rd["Credit"]);
                }
            }
        }

        private static decimal MontantSql(object o)
        {
            return o == null || o == DBNull.Value ? 0m : Convert.ToDecimal(o, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Situation du compte de trésorerie affichée en tête de la saisie : solde avant la période,
        /// mouvements du compte dans le journal (écritures enregistrées) et nouveau solde.
        /// </summary>
        public static SituationTresorerie Situation(string compte, string intitule, decimal ancienSolde, IEnumerable<LigneEtat> brouillard)
        {
            var cle = (compte ?? "").Trim();
            var s = new SituationTresorerie { Compte = cle, Intitule = intitule ?? "", AncienSolde = ancienSolde };
            foreach (var l in brouillard ?? Enumerable.Empty<LigneEtat>())
            {
                if (l.Genre != GenreLigne.Detail || !string.Equals((l.Compte ?? "").Trim(), cle, StringComparison.OrdinalIgnoreCase)) continue;
                s.MouvementsDebit += l.Debit ?? 0m;
                s.MouvementsCredit += l.Credit ?? 0m;
            }
            return s;
        }

        /// <summary>Lignes du brouillard à afficher, avec un total en fin de liste.</summary>
        public static BrouillardPeriode Brouillard(IEnumerable<EcritureEtat> ecritures)
        {
            var b = new BrouillardPeriode();
            var liste = (ecritures ?? Enumerable.Empty<EcritureEtat>()).ToList();
            foreach (var e in liste)
            {
                b.Lignes.Add(new LigneEtat
                {
                    Genre = GenreLigne.Detail, Date = e.Date, Journal = e.Journal, Piece = e.Piece, RefPiece = e.RefPiece, Reference = e.Reference,
                    Compte = e.CompteGeneral, CompteTiers = e.CompteTiers, Libelle = e.Libelle,
                    Debit = e.Debit == 0 ? (decimal?)null : e.Debit, Credit = e.Credit == 0 ? (decimal?)null : e.Credit,
                    Echeance = e.Echeance, EcNo = e.EcNo
                });
                b.TotalDebit += e.Debit;
                b.TotalCredit += e.Credit;
            }
            b.NombreEcritures = liste.Count;
            b.NombrePieces = liste.Select(e => (e.Piece ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (liste.Count > 0)
                b.Lignes.Add(new LigneEtat
                {
                    Genre = GenreLigne.TotalGeneral, Libelle = "Total de la période",
                    Debit = b.TotalDebit == 0 ? (decimal?)null : b.TotalDebit, Credit = b.TotalCredit == 0 ? (decimal?)null : b.TotalCredit
                });
            return b;
        }
    }
}
