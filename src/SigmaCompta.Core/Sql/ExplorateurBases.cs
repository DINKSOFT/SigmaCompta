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
    /// <summary>Exploration du serveur : bases, détection des bases comptables, tables et structure.</summary>
    public static class ExplorateurBases
    {
        /// <summary>Colonnes de F_ECRITUREC écrites par imp.PS_ImportPiece.</summary>
        public static readonly string[][] ColonnesEcriture =
        {
            new[] { "JO_Num", "Code journal" },
            new[] { "cbJO_Num", "Clé binaire du journal" },
            new[] { "EC_No", "N° d'écriture (posé par le déclencheur)" },
            new[] { "JM_Date", "Période (1er du mois) → F_JMOUV" },
            new[] { "EC_Jour", "Jour du mois" },
            new[] { "EC_Date", "Date de saisie (la date comptable est JM_Date + EC_Jour)" },
            new[] { "EC_Piece", "N° de pièce" },
            new[] { "cbEC_Piece", "Clé binaire de la pièce" },
            new[] { "EC_RefPiece", "Référence pièce / n° facture" },
            new[] { "cbEC_RefPiece", "Clé binaire de la référence" },
            new[] { "CG_Num", "Compte général" },
            new[] { "cbCG_Num", "Clé binaire du compte" },
            new[] { "CT_Num", "Compte tiers" },
            new[] { "cbCT_Num", "Clé binaire du tiers" },
            new[] { "EC_Intitule", "Libellé" },
            new[] { "EC_Sens", "Sens (0 débit, 1 crédit)" },
            new[] { "EC_Montant", "Montant" },
            new[] { "EC_Echeance", "Échéance" },
            new[] { "EC_Norme", "Norme (0)" },
        };

        public static async Task<InfoServeur> InfoServeurAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            var dt = await SqlOutils.TableAsync(c,
                "SELECT @@SERVERNAME AS Nom, CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)) AS Version, " +
                "CAST(SERVERPROPERTY('Edition') AS nvarchar(200)) AS Edition, SUSER_SNAME() AS LoginConnecte, " +
                "ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) AS SysAdmin, " +
                "(SELECT COUNT(*) FROM sys.databases WHERE database_id > 4) AS NbBases", ct).ConfigureAwait(false);
            var r = dt.Rows[0];
            return new InfoServeur
            {
                Nom = SqlOutils.Texte(r["Nom"]),
                Version = SqlOutils.Texte(r["Version"]),
                Edition = SqlOutils.Texte(r["Edition"]),
                LoginConnecte = SqlOutils.Texte(r["LoginConnecte"]),
                EstSysAdmin = SqlOutils.Entier(r["SysAdmin"]) == 1,
                NombreBases = SqlOutils.Entier(r["NbBases"])
            };
        }

        public static async Task<List<InfoBase>> ListerBasesAsync(SqlConnection c, IProgress<string> progression = null, CancellationToken ct = default(CancellationToken))
        {
            var dt = await SqlOutils.TableAsync(c,
                "SELECT d.name, d.state_desc, d.compatibility_level, ISNULL(HAS_DBACCESS(d.name), 0) AS acces " +
                "FROM sys.databases d WHERE d.database_id > 4 ORDER BY d.name", ct).ConfigureAwait(false);

            var bases = dt.Rows.Cast<DataRow>().Select(r => new InfoBase
            {
                Nom = SqlOutils.Texte(r["name"]),
                Etat = SqlOutils.Texte(r["state_desc"]),
                NiveauCompatibilite = SqlOutils.Entier(r["compatibility_level"]),
                Accessible = SqlOutils.Entier(r["acces"]) == 1 && SqlOutils.Texte(r["state_desc"]) == "ONLINE"
            }).ToList();

            var baseInitiale = c.Database;
            try
            {
                foreach (var b in bases.Where(x => x.Accessible))
                {
                    ct.ThrowIfCancellationRequested();
                    if (progression != null) progression.Report("Analyse de " + b.Nom + "…");
                    try
                    {
                        c.ChangeDatabase(b.Nom);
                        await AnalyserBaseAsync(c, b, ct).ConfigureAwait(false);
                    }
                    catch (SqlException ex)
                    {
                        b.Erreur = ex.Message;
                    }
                }
            }
            finally
            {
                try { c.ChangeDatabase(baseInitiale); } catch (Exception) { }
            }
            return bases;
        }

        private const string RequeteAnalyse = @"
DECLARE @rs nvarchar(200), @deb datetime, @fin datetime, @der datetime, @nb bigint, @def nvarchar(max);
DECLARE @compta bit = CASE WHEN OBJECT_ID(N'dbo.F_ECRITUREC', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.F_JOURNAUX', N'U') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @gescom bit = CASE WHEN OBJECT_ID(N'dbo.F_DOCENTETE', N'U') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @proc bit = CASE WHEN OBJECT_ID(N'imp.PS_ImportPiece', N'P') IS NOT NULL THEN 1 ELSE 0 END;
IF @proc = 1 SET @def = OBJECT_DEFINITION(OBJECT_ID(N'imp.PS_ImportPiece'));
IF COL_LENGTH(N'dbo.P_DOSSIER', N'D_RaisonSoc') IS NOT NULL
    EXEC sys.sp_executesql N'SELECT TOP (1) @rs = D_RaisonSoc FROM dbo.P_DOSSIER', N'@rs nvarchar(200) OUTPUT', @rs OUTPUT;
IF COL_LENGTH(N'dbo.P_DOSSIER', N'D_DebutExo05') IS NOT NULL
    EXEC sys.sp_executesql N'
        SELECT TOP (1) @deb = v.d, @fin = v.f
        FROM dbo.P_DOSSIER p
        CROSS APPLY (VALUES (p.D_DebutExo01, p.D_FinExo01), (p.D_DebutExo02, p.D_FinExo02), (p.D_DebutExo03, p.D_FinExo03),
                            (p.D_DebutExo04, p.D_FinExo04), (p.D_DebutExo05, p.D_FinExo05)) v(d, f)
        WHERE v.d > ''19000101''
        ORDER BY CASE WHEN GETDATE() BETWEEN v.d AND v.f THEN 0 ELSE 1 END, v.f DESC',
        N'@deb datetime OUTPUT, @fin datetime OUTPUT', @deb OUTPUT, @fin OUTPUT;
IF @compta = 1
BEGIN
    SELECT @nb = SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(N'dbo.F_ECRITUREC') AND p.index_id IN (0, 1);
    IF COL_LENGTH(N'dbo.F_ECRITUREC', N'cbMarq') IS NOT NULL
        EXEC sys.sp_executesql N'SELECT TOP (1) @der = EC_Date FROM dbo.F_ECRITUREC ORDER BY cbMarq DESC', N'@der datetime OUTPUT', @der OUTPUT;
END;
SELECT @compta AS compta, @gescom AS gescom, @proc AS proc_, @def AS def, @rs AS rs, @deb AS deb, @fin AS fin, @nb AS nb, @der AS der;";

        private static async Task AnalyserBaseAsync(SqlConnection c, InfoBase b, CancellationToken ct)
        {
            var dt = await SqlOutils.TableAsync(c, RequeteAnalyse, ct).ConfigureAwait(false);
            var r = dt.Rows[0];
            b.EstComptabilite = SqlOutils.Entier(r["compta"]) == 1;
            b.EstGestionCommerciale = SqlOutils.Entier(r["gescom"]) == 1;
            b.ProcedureInstallee = SqlOutils.Entier(r["proc_"]) == 1;
            b.VersionProcedure = ExtraireVersion(SqlOutils.Texte(r["def"]));
            b.RaisonSociale = (SqlOutils.Texte(r["rs"]) ?? "").Trim();
            b.DebutExercice = SqlOutils.Date(r["deb"]);
            b.FinExercice = SqlOutils.Date(r["fin"]);
            b.NombreEcritures = SqlOutils.Long(r["nb"]);
            b.DerniereEcriture = SqlOutils.Date(r["der"]);
        }

        public static string ExtraireVersion(string definition)
        {
            if (string.IsNullOrEmpty(definition)) return null;
            var m = Regex.Match(definition, @"SigmaCompta-Version:\s*([0-9.]+)");
            return m.Success ? m.Groups[1].Value : "inconnue";
        }

        /// <summary>Tables de la base courante, avec le nombre de colonnes « écriture » présentes.</summary>
        public static async Task<List<InfoTable>> ListerTablesAsync(SqlConnection c, CancellationToken ct = default(CancellationToken))
        {
            var liste = string.Join(", ", ColonnesEcriture.Select(x => SqlOutils.Litteral(x[0])));
            var sql =
                "SELECT s.name AS sch, t.name AS tab, " +
                "ISNULL((SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0, 1)), 0) AS nb, " +
                "(SELECT COUNT(*) FROM sys.columns col WHERE col.object_id = t.object_id AND col.name IN (" + liste + ")) AS req " +
                "FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "ORDER BY CASE WHEN t.name = N'F_ECRITUREC' THEN 0 ELSE 1 END, s.name, t.name";
            var dt = await SqlOutils.TableAsync(c, sql, ct).ConfigureAwait(false);
            return dt.Rows.Cast<DataRow>().Select(r => new InfoTable
            {
                Schema = SqlOutils.Texte(r["sch"]),
                Nom = SqlOutils.Texte(r["tab"]),
                NombreLignes = SqlOutils.Long(r["nb"]),
                ColonnesRequisesPresentes = SqlOutils.Entier(r["req"]),
                ColonnesRequisesTotal = ColonnesEcriture.Length
            }).ToList();
        }

        public static async Task<List<ColonneStructure>> StructureAsync(SqlConnection c, string schema, string table, CancellationToken ct = default(CancellationToken))
        {
            var sql =
                "SELECT c.name, TYPE_NAME(c.user_type_id) AS typ, c.max_length, c.precision, c.scale, c.is_nullable, c.is_computed " +
                "FROM sys.columns c WHERE c.object_id = OBJECT_ID(" + SqlOutils.Litteral(SqlOutils.Crochets(schema) + "." + SqlOutils.Crochets(table)) + ")";
            var dt = await SqlOutils.TableAsync(c, sql, ct).ConfigureAwait(false);
            var colonnes = dt.Rows.Cast<DataRow>().ToDictionary(r => SqlOutils.Texte(r["name"]), r => r, StringComparer.OrdinalIgnoreCase);

            var res = new List<ColonneStructure>();
            foreach (var def in ColonnesEcriture)
            {
                DataRow r;
                var cs = new ColonneStructure { Nom = def[0], Role = def[1] };
                if (colonnes.TryGetValue(def[0], out r))
                {
                    cs.Presente = true;
                    cs.Calculee = Convert.ToBoolean(r["is_computed"]);
                    cs.Nullable = Convert.ToBoolean(r["is_nullable"]);
                    cs.Type = FormaterType(SqlOutils.Texte(r["typ"]), SqlOutils.Entier(r["max_length"]), SqlOutils.Entier(r["precision"]), SqlOutils.Entier(r["scale"]));
                    if (cs.Calculee) cs.Type += " · calculée";
                }
                else cs.Type = "—";
                res.Add(cs);
            }
            return res;
        }

        private static string FormaterType(string type, int longueur, int precision, int echelle)
        {
            switch (type)
            {
                case "varchar":
                case "char":
                case "varbinary":
                case "binary":
                    return type + "(" + (longueur < 0 ? "max" : longueur.ToString()) + ")";
                case "nvarchar":
                case "nchar":
                    return type + "(" + (longueur < 0 ? "max" : (longueur / 2).ToString()) + ")";
                case "numeric":
                case "decimal":
                    return type + "(" + precision + "," + echelle + ")";
                default:
                    return type;
            }
        }
    }
}
