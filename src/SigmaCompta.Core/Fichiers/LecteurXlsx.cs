using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace SigmaCompta.Core.Fichiers
{
    /// <summary>
    /// Lecteur .xlsx autonome (Open XML via ZipArchive), sans dépendance NuGet.
    /// Retourne les cellules en texte : les dates Excel sont converties en « yyyy-MM-dd ».
    /// </summary>
    public sealed class LecteurXlsx : IDisposable
    {
        private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        private readonly ZipArchive _zip;
        private readonly Stream _flux;
        private List<string> _chainesPartagees;
        private HashSet<int> _stylesDate;
        private readonly List<KeyValuePair<string, string>> _feuilles = new List<KeyValuePair<string, string>>();

        public LecteurXlsx(string chemin)
        {
            // FileShare.ReadWrite : le fichier peut rester ouvert dans Excel.
            _flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            try
            {
                _zip = new ZipArchive(_flux, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                _flux.Dispose();
                throw new InvalidDataException("Ce fichier n'est pas un classeur .xlsx valide (les anciens .xls doivent être réenregistrés au format .xlsx).");
            }
            ChargerClasseur();
        }

        public IReadOnlyList<string> Feuilles { get { return _feuilles.Select(f => f.Key).ToList(); } }

        private XDocument Charger(string chemin)
        {
            var e = _zip.GetEntry(chemin.TrimStart('/'));
            if (e == null) return null;
            using (var s = e.Open())
                return XDocument.Load(s);
        }

        private void ChargerClasseur()
        {
            var wb = Charger("xl/workbook.xml");
            if (wb == null) throw new InvalidDataException("Classeur Excel invalide : xl/workbook.xml introuvable.");
            var rels = Charger("xl/_rels/workbook.xml.rels");
            var cibles = rels == null
                ? new Dictionary<string, string>()
                : rels.Root.Elements(NsPkgRel + "Relationship")
                      .ToDictionary(r => (string)r.Attribute("Id"), r => (string)r.Attribute("Target"));

            foreach (var sh in wb.Root.Descendants(Ns + "sheet"))
            {
                var nom = (string)sh.Attribute("name");
                var id = (string)sh.Attribute(NsRel + "id");
                string cible;
                if (id == null || !cibles.TryGetValue(id, out cible)) continue;
                cible = cible.StartsWith("/") ? cible.TrimStart('/') : "xl/" + cible;
                _feuilles.Add(new KeyValuePair<string, string>(nom, cible));
            }

            _chainesPartagees = new List<string>();
            var ss = Charger("xl/sharedStrings.xml");
            if (ss != null)
            {
                foreach (var si in ss.Root.Elements(Ns + "si"))
                    _chainesPartagees.Add(string.Concat(si.Descendants(Ns + "t").Select(t => (string)t)));
            }

            _stylesDate = new HashSet<int>();
            var st = Charger("xl/styles.xml");
            if (st != null)
            {
                var formatsPerso = st.Root.Descendants(Ns + "numFmt")
                    .ToDictionary(n => (int)n.Attribute("numFmtId"), n => (string)n.Attribute("formatCode") ?? "");
                var xfs = st.Root.Element(Ns + "cellXfs");
                if (xfs != null)
                {
                    int i = 0;
                    foreach (var xf in xfs.Elements(Ns + "xf"))
                    {
                        int fmt = (int?)xf.Attribute("numFmtId") ?? 0;
                        string code;
                        if (EstFormatDate(fmt, formatsPerso.TryGetValue(fmt, out code) ? code : null))
                            _stylesDate.Add(i);
                        i++;
                    }
                }
            }
        }

        private static bool EstFormatDate(int id, string code)
        {
            if ((id >= 14 && id <= 22) || (id >= 45 && id <= 47) || (id >= 27 && id <= 36) || (id >= 50 && id <= 58))
                return true;
            if (string.IsNullOrEmpty(code)) return false;
            // Retire les littéraux entre guillemets et les couleurs/conditions entre crochets.
            var sansLitteraux = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
            var c = sansLitteraux.ToLowerInvariant();
            return (c.Contains("d") || c.Contains("y")) && !c.Contains("0") && !c.Contains("#");
        }

        /// <summary>Lit une feuille (nom vide = première) dans une <see cref="Modeles.TableSource"/>.</summary>
        public Modeles.TableSource Lire(string feuille, int ligneEntete)
        {
            if (_feuilles.Count == 0) throw new InvalidDataException("Le classeur ne contient aucune feuille.");
            var choisie = string.IsNullOrWhiteSpace(feuille)
                ? _feuilles[0]
                : _feuilles.FirstOrDefault(f => string.Equals(f.Key, feuille.Trim(), StringComparison.OrdinalIgnoreCase));
            if (choisie.Key == null)
                throw new InvalidDataException("Feuille « " + feuille + " » introuvable. Feuilles disponibles : " + string.Join(", ", Feuilles));

            var doc = Charger(choisie.Value);
            if (doc == null) throw new InvalidDataException("Contenu de la feuille « " + choisie.Key + " » introuvable.");

            var table = new Modeles.TableSource { Feuille = choisie.Key };
            table.FeuillesDisponibles.AddRange(Feuilles);

            var lignes = new SortedDictionary<int, Dictionary<int, string>>();
            int numLigneAuto = 0;
            foreach (var row in doc.Root.Descendants(Ns + "row"))
            {
                int numLigne = (int?)row.Attribute("r") ?? (numLigneAuto + 1);
                numLigneAuto = numLigne;
                var cellules = new Dictionary<int, string>();
                int colAuto = -1;
                foreach (var c in row.Elements(Ns + "c"))
                {
                    var r = (string)c.Attribute("r");
                    int col = r != null ? Colonnes.Index(new string(r.TakeWhile(char.IsLetter).ToArray())) : colAuto + 1;
                    colAuto = col;
                    var v = ValeurCellule(c);
                    if (!string.IsNullOrEmpty(v)) cellules[col] = v;
                }
                if (cellules.Count > 0) lignes[numLigne] = cellules;
            }

            Dictionary<int, string> entete;
            if (!lignes.TryGetValue(ligneEntete, out entete))
                throw new InvalidDataException("La ligne d'en-tête n° " + ligneEntete + " est vide dans la feuille « " + choisie.Key + " ».");

            int nbCol = Math.Max(entete.Keys.Max() + 1, lignes.Values.Where(l => l.Count > 0).Select(l => l.Keys.Max() + 1).DefaultIfEmpty(0).Max());
            for (int i = 0; i < nbCol; i++)
            {
                string t;
                table.Entetes.Add(entete.TryGetValue(i, out t) ? t.Trim() : "");
            }

            foreach (var kv in lignes.Where(l => l.Key > ligneEntete))
            {
                var valeurs = new string[nbCol];
                foreach (var cel in kv.Value)
                    if (cel.Key < nbCol) valeurs[cel.Key] = cel.Value;
                if (valeurs.All(string.IsNullOrWhiteSpace)) continue;
                table.Lignes.Add(valeurs);
                table.NumerosLigne.Add(kv.Key);
            }
            return table;
        }

        private string ValeurCellule(XElement c)
        {
            var type = (string)c.Attribute("t");
            var v = (string)c.Element(Ns + "v");
            switch (type)
            {
                case "s":
                    int idx;
                    return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) && idx >= 0 && idx < _chainesPartagees.Count
                        ? _chainesPartagees[idx] : "";
                case "inlineStr":
                    var isEl = c.Element(Ns + "is");
                    return isEl == null ? "" : string.Concat(isEl.Descendants(Ns + "t").Select(t => (string)t));
                case "str":
                case "e":
                    return v ?? "";
                case "b":
                    return v == "1" ? "VRAI" : "FAUX";
                case "d":
                    return v ?? "";
                default:
                    if (string.IsNullOrEmpty(v)) return "";
                    double d;
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return v;
                    int style = (int?)c.Attribute("s") ?? 0;
                    if (_stylesDate.Contains(style) && d > 0 && d < 2958466)
                        return DateTime.FromOADate(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return Analyseurs.NombreEnTexte(d);
            }
        }

        public void Dispose()
        {
            _zip.Dispose();
            _flux.Dispose();
        }
    }
}
