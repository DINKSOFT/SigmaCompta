using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SigmaCompta.Core.Fichiers
{
    public enum PolicePdf { Normal, Gras, Italique }

    /// <summary>Couleur RVB (composantes de 0 à 1).</summary>
    public struct CouleurPdf
    {
        public CouleurPdf(double r, double v, double b) { R = r; V = v; B = b; }
        public double R { get; private set; }
        public double V { get; private set; }
        public double B { get; private set; }

        public static CouleurPdf Noir { get { return new CouleurPdf(0.08, 0.13, 0.20); } }
        public static CouleurPdf Gris { get { return new CouleurPdf(0.42, 0.47, 0.56); } }
        public static CouleurPdf Bleu { get { return new CouleurPdf(0.15, 0.26, 0.72); } }
    }

    /// <summary>
    /// Document PDF minimal, sans bibliothèque externe : pages A4, polices standard Helvetica
    /// (rien à embarquer, encodage WinAnsi pour les accents), texte, traits et rectangles pleins.
    /// Coordonnées en points, origine en haut à gauche de la page.
    /// </summary>
    public sealed class DocumentPdf
    {
        public const double A4Largeur = 595.28;
        public const double A4Hauteur = 841.89;

        private readonly List<PagePdf> _pages = new List<PagePdf>();

        public string Titre { get; set; }
        /// <summary>Flux de page compressés (désactivable pour les tests).</summary>
        public bool Compresser { get; set; } = true;
        public IList<PagePdf> Pages { get { return _pages; } }

        public PagePdf AjouterPage(bool paysage)
        {
            var p = paysage ? new PagePdf(A4Hauteur, A4Largeur) : new PagePdf(A4Largeur, A4Hauteur);
            _pages.Add(p);
            return p;
        }

        // ---------------------------------------------------------------- Mesure du texte

        /// <summary>Largeur du texte en points.</summary>
        public static double Largeur(string texte, double taille, PolicePdf police)
        {
            if (string.IsNullOrEmpty(texte)) return 0;
            var table = police == PolicePdf.Gras ? LargeursGras : LargeursNormal;
            long total = 0;
            foreach (var ch in texte) total += table[CodeWinAnsi(ch)];
            return total * taille / 1000.0;
        }

        /// <summary>Texte raccourci avec « … » pour tenir dans la largeur donnée.</summary>
        public static string Tronquer(string texte, double largeurMax, double taille, PolicePdf police)
        {
            if (string.IsNullOrEmpty(texte) || Largeur(texte, taille, police) <= largeurMax) return texte ?? "";
            const string suite = "…";
            var n = texte.Length;
            while (n > 0 && Largeur(texte.Substring(0, n) + suite, taille, police) > largeurMax) n--;
            return n == 0 ? "" : texte.Substring(0, n).TrimEnd() + suite;
        }

        /// <summary>Découpe le texte en lignes qui tiennent dans la largeur donnée (coupure aux espaces).</summary>
        public static List<string> Couper(string texte, double largeurMax, double taille, PolicePdf police)
        {
            var lignes = new List<string>();
            var courante = "";
            foreach (var mot in (texte ?? "").Split(' '))
            {
                var essai = courante.Length == 0 ? mot : courante + " " + mot;
                if (courante.Length > 0 && Largeur(essai, taille, police) > largeurMax)
                {
                    lignes.Add(courante);
                    courante = mot;
                }
                else courante = essai;
            }
            if (courante.Length > 0) lignes.Add(Tronquer(courante, largeurMax, taille, police));
            return lignes;
        }

        // ---------------------------------------------------------------- Écriture

        public void Ecrire(string chemin)
        {
            File.WriteAllBytes(chemin, Generer());
        }

        public byte[] Generer()
        {
            if (_pages.Count == 0) AjouterPage(false);
            var objets = new List<byte[]>();
            int nbFixes = 6;                                    // catalogue, pages, 3 polices, informations
            var kids = new StringBuilder();
            for (int i = 0; i < _pages.Count; i++)
                kids.Append(nbFixes + 1 + 2 * i).Append(" 0 R ");

            objets.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
            objets.Add(Ascii("<< /Type /Pages /Kids [" + kids.ToString().TrimEnd() + "] /Count " + _pages.Count + " >>"));
            objets.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
            objets.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
            objets.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>"));
            objets.Add(Ascii("<< /Title " + ChainePdf(Titre ?? "") + " /Producer (Sigma Compta) /CreationDate (D:" +
                             DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ") >>"));

            for (int i = 0; i < _pages.Count; i++)
            {
                var p = _pages[i];
                int noContenu = nbFixes + 2 + 2 * i;
                objets.Add(Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + Nombre(p.Largeur) + " " + Nombre(p.Hauteur) + "] " +
                                 "/Resources << /Font << /F1 3 0 R /F2 4 0 R /F3 5 0 R >> >> /Contents " + noContenu + " 0 R >>"));
                var flux = Ascii(p.Contenu.ToString());
                if (Compresser) flux = Zlib(flux);
                var entete = Ascii("<< /Length " + flux.Length + (Compresser ? " /Filter /FlateDecode" : "") + " >>\nstream\n");
                var fin = Ascii("\nendstream");
                var obj = new byte[entete.Length + flux.Length + fin.Length];
                Buffer.BlockCopy(entete, 0, obj, 0, entete.Length);
                Buffer.BlockCopy(flux, 0, obj, entete.Length, flux.Length);
                Buffer.BlockCopy(fin, 0, obj, entete.Length + flux.Length, fin.Length);
                objets.Add(obj);
            }

            using (var ms = new MemoryStream())
            {
                Ecrire(ms, Ascii("%PDF-1.4\n"));
                Ecrire(ms, new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
                var positions = new long[objets.Count];
                for (int i = 0; i < objets.Count; i++)
                {
                    positions[i] = ms.Position;
                    Ecrire(ms, Ascii((i + 1) + " 0 obj\n"));
                    Ecrire(ms, objets[i]);
                    Ecrire(ms, Ascii("\nendobj\n"));
                }
                long xref = ms.Position;
                var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(objets.Count + 1).Append('\n');
                sb.Append("0000000000 65535 f\r\n");
                foreach (var pos in positions) sb.Append(pos.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n\r\n");
                sb.Append("trailer\n<< /Size ").Append(objets.Count + 1).Append(" /Root 1 0 R /Info 6 0 R >>\n");
                sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
                Ecrire(ms, Ascii(sb.ToString()));
                return ms.ToArray();
            }
        }

        private static void Ecrire(Stream s, byte[] b) { s.Write(b, 0, b.Length); }

        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        internal static string Nombre(double v)
        {
            return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Chaîne PDF littérale, caractères hors ASCII en octal (codes WinAnsi).</summary>
        public static string ChainePdf(string texte)
        {
            var sb = new StringBuilder("(");
            foreach (var ch in texte ?? "")
            {
                var b = CodeWinAnsi(ch);
                if (b == (byte)'(' || b == (byte)')' || b == 92) sb.Append((char)92).Append((char)b);   // 92 = barre oblique inverse
                else if (b < 32 || b > 126) sb.Append((char)92).Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else sb.Append((char)b);
            }
            return sb.Append(')').ToString();
        }

        /// <summary>Code du caractère dans l'encodage WinAnsi (Windows-1252) ; « ? » s'il n'y existe pas.</summary>
        internal static byte CodeWinAnsi(char c)
        {
            if (c < 0x80) return c < 32 ? (byte)' ' : (byte)c;
            if (c >= 0xA0 && c <= 0xFF) return (byte)c;
            switch (c)
            {
                case '€': return 0x80;
                case '‚': return 0x82;
                case 'ƒ': return 0x83;
                case '„': return 0x84;
                case '…': return 0x85;
                case '†': return 0x86;
                case '‡': return 0x87;
                case 'ˆ': return 0x88;
                case '‰': return 0x89;
                case 'Š': return 0x8A;
                case '‹': return 0x8B;
                case 'Œ': return 0x8C;
                case 'Ž': return 0x8E;
                case '‘': return 0x91;
                case '’': return 0x92;
                case '“': return 0x93;
                case '”': return 0x94;
                case '•': return 0x95;
                case '–': return 0x96;
                case '—': return 0x97;
                case '˜': return 0x98;
                case '™': return 0x99;
                case 'š': return 0x9A;
                case '›': return 0x9B;
                case 'œ': return 0x9C;
                case 'ž': return 0x9E;
                case 'Ÿ': return 0x9F;
                case ' ':
                case ' ':
                case ' ': return 0xA0;   // espaces fines (séparateurs de milliers)
                case '−': return (byte)'-';
                case '→': return (byte)'>';
                default: return (byte)'?';
            }
        }

        /// <summary>Compression zlib (en-tête, deflate, somme Adler-32) attendue par /FlateDecode.</summary>
        private static byte[] Zlib(byte[] donnees)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (var d = new DeflateStream(ms, CompressionLevel.Optimal, true))
                    d.Write(donnees, 0, donnees.Length);
                uint a = 1, b = 0;
                foreach (var x in donnees)
                {
                    a = (a + x) % 65521;
                    b = (b + a) % 65521;
                }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24));
                ms.WriteByte((byte)(adler >> 16));
                ms.WriteByte((byte)(adler >> 8));
                ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        // Largeurs des polices standard Helvetica et Helvetica-Bold (millièmes d'em, codes WinAnsi 0 à 255).
        private static readonly short[] LargeursNormal =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584, 350,
            556, 350, 222, 556, 333, 1000, 556, 556, 333, 1000, 667, 333, 1000, 350, 611, 350,
            350, 222, 222, 333, 333, 350, 556, 1000, 333, 1000, 500, 333, 944, 350, 500, 667,
            278, 333, 556, 556, 556, 556, 260, 556, 333, 737, 370, 556, 584, 333, 737, 333,
            400, 584, 333, 333, 333, 556, 537, 278, 333, 333, 365, 556, 834, 834, 834, 611,
            667, 667, 667, 667, 667, 667, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
            722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
            556, 556, 556, 556, 556, 556, 889, 500, 556, 556, 556, 556, 278, 278, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 584, 611, 556, 556, 556, 556, 500, 556, 500,
        };
        private static readonly short[] LargeursGras =
        {
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
            975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
            333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
            611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584, 350,
            556, 350, 278, 556, 500, 1000, 556, 556, 333, 1000, 667, 333, 1000, 350, 611, 350,
            350, 278, 278, 500, 500, 350, 556, 1000, 333, 1000, 556, 333, 944, 350, 500, 667,
            278, 333, 556, 556, 556, 556, 280, 556, 333, 737, 370, 556, 584, 333, 737, 333,
            400, 584, 333, 333, 333, 611, 556, 278, 333, 333, 365, 556, 834, 834, 834, 611,
            722, 722, 722, 722, 722, 722, 1000, 722, 667, 667, 667, 667, 278, 278, 278, 278,
            722, 722, 778, 778, 778, 778, 778, 584, 778, 722, 722, 722, 722, 667, 667, 611,
            556, 556, 556, 556, 556, 556, 889, 556, 556, 556, 556, 556, 278, 278, 278, 278,
            611, 611, 611, 611, 611, 611, 611, 584, 611, 611, 611, 611, 611, 556, 611, 556,
        };
    }

    /// <summary>Une page : les instructions de dessin s'accumulent dans son flux.</summary>
    public sealed class PagePdf
    {
        internal PagePdf(double largeur, double hauteur)
        {
            Largeur = largeur;
            Hauteur = hauteur;
        }

        public double Largeur { get; private set; }
        public double Hauteur { get; private set; }
        internal StringBuilder Contenu { get; } = new StringBuilder();

        private static string N(double v) { return DocumentPdf.Nombre(v); }

        private static string Police(PolicePdf p)
        {
            return p == PolicePdf.Gras ? "/F2" : p == PolicePdf.Italique ? "/F3" : "/F1";
        }

        /// <summary>Texte dont la ligne de base est à <paramref name="y"/> (depuis le haut de la page).</summary>
        public void Texte(double x, double y, string texte, double taille, PolicePdf police, CouleurPdf couleur)
        {
            if (string.IsNullOrEmpty(texte)) return;
            Contenu.Append("BT ").Append(Police(police)).Append(' ').Append(N(taille)).Append(" Tf ")
                   .Append(N(couleur.R)).Append(' ').Append(N(couleur.V)).Append(' ').Append(N(couleur.B)).Append(" rg ")
                   .Append(N(x)).Append(' ').Append(N(Hauteur - y)).Append(" Td ")
                   .Append(DocumentPdf.ChainePdf(texte)).Append(" Tj ET\n");
        }

        public void Texte(double x, double y, string texte, double taille, PolicePdf police)
        {
            Texte(x, y, texte, taille, police, CouleurPdf.Noir);
        }

        /// <summary>Texte aligné à droite sur <paramref name="xDroite"/>.</summary>
        public void TexteDroite(double xDroite, double y, string texte, double taille, PolicePdf police, CouleurPdf couleur)
        {
            Texte(xDroite - DocumentPdf.Largeur(texte, taille, police), y, texte, taille, police, couleur);
        }

        public void Trait(double x1, double y1, double x2, double y2, double epaisseur, CouleurPdf couleur)
        {
            Contenu.Append("q ").Append(N(epaisseur)).Append(" w ")
                   .Append(N(couleur.R)).Append(' ').Append(N(couleur.V)).Append(' ').Append(N(couleur.B)).Append(" RG ")
                   .Append(N(x1)).Append(' ').Append(N(Hauteur - y1)).Append(" m ")
                   .Append(N(x2)).Append(' ').Append(N(Hauteur - y2)).Append(" l S Q\n");
        }

        /// <summary>Rectangle plein ; (x, y) est le coin supérieur gauche.</summary>
        public void Rectangle(double x, double y, double largeur, double hauteur, CouleurPdf couleur)
        {
            Contenu.Append("q ").Append(N(couleur.R)).Append(' ').Append(N(couleur.V)).Append(' ').Append(N(couleur.B)).Append(" rg ")
                   .Append(N(x)).Append(' ').Append(N(Hauteur - y - hauteur)).Append(' ')
                   .Append(N(largeur)).Append(' ').Append(N(hauteur)).Append(" re f Q\n");
        }
    }
}
