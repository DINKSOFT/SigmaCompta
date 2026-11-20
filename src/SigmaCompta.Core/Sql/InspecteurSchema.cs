using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Sql
{
    /// <summary>
    /// Version structurée du script 01_preverification_schema.sql : chaque contrôle
    /// produit un statut lisible. Toutes les requêtes sont construites d'après
    /// sys.columns pour ne jamais échouer sur une colonne absente.
    /// </summary>
    public static class InspecteurSchema
    {
        private static readonly string[] TablesComptables = { "F_ECRITUREC", "F_JMOUV", "F_JOURNAUX", "F_COMPTEG", "F_COMPTET", "P_DOSSIER" };
        private static readonly string[] ColonnesJmouv = { "JO_Num", "cbJO_Num", "JM_Date", "JM_Cloture", "JM_Impression" };
        private static readonly string[] ColonnesJournaux = { "JO_Num", "JO_Intitule", "JO_Type", "JO_NumPiece", "JO_Sommeil" };
        private static readonly string[] ColonnesDossier =
        {
            "D_DebutExo01", "D_FinExo01", "D_DebutExo02", "D_FinExo02", "D_DebutExo03", "D_FinExo03",
            "D_DebutExo04", "D_FinExo04", "D_DebutExo05", "D_FinExo05"
        };

        /// <summary>
        /// Contrôle 1. THROW, TRY_CAST et DATEFROMPARTS, utilisés par la procédure, dépendent de la version
        /// du serveur (SQL Server 2012 = 11.x) et non du niveau de compatibilité de la base : vérifié sur une
        /// base au niveau 100 d'un serveur 11.0. Seul un serveur antérieur à 2012 bloque l'import.
        /// </summary>
        public static Controle ControleVersion(string versionServeur, int niveau)
        {
            const string titre = "Version SQL Server et compatibilité";
            int majeure = VersionMajeure(versionServeur);
            string detail = "SQL Server " + (string.IsNullOrWhiteSpace(versionServeur) ? "(version inconnue)" : versionServeur.Trim())
                            + " — niveau de compatibilité de la base : " + niveau;

            if (majeure > 0 && majeure < 11)
                return Controle.Creer(titre, StatutControle.Erreur,
                    detail + " (serveur SQL Server 2012 minimum requis : THROW, TRY_CAST, DATEFROMPARTS).", true);

            if (majeure == 0)
                // Version illisible : on garde l'ancienne règle, fondée sur le niveau de la base.
                return niveau >= 110
                    ? Controle.Creer(titre, StatutControle.Ok, detail)
                    : Controle.Creer(titre, StatutControle.Erreur,
                        detail + " (version du serveur illisible : niveau 110 requis par prudence).", true);

            if (niveau < 110)
                return Controle.Creer(titre, StatutControle.Info,
                    detail + ". Niveau inférieur à 110 accepté : le serveur est en version 2012 ou plus, où THROW, TRY_CAST "
                    + "et DATEFROMPARTS fonctionnent quel que soit le niveau de la base. Ne pas modifier ce niveau sans "
                    + "vérifier ce que le logiciel comptable accepte.");

            return Controle.Creer(titre, StatutControle.Ok, detail);
        }

        /// <summary>N° de version majeure de SERVERPROPERTY('ProductVersion') (« 11.0.2100.60 » → 11) ; 0 si illisible.</summary>
        public static int VersionMajeure(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return 0;
            int n;
            return int.TryParse(version.Trim().Split('.')[0], out n) ? n : 0;
        }

        private sealed class InfoColonne
        {
            public string Table, Nom, Type;
            public bool Nullable, Calculee, Identite, ADefaut;
        }

        public static async Task<List<Controle>> ControlerAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            var res = new List<Controle>();

            // 1. Version et compatibilité
            var v = (await SqlOutils.TableAsync(c,
                "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS v, " +
                "(SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) AS niv", ct).ConfigureAwait(false)).Rows[0];
            res.Add(ControleVersion(SqlOutils.Texte(v["v"]), SqlOutils.Entier(v["niv"])));

            // 2. Tables comptables
            var noms = string.Join(", ", TablesComptables.Select(SqlOutils.Litteral));
            var dtTables = await SqlOutils.TableAsync(c,
                "SELECT t.name FROM sys.tables t WHERE SCHEMA_NAME(t.schema_id) = N'dbo' AND t.name IN (" + noms + ")", ct).ConfigureAwait(false);
            var presentes = new HashSet<string>(dtTables.Rows.Cast<DataRow>().Select(r => SqlOutils.Texte(r[0])), StringComparer.OrdinalIgnoreCase);
            var manquantes = TablesComptables.Where(t => !presentes.Contains(t)).ToList();
            res.Add(Controle.Creer("Tables comptables",
                manquantes.Count == 0 ? StatutControle.Ok : StatutControle.Erreur,
                manquantes.Count == 0 ? string.Join(", ", TablesComptables) + " présentes." : "Tables absentes : " + string.Join(", ", manquantes) + ". Ce n'est pas une base comptable compatible.",
                manquantes.Count > 0));
            if (manquantes.Contains("F_ECRITUREC")) return res;

            // 3. Colonnes
            var dtCol = await SqlOutils.TableAsync(c,
                "SELECT t.name AS tab, c.name AS col, TYPE_NAME(c.user_type_id) AS typ, c.is_nullable, c.is_computed, c.is_identity, " +
                "CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END AS a_defaut " +
                "FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id " +
                "WHERE SCHEMA_NAME(t.schema_id) = N'dbo' AND t.name IN (" + noms + ")", ct).ConfigureAwait(false);
            var colonnes = dtCol.Rows.Cast<DataRow>().Select(r => new InfoColonne
            {
                Table = SqlOutils.Texte(r["tab"]),
                Nom = SqlOutils.Texte(r["col"]),
                Type = SqlOutils.Texte(r["typ"]),
                Nullable = Convert.ToBoolean(r["is_nullable"]),
                Calculee = Convert.ToBoolean(r["is_computed"]),
                Identite = Convert.ToBoolean(r["is_identity"]),
                ADefaut = SqlOutils.Entier(r["a_defaut"]) == 1
            }).ToList();
            Func<string, string, InfoColonne> col = (t, n) => colonnes.FirstOrDefault(x =>
                string.Equals(x.Table, t, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Nom, n, StringComparison.OrdinalIgnoreCase));

            var requisesEc = ExplorateurBases.ColonnesEcriture.Select(x => x[0]).ToArray();
            res.Add(ControleColonnes("Colonnes de F_ECRITUREC utilisées par l'import", "F_ECRITUREC", requisesEc, col));
            res.Add(ControleColonnes("Colonnes de F_JMOUV (période journal)", "F_JMOUV", ColonnesJmouv, col));
            res.Add(ControleColonnes("Colonnes de F_JOURNAUX", "F_JOURNAUX", ColonnesJournaux, col));
            res.Add(ControleColonnes("Exercices dans P_DOSSIER", "P_DOSSIER", ColonnesDossier, col));

            // 4. NOT NULL sans défaut non fournies par la procédure
            var fournies = new HashSet<string>(requisesEc.Concat(ColonnesJmouv), StringComparer.OrdinalIgnoreCase);
            var nonFournies = colonnes.Where(x => (x.Table == "F_ECRITUREC" || x.Table == "F_JMOUV")
                                                 && !x.Nullable && !x.ADefaut && !x.Identite && !x.Calculee
                                                 && x.Type != "timestamp" && x.Type != "rowversion"
                                                 && !fournies.Contains(x.Nom)).ToList();
            res.Add(Controle.Creer("Colonnes obligatoires sans valeur par défaut",
                nonFournies.Count == 0 ? StatutControle.Ok : StatutControle.Erreur,
                nonFournies.Count == 0
                    ? "Toutes les colonnes non fournies par l'import ont une valeur par défaut."
                    : "À ajouter à l'INSERT de la procédure : " + string.Join(", ", nonFournies.Select(x => x.Table + "." + x.Nom)),
                nonFournies.Count > 0));

            // 5. Fonction CB_FntStr
            var fnt = await SqlOutils.ScalaireAsync(c,
                "IF OBJECT_ID(N'dbo.CB_FntStr') IS NULL SELECT CAST(NULL AS varbinary(50)) ELSE EXEC (N'SELECT dbo.CB_FntStr(''OD'')')", ct).ConfigureAwait(false);
            var octets = fnt as byte[];
            bool fntOk = octets != null && octets.Length == 2 && octets[0] == 0x4F && octets[1] == 0x44;
            res.Add(Controle.Creer("Fonction d'encodage dbo.CB_FntStr",
                fnt == null ? StatutControle.Erreur : fntOk ? StatutControle.Ok : StatutControle.Avertissement,
                fnt == null ? "Fonction absente : les clés cb* ne peuvent pas être calculées."
                    : fntOk ? "CB_FntStr('OD') = 0x4F44 (format confirmé)."
                    : "CB_FntStr('OD') = 0x" + BitConverter.ToString(octets ?? new byte[0]).Replace("-", "") + " (attendu 0x4F44) : vérifier l'encodage.",
                fnt == null));

            // 6. Encodage cb* sur les dernières écritures saisies par le logiciel comptable
            //    Seules les clés réellement écrites par l'import (non calculées) sont comparées.
            var paires = new[] { "JO_Num", "CG_Num", "CT_Num", "EC_Piece", "EC_RefPiece" }
                .Where(n => col("F_ECRITUREC", n) != null && col("F_ECRITUREC", "cb" + n) != null && col("F_ECRITUREC", "cbMarq") != null
                            && !col("F_ECRITUREC", "cb" + n).Calculee)
                .ToList();
            var clesCalculees = new[] { "JO_Num", "CG_Num", "CT_Num", "EC_Piece", "EC_RefPiece" }
                .Where(n => col("F_ECRITUREC", "cb" + n) != null && col("F_ECRITUREC", "cb" + n).Calculee)
                .Select(n => "cb" + n).ToList();
            if (paires.Count == 0 && clesCalculees.Count > 0)
            {
                res.Add(Controle.Creer("Encodage des clés cb*", StatutControle.Ok,
                    "Clés calculées par SQL Server (" + string.Join(", ", clesCalculees) + ") : la procédure ne les écrit pas, elles sont toujours conformes."));
            }
            if (fnt != null && paires.Count > 0)
            {
                var sql = "SELECT " + string.Join(", ", paires.Select(n =>
                              "SUM(CASE WHEN cb" + n + " <> ISNULL(dbo.CB_FntStr(" + n + "), 0x) THEN 1 ELSE 0 END) AS " + n)) +
                          ", COUNT(*) AS total FROM (SELECT TOP (2000) * FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC) e";
                var r = (await SqlOutils.TableAsync(c, sql, ct, 120).ConfigureAwait(false)).Rows[0];
                var ecarts = paires.Where(n => SqlOutils.Entier(r[n]) > 0).Select(n => "cb" + n + " (" + SqlOutils.Entier(r[n]) + ")").ToList();
                int total = SqlOutils.Entier(r["total"]);
                res.Add(Controle.Creer("Encodage des clés cb* conforme",
                    total == 0 ? StatutControle.Info : ecarts.Count == 0 ? StatutControle.Ok : StatutControle.Avertissement,
                    total == 0 ? "Aucune écriture existante pour comparer."
                        : ecarts.Count == 0 ? "CB_FntStr reproduit exactement les clés des " + total + " dernières écritures (" + string.Join(", ", paires.Select(p => "cb" + p)) + ")."
                        : "Écarts sur les " + total + " dernières écritures : " + string.Join(", ", ecarts) + ". Vérifier l'encodage avant import."));
            }

            // 7. Déclencheurs de la base
            var dtTrig = await SqlOutils.TableAsync(c,
                "SELECT name, is_disabled FROM sys.triggers WHERE parent_id = OBJECT_ID(N'dbo.F_ECRITUREC')", ct).ConfigureAwait(false);
            var trig = dtTrig.Rows.Cast<DataRow>().ToDictionary(x => SqlOutils.Texte(x["name"]), x => Convert.ToBoolean(x["is_disabled"]), StringComparer.OrdinalIgnoreCase);
            bool cbins, ins;
            bool cbinsOk = trig.TryGetValue("TG_CBINS_F_ECRITUREC", out cbins) && !cbins;
            bool insOk = trig.TryGetValue("TG_INS_F_ECRITUREC", out ins) && !ins;
            res.Add(Controle.Creer("Déclencheurs sur F_ECRITUREC",
                cbinsOk && insOk ? StatutControle.Ok : StatutControle.Avertissement,
                cbinsOk && insOk ? "TG_CBINS_F_ECRITUREC (numérotation EC_No) et TG_INS_F_ECRITUREC (contrôles) actifs."
                    : "Déclencheurs trouvés : " + (trig.Count == 0 ? "aucun" : string.Join(", ", trig.Select(t => t.Key + (t.Value ? " (désactivé)" : "")))) +
                      ". Sans TG_CBINS, EC_No ne sera pas attribué et l'import sera refusé."));

            // 8. Chaînage d'inaltérabilité
            if (col("F_ECRITUREC", "cbHashVersion") != null && col("F_ECRITUREC", "cbMarq") != null)
            {
                var h = await SqlOutils.ScalaireAsync(c, "SELECT TOP (1) CAST(cbHashVersion AS nvarchar(50)) FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC", ct).ConfigureAwait(false);
                res.Add(Controle.Creer("Chaînage d'inaltérabilité (cbHash*)",
                    h == null ? StatutControle.Ok : StatutControle.Avertissement,
                    h == null ? "Inactif sur la dernière écriture : une insertion SQL ne crée pas d'incohérence de chaînage."
                              : "Actif (cbHashVersion = " + h + ") : la procédure refusera l'import sauf option « Ignorer le chaînage ». Préférer l'import natif du logiciel comptable."));
            }
            else
            {
                res.Add(Controle.Creer("Chaînage d'inaltérabilité (cbHash*)", StatutControle.Info, "Colonnes cbHash* absentes de cette version de la base."));
            }

            // 9. Droits
            var perm = (await SqlOutils.TableAsync(c,
                "SELECT ISNULL(HAS_PERMS_BY_NAME(N'dbo.F_ECRITUREC', N'OBJECT', N'INSERT'), 0) AS ins, " +
                "ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'CREATE PROCEDURE'), 0) AS crp, " +
                "ISNULL(HAS_PERMS_BY_NAME(N'dbo.F_JMOUV', N'OBJECT', N'INSERT'), 0) AS jm", ct).ConfigureAwait(false)).Rows[0];
            bool droitsOk = SqlOutils.Entier(perm["ins"]) == 1 && SqlOutils.Entier(perm["jm"]) == 1;
            res.Add(Controle.Creer("Droits du login connecté",
                droitsOk ? (SqlOutils.Entier(perm["crp"]) == 1 ? StatutControle.Ok : StatutControle.Info) : StatutControle.Erreur,
                (droitsOk ? "INSERT autorisé sur F_ECRITUREC et F_JMOUV." : "INSERT refusé sur F_ECRITUREC ou F_JMOUV.") +
                (SqlOutils.Entier(perm["crp"]) == 1 ? " Création de procédure autorisée." : " Création de procédure non autorisée : l'installation devra être faite par un administrateur."),
                !droitsOk));

            // 10. Procédure installée
            var dtProc = await SqlOutils.TableAsync(c,
                "SELECT OBJECT_DEFINITION(OBJECT_ID(N'imp.PS_ImportPiece')) AS def, TYPE_ID(N'imp.TT_LigneEcriture') AS typ, " +
                "(SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(N'imp.PS_ImportPiece')) AS modif", ct).ConfigureAwait(false);
            var def = SqlOutils.Texte(dtProc.Rows[0]["def"]);
            var versionBase = ExplorateurBases.ExtraireVersion(def);
            var versionApp = ScriptsEmbarques.VersionProcedureEmbarquee;
            var modif = SqlOutils.Date(dtProc.Rows[0]["modif"]);
            if (def == null)
                res.Add(Controle.Creer("Procédure imp.PS_ImportPiece", StatutControle.Erreur,
                    "Non installée dans cette base. Utilise « Installer la procédure » (version " + versionApp + ").", true));
            else if (versionBase != versionApp)
                // Bloquant : l'application passe des paramètres que l'ancienne version ne connaît pas.
                res.Add(Controle.Creer("Procédure imp.PS_ImportPiece", StatutControle.Erreur,
                    "Version installée " + versionBase + ", version de l'application " + versionApp + " : mise à jour obligatoire. Utilise « Installer la procédure ».", true));
            else
                res.Add(Controle.Creer("Procédure imp.PS_ImportPiece", StatutControle.Ok,
                    "Version " + versionBase + " installée" + (modif.HasValue ? " le " + modif.Value.ToString("dd/MM/yyyy à HH:mm") : "") + "."));

            // 11. Numérotation des journaux (information)
            if (col("F_JOURNAUX", "JO_NumPiece") != null)
            {
                var dtJ = await SqlOutils.TableAsync(c, "SELECT JO_NumPiece AS m, COUNT(*) AS n FROM dbo.F_JOURNAUX GROUP BY JO_NumPiece ORDER BY JO_NumPiece", ct).ConfigureAwait(false);
                var parts = dtJ.Rows.Cast<DataRow>().Select(r => new Journal { ModeNumerotation = SqlOutils.Entier(r["m"]) }.NumerotationLibelle + " : " + SqlOutils.Entier(r["n"]));
                res.Add(Controle.Creer("Numérotation des pièces par journal", StatutControle.Info,
                    string.Join(" · ", parts) + " (codes JO_NumPiece supposés 0 manuelle / 1 continue journal / 2 continue base / 3 mensuelle — à confirmer dans le logiciel comptable)."));
            }

            return res;
        }

        /// <summary>Clés binaires cb* : facultatives (calculées par SQL Server dans les versions récentes).</summary>
        private static bool EstCle(string colonne)
        {
            return colonne.StartsWith("cb", StringComparison.Ordinal) && colonne.Length > 2 && char.IsUpper(colonne[2]);
        }

        private static Controle ControleColonnes(string titre, string table, string[] requises, Func<string, string, InfoColonne> col)
        {
            var cles = requises.Where(EstCle).ToList();
            var donnees = requises.Where(n => !EstCle(n)).ToArray();
            var clesCalculees = cles.Where(n => col(table, n) != null && col(table, n).Calculee).ToList();
            var clesAbsentes = cles.Where(n => col(table, n) == null).ToList();

            var absentes = donnees.Where(n => col(table, n) == null).ToList();
            var calculees = donnees.Where(n => col(table, n) != null && col(table, n).Calculee).ToList();
            if (absentes.Count == 0 && calculees.Count == 0)
            {
                var detailOk = donnees.Length + " colonnes de données présentes et modifiables.";
                if (clesCalculees.Count > 0)
                    detailOk += " Clés calculées par SQL Server (non écrites par l'import, adaptation automatique) : " + string.Join(", ", clesCalculees) + ".";
                if (clesAbsentes.Count > 0)
                    detailOk += " Clés absentes de cette version (ignorées) : " + string.Join(", ", clesAbsentes) + ".";
                return Controle.Creer(titre, StatutControle.Ok, detailOk);
            }
            var detail = new List<string>();
            if (absentes.Count > 0) detail.Add("absentes : " + string.Join(", ", absentes));
            if (calculees.Count > 0) detail.Add("calculées (non insérables) : " + string.Join(", ", calculees));
            return Controle.Creer(titre, StatutControle.Erreur, char.ToUpper(detail[0][0]) + string.Join(" ; ", detail).Substring(1) + ". La procédure ne pourra pas être créée telle quelle.", true);
        }
    }
}
