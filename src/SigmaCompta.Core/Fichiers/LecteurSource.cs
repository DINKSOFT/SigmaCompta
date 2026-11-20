using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Fichiers
{
    /// <summary>Point d'entrée unique : lit un fichier Excel ou CSV selon le profil.</summary>
    public static class LecteurSource
    {
        public static readonly string[] ExtensionsExcel = { ".xlsx", ".xlsm" };
        public static readonly string[] ExtensionsTexte = { ".csv", ".txt", ".tsv" };

        public const string FiltreDialogue =
            "Fichiers de données (*.xlsx;*.xlsm;*.csv;*.txt)|*.xlsx;*.xlsm;*.csv;*.txt|Excel (*.xlsx)|*.xlsx;*.xlsm|CSV / texte (*.csv;*.txt)|*.csv;*.txt|Tous les fichiers|*.*";

        public static bool EstExcel(string chemin, SourceProfil source)
        {
            if (source != null && string.Equals(source.Type, "Excel", StringComparison.OrdinalIgnoreCase)) return true;
            if (source != null && string.Equals(source.Type, "Csv", StringComparison.OrdinalIgnoreCase)) return false;
            var ext = (Path.GetExtension(chemin) ?? "").ToLowerInvariant();
            if (ext == ".xls")
                throw new InvalidDataException("Les classeurs .xls (Excel 97-2003) ne sont pas pris en charge : réenregistre le fichier au format .xlsx.");
            return ExtensionsExcel.Contains(ext);
        }

        public static IList<string> ListerFeuilles(string chemin)
        {
            using (var x = new LecteurXlsx(chemin))
                return x.Feuilles.ToList();
        }

        public static TableSource Lire(string chemin, SourceProfil source, string feuilleForcee = null)
        {
            if (!File.Exists(chemin)) throw new FileNotFoundException("Fichier introuvable : " + chemin);
            source = source ?? new SourceProfil();
            TableSource t;
            if (EstExcel(chemin, source))
            {
                using (var x = new LecteurXlsx(chemin))
                    t = x.Lire(string.IsNullOrWhiteSpace(feuilleForcee) ? source.Feuille : feuilleForcee, source.LigneEntete);
            }
            else
            {
                t = LecteurCsv.Lire(chemin, source.Separateur, source.Encodage, source.LigneEntete);
            }
            t.Chemin = chemin;
            return t;
        }
    }
}
