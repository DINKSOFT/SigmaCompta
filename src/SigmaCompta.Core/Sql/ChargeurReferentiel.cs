using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Sql
{
    /// <summary>Charge journaux, comptes, tiers, exercices et périodes clôturées de la base comptable.</summary>
    public static class ChargeurReferentiel
    {
        public static async Task<Referentiel> ChargerAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            var r = new Referentiel();

            // F_JOURNAUX.CG_Num (compte de trésorerie) n'est lu que s'il existe.
            var aCompteTreso = await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_JOURNAUX', N'CG_Num')", ct).ConfigureAwait(false) != null;
            foreach (DataRow l in (await SqlOutils.TableAsync(c,
                "SELECT JO_Num, JO_Intitule, JO_Type, JO_NumPiece, JO_Sommeil, " +
                (aCompteTreso ? "CG_Num" : "CAST(NULL AS varchar(13)) AS CG_Num") +
                " FROM dbo.F_JOURNAUX ORDER BY JO_Num", ct).ConfigureAwait(false)).Rows)
            {
                var j = new Journal
                {
                    Code = SqlOutils.Texte(l["JO_Num"]),
                    Intitule = SqlOutils.Texte(l["JO_Intitule"]),
                    Type = SqlOutils.Entier(l["JO_Type"]),
                    ModeNumerotation = SqlOutils.Entier(l["JO_NumPiece"], 1),
                    EnSommeil = SqlOutils.Entier(l["JO_Sommeil"]) == 1,
                    CompteTresorerie = (SqlOutils.Texte(l["CG_Num"]) ?? "").Trim()
                };
                r.Journaux[j.Code] = j;
            }

            foreach (DataRow l in (await SqlOutils.TableAsync(c,
                "SELECT CG_Num, CG_Intitule, CG_Type FROM dbo.F_COMPTEG", ct).ConfigureAwait(false)).Rows)
            {
                var g = new CompteGeneral
                {
                    Numero = SqlOutils.Texte(l["CG_Num"]),
                    Intitule = SqlOutils.Texte(l["CG_Intitule"]),
                    Type = SqlOutils.Entier(l["CG_Type"])
                };
                r.Comptes[g.Numero] = g;
            }

            // F_COMPTET.CT_Type (client, fournisseur, salarié, autre) n'est lu que s'il existe.
            var aTypeTiers = await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_COMPTET', N'CT_Type')", ct).ConfigureAwait(false) != null;
            // Compte collectif principal du tiers (proposé à la saisie), lu s'il existe.
            var aCollectif = await SqlOutils.ScalaireAsync(c, "SELECT COL_LENGTH(N'dbo.F_COMPTET', N'CG_NumPrinc')", ct).ConfigureAwait(false) != null;
            foreach (DataRow l in (await SqlOutils.TableAsync(c,
                "SELECT CT_Num, CT_Intitule, CT_Prospect, " +
                (aTypeTiers ? "CT_Type" : "CAST(NULL AS smallint) AS CT_Type") + ", " +
                (aCollectif ? "CG_NumPrinc" : "CAST(NULL AS varchar(13)) AS CG_NumPrinc") +
                " FROM dbo.F_COMPTET", ct, 120).ConfigureAwait(false)).Rows)
            {
                var t = new Tiers
                {
                    Numero = SqlOutils.Texte(l["CT_Num"]),
                    Intitule = SqlOutils.Texte(l["CT_Intitule"]),
                    EstProspect = SqlOutils.Entier(l["CT_Prospect"]) != 0,
                    Type = SqlOutils.Entier(l["CT_Type"], -1),
                    Collectif = (SqlOutils.Texte(l["CG_NumPrinc"]) ?? "").Trim()
                };
                r.Tiers[t.Numero] = t;
            }

            var dos = await SqlOutils.TableAsync(c,
                "SELECT D_DebutExo01, D_FinExo01, D_DebutExo02, D_FinExo02, D_DebutExo03, D_FinExo03, " +
                "D_DebutExo04, D_FinExo04, D_DebutExo05, D_FinExo05 FROM dbo.P_DOSSIER", ct).ConfigureAwait(false);
            if (dos.Rows.Count > 0)
            {
                var d = dos.Rows[0];
                for (int i = 1; i <= 5; i++)
                {
                    var deb = SqlOutils.Date(d["D_DebutExo0" + i]);
                    var fin = SqlOutils.Date(d["D_FinExo0" + i]);
                    if (deb.HasValue && fin.HasValue && fin >= deb)
                        r.Exercices.Add(new Exercice { Debut = deb.Value, Fin = fin.Value });
                }
            }

            foreach (DataRow l in (await SqlOutils.TableAsync(c,
                "SELECT JO_Num, JM_Date FROM dbo.F_JMOUV WHERE JM_Cloture = 2", ct).ConfigureAwait(false)).Rows)
            {
                var date = SqlOutils.Date(l["JM_Date"]);
                if (date.HasValue) r.PeriodesCloturees.Add(Referentiel.ClePeriode(SqlOutils.Texte(l["JO_Num"]), date.Value));
            }

            r.ChargeLe = DateTime.Now;
            return r;
        }
    }

    /// <summary>Traduction des codes d'erreur des déclencheurs de la base (CB_Error) et de la procédure.</summary>
    public static class ErreursComptables
    {
        private static readonly Dictionary<string, string> Codes = new Dictionary<string, string>
        {
            { "80003", "Journal/période verrouillé : un utilisateur est probablement en saisie sur ce journal." },
            { "80004", "Table F_ECRITUREC verrouillée par une autre session." },
            { "80008", "Table F_ECRITUREC verrouillée par une autre session." },
            { "80012", "Synchronisation comptable en cours sur une autre session." },
            { "81035", "Compte général inexistant ou non imputable (compte de total)." },
            { "81149", "Ligne F_JMOUV (journal + mois) manquante." },
            { "81150", "Écriture liée (EC_NoLink) inexistante." },
            { "81234", "Norme IFRS refusée : la gestion IFRS n'est pas activée." },
            { "81235", "Incohérence de norme IFRS avec le journal." },
            { "81236", "Incohérence de norme IFRS avec le dossier." },
            { "81243", "Règlement interdit sur ce type de journal." },
            { "81244", "Date de règlement interdite sur ce type de journal." },
            { "81379", "Le tiers est un prospect : écriture interdite." },
        };

        public static string Expliquer(Exception ex)
        {
            var sql = ex as SqlException;
            var msg = ex.Message;
            if (sql != null)
            {
                switch (sql.Number)
                {
                    case 50001: return msg;
                    case 50002: return "Un autre import est en cours. Réessaie dans quelques secondes.";
                    case 50003: case 50004: case 50005: case 50006: return msg;
                    case 1205: return "Interblocage SQL (deadlock) : réessayer l'import de cette pièce.";
                    case 2812: return "Procédure imp.PS_ImportPiece introuvable : installe-la depuis l'écran « Vérification ».";
                    case 8152: case 2628: return "Valeur trop longue pour une colonne : " + msg;
                }
            }
            foreach (Match m in Regex.Matches(msg, @"\b8\d{4}\b"))
            {
                string explication;
                if (Codes.TryGetValue(m.Value, out explication))
                    return explication + " [Code " + m.Value + "] " + msg;
            }
            return msg;
        }
    }
}
