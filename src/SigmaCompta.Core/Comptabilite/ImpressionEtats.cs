using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Comptabilite
{
    /// <summary>
    /// Mise en page PDF des états comptables (A4 paysage) : en-tête société, titre et période sur chaque page,
    /// colonnes propres à l'état, lignes de regroupement et totaux en gras, « Page x / n » en pied de page.
    /// </summary>
    public static class ImpressionEtats
    {
        private const double Marge = 28;
        private const double HauteurLigne = 11.5;
        private const double TailleTexte = 7.5;
        private const double HautTableau = 87;      // première ligne de données (sous l'en-tête et les titres de colonnes)
        private const double MargeBasse = 36;

        private static readonly CouleurPdf FondTitres = new CouleurPdf(0.93, 0.94, 0.97);
        private static readonly CouleurPdf FondGroupe = new CouleurPdf(0.92, 0.94, 1.00);
        private static readonly CouleurPdf FondTotal = new CouleurPdf(0.955, 0.96, 0.975);
        private static readonly CouleurPdf FondTotalGeneral = new CouleurPdf(0.86, 0.89, 0.99);
        private static readonly CouleurPdf Trait = new CouleurPdf(0.78, 0.81, 0.87);

        private sealed class Colonne
        {
            public Colonne(string titre, double largeur, bool droite, Func<LigneEtat, string> valeur)
            {
                Titre = titre;
                LargeurFixe = largeur;
                Droite = droite;
                Valeur = valeur;
            }

            public string Titre { get; private set; }
            /// <summary>0 = colonne élastique (prend la place restante).</summary>
            public double LargeurFixe { get; private set; }
            public bool Droite { get; private set; }
            public Func<LigneEtat, string> Valeur { get; private set; }
            public double X { get; set; }
            public double Largeur { get; set; }
        }

        private static readonly NumberFormatInfo FormatMontant = CreerFormat();

        private static NumberFormatInfo CreerFormat()
        {
            var f = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            f.NumberGroupSeparator = " ";
            f.NumberDecimalSeparator = ",";
            return f;
        }

        /// <summary>« 1 250 000 », « 1 250,5 » ou vide.</summary>
        public static string Montant(decimal? v)
        {
            return v.HasValue ? v.Value.ToString("#,##0.##", FormatMontant) : "";
        }

        /// <summary>« 1 300 000 D », « 500 C », « 0 » ou vide.</summary>
        public static string Solde(decimal? v)
        {
            if (!v.HasValue) return "";
            if (v.Value == 0) return "0";
            return Montant(Math.Abs(v.Value)) + (v.Value > 0 ? " D" : " C");
        }

        private static string Date(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : "";
        }

        private static List<Colonne> Colonnes(TypeEtat etat)
        {
            switch (etat)
            {
                case TypeEtat.Balance:
                    return new List<Colonne>
                    {
                        new Colonne("Compte", 70, false, l => l.Genre == GenreLigne.Detail ? l.Compte : ""),
                        new Colonne("Intitulé", 0, false, l => l.Libelle),
                        new Colonne("Ouverture débit", 74, true, l => Montant(l.OuvertureDebit)),
                        new Colonne("Ouverture crédit", 74, true, l => Montant(l.OuvertureCredit)),
                        new Colonne("Mouvements débit", 76, true, l => Montant(l.MouvementDebit)),
                        new Colonne("Mouvements crédit", 76, true, l => Montant(l.MouvementCredit)),
                        new Colonne("Solde débiteur", 76, true, l => Montant(l.SoldeDebit)),
                        new Colonne("Solde créditeur", 76, true, l => Montant(l.SoldeCredit))
                    };
                case TypeEtat.GrandLivre:
                    return new List<Colonne>
                    {
                        new Colonne("Date", 50, false, l => Date(l.Date)),
                        new Colonne("Jnl", 34, false, l => l.Journal),
                        new Colonne("Pièce", 54, false, l => l.Piece),
                        new Colonne("Libellé", 0, false, l => l.Libelle),
                        new Colonne("Cpte associé", 64, false, l => l.CompteAssocie),
                        new Colonne("Débit", 76, true, l => Montant(l.Debit)),
                        new Colonne("Crédit", 76, true, l => Montant(l.Credit)),
                        new Colonne("Solde", 86, true, l => Solde(l.Solde)),
                        new Colonne("Lettr.", 32, false, l => l.Lettrage)
                    };
                case TypeEtat.Journal:
                    return new List<Colonne>
                    {
                        new Colonne("Date", 50, false, l => Date(l.Date)),
                        new Colonne("Pièce", 56, false, l => l.Piece),
                        new Colonne("Référence", 66, false, l => l.RefPiece),
                        new Colonne("Compte", 62, false, l => l.Compte),
                        new Colonne("Tiers", 60, false, l => l.CompteTiers),
                        new Colonne("Libellé", 0, false, l => l.Libelle),
                        new Colonne("Débit", 82, true, l => Montant(l.Debit)),
                        new Colonne("Crédit", 82, true, l => Montant(l.Credit))
                    };
                default:
                    return new List<Colonne>
                    {
                        new Colonne("Période", 110, false, l => l.Periode),
                        new Colonne("Journal", 60, false, l => l.Journal),
                        new Colonne("Intitulé", 0, false, l => l.Libelle),
                        new Colonne("Débit", 110, true, l => Montant(l.Debit)),
                        new Colonne("Crédit", 110, true, l => Montant(l.Credit))
                    };
            }
        }

        private static void Placer(List<Colonne> colonnes, double gauche, double largeurTotale)
        {
            const double espace = 6;
            var fixe = colonnes.Sum(c => c.LargeurFixe) + espace * (colonnes.Count - 1);
            var elastiques = colonnes.Count(c => c.LargeurFixe <= 0);
            var reste = Math.Max(60, largeurTotale - fixe);
            double x = gauche;
            foreach (var c in colonnes)
            {
                c.X = x;
                c.Largeur = c.LargeurFixe > 0 ? c.LargeurFixe : reste / Math.Max(1, elastiques);
                x += c.Largeur + espace;
            }
        }

        /// <summary>Le PDF de l'état. <paramref name="societe"/> et <paramref name="dossier"/> figurent en en-tête et en pied de page.</summary>
        public static byte[] Generer(ResultatEtat r, string societe, string dossier, DateTime? edition = null, bool compresser = true)
        {
            if (r == null) throw new ArgumentNullException("r");
            var doc = new DocumentPdf { Titre = r.Titre, Compresser = compresser };
            var colonnes = Colonnes(r.Etat);
            var quand = edition ?? DateTime.Now;

            PagePdf page = null;
            double largeurUtile = 0, bas = 0, y = 0;
            Action nouvellePage = () =>
            {
                page = doc.AjouterPage(true);
                largeurUtile = page.Largeur - 2 * Marge;
                bas = page.Hauteur - MargeBasse;
                Placer(colonnes, Marge, largeurUtile);
                EnTete(page, r, societe, quand, colonnes);
                y = HautTableau;
            };
            nouvellePage();

            for (int i = 0; i < r.Lignes.Count; i++)
            {
                var l = r.Lignes[i];
                // Un titre de groupe n'est jamais laissé seul en bas de page.
                double besoin = l.Genre == GenreLigne.Entete ? 3 * HauteurLigne : HauteurLigne;
                if (y + besoin > bas) nouvellePage();
                Ligne(page, l, colonnes, y, largeurUtile);
                y += HauteurLigne;
            }

            if (r.Avertissements.Count > 0)
            {
                y += HauteurLigne * 0.8;
                if (y + 2 * HauteurLigne > bas) nouvellePage();
                page.Texte(Marge, y + 8, "Avertissements", TailleTexte + 0.5, PolicePdf.Gras, CouleurPdf.Noir);
                y += HauteurLigne;
                foreach (var a in r.Avertissements)
                    foreach (var morceau in DocumentPdf.Couper("• " + a, largeurUtile, TailleTexte, PolicePdf.Normal))
                    {
                        if (y + HauteurLigne > bas) nouvellePage();
                        page.Texte(Marge, y + 8, morceau, TailleTexte, PolicePdf.Normal, CouleurPdf.Noir);
                        y += HauteurLigne;
                    }
            }

            // Pieds de page, une fois le nombre de pages connu.
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                var p = doc.Pages[i];
                var py = p.Hauteur - 18;
                p.Trait(Marge, py - 10, p.Largeur - Marge, py - 10, 0.5, Trait);
                p.Texte(Marge, py, "Sigma Compta" + (string.IsNullOrWhiteSpace(dossier) ? "" : " · " + dossier.Trim()), 7, PolicePdf.Normal, CouleurPdf.Gris);
                p.TexteDroite(p.Largeur - Marge, py, "Page " + (i + 1) + " / " + doc.Pages.Count, 7, PolicePdf.Normal, CouleurPdf.Gris);
            }
            return doc.Generer();
        }

        public static void Ecrire(string chemin, ResultatEtat r, string societe, string dossier)
        {
            System.IO.File.WriteAllBytes(chemin, Generer(r, societe, dossier));
        }

        private static void EnTete(PagePdf p, ResultatEtat r, string societe, DateTime edition, List<Colonne> colonnes)
        {
            var droite = p.Largeur - Marge;
            p.Texte(Marge, 30, DocumentPdf.Tronquer(string.IsNullOrWhiteSpace(societe) ? "" : societe.Trim(), 420, 10, PolicePdf.Gras), 10, PolicePdf.Gras, CouleurPdf.Noir);
            p.TexteDroite(droite, 30, "Édité le " + edition.ToString("dd/MM/yyyy à HH:mm", CultureInfo.InvariantCulture), 7.5, PolicePdf.Normal, CouleurPdf.Gris);
            p.Texte(Marge, 50, r.Titre ?? "", 15, PolicePdf.Gras, CouleurPdf.Bleu);
            p.Texte(Marge, 63, DocumentPdf.Tronquer(r.SousTitre ?? "", droite - Marge, 8.5, PolicePdf.Normal), 8.5, PolicePdf.Normal, CouleurPdf.Gris);

            // Titres de colonnes
            const double haut = 70, hauteur = 14;
            p.Rectangle(Marge, haut, droite - Marge, hauteur, FondTitres);
            foreach (var c in colonnes)
            {
                var t = DocumentPdf.Tronquer(c.Titre, c.Largeur, 7, PolicePdf.Gras);
                if (c.Droite) p.TexteDroite(c.X + c.Largeur, haut + 9.5, t, 7, PolicePdf.Gras, CouleurPdf.Gris);
                else p.Texte(c.X, haut + 9.5, t, 7, PolicePdf.Gras, CouleurPdf.Gris);
            }
        }

        private static void Ligne(PagePdf p, LigneEtat l, List<Colonne> colonnes, double y, double largeurUtile)
        {
            var police = PolicePdf.Normal;
            var couleur = CouleurPdf.Noir;
            switch (l.Genre)
            {
                case GenreLigne.Entete:
                    p.Rectangle(Marge, y + 1, largeurUtile, HauteurLigne, FondGroupe);
                    p.Texte(Marge + 2, y + 9, DocumentPdf.Tronquer(l.Libelle ?? "", largeurUtile - 4, TailleTexte + 0.5, PolicePdf.Gras),
                            TailleTexte + 0.5, PolicePdf.Gras, CouleurPdf.Bleu);
                    return;
                case GenreLigne.Report:
                    police = PolicePdf.Italique;
                    break;
                case GenreLigne.SousTotal:
                case GenreLigne.Total:
                    police = PolicePdf.Gras;
                    p.Rectangle(Marge, y + 1, largeurUtile, HauteurLigne, FondTotal);
                    break;
                case GenreLigne.TotalGeneral:
                    police = PolicePdf.Gras;
                    couleur = CouleurPdf.Bleu;
                    p.Rectangle(Marge, y + 1, largeurUtile, HauteurLigne, FondTotalGeneral);
                    p.Trait(Marge, y + 1, Marge + largeurUtile, y + 1, 0.8, CouleurPdf.Bleu);
                    break;
            }
            foreach (var c in colonnes)
            {
                var texte = DocumentPdf.Tronquer(c.Valeur(l) ?? "", c.Largeur, TailleTexte, police);
                if (texte.Length == 0) continue;
                if (c.Droite) p.TexteDroite(c.X + c.Largeur, y + 9, texte, TailleTexte, police, couleur);
                else p.Texte(c.X, y + 9, texte, TailleTexte, police, couleur);
            }
        }

        // ================================================================ Extrait de compte

        /// <summary>
        /// Consultation d'un compte présentée comme un état (grand livre d'un seul compte) pour l'impression :
        /// titre du compte, report, écritures avec solde progressif, total des mouvements.
        /// </summary>
        public static ResultatEtat ExtraitDeCompte(ResultatConsultation c)
        {
            if (c == null) throw new ArgumentNullException("c");
            var p = new ParametresEtat { Etat = TypeEtat.GrandLivre, Nature = c.Nature, Du = c.Du, Au = c.Au, CompteDe = c.Compte, CompteA = c.Compte };
            var filtre = c.Lettrage == FiltreLettrage.NonLettrees ? " · lignes non lettrées"
                       : c.Lettrage == FiltreLettrage.Lettrees ? " · lignes lettrées" : "";
            var r = new ResultatEtat
            {
                Parametres = p,
                Titre = "Extrait de compte",
                SousTitre = (c.Nature == NatureCompte.General ? "Compte " : "Tiers ") + c.Compte + " " + (c.Intitule ?? "") +
                            " · du " + c.Du.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) +
                            " au " + c.Au.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) + filtre,
                DebutReport = c.DebutReport,
                TotalDebit = c.MouvementsDebit,
                TotalCredit = c.MouvementsCredit,
                NombreComptes = 1,
                NombreEcritures = c.Lignes.Count
            };
            r.Lignes.Add(new LigneEtat { Genre = GenreLigne.Entete, Compte = c.Compte, Libelle = c.Compte + "  " + (c.Intitule ?? "") });
            foreach (var l in c.LignesAvecReport)
                r.Lignes.Add(new LigneEtat
                {
                    Genre = l.EstReport ? GenreLigne.Report : GenreLigne.Detail,
                    Compte = c.Compte, Date = l.Date, Journal = l.Journal, Piece = l.Piece, RefPiece = l.RefPiece,
                    CompteAssocie = l.CompteAssocie, Libelle = l.Libelle,
                    Debit = l.DebitAffiche, Credit = l.CreditAffiche, Solde = l.Solde, Lettrage = l.Lettrage, Echeance = l.Echeance
                });
            r.Lignes.Add(new LigneEtat
            {
                Genre = GenreLigne.TotalGeneral, Compte = c.Compte, Libelle = "Total des mouvements · solde au " + c.Au.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                Debit = c.MouvementsDebit == 0 ? (decimal?)null : c.MouvementsDebit,
                Credit = c.MouvementsCredit == 0 ? (decimal?)null : c.MouvementsCredit,
                Solde = c.SoldeFinal
            });
            return r;
        }
    }
}
