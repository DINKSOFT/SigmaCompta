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
    /// États comptables : balance (six colonnes), grand livre, journaux et journal centralisateur.
    /// Lecture seule. Les requêtes ramènent des données brutes ; la mise en forme (groupes, totaux,
    /// soldes) est faite par les méthodes « Construire… », testées sans base.
    /// </summary>
    public static class ServiceEtats
    {
        private static readonly CultureInfo Fr = new CultureInfo("fr-FR");
        private const int DelaiRequete = 300;

        // ================================================================ Libellés

        public static string Titre(TypeEtat etat, NatureCompte nature)
        {
            switch (etat)
            {
                case TypeEtat.Balance: return nature == NatureCompte.General ? "Balance générale" : "Balance des tiers";
                case TypeEtat.GrandLivre: return nature == NatureCompte.General ? "Grand livre des comptes généraux" : "Grand livre des tiers";
                case TypeEtat.Journal: return "Journaux";
                default: return "Journal centralisateur";
            }
        }

        /// <summary>« Janvier 2026 ».</summary>
        public static string Mois(DateTime d)
        {
            var s = d.ToString("MMMM yyyy", Fr);
            return s.Length == 0 ? s : char.ToUpper(s[0], Fr) + s.Substring(1);
        }

        /// <summary>Intitulés des classes du plan comptable SYSCOHADA.</summary>
        public static string LibelleClasse(string classe)
        {
            switch (classe)
            {
                case "1": return "Comptes de ressources durables";
                case "2": return "Comptes d'actif immobilisé";
                case "3": return "Comptes de stocks";
                case "4": return "Comptes de tiers";
                case "5": return "Comptes de trésorerie";
                case "6": return "Comptes de charges des activités ordinaires";
                case "7": return "Comptes de produits des activités ordinaires";
                case "8": return "Comptes des autres charges et des autres produits";
                case "9": return "Comptes des engagements hors bilan et de la comptabilité analytique";
                default: return "Autres comptes";
            }
        }

        public static string Intitule(Referentiel r, NatureCompte nature, string compte)
        {
            return ServiceConsultation.Intitule(r, nature, compte);
        }

        public static string IntituleJournal(Referentiel r, string code)
        {
            Journal j;
            return r != null && code != null && r.Journaux.TryGetValue(code.Trim(), out j) ? (j.Intitule ?? "").Trim() : "";
        }

        public static int TypeTiers(Referentiel r, string compte)
        {
            Tiers t;
            return r != null && compte != null && r.Tiers.TryGetValue(compte.Trim(), out t) ? t.Type : -1;
        }

        /// <summary>
        /// Journal des à-nouveaux proposé par défaut : un code usuel (RAN, AN, ANO…), sinon un journal
        /// dont l'intitulé contient « nouveau ». Null si aucun ne correspond.
        /// </summary>
        public static string DetecterJournalANouveaux(Referentiel r)
        {
            if (r == null) return null;
            var codes = new HashSet<string>(new[] { "RAN", "AN", "ANO", "ANX", "A-N", "A/N", "AN0", "RAN0" }, StringComparer.OrdinalIgnoreCase);
            var journaux = r.Journaux.Values.Where(j => !string.IsNullOrWhiteSpace(j.Code))
                                            .OrderBy(j => j.Code, StringComparer.OrdinalIgnoreCase).ToList();
            var parCode = journaux.FirstOrDefault(j => codes.Contains(j.Code.Trim()));
            if (parCode != null) return parCode.Code.Trim();
            var parIntitule = journaux.FirstOrDefault(j => SansAccents(j.Intitule).Contains("NOUVEAU"));
            return parIntitule == null ? null : parIntitule.Code.Trim();
        }

        private static string SansAccents(string s)
        {
            var d = (s ?? "").Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (var ch in d)
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString().ToUpperInvariant();
        }

        // ================================================================ Contrôle des paramètres

        /// <summary>
        /// Contrôle la période et renvoie le début de la période d'ouverture (début d'exercice), null si la
        /// période commence avec l'exercice. La balance et le grand livre restent dans un seul exercice :
        /// au-delà, les à-nouveaux de l'exercice suivant doubleraient les soldes.
        /// </summary>
        public static DateTime? Valider(ParametresEtat p, Referentiel r)
        {
            if (p == null) throw new ArgumentNullException("p");
            if (p.Au.Date < p.Du.Date) throw new ArgumentException("La date de fin précède la date de début.");
            var exercices = r == null ? null : r.Exercices;
            if (p.Etat == TypeEtat.Balance || p.Etat == TypeEtat.GrandLivre)
            {
                var exo = exercices == null ? null
                        : exercices.Where(e => e.Contient(p.Du)).OrderByDescending(e => e.Debut).FirstOrDefault();
                if (exo != null && !exo.Contient(p.Au))
                    throw new ArgumentException("La période doit rester dans un même exercice : l'exercice du " +
                        exo.Debut.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + " se termine le " +
                        exo.Fin.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ".");
            }
            return p.Etat == TypeEtat.Balance || p.Etat == TypeEtat.GrandLivre
                ? ServiceConsultation.DebutReport(p.Du.Date, exercices)
                : null;
        }

        private static ResultatEtat Nouveau(ParametresEtat p, DateTime? debutReport)
        {
            var periode = "du " + p.Du.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) +
                          " au " + p.Au.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            string precision;
            if (p.Etat == TypeEtat.Balance || p.Etat == TypeEtat.GrandLivre)
            {
                precision = p.Plage.Libelle;
                if (p.Nature == NatureCompte.Tiers && p.TypeTiers.HasValue)
                    precision += " · " + TypesTiers.Libelle(p.TypeTiers.Value).ToLower(Fr);
            }
            else
            {
                precision = string.IsNullOrWhiteSpace(p.Journal) ? "tous les journaux" : "journal " + p.Journal.Trim();
            }
            return new ResultatEtat
            {
                Parametres = p,
                Titre = Titre(p.Etat, p.Nature),
                SousTitre = periode + " · " + precision,
                DebutReport = debutReport
            };
        }

        private static decimal? NonNul(decimal v)
        {
            return v == 0 ? (decimal?)null : v;
        }

        private static decimal Val(decimal? v)
        {
            return v ?? 0m;
        }

        private static string Format(decimal v)
        {
            return v.ToString("#,##0.##", Fr);
        }

        private static Func<string, bool> FiltreComptes(ParametresEtat p, Referentiel r)
        {
            var plage = p.Plage;
            return c => !string.IsNullOrWhiteSpace(c) && plage.Contient(c)
                        && (p.Nature == NatureCompte.General || !p.TypeTiers.HasValue || TypeTiers(r, c) == p.TypeTiers.Value);
        }

        private static string AvertissementANouveaux(ParametresEtat p, DateTime? debutReport)
        {
            if (!string.IsNullOrWhiteSpace(p.JournalANouveaux) || debutReport.HasValue) return null;
            return "Aucun journal d'à-nouveaux choisi : les à-nouveaux de début d'exercice sont comptés dans les mouvements de la période.";
        }

        // ================================================================ Balance

        private sealed class Totaux
        {
            public decimal OuvD, OuvC, MvtD, MvtC, SolD, SolC;

            public void Ajouter(LigneEtat l)
            {
                OuvD += Val(l.OuvertureDebit); OuvC += Val(l.OuvertureCredit);
                MvtD += Val(l.MouvementDebit); MvtC += Val(l.MouvementCredit);
                SolD += Val(l.SoldeDebit); SolC += Val(l.SoldeCredit);
            }

            public void Ajouter(Totaux t)
            {
                OuvD += t.OuvD; OuvC += t.OuvC; MvtD += t.MvtD; MvtC += t.MvtC; SolD += t.SolD; SolC += t.SolC;
            }

            public LigneEtat Ligne(GenreLigne genre, string libelle)
            {
                return new LigneEtat
                {
                    Genre = genre, Libelle = libelle,
                    OuvertureDebit = NonNul(OuvD), OuvertureCredit = NonNul(OuvC),
                    MouvementDebit = NonNul(MvtD), MouvementCredit = NonNul(MvtC),
                    SoldeDebit = NonNul(SolD), SoldeCredit = NonNul(SolC)
                };
            }
        }

        /// <summary>Ligne de balance : soldes d'ouverture et final en net (débiteur ou créditeur), mouvements en brut.</summary>
        public static LigneEtat LigneBalance(CumulCompte c, string intitule)
        {
            var ouv = c.SoldeOuverture;
            var fin = c.SoldeFinal;
            return new LigneEtat
            {
                Genre = GenreLigne.Detail,
                Compte = (c.Compte ?? "").Trim(),
                Libelle = intitule ?? "",
                OuvertureDebit = ouv > 0 ? ouv : (decimal?)null,
                OuvertureCredit = ouv < 0 ? -ouv : (decimal?)null,
                MouvementDebit = NonNul(c.MouvementDebit),
                MouvementCredit = NonNul(c.MouvementCredit),
                SoldeDebit = fin > 0 ? fin : (decimal?)null,
                SoldeCredit = fin < 0 ? -fin : (decimal?)null
            };
        }

        /// <summary>Balance : comptes triés, total par classe (comptes généraux) ou par type (tiers), total général.</summary>
        public static ResultatEtat ConstruireBalance(ParametresEtat p, DateTime? debutReport, IEnumerable<CumulCompte> cumuls, Referentiel r)
        {
            var res = Nouveau(p, debutReport);
            var retenu = FiltreComptes(p, r);
            var comptes = (cumuls ?? Enumerable.Empty<CumulCompte>())
                .Where(c => retenu(c.Compte))
                .GroupBy(c => c.Compte.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new CumulCompte
                {
                    Compte = g.Key,
                    OuvertureDebit = g.Sum(x => x.OuvertureDebit), OuvertureCredit = g.Sum(x => x.OuvertureCredit),
                    MouvementDebit = g.Sum(x => x.MouvementDebit), MouvementCredit = g.Sum(x => x.MouvementCredit)
                })
                .Where(c => c.OuvertureDebit != 0 || c.OuvertureCredit != 0 || c.MouvementDebit != 0 || c.MouvementCredit != 0)
                .Where(c => p.InclureComptesSoldes || c.SoldeFinal != 0)
                .ToList();

            // Groupe : classe (1er caractère du compte) ou type de tiers.
            Func<string, int> ordre;
            Func<string, string> groupe, libelleGroupe;
            if (p.Nature == NatureCompte.General)
            {
                groupe = c => c.Substring(0, 1);
                ordre = c => char.IsDigit(c[0]) ? c[0] - '0' : 10;
                libelleGroupe = c => "Total classe " + c.Substring(0, 1) + " · " + LibelleClasse(c.Substring(0, 1));
            }
            else
            {
                groupe = c => TypeTiers(r, c).ToString(CultureInfo.InvariantCulture);
                ordre = c => { var t = TypeTiers(r, c); return t < 0 ? 99 : t; };
                libelleGroupe = c => "Total " + TypesTiers.Libelle(TypeTiers(r, c)).ToLower(Fr);
            }

            var total = new Totaux();
            var tries = comptes.OrderBy(c => ordre(c.Compte)).ThenBy(c => groupe(c.Compte), StringComparer.OrdinalIgnoreCase)
                               .ThenBy(c => c.Compte, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var g in tries.GroupBy(c => groupe(c.Compte), StringComparer.OrdinalIgnoreCase))
            {
                var st = new Totaux();
                foreach (var cumul in g)
                {
                    var l = LigneBalance(cumul, Intitule(r, p.Nature, cumul.Compte));
                    res.Lignes.Add(l);
                    st.Ajouter(l);
                }
                res.Lignes.Add(st.Ligne(GenreLigne.SousTotal, libelleGroupe(g.First().Compte)));
                total.Ajouter(st);
            }
            res.Lignes.Add(total.Ligne(GenreLigne.TotalGeneral, "Total général"));

            res.TotalDebit = total.MvtD;
            res.TotalCredit = total.MvtC;
            res.NombreComptes = comptes.Count;

            var an = AvertissementANouveaux(p, debutReport);
            if (an != null) res.Avertissements.Add(an);
            // Contrôle d'équilibre : seulement sur la balance générale complète.
            if (p.Nature == NatureCompte.General && p.Plage.EstVide)
            {
                if (total.OuvD != total.OuvC)
                    res.Avertissements.Add("Soldes d'ouverture déséquilibrés : écart de " + Format(total.OuvD - total.OuvC) + ".");
                if (total.MvtD != total.MvtC)
                    res.Avertissements.Add("Mouvements de la période déséquilibrés : écart de " + Format(total.MvtD - total.MvtC) + ".");
                if (total.SolD != total.SolC)
                    res.Avertissements.Add("Soldes finaux déséquilibrés : écart de " + Format(total.SolD - total.SolC) + ".");
            }
            return res;
        }

        // ================================================================ Grand livre

        /// <summary>
        /// Grand livre : pour chaque compte, titre, solde d'ouverture, écritures avec solde progressif et total.
        /// <paramref name="ouvertures"/> ne porte que les colonnes d'ouverture.
        /// </summary>
        public static ResultatEtat ConstruireGrandLivre(ParametresEtat p, DateTime? debutReport, IEnumerable<CumulCompte> ouvertures,
                                                        IEnumerable<EcritureEtat> ecritures, Referentiel r)
        {
            var res = Nouveau(p, debutReport);
            var retenu = FiltreComptes(p, r);
            var comparateur = StringComparer.OrdinalIgnoreCase;
            bool general = p.Nature == NatureCompte.General;
            Func<EcritureEtat, string> compteDe = e => ((general ? e.CompteGeneral : e.CompteTiers) ?? "").Trim();

            var ouv = (ouvertures ?? Enumerable.Empty<CumulCompte>())
                .Where(o => retenu(o.Compte))
                .GroupBy(o => o.Compte.Trim(), comparateur)
                .Select(g => new CumulCompte { Compte = g.Key, OuvertureDebit = g.Sum(x => x.OuvertureDebit), OuvertureCredit = g.Sum(x => x.OuvertureCredit) })
                .Where(o => o.OuvertureDebit != 0 || o.OuvertureCredit != 0)
                .ToDictionary(o => o.Compte, comparateur);
            var parCompte = (ecritures ?? Enumerable.Empty<EcritureEtat>())
                .Where(e => retenu(compteDe(e)))
                .GroupBy(compteDe, comparateur)
                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Date).ToList(), comparateur);   // tri stable : l'ordre SQL est gardé dans la journée
            var comptes = ouv.Keys.Union(parCompte.Keys, comparateur).OrderBy(c => c, comparateur).ToList();

            var libelleOuverture = debutReport.HasValue
                ? "Solde d'ouverture (cumul depuis le " + debutReport.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + ")"
                : "Solde d'ouverture";
            decimal totD = 0, totC = 0, totSolde = 0;
            int nombre = 0;
            foreach (var compte in comptes)
            {
                var intitule = Intitule(r, p.Nature, compte);
                res.Lignes.Add(new LigneEtat { Genre = GenreLigne.Entete, Compte = compte, Libelle = compte + "  " + intitule });

                decimal solde = 0, debit = 0, credit = 0;
                CumulCompte ouverture;
                if (ouv.TryGetValue(compte, out ouverture))
                {
                    solde = ouverture.SoldeOuverture;
                    res.Lignes.Add(new LigneEtat
                    {
                        Genre = GenreLigne.Report, Compte = compte, Date = p.Du.Date, Libelle = libelleOuverture,
                        Debit = NonNul(ouverture.OuvertureDebit), Credit = NonNul(ouverture.OuvertureCredit), Solde = solde
                    });
                }
                List<EcritureEtat> lignes;
                if (parCompte.TryGetValue(compte, out lignes))
                {
                    foreach (var e in lignes)
                    {
                        solde += e.Debit - e.Credit;
                        debit += e.Debit;
                        credit += e.Credit;
                        nombre++;
                        res.Lignes.Add(new LigneEtat
                        {
                            Genre = GenreLigne.Detail, Compte = compte, Date = e.Date, Journal = e.Journal, Piece = e.Piece, RefPiece = e.RefPiece,
                            CompteAssocie = general ? e.CompteTiers : e.CompteGeneral, Libelle = e.Libelle,
                            Debit = NonNul(e.Debit), Credit = NonNul(e.Credit), Solde = solde,
                            Lettrage = e.Lettrage, Echeance = e.Echeance, EcNo = e.EcNo
                        });
                    }
                }
                res.Lignes.Add(new LigneEtat
                {
                    Genre = GenreLigne.Total, Compte = compte, Libelle = "Total " + compte + " (mouvements de la période)",
                    Debit = NonNul(debit), Credit = NonNul(credit), Solde = solde
                });
                totD += debit;
                totC += credit;
                totSolde += solde;
            }
            res.Lignes.Add(new LigneEtat
            {
                Genre = GenreLigne.TotalGeneral, Libelle = "Total général (mouvements de la période)",
                Debit = NonNul(totD), Credit = NonNul(totC), Solde = totSolde
            });

            res.TotalDebit = totD;
            res.TotalCredit = totC;
            res.NombreComptes = comptes.Count;
            res.NombreEcritures = nombre;
            var an = AvertissementANouveaux(p, debutReport);
            if (an != null) res.Avertissements.Add(an);
            return res;
        }

        // ================================================================ Journaux

        /// <summary>Journaux : par journal puis par mois, écritures, total du mois, total du journal, total général.</summary>
        public static ResultatEtat ConstruireJournal(ParametresEtat p, IEnumerable<EcritureEtat> ecritures, Referentiel r)
        {
            var res = Nouveau(p, null);
            var filtre = string.IsNullOrWhiteSpace(p.Journal) ? null : p.Journal.Trim();
            decimal totD = 0, totC = 0;
            int nombre = 0, journaux = 0;
            var parJournal = (ecritures ?? Enumerable.Empty<EcritureEtat>())
                .Where(e => filtre == null || string.Equals((e.Journal ?? "").Trim(), filtre, StringComparison.OrdinalIgnoreCase))
                .GroupBy(e => (e.Journal ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var gj in parJournal)
            {
                journaux++;
                var intituleJ = IntituleJournal(r, gj.Key);
                decimal jd = 0, jc = 0;
                int mois = 0;
                foreach (var gm in gj.GroupBy(e => new DateTime(e.Date.Year, e.Date.Month, 1)).OrderBy(g => g.Key))
                {
                    mois++;
                    var nomMois = Mois(gm.Key);
                    res.Lignes.Add(new LigneEtat
                    {
                        Genre = GenreLigne.Entete, Journal = gj.Key, Periode = nomMois,
                        Libelle = gj.Key + (intituleJ.Length > 0 ? " · " + intituleJ : "") + " · " + nomMois
                    });
                    decimal md = 0, mc = 0;
                    foreach (var e in gm.OrderBy(x => x.Date))
                    {
                        md += e.Debit;
                        mc += e.Credit;
                        nombre++;
                        res.Lignes.Add(new LigneEtat
                        {
                            Genre = GenreLigne.Detail, Journal = gj.Key, Periode = nomMois, Date = e.Date, Piece = e.Piece, RefPiece = e.RefPiece,
                            Compte = e.CompteGeneral, CompteTiers = e.CompteTiers, Libelle = e.Libelle,
                            Debit = NonNul(e.Debit), Credit = NonNul(e.Credit), Echeance = e.Echeance, EcNo = e.EcNo
                        });
                    }
                    res.Lignes.Add(new LigneEtat
                    {
                        Genre = GenreLigne.SousTotal, Journal = gj.Key, Periode = nomMois,
                        Libelle = "Total " + gj.Key + " · " + nomMois, Debit = NonNul(md), Credit = NonNul(mc)
                    });
                    if (md != mc)
                        res.Avertissements.Add("Journal " + gj.Key + ", " + nomMois + " : débit et crédit diffèrent de " + Format(md - mc) + ".");
                    jd += md;
                    jc += mc;
                }
                if (mois > 1)
                    res.Lignes.Add(new LigneEtat
                    {
                        Genre = GenreLigne.Total, Journal = gj.Key,
                        Libelle = "Total journal " + gj.Key + (intituleJ.Length > 0 ? " · " + intituleJ : ""), Debit = NonNul(jd), Credit = NonNul(jc)
                    });
                totD += jd;
                totC += jc;
            }
            res.Lignes.Add(new LigneEtat { Genre = GenreLigne.TotalGeneral, Libelle = "Total général", Debit = NonNul(totD), Credit = NonNul(totC) });
            res.TotalDebit = totD;
            res.TotalCredit = totC;
            res.NombreEcritures = nombre;
            res.NombreJournaux = journaux;
            return res;
        }

        /// <summary>Journal centralisateur : pour chaque mois, total de chaque journal, puis total du mois et total général.</summary>
        public static ResultatEtat ConstruireCentralisateur(ParametresEtat p, IEnumerable<CumulJournal> cumuls, Referentiel r)
        {
            var res = Nouveau(p, null);
            var filtre = string.IsNullOrWhiteSpace(p.Journal) ? null : p.Journal.Trim();
            var liste = (cumuls ?? Enumerable.Empty<CumulJournal>())
                .Where(c => filtre == null || string.Equals((c.Journal ?? "").Trim(), filtre, StringComparison.OrdinalIgnoreCase))
                .ToList();
            decimal totD = 0, totC = 0;
            foreach (var gm in liste.GroupBy(c => new DateTime(c.Mois.Year, c.Mois.Month, 1)).OrderBy(g => g.Key))
            {
                var nomMois = Mois(gm.Key);
                decimal md = 0, mc = 0;
                foreach (var gj in gm.GroupBy(c => (c.Journal ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    decimal debit = gj.Sum(x => x.Debit), credit = gj.Sum(x => x.Credit);
                    res.Lignes.Add(new LigneEtat
                    {
                        Genre = GenreLigne.Detail, Periode = nomMois, Date = gm.Key, Journal = gj.Key,
                        Libelle = IntituleJournal(r, gj.Key), Debit = NonNul(debit), Credit = NonNul(credit)
                    });
                    if (debit != credit)
                        res.Avertissements.Add("Journal " + gj.Key + ", " + nomMois + " : débit et crédit diffèrent de " + Format(debit - credit) + ".");
                    md += debit;
                    mc += credit;
                }
                res.Lignes.Add(new LigneEtat
                {
                    Genre = GenreLigne.SousTotal, Periode = nomMois, Date = gm.Key, Libelle = "Total " + nomMois,
                    Debit = NonNul(md), Credit = NonNul(mc)
                });
                totD += md;
                totC += mc;
            }
            res.Lignes.Add(new LigneEtat { Genre = GenreLigne.TotalGeneral, Libelle = "Total général", Debit = NonNul(totD), Credit = NonNul(totC) });
            res.TotalDebit = totD;
            res.TotalCredit = totC;
            res.NombreEcritures = liste.Sum(c => c.Nombre);
            res.NombreJournaux = liste.Select(c => (c.Journal ?? "").Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return res;
        }

        // ================================================================ Requêtes

        private static string Colonne(NatureCompte nature)
        {
            return nature == NatureCompte.General ? "e.CG_Num" : "e.CT_Num";
        }

        /// <summary>Compte renseigné et plage. Paramètres : @de, @a (seulement si la borne est fournie).</summary>
        public static string ConditionComptes(NatureCompte nature, PlageComptes plage)
        {
            var col = Colonne(nature);
            var sb = new StringBuilder();
            sb.Append(col).Append(" IS NOT NULL AND ").Append(col).Append(" <> ''");
            if (plage != null && plage.De.Length > 0) sb.Append(" AND ").Append(col).Append(" >= @de");
            if (plage != null && plage.A.Length > 0) sb.Append(" AND LEFT(").Append(col).Append(", LEN(@a)) <= @a");
            return sb.ToString();
        }

        /// <summary>Une écriture va dans l'ouverture si elle précède la période ou appartient au journal des à-nouveaux.</summary>
        public static string ConditionOuverture(bool avecJournalANouveaux)
        {
            return avecJournalANouveaux ? "(" + SqlOutils.DateEcriture + " < @du OR e.JO_Num = @jan)" : "(" + SqlOutils.DateEcriture + " < @du)";
        }

        /// <summary>Cumuls par compte, de @debut (début d'exercice) à la fin de la période. Paramètres : @debut, @du, @auExclu, [@jan], [@de], [@a].</summary>
        public static string RequeteBalance(NatureCompte nature, PlageComptes plage, bool avecJournalANouveaux)
        {
            var col = Colonne(nature);
            var ouv = ConditionOuverture(avecJournalANouveaux);
            return "SELECT " + col + " AS Compte, " +
                   "SUM(CASE WHEN " + ouv + " AND e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS OuvDebit, " +
                   "SUM(CASE WHEN " + ouv + " AND e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS OuvCredit, " +
                   "SUM(CASE WHEN NOT " + ouv + " AND e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS MvtDebit, " +
                   "SUM(CASE WHEN NOT " + ouv + " AND e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS MvtCredit " +
                   "FROM dbo.F_ECRITUREC e WHERE " + SqlOutils.DateEcriture + " >= @debut AND " + SqlOutils.DateEcriture + " < @auExclu AND " + ConditionComptes(nature, plage) +
                   " GROUP BY " + col;
        }

        /// <summary>Soldes d'ouverture du grand livre. Paramètres : @debut, @du, @auExclu, [@jan], [@de], [@a].</summary>
        public static string RequeteOuverturesGrandLivre(NatureCompte nature, PlageComptes plage, bool avecJournalANouveaux)
        {
            var col = Colonne(nature);
            return "SELECT " + col + " AS Compte, " +
                   "SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS OuvDebit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS OuvCredit " +
                   "FROM dbo.F_ECRITUREC e WHERE " + SqlOutils.DateEcriture + " >= @debut AND " + SqlOutils.DateEcriture + " < @auExclu AND " + ConditionOuverture(avecJournalANouveaux) +
                   " AND " + ConditionComptes(nature, plage) + " GROUP BY " + col;
        }

        private static string ColonnesEcriture(bool colonneLettrage, bool colonneReference = false)
        {
            return "SELECT e.EC_No, e.JO_Num, " + SqlOutils.DateEcriture + " AS EC_Date, e.EC_Piece, e.EC_RefPiece, " +
                   (colonneReference ? "e.EC_Reference, " : "") + "e.CG_Num, e.CT_Num, e.EC_Intitule, " +
                   "e.EC_Sens, e.EC_Montant, e.EC_Echeance, " +
                   (colonneLettrage ? "e.EC_Lettrage" : "CAST(NULL AS varchar(3)) AS EC_Lettrage") + " FROM dbo.F_ECRITUREC e ";
        }

        /// <summary>Écritures du grand livre (hors journal des à-nouveaux). Paramètres : @du, @auExclu, [@jan], [@de], [@a].</summary>
        public static string RequeteLignesGrandLivre(NatureCompte nature, PlageComptes plage, bool avecJournalANouveaux, bool colonneLettrage)
        {
            var col = Colonne(nature);
            return ColonnesEcriture(colonneLettrage) +
                   "WHERE " + SqlOutils.DateEcriture + " >= @du AND " + SqlOutils.DateEcriture + " < @auExclu" + (avecJournalANouveaux ? " AND e.JO_Num <> @jan" : "") +
                   " AND " + ConditionComptes(nature, plage) +
                   " ORDER BY " + col + ", " + SqlOutils.DateEcriture + ", e.JO_Num, e.EC_Piece, e.EC_No";
        }

        /// <summary>Écritures des journaux. Paramètres : @du, @auExclu, [@journal]. <paramref name="colonneReference"/> : lit aussi EC_Reference.</summary>
        public static string RequeteJournal(bool unJournal, bool colonneReference = false)
        {
            return ColonnesEcriture(false, colonneReference) +
                   "WHERE " + SqlOutils.DateEcriture + " >= @du AND " + SqlOutils.DateEcriture + " < @auExclu" + (unJournal ? " AND e.JO_Num = @journal" : "") +
                   " ORDER BY e.JO_Num, " + SqlOutils.DateEcriture + ", e.EC_Piece, e.EC_No";
        }

        /// <summary>Totaux par journal et par mois. Paramètres : @du, @auExclu, [@journal].</summary>
        public static string RequeteCentralisateur(bool unJournal)
        {
            return "SELECT e.JO_Num, YEAR(" + SqlOutils.DateEcriture + ") AS Annee, MONTH(" + SqlOutils.DateEcriture + ") AS Mois, " +
                   "SUM(CASE WHEN e.EC_Sens = 0 THEN e.EC_Montant ELSE 0 END) AS Debit, " +
                   "SUM(CASE WHEN e.EC_Sens = 1 THEN e.EC_Montant ELSE 0 END) AS Credit, COUNT(*) AS Nombre " +
                   "FROM dbo.F_ECRITUREC e WHERE " + SqlOutils.DateEcriture + " >= @du AND " + SqlOutils.DateEcriture + " < @auExclu" + (unJournal ? " AND e.JO_Num = @journal" : "") +
                   " GROUP BY e.JO_Num, YEAR(" + SqlOutils.DateEcriture + "), MONTH(" + SqlOutils.DateEcriture + ")";
        }

        // ================================================================ Exécution

        public static async Task<ResultatEtat> GenererAsync(SqlConnection c, ParametresEtat p, Referentiel referentiel,
                                                           CancellationToken ct = default(CancellationToken))
        {
            var debutReport = Valider(p, referentiel);
            var jan = string.IsNullOrWhiteSpace(p.JournalANouveaux) ? null : p.JournalANouveaux.Trim();
            var journal = string.IsNullOrWhiteSpace(p.Journal) ? null : p.Journal.Trim();
            var plage = p.Plage;
            var du = p.Du.Date;
            var debut = debutReport ?? du;

            switch (p.Etat)
            {
                case TypeEtat.Balance:
                {
                    var cumuls = new List<CumulCompte>();
                    using (var cmd = SqlOutils.Commande(c, RequeteBalance(p.Nature, plage, jan != null), DelaiRequete))
                    {
                        Parametres(cmd, p, debut, jan, null, plage);
                        using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                            while (await rd.ReadAsync(ct).ConfigureAwait(false))
                                cumuls.Add(new CumulCompte
                                {
                                    Compte = (SqlOutils.Texte(rd["Compte"]) ?? "").Trim(),
                                    OuvertureDebit = Montant(rd["OuvDebit"]), OuvertureCredit = Montant(rd["OuvCredit"]),
                                    MouvementDebit = Montant(rd["MvtDebit"]), MouvementCredit = Montant(rd["MvtCredit"])
                                });
                    }
                    return ConstruireBalance(p, debutReport, cumuls, referentiel);
                }

                case TypeEtat.GrandLivre:
                {
                    var ouvertures = new List<CumulCompte>();
                    if (debutReport.HasValue || jan != null)
                    {
                        using (var cmd = SqlOutils.Commande(c, RequeteOuverturesGrandLivre(p.Nature, plage, jan != null), DelaiRequete))
                        {
                            Parametres(cmd, p, debut, jan, null, plage);
                            using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                                while (await rd.ReadAsync(ct).ConfigureAwait(false))
                                    ouvertures.Add(new CumulCompte
                                    {
                                        Compte = (SqlOutils.Texte(rd["Compte"]) ?? "").Trim(),
                                        OuvertureDebit = Montant(rd["OuvDebit"]), OuvertureCredit = Montant(rd["OuvCredit"])
                                    });
                        }
                    }
                    bool colonneLettrage = await ServiceConsultation.ColonneExisteAsync(c, "EC_Lettrage", ct).ConfigureAwait(false);
                    var lignes = await LireEcrituresAsync(c, RequeteLignesGrandLivre(p.Nature, plage, jan != null, colonneLettrage),
                                                          cmd => Parametres(cmd, p, null, jan, null, plage), ct).ConfigureAwait(false);
                    return ConstruireGrandLivre(p, debutReport, ouvertures, lignes, referentiel);
                }

                case TypeEtat.Journal:
                {
                    var lignes = await LireEcrituresAsync(c, RequeteJournal(journal != null),
                                                          cmd => Parametres(cmd, p, null, null, journal, null), ct).ConfigureAwait(false);
                    return ConstruireJournal(p, lignes, referentiel);
                }

                default:
                {
                    var cumuls = new List<CumulJournal>();
                    using (var cmd = SqlOutils.Commande(c, RequeteCentralisateur(journal != null), DelaiRequete))
                    {
                        Parametres(cmd, p, null, null, journal, null);
                        using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                            while (await rd.ReadAsync(ct).ConfigureAwait(false))
                                cumuls.Add(new CumulJournal
                                {
                                    Journal = (SqlOutils.Texte(rd["JO_Num"]) ?? "").Trim(),
                                    Mois = new DateTime(SqlOutils.Entier(rd["Annee"]), SqlOutils.Entier(rd["Mois"]), 1),
                                    Debit = Montant(rd["Debit"]), Credit = Montant(rd["Credit"]), Nombre = SqlOutils.Entier(rd["Nombre"])
                                });
                    }
                    return ConstruireCentralisateur(p, cumuls, referentiel);
                }
            }
        }

        private static void Parametres(SqlCommand cmd, ParametresEtat p, DateTime? debut, string jan, string journal, PlageComptes plage)
        {
            if (debut.HasValue) cmd.Parameters.Add(new SqlParameter("@debut", SqlDbType.DateTime) { Value = debut.Value });
            cmd.Parameters.Add(new SqlParameter("@du", SqlDbType.DateTime) { Value = p.Du.Date });
            cmd.Parameters.Add(new SqlParameter("@auExclu", SqlDbType.DateTime) { Value = p.Au.Date.AddDays(1) });
            if (jan != null) cmd.Parameters.Add(new SqlParameter("@jan", SqlDbType.VarChar, 13) { Value = jan });
            if (journal != null) cmd.Parameters.Add(new SqlParameter("@journal", SqlDbType.VarChar, 13) { Value = journal });
            if (plage != null && plage.De.Length > 0) cmd.Parameters.Add(new SqlParameter("@de", SqlDbType.VarChar, 17) { Value = plage.De });
            if (plage != null && plage.A.Length > 0) cmd.Parameters.Add(new SqlParameter("@a", SqlDbType.VarChar, 17) { Value = plage.A });
        }

        internal static async Task<List<EcritureEtat>> LireEcrituresAsync(SqlConnection c, string sql, Action<SqlCommand> parametres, CancellationToken ct)
        {
            var liste = new List<EcritureEtat>();
            using (var cmd = SqlOutils.Commande(c, sql, DelaiRequete))
            {
                parametres(cmd);
                using (var rd = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    int iReference = -1;
                    for (int k = 0; k < rd.FieldCount; k++)
                        if (string.Equals(rd.GetName(k), "EC_Reference", StringComparison.OrdinalIgnoreCase)) iReference = k;
                    while (await rd.ReadAsync(ct).ConfigureAwait(false))
                    {
                        bool credit = SqlOutils.Entier(rd["EC_Sens"]) == 1;
                        decimal montant = Montant(rd["EC_Montant"]);
                        liste.Add(new EcritureEtat
                        {
                            EcNo = SqlOutils.Entier(rd["EC_No"]),
                            Journal = (SqlOutils.Texte(rd["JO_Num"]) ?? "").Trim(),
                            Date = SqlOutils.Date(rd["EC_Date"]) ?? DateTime.MinValue,
                            Piece = (SqlOutils.Texte(rd["EC_Piece"]) ?? "").Trim(),
                            RefPiece = (SqlOutils.Texte(rd["EC_RefPiece"]) ?? "").Trim(),
                            Reference = iReference < 0 ? "" : (SqlOutils.Texte(rd.GetValue(iReference)) ?? "").Trim(),
                            CompteGeneral = (SqlOutils.Texte(rd["CG_Num"]) ?? "").Trim(),
                            CompteTiers = (SqlOutils.Texte(rd["CT_Num"]) ?? "").Trim(),
                            Libelle = (SqlOutils.Texte(rd["EC_Intitule"]) ?? "").Trim(),
                            Debit = credit ? 0m : montant,
                            Credit = credit ? montant : 0m,
                            Echeance = SqlOutils.Date(rd["EC_Echeance"]),
                            Lettrage = (SqlOutils.Texte(rd["EC_Lettrage"]) ?? "").Trim()
                        });
                    }
                }
            }
            return liste;
        }

        private static decimal Montant(object o)
        {
            return o == null || o == DBNull.Value ? 0m : Convert.ToDecimal(o);
        }
    }
}
