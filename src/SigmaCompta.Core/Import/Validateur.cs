using System;
using System.Collections.Generic;
using System.Linq;
using SigmaCompta.Core.Modeles;

namespace SigmaCompta.Core.Import
{
    /// <summary>
    /// Validation locale avant tout appel SQL : mêmes règles que imp.PS_ImportPiece
    /// et TG_INS_F_ECRITUREC, avec des messages par ligne du fichier.
    /// Les codes saisis sont remplacés par leur forme exacte dans la base (casse).
    /// </summary>
    public static class Validateur
    {
        public static void Valider(IList<PieceImport> pieces, Referentiel referentiel, ProfilImport profil)
        {
            foreach (var p in pieces)
            {
                // Les anomalies issues de la lecture du fichier sont conservées ; on retire celles d'une validation précédente.
                p.Anomalies.RemoveAll(a => a.DeValidation);
                foreach (var l in p.Lignes) l.Anomalies.RemoveAll(a => a.DeValidation);
                p.ReinitialiserResultatServeur();
                ValiderPiece(p, referentiel);
                p.ActualiserStatutLocal();
            }

            // N° de pièce répété dans le fichier, dans des groupes différents. Le périmètre suit le
            // contrôle de doublon de imp.PS_ImportPiece, selon le mode de numérotation du journal (JO_NumPiece) :
            //   2 = continue pour la base -> tous journaux confondus ;
            //   3 = mensuelle             -> même journal et même mois ;
            //   0, 1 ou journal inconnu   -> même journal, toutes périodes confondues.
            var doublons = pieces.Where(p => !p.NumerotationAuto)
                                 .GroupBy(p => CleDoublon(p, referentiel))
                                 .Where(g => g.Count() > 1);
            foreach (var g in doublons)
                foreach (var p in g)
                {
                    p.Anomalies.Add(Avert(0, "Le n° de pièce « " + p.PieceFournie.Trim() + " » apparaît dans " + g.Count() + " pièces du fichier " + PerimetreDoublon(p, referentiel) + "."));
                    p.ActualiserStatutLocal();
                }
        }

        /// <summary>Mode de numérotation du journal de la pièce (1 = continue par journal si le journal est inconnu, comme la procédure).</summary>
        private static int ModeNumerotationPiece(PieceImport p, Referentiel r)
        {
            Journal j;
            if (!string.IsNullOrWhiteSpace(p.Journal) && r.Journaux.TryGetValue(p.Journal.Trim(), out j))
                return j.ModeNumerotation;
            return 1;
        }

        /// <summary>Clé de regroupement des doublons, alignée sur le périmètre de numérotation de imp.PS_ImportPiece.</summary>
        private static string CleDoublon(PieceImport p, Referentiel r)
        {
            var piece = p.PieceFournie.Trim().ToUpperInvariant();
            var journal = (p.Journal ?? "").Trim().ToUpperInvariant();
            switch (ModeNumerotationPiece(p, r))
            {
                case 2: return "*|" + piece;
                case 3: return journal + "|" + (p.Date.HasValue ? p.Date.Value.ToString("yyyyMM") : "?") + "|" + piece;
                default: return journal + "|" + piece;
            }
        }

        /// <summary>Texte du périmètre dans lequel le n° de pièce doit être unique.</summary>
        private static string PerimetreDoublon(PieceImport p, Referentiel r)
        {
            var journal = (p.Journal ?? "").Trim();
            switch (ModeNumerotationPiece(p, r))
            {
                case 2: return "(numérotation continue pour la base : tous journaux confondus)";
                case 3: return "pour le journal " + journal + " en " + (p.Date.HasValue ? p.Date.Value.ToString("MM/yyyy") : "période non renseignée") + " (numérotation mensuelle)";
                default: return "pour le journal " + journal + " (numérotation continue du journal : toutes périodes confondues)";
            }
        }

        private static Anomalie Err(int ligne, string m) { return new Anomalie { Niveau = NiveauAnomalie.Erreur, Message = m, LigneFichier = ligne, DeValidation = true }; }
        private static Anomalie Avert(int ligne, string m) { return new Anomalie { Niveau = NiveauAnomalie.Avertissement, Message = m, LigneFichier = ligne, DeValidation = true }; }

        private static void ValiderPiece(PieceImport p, Referentiel r)
        {
            Journal j = null;
            if (string.IsNullOrWhiteSpace(p.Journal))
                p.Anomalies.Add(Err(0, "Journal non renseigné (colonne vide et pas de journal par défaut)."));
            else if (!r.Journaux.TryGetValue(p.Journal.Trim(), out j))
                p.Anomalies.Add(Err(0, "Journal « " + p.Journal + " » inexistant dans la base comptable."));
            else
            {
                p.Journal = j.Code;
                foreach (var l in p.Lignes) l.Journal = j.Code;
                if (j.EnSommeil) p.Anomalies.Add(Err(0, "Journal « " + j.Code + " » mis en sommeil."));
            }

            if (p.Date.HasValue)
            {
                if (r.Exercices.Count > 0 && !r.Exercices.Any(e => e.Contient(p.Date.Value)))
                    p.Anomalies.Add(Err(0, "Date " + p.Date.Value.ToString("dd/MM/yyyy") + " hors des exercices du dossier (" + string.Join(", ", r.Exercices) + ")."));
                if (j != null && r.PeriodesCloturees.Contains(Referentiel.ClePeriode(j.Code, p.Date.Value)))
                    p.Anomalies.Add(Err(0, "Période " + p.Date.Value.ToString("MM/yyyy") + " du journal " + j.Code + " clôturée."));
            }

            if (p.Lignes.Count < 2)
                p.Anomalies.Add(Err(0, "Une pièce doit comporter au moins 2 lignes."));
            if (p.Ecart != 0)
                p.Anomalies.Add(Err(0, "Pièce déséquilibrée : débit " + p.TotalDebit.ToString("#,##0.##") + " / crédit " + p.TotalCredit.ToString("#,##0.##") + " (écart " + p.Ecart.ToString("#,##0.##") + ")."));
            if (!p.NumerotationAuto && p.PieceFournie.Length > 13)
                p.Anomalies.Add(Err(0, "N° de pièce « " + p.PieceFournie + " » trop long (13 caractères maximum)."));

            if (j != null && j.Type == 2 && !string.IsNullOrEmpty(j.CompteTresorerie)
                && !p.Lignes.Any(l => string.Equals(l.CompteGeneral, j.CompteTresorerie, StringComparison.OrdinalIgnoreCase)))
                p.Anomalies.Add(Avert(0, "Journal de trésorerie : aucune ligne sur le compte " + j.CompteTresorerie + " (la contrepartie n'est pas générée automatiquement)."));

            foreach (var l in p.Lignes)
            {
                if (string.IsNullOrWhiteSpace(l.CompteGeneral))
                    l.Anomalies.Add(Err(l.LigneFichier, "Compte général vide."));
                else
                {
                    CompteGeneral g;
                    if (!r.Comptes.TryGetValue(l.CompteGeneral.Trim(), out g))
                        l.Anomalies.Add(Err(l.LigneFichier, "Compte « " + l.CompteGeneral + " » inexistant dans le plan comptable."));
                    else
                    {
                        l.CompteGeneral = g.Numero;
                        l.IntituleCompte = g.Intitule;
                        if (!g.EstImputable)
                            l.Anomalies.Add(Err(l.LigneFichier, "Compte « " + g.Numero + " » non imputable (compte de total)."));
                    }
                }

                if (!string.IsNullOrWhiteSpace(l.CompteTiers))
                {
                    Tiers t;
                    if (!r.Tiers.TryGetValue(l.CompteTiers.Trim(), out t))
                        l.Anomalies.Add(Err(l.LigneFichier, "Tiers « " + l.CompteTiers + " » inexistant."));
                    else
                    {
                        l.CompteTiers = t.Numero;
                        if (t.EstProspect) l.Anomalies.Add(Err(l.LigneFichier, "Tiers « " + t.Numero + " » est un prospect."));
                    }
                }

                if (l.Debit < 0 || l.Credit < 0)
                    l.Anomalies.Add(Err(l.LigneFichier, "Montant négatif."));
                if (l.Debit != 0 && l.Credit != 0)
                    l.Anomalies.Add(Err(l.LigneFichier, "Débit et crédit renseignés sur la même ligne."));
                if (l.Debit == 0 && l.Credit == 0)
                    l.Anomalies.Add(Err(l.LigneFichier, "Ni débit ni crédit."));
            }
        }

    }
}
