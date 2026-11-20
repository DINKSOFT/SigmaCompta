using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace SigmaCompta.Core.Fichiers
{
    /// <summary>
    /// Écrit un classeur .xlsx minimal (une feuille, en-tête en gras, colonnes dimensionnées).
    /// Les valeurs <see cref="decimal"/>/<see cref="double"/>/<see cref="int"/> sont écrites en nombre,
    /// les <see cref="DateTime"/> en date Excel, le reste en texte.
    /// <paramref name="enGras"/> (facultatif) reçoit l'index de la ligne de données (0 = première ligne
    /// après l'en-tête) et indique les lignes de total à écrire en gras.
    /// </summary>
    public static class EcrivainXlsx
    {
        public static void Ecrire(string chemin, string nomFeuille, IList<string> entetes, IEnumerable<object[]> lignes, IList<double> largeurs = null,
                                  Func<int, bool> enGras = null)
        {
            if (File.Exists(chemin)) File.Delete(chemin);
            using (var fs = new FileStream(chemin, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Ajouter(zip, "[Content_Types].xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                    "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                    "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                    "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                    "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                    "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                    "</Types>");
                Ajouter(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                    "</Relationships>");
                Ajouter(zip, "xl/workbook.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                    "<sheets><sheet name=\"" + Xml(Tronquer(nomFeuille, 31)) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Ajouter(zip, "xl/_rels/workbook.xml.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                    "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                    "</Relationships>");
                // Styles : 0 = normal, 1 = en-tête gras sur fond, 2 = date jj/mm/aaaa, 3 = nombre # ##0.##,
                //          4 = texte gras, 5 = nombre gras, 6 = date grasse (lignes de total)
                Ajouter(zip, "xl/styles.xml",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                    "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                    "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.##\"/></numFmts>" +
                    "<fonts count=\"3\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
                    "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                    "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                    "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1E3A5F\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                    "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                    "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                    "<cellXfs count=\"7\">" +
                    "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                    "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                    "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                    "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                    "<xf numFmtId=\"164\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
                    "<xf numFmtId=\"14\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
                    "</cellXfs>" +
                    "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                    "</styleSheet>");

                var sb = new StringBuilder();
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
                sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
                sb.Append("<cols>");
                for (int i = 0; i < entetes.Count; i++)
                {
                    double l = largeurs != null && i < largeurs.Count ? largeurs[i] : Math.Max(12, entetes[i].Length + 4);
                    sb.Append("<col min=\"").Append(i + 1).Append("\" max=\"").Append(i + 1).Append("\" width=\"")
                      .Append(l.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
                }
                sb.Append("</cols><sheetData>");

                int r = 1;
                sb.Append("<row r=\"1\">");
                for (int i = 0; i < entetes.Count; i++) Cellule(sb, i, r, entetes[i], 1);
                sb.Append("</row>");

                foreach (var ligne in lignes)
                {
                    r++;
                    int mode = enGras != null && enGras(r - 2) ? 2 : 0;
                    sb.Append("<row r=\"").Append(r).Append("\">");
                    for (int i = 0; i < ligne.Length; i++)
                    {
                        if (ligne[i] == null) continue;
                        Cellule(sb, i, r, ligne[i], mode);
                    }
                    sb.Append("</row>");
                }
                sb.Append("</sheetData>");
                sb.Append("<autoFilter ref=\"A1:").Append(Colonnes.Lettre(Math.Max(0, entetes.Count - 1))).Append(r).Append("\"/>");
                sb.Append("</worksheet>");
                Ajouter(zip, "xl/worksheets/sheet1.xml", sb.ToString());
            }
        }

        /// <summary>mode : 0 = donnée, 1 = en-tête, 2 = donnée en gras.</summary>
        private static void Cellule(StringBuilder sb, int col, int ligne, object valeur, int mode)
        {
            var reference = Colonnes.Lettre(col) + ligne;
            if (valeur is DateTime)
            {
                sb.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(mode == 2 ? 6 : 2).Append("\"><v>")
                  .Append(((DateTime)valeur).ToOADate().ToString(CultureInfo.InvariantCulture)).Append("</v></c>");
                return;
            }
            if (valeur is decimal || valeur is double || valeur is int || valeur is long)
            {
                var txt = Convert.ToDecimal(valeur, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                sb.Append("<c r=\"").Append(reference).Append("\" s=\"").Append(mode == 1 ? 1 : mode == 2 ? 5 : 3).Append("\"><v>").Append(txt).Append("</v></c>");
                return;
            }
            int style = mode == 1 ? 1 : mode == 2 ? 4 : 0;
            sb.Append("<c r=\"").Append(reference).Append("\" t=\"inlineStr\"");
            if (style != 0) sb.Append(" s=\"").Append(style).Append("\"");
            sb.Append("><is><t xml:space=\"preserve\">").Append(Xml(Convert.ToString(valeur, CultureInfo.InvariantCulture))).Append("</t></is></c>");
        }

        private static string Xml(string s)
        {
            return SecurityElement.Escape(s ?? "") ?? "";
        }

        private static string Tronquer(string s, int n)
        {
            s = string.IsNullOrWhiteSpace(s) ? "Feuil1" : s;
            foreach (var ch in new[] { '\\', '/', '?', '*', '[', ']', ':' }) s = s.Replace(ch, '_');
            return s.Length > n ? s.Substring(0, n) : s;
        }

        private static void Ajouter(ZipArchive zip, string nom, string contenu)
        {
            var e = zip.CreateEntry(nom, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                w.Write(contenu);
        }
    }
}
