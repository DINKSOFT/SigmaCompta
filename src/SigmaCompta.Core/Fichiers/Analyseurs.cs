using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SigmaCompta.Core.Fichiers
{
    public static class Colonnes
    {
        /// <summary>0 → A, 25 → Z, 26 → AA…</summary>
        public static string Lettre(int index)
        {
            if (index < 0) return "";
            var sb = new StringBuilder();
            index++;
            while (index > 0)
            {
                int r = (index - 1) % 26;
                sb.Insert(0, (char)('A' + r));
                index = (index - 1) / 26;
            }
            return sb.ToString();
        }

        /// <summary>A → 0, AA → 26. Retourne -1 si invalide.</summary>
        public static int Index(string lettres)
        {
            if (string.IsNullOrWhiteSpace(lettres)) return -1;
            int n = 0;
            foreach (var ch in lettres.Trim().ToUpperInvariant())
            {
                if (ch < 'A' || ch > 'Z') return -1;
                n = n * 26 + (ch - 'A' + 1);
            }
            return n - 1;
        }
    }

    public static class Analyseurs
    {
        private static readonly CultureInfo Fr = new CultureInfo("fr-FR");

        /// <summary>Normalise un en-tête : minuscules, sans accents, sans espaces ni ponctuation.</summary>
        public static string NormaliserEntete(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var d = s.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(d.Length);
            foreach (var ch in d)
            {
                var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else if (ch == '°' || ch == 'º') sb.Append('o');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Analyse un montant saisi en français ou en anglais :
        /// « 1 500 000 », « 1.500.000,50 », « 1,500,000.50 », « -250 », « (250) ».
        /// </summary>
        public static bool EssayerMontant(string texte, string separateurDecimal, out decimal valeur)
        {
            valeur = 0m;
            if (string.IsNullOrWhiteSpace(texte)) return true; // vide = 0
            var s = texte.Trim();
            bool negatif = false;
            if (s.StartsWith("(") && s.EndsWith(")")) { negatif = true; s = s.Substring(1, s.Length - 2); }
            s = new string(s.Where(ch => !char.IsWhiteSpace(ch) && ch != ' ' && ch != ' ' && ch != '\'' && ch != '€').ToArray());
            if (s.EndsWith("FCFA", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            if (s.EndsWith("CFA", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 3);
            if (s.EndsWith("-")) { negatif = !negatif; s = s.Substring(0, s.Length - 1); }
            if (s.Length == 0) return true;

            int virgule = s.LastIndexOf(','), point = s.LastIndexOf('.');
            char dec;
            if (separateurDecimal == ",") dec = ',';
            else if (separateurDecimal == ".") dec = '.';
            else if (virgule >= 0 && point >= 0) dec = virgule > point ? ',' : '.';
            else if (virgule >= 0) dec = s.Count(c => c == ',') > 1 ? '.' : ',';
            else if (point >= 0) dec = s.Count(c => c == '.') > 1 ? ',' : '.';
            else dec = '.';

            char milliers = dec == ',' ? '.' : ',';
            s = s.Replace(milliers.ToString(), "");
            if (dec == ',') s = s.Replace(',', '.');

            decimal v;
            if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                                  CultureInfo.InvariantCulture, out v))
                return false;
            valeur = negatif ? -v : v;
            return true;
        }

        /// <summary>
        /// Analyse une date : formats du profil, date ISO produite par le lecteur Excel,
        /// ou numéro de série Excel (ex. 46326).
        /// </summary>
        public static bool EssayerDate(string texte, string formats, out DateTime valeur)
        {
            valeur = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(texte)) return false;
            var s = texte.Trim();

            var liste = (formats ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
            liste.Add("yyyy-MM-dd");
            liste.Add("yyyy-MM-ddTHH:mm:ss");
            liste.Add("yyyy-MM-dd HH:mm:ss");
            liste.Add("dd/MM/yyyy HH:mm:ss");
            liste.Add("dd/MM/yyyy HH:mm");

            if (DateTime.TryParseExact(s, liste.ToArray(), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out valeur))
            {
                valeur = valeur.Date;
                return true;
            }

            double serie;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out serie) && serie > 20000 && serie < 80000)
            {
                valeur = DateTime.FromOADate(Math.Floor(serie));
                return true;
            }

            if (DateTime.TryParse(s, Fr, DateTimeStyles.AllowWhiteSpaces, out valeur))
            {
                valeur = valeur.Date;
                return true;
            }
            return false;
        }

        /// <summary>Texte d'un nombre lu dans Excel, sans notation scientifique (4220000 et non 4.22E+06).</summary>
        public static string NombreEnTexte(double d)
        {
            if (Math.Abs(d % 1) < 1e-9 && Math.Abs(d) < 1e15)
                return ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture);
            return ((decimal)d).ToString("0.##########", CultureInfo.InvariantCulture);
        }

        public static string Tronquer(string s, int longueur)
        {
            if (s == null) return null;
            return s.Length <= longueur ? s : s.Substring(0, longueur);
        }

        /// <summary>Complète un compte numérique par des zéros à droite (422 → 4220000).</summary>
        public static string CompleterCompte(string compte, int longueur)
        {
            if (string.IsNullOrEmpty(compte) || longueur <= 0 || compte.Length >= longueur) return compte;
            if (!compte.All(char.IsDigit)) return compte;
            return compte.PadRight(longueur, '0');
        }
    }
}
