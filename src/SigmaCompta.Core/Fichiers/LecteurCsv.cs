using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SigmaCompta.Core.Fichiers
{
    /// <summary>Lecteur CSV/TXT : guillemets, séparateur auto-détecté, encodage ANSI ou UTF-8.</summary>
    public static class LecteurCsv
    {
        public static Modeles.TableSource Lire(string chemin, string separateur, string encodage, int ligneEntete)
        {
            var texte = LireTexte(chemin, encodage);
            var lignesBrutes = DecouperEnregistrements(texte);
            if (lignesBrutes.Count == 0) throw new InvalidDataException("Le fichier est vide.");
            if (ligneEntete < 1) ligneEntete = 1;
            if (ligneEntete > lignesBrutes.Count)
                throw new InvalidDataException("La ligne d'en-tête n° " + ligneEntete + " dépasse la fin du fichier.");

            char sep = ResoudreSeparateur(separateur, lignesBrutes[ligneEntete - 1].Texte);
            var table = new Modeles.TableSource();

            var entetes = Champs(lignesBrutes[ligneEntete - 1].Texte, sep);
            table.Entetes.AddRange(entetes.Select(e => e.Trim()));

            for (int i = ligneEntete; i < lignesBrutes.Count; i++)
            {
                var champs = Champs(lignesBrutes[i].Texte, sep);
                if (champs.All(string.IsNullOrWhiteSpace)) continue;
                while (table.Entetes.Count < champs.Count) table.Entetes.Add("");
                table.Lignes.Add(champs.ToArray());
                table.NumerosLigne.Add(lignesBrutes[i].Numero);
            }
            return table;
        }

        public static string LireTexte(string chemin, string encodage)
        {
            byte[] octets;
            using (var fs = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                octets = ms.ToArray();
            }
            if (octets.Length >= 3 && octets[0] == 0xEF && octets[1] == 0xBB && octets[2] == 0xBF)
                return new UTF8Encoding(false).GetString(octets, 3, octets.Length - 3);
            if (octets.Length >= 2 && octets[0] == 0xFF && octets[1] == 0xFE)
                return Encoding.Unicode.GetString(octets, 2, octets.Length - 2);

            if (string.Equals(encodage, "utf-8", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(encodage, "utf8", StringComparison.OrdinalIgnoreCase))
                return new UTF8Encoding(false).GetString(octets);

            // ANSI par défaut, mais un fichier UTF-8 sans BOM valide est reconnu.
            try
            {
                return new UTF8Encoding(false, true).GetString(octets);
            }
            catch (DecoderFallbackException)
            {
                Encodages.Initialiser();
                return Encoding.GetEncoding(string.IsNullOrWhiteSpace(encodage) ? "windows-1252" : encodage).GetString(octets);
            }
        }

        private sealed class Enregistrement
        {
            public string Texte;
            public int Numero;
        }

        /// <summary>Découpe en enregistrements en respectant les retours à la ligne entre guillemets.</summary>
        private static List<Enregistrement> DecouperEnregistrements(string texte)
        {
            var res = new List<Enregistrement>();
            var sb = new StringBuilder();
            bool guillemets = false;
            int ligne = 1, debut = 1;
            for (int i = 0; i < texte.Length; i++)
            {
                char c = texte[i];
                if (c == '"') guillemets = !guillemets;
                if (!guillemets && (c == '\n' || c == '\r'))
                {
                    if (c == '\r' && i + 1 < texte.Length && texte[i + 1] == '\n') i++;
                    res.Add(new Enregistrement { Texte = sb.ToString(), Numero = debut });
                    sb.Clear();
                    ligne++;
                    debut = ligne;
                    continue;
                }
                if (c == '\n') ligne++;
                sb.Append(c);
            }
            if (sb.Length > 0) res.Add(new Enregistrement { Texte = sb.ToString(), Numero = debut });
            return res;
        }

        public static char ResoudreSeparateur(string parametre, string ligneEntete)
        {
            var p = (parametre ?? "auto").Trim().ToLowerInvariant();
            if (p == "tab" || p == "\\t" || p == "tabulation") return '\t';
            if (p.Length == 1) return p[0];
            var candidats = new[] { ';', '\t', ',', '|' };
            return candidats.OrderByDescending(c => ligneEntete.Count(x => x == c)).First();
        }

        private static List<string> Champs(string ligne, char sep)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            bool guillemets = false;
            for (int i = 0; i < ligne.Length; i++)
            {
                char c = ligne[i];
                if (guillemets)
                {
                    if (c == '"')
                    {
                        if (i + 1 < ligne.Length && ligne[i + 1] == '"') { sb.Append('"'); i++; }
                        else guillemets = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') guillemets = true;
                else if (c == sep) { res.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            res.Add(sb.ToString());
            return res;
        }

        public static void Ecrire(string chemin, IList<string> entetes, IEnumerable<object[]> lignes, char sep = ';')
        {
            using (var w = new StreamWriter(chemin, false, new UTF8Encoding(true)))
            {
                w.WriteLine(string.Join(sep.ToString(), entetes.Select(e => Echapper(e, sep))));
                foreach (var l in lignes)
                    w.WriteLine(string.Join(sep.ToString(), l.Select(v => Echapper(Formater(v), sep))));
            }
        }

        private static string Formater(object v)
        {
            if (v == null) return "";
            if (v is DateTime) return ((DateTime)v).ToString("dd/MM/yyyy");
            if (v is decimal) return ((decimal)v).ToString("0.##", new System.Globalization.CultureInfo("fr-FR"));
            return Convert.ToString(v, System.Globalization.CultureInfo.CurrentCulture);
        }

        private static string Echapper(string s, char sep)
        {
            s = s ?? "";
            if (s.IndexOf(sep) >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0)
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
