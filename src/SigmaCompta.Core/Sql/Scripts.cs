using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SigmaCompta.Core.Sql
{
    /// <summary>Scripts SQL embarqués dans l'assembly (dossier Scripts du projet Core).</summary>
    public static class ScriptsEmbarques
    {
        public const string PreVerification = "01_preverification_schema.sql";
        public const string Procedure = "02_imp_PS_ImportPiece.sql";
        public const string Tests = "03_tests_PS_ImportPiece.sql";

        /// <summary>Version de imp.PS_ImportPiece livrée avec cette application.</summary>
        public static string VersionProcedureEmbarquee
        {
            get { return ExplorateurBases.ExtraireVersion(Lire(Procedure)) ?? "?"; }
        }

        public static IEnumerable<string> Noms
        {
            get { return new[] { PreVerification, Procedure, Tests }; }
        }

        public static string Lire(string nom)
        {
            var asm = typeof(ScriptsEmbarques).Assembly;
            var ressource = "Scripts." + nom;
            using (var s = asm.GetManifestResourceStream(ressource))
            {
                if (s == null)
                    throw new InvalidOperationException("Script embarqué introuvable : " + ressource + ". Ressources : " + string.Join(", ", asm.GetManifestResourceNames()));
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
        }

        public static void Exporter(string dossier)
        {
            Directory.CreateDirectory(dossier);
            foreach (var n in Noms)
                File.WriteAllText(Path.Combine(dossier, n), Lire(n), new UTF8Encoding(true));
        }
    }

    public sealed class ResultatLot
    {
        public int Numero { get; set; }
        public string Titre { get; set; }
        public string Sql { get; set; }
        public List<DataTable> Tables { get; } = new List<DataTable>();
        public List<string> Messages { get; } = new List<string>();
        public string Erreur { get; set; }
        public bool EnErreur { get { return !string.IsNullOrEmpty(Erreur); } }
        public TimeSpan Duree { get; set; }
    }

    /// <summary>Exécute un script T-SQL découpé sur les séparateurs GO, comme SSMS.</summary>
    public static class ExecuteurScript
    {
        private static readonly Regex Go = new Regex(@"^\s*GO\s*(?:--.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        public static List<string> DecouperLots(string script)
        {
            var lots = new List<string>();
            var sb = new StringBuilder();
            bool dansCommentaireBloc = false;
            foreach (var ligneBrute in script.Replace("\r\n", "\n").Split('\n'))
            {
                var ligne = ligneBrute;
                if (!dansCommentaireBloc && Go.IsMatch(ligne))
                {
                    if (sb.ToString().Trim().Length > 0) lots.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }
                // Suivi minimal des commentaires /* */ pour ne pas couper sur un GO commenté.
                int i = 0;
                while (i < ligne.Length)
                {
                    if (!dansCommentaireBloc && i + 1 < ligne.Length && ligne[i] == '-' && ligne[i + 1] == '-') break;
                    if (!dansCommentaireBloc && i + 1 < ligne.Length && ligne[i] == '/' && ligne[i + 1] == '*') { dansCommentaireBloc = true; i += 2; continue; }
                    if (dansCommentaireBloc && i + 1 < ligne.Length && ligne[i] == '*' && ligne[i + 1] == '/') { dansCommentaireBloc = false; i += 2; continue; }
                    i++;
                }
                sb.Append(ligne).Append("\r\n");
            }
            if (sb.ToString().Trim().Length > 0) lots.Add(sb.ToString().Trim());
            // Un lot qui n'est qu'un commentaire n'a pas besoin d'être envoyé.
            return lots.Where(l => !EstUniquementCommentaire(l)).ToList();
        }

        private static bool EstUniquementCommentaire(string lot)
        {
            var sansBlocs = Regex.Replace(lot, @"/\*.*?\*/", "", RegexOptions.Singleline);
            var sansLignes = Regex.Replace(sansBlocs, @"--[^\n]*", "");
            return sansLignes.Trim().Length == 0;
        }

        public static string TitreLot(string lot, int numero)
        {
            var m = Regex.Match(lot, @"/\*\s*(.+?)\s*(?:\r?\n|\*/)");
            if (!m.Success) m = Regex.Match(lot, @"--\s*(.+)");
            var t = m.Success ? m.Groups[1].Value.Trim().TrimEnd('=', ' ') : "";
            if (t.Length == 0 || t.All(ch => ch == '=' || ch == '-'))
            {
                var premiere = lot.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith("--") && !x.StartsWith("/*"));
                t = premiere ?? ("Lot " + numero);
            }
            return t.Length > 90 ? t.Substring(0, 87) + "…" : t;
        }

        public static async Task<List<ResultatLot>> ExecuterAsync(SqlConnection c, string script, SqlTransaction tx = null,
            bool arreterSurErreur = false, IProgress<string> progression = null, CancellationToken ct = default(CancellationToken))
        {
            var resultats = new List<ResultatLot>();
            var lots = DecouperLots(script);
            ResultatLot courant = null;
            SqlInfoMessageEventHandler gestionnaire = (s, e) =>
            {
                if (courant != null) courant.Messages.Add(e.Message);
            };
            c.InfoMessage += gestionnaire;
            try
            {
                for (int i = 0; i < lots.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    courant = new ResultatLot { Numero = i + 1, Sql = lots[i], Titre = TitreLot(lots[i], i + 1) };
                    resultats.Add(courant);
                    if (progression != null) progression.Report("Lot " + (i + 1) + "/" + lots.Count + " — " + courant.Titre);
                    var debut = DateTime.Now;
                    try
                    {
                        using (var cmd = SqlOutils.Commande(c, lots[i], 300, tx))
                        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        {
                            while (!r.IsClosed)
                            {
                                var dt = new DataTable();
                                dt.Load(r);
                                if (dt.Columns.Count > 0) courant.Tables.Add(dt);
                            }
                        }
                    }
                    catch (SqlException ex)
                    {
                        courant.Erreur = ex.Message;
                        if (arreterSurErreur) throw new ExceptionScript(courant, ex);
                    }
                    courant.Duree = DateTime.Now - debut;
                }
            }
            finally
            {
                c.InfoMessage -= gestionnaire;
            }
            return resultats;
        }
    }

    public sealed class ExceptionScript : Exception
    {
        public ExceptionScript(ResultatLot lot, Exception inner)
            : base("Échec du lot " + lot.Numero + " (" + lot.Titre + ") : " + inner.Message, inner)
        {
            Lot = lot;
        }

        public ResultatLot Lot { get; private set; }
    }

    /// <summary>Installe ou met à jour imp.TT_LigneEcriture + imp.PS_ImportPiece dans une transaction.</summary>
    /// <summary>Script de la procédure adapté à une base précise.</summary>
    public sealed class ScriptAdapte
    {
        public string Script { get; set; }
        /// <summary>Colonnes cb* retirées de l'INSERT (calculées par SQL Server ou absentes).</summary>
        public List<string> ColonnesRetirees { get; } = new List<string>();
    }

    public static class InstallateurProcedure
    {
        private static readonly Regex Marqueur = new Regex(@"--#(CB|SI):(\w+)\.(\w+)");

        /// <summary>
        /// Ne garde les lignes marquées « --#CB:Table.Colonne » que si la colonne existe et n'est pas calculée,
        /// et les lignes « --#SI:Table.Colonne » que si la colonne existe.
        /// </summary>
        public static ScriptAdapte AdapterScript(string script, ISet<string> colonnesInserables, ISet<string> colonnesExistantes = null)
        {
            if (colonnesExistantes == null) colonnesExistantes = colonnesInserables;
            var res = new ScriptAdapte();
            var lignes = new List<string>();
            foreach (var ligne in script.Replace("\r\n", "\n").Split('\n'))
            {
                var m = Marqueur.Match(ligne);
                if (m.Success)
                {
                    var nom = m.Groups[2].Value + "." + m.Groups[3].Value;
                    var garder = m.Groups[1].Value == "CB" ? colonnesInserables.Contains(nom) : colonnesExistantes.Contains(nom);
                    if (!garder)
                    {
                        if (!res.ColonnesRetirees.Contains(nom)) res.ColonnesRetirees.Add(nom);
                        continue;
                    }
                }
                lignes.Add(ligne);
            }
            res.Script = string.Join("\r\n", lignes);
            return res;
        }

        /// <summary>Lit dans la base les colonnes insérables (non calculées) de F_ECRITUREC et F_JMOUV, puis adapte le script.</summary>
        public static async Task<ScriptAdapte> PreparerAsync(SqlConnection c, SqlTransaction tx = null, CancellationToken ct = default(CancellationToken))
        {
            var inserables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var existantes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = SqlOutils.Commande(c,
                "SELECT t.name + N'.' + col.name, col.is_computed FROM sys.columns col JOIN sys.tables t ON t.object_id = col.object_id " +
                "WHERE SCHEMA_NAME(t.schema_id) = N'dbo' AND t.name IN (N'F_ECRITUREC', N'F_JMOUV')", 60, tx))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    existantes.Add(r.GetString(0));
                    if (!r.GetBoolean(1)) inserables.Add(r.GetString(0));
                }
            }
            return AdapterScript(ScriptsEmbarques.Lire(ScriptsEmbarques.Procedure), inserables, existantes);
        }

        public static async Task<List<ResultatLot>> InstallerAsync(SqlConnection c, IProgress<string> progression = null, CancellationToken ct = default(CancellationToken))
        {
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    var script = (await PreparerAsync(c, tx, ct).ConfigureAwait(false)).Script;
                    var res = await ExecuteurScript.ExecuterAsync(c, script, tx, true, progression, ct).ConfigureAwait(false);
                    tx.Commit();
                    return res;
                }
                catch
                {
                    try { tx.Rollback(); } catch (Exception) { }
                    throw;
                }
            }
        }
    }
}
