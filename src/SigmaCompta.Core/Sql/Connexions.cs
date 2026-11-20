using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sql;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Sql
{
    public static class SqlOutils
    {
        /// <summary>
        /// Date comptable d'une écriture de F_ECRITUREC (alias <c>e</c>) : période (JM_Date, 1er du mois) + jour (EC_Jour).
        /// EC_Date est la date de saisie dans le logiciel comptable : elle ne sert ni aux filtres ni aux tris.
        /// </summary>
        public const string DateEcriture = "DATEADD(day, e.EC_Jour - 1, e.JM_Date)";

        /// <summary>Équivalent C# de QUOTENAME().</summary>
        public static string Crochets(string nom)
        {
            return "[" + (nom ?? "").Replace("]", "]]") + "]";
        }

        /// <summary>Littéral N'…' (apostrophes doublées).</summary>
        public static string Litteral(string s)
        {
            return "N'" + (s ?? "").Replace("'", "''") + "'";
        }

        public static string Texte(object o)
        {
            return o == null || o == DBNull.Value ? null : Convert.ToString(o);
        }

        public static int Entier(object o, int defaut = 0)
        {
            return o == null || o == DBNull.Value ? defaut : Convert.ToInt32(o);
        }

        public static long Long(object o)
        {
            return o == null || o == DBNull.Value ? 0L : Convert.ToInt64(o);
        }

        public static DateTime? Date(object o)
        {
            if (o == null || o == DBNull.Value) return null;
            var d = Convert.ToDateTime(o);
            return d.Year <= 1900 ? (DateTime?)null : d;
        }

        public static SqlCommand Commande(SqlConnection c, string sql, int delaiSecondes = 60, SqlTransaction tx = null)
        {
            return new SqlCommand(sql, c, tx) { CommandTimeout = delaiSecondes, CommandType = CommandType.Text };
        }

        public static async Task<object> ScalaireAsync(SqlConnection c, string sql, CancellationToken ct = default(CancellationToken))
        {
            using (var cmd = Commande(c, sql))
            {
                var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                return r == DBNull.Value ? null : r;
            }
        }

        public static async Task<DataTable> TableAsync(SqlConnection c, string sql, CancellationToken ct = default(CancellationToken), int delai = 60)
        {
            using (var cmd = Commande(c, sql, delai))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                var dt = new DataTable();
                dt.Load(r);
                return dt;
            }
        }

        /// <summary>Message lisible pour une exception SQL (connexion, droits…).</summary>
        public static string MessageConvivial(Exception ex)
        {
            var sql = ex as SqlException;
            if (sql == null) return ex.Message;
            switch (sql.Number)
            {
                case 53:
                case -1:
                case 2:
                    return "Serveur introuvable ou injoignable (erreur " + sql.Number + "). Vérifie le nom du serveur\\instance, que le service SQL Server et SQL Browser sont démarrés, et que le pare-feu autorise la connexion.";
                case 18456:
                    return "Échec de l'authentification (18456) : login ou mot de passe incorrect, ou authentification SQL désactivée sur le serveur.";
                case 4060:
                    return "Base de données inaccessible (4060) : elle n'existe pas ou ce login n'y a pas accès.";
                case 229:
                case 230:
                    return "Droits insuffisants (" + sql.Number + ") : " + sql.Message;
                case -2:
                    return "Délai d'attente dépassé : le serveur ne répond pas assez vite.";
                default:
                    return sql.Message;
            }
        }
    }

    /// <summary>Ouvre des connexions à partir des paramètres de session.</summary>
    public sealed class FabriqueConnexion
    {
        public FabriqueConnexion(ParametresConnexion parametres)
        {
            Parametres = parametres;
        }

        public ParametresConnexion Parametres { get; private set; }

        public async Task<SqlConnection> OuvrirAsync(string baseDeDonnees = null, CancellationToken ct = default(CancellationToken))
        {
            var c = new SqlConnection(Parametres.ConstruireChaine(baseDeDonnees));
            try
            {
                await c.OpenAsync(ct).ConfigureAwait(false);
                return c;
            }
            catch
            {
                c.Dispose();
                throw;
            }
        }
    }

    /// <summary>Détection des instances SQL Server : registre local + diffusion réseau (SQL Browser).</summary>
    public static class DecouverteServeurs
    {
        public static List<string> InstancesLocales()
        {
            var res = new List<string>();
            var machine = Environment.MachineName;
            foreach (var vue in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, vue))
                    using (var cle = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
                    {
                        if (cle == null) continue;
                        foreach (var instance in cle.GetValueNames())
                        {
                            var nom = string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase)
                                ? machine : machine + "\\" + instance;
                            if (!res.Contains(nom, StringComparer.OrdinalIgnoreCase)) res.Add(nom);
                        }
                    }
                }
                catch (Exception)
                {
                    // Registre inaccessible (droits, plateforme) : on ignore.
                }
            }
            return res;
        }

        /// <summary>Interroge le réseau (SQL Browser). Peut prendre plusieurs secondes.</summary>
        public static async Task<List<string>> InstancesReseauAsync(TimeSpan delai)
        {
            var tache = Task.Run(() =>
            {
                var res = new List<string>();
                DataTable dt = SqlDataSourceEnumerator.Instance.GetDataSources();
                foreach (DataRow r in dt.Rows)
                {
                    var serveur = SqlOutils.Texte(r["ServerName"]);
                    var instance = SqlOutils.Texte(r["InstanceName"]);
                    if (string.IsNullOrWhiteSpace(serveur)) continue;
                    res.Add(string.IsNullOrWhiteSpace(instance) ? serveur : serveur + "\\" + instance);
                }
                return res;
            });
            var fin = await Task.WhenAny(tache, Task.Delay(delai)).ConfigureAwait(false);
            if (fin != tache) return new List<string>();
            try { return await tache.ConfigureAwait(false); }
            catch (Exception) { return new List<string>(); }
        }

        public static async Task<List<string>> ToutesAsync(TimeSpan delaiReseau)
        {
            var locales = InstancesLocales();
            var reseau = await InstancesReseauAsync(delaiReseau).ConfigureAwait(false);
            return locales.Concat(reseau)
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                          .ToList();
        }
    }
}
