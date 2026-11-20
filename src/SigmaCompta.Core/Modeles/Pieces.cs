using System;
using System.Collections.Generic;
using System.Linq;

namespace SigmaCompta.Core.Modeles
{
    public enum NiveauAnomalie { Avertissement, Erreur }

    public sealed class Anomalie
    {
        public NiveauAnomalie Niveau { get; set; }
        public string Message { get; set; }
        /// <summary>N° de ligne dans le fichier source (0 = concerne la pièce entière).</summary>
        public int LigneFichier { get; set; }
        /// <summary>Produite par la validation contre le référentiel comptable (recalculée à chaque validation).</summary>
        public bool DeValidation { get; set; }

        public bool EstErreur { get { return Niveau == NiveauAnomalie.Erreur; } }

        public override string ToString()
        {
            return (LigneFichier > 0 ? "Ligne " + LigneFichier + " : " : "") + Message;
        }
    }

    /// <summary>Une ligne d'écriture issue du fichier source.</summary>
    public sealed class LigneImport
    {
        public int LigneFichier { get; set; }
        public int NumLigne { get; set; }
        public string Journal { get; set; }
        public DateTime? Date { get; set; }
        public string Piece { get; set; }
        public string Reference { get; set; }
        public string RefPiece { get; set; }
        public string CompteGeneral { get; set; }
        public string CompteTiers { get; set; }
        public string Libelle { get; set; }
        public decimal Debit { get; set; }
        public decimal Credit { get; set; }
        public DateTime? Echeance { get; set; }

        /// <summary>Intitulé du compte (renseigné par la validation, pour affichage).</summary>
        public string IntituleCompte { get; set; }
        public int? EcNo { get; set; }

        public List<Anomalie> Anomalies { get; } = new List<Anomalie>();
        public bool AErreur { get { return Anomalies.Any(a => a.EstErreur); } }
        public bool AAvertissement { get { return Anomalies.Any(a => !a.EstErreur); } }
        public string ResumeAnomalies { get { return string.Join(" · ", Anomalies.Select(a => a.Message)); } }
    }

    public enum StatutPiece { AControler, Valide, Avertissement, Erreur, Simulee, Importee, Echec, Annulee }

    /// <summary>Une pièce = un appel à imp.PS_ImportPiece.</summary>
    public sealed class PieceImport
    {
        public int Numero { get; set; }
        public string Cle { get; set; }
        public string Journal { get; set; }
        public DateTime? Date { get; set; }
        /// <summary>N° de pièce fourni par le fichier (vide = numérotation automatique).</summary>
        public string PieceFournie { get; set; }
        public string RefPiece { get; set; }
        public List<LigneImport> Lignes { get; } = new List<LigneImport>();
        public List<Anomalie> Anomalies { get; } = new List<Anomalie>();

        public StatutPiece Statut { get; set; } = StatutPiece.AControler;
        public string NumeroAttribue { get; set; }
        public int? EcNoDebut { get; set; }
        public int? EcNoFin { get; set; }
        public string MessageServeur { get; set; }
        /// <summary>Simulation/import : nombre de lignes dont cbHash a été renseigné à l'insertion (null = inconnu).</summary>
        public int? LignesHashees { get; set; }

        public decimal TotalDebit { get { return Lignes.Sum(l => l.Debit); } }
        public decimal TotalCredit { get { return Lignes.Sum(l => l.Credit); } }
        public decimal Ecart { get { return TotalDebit - TotalCredit; } }
        public int NombreLignes { get { return Lignes.Count; } }
        public bool NumerotationAuto { get { return string.IsNullOrWhiteSpace(PieceFournie); } }
        /// <summary>Le n° fourni était déjà utilisé : le serveur a attribué le n° suivant de la séquence.</summary>
        public bool Renumerotee
        {
            get
            {
                return !NumerotationAuto && !string.IsNullOrEmpty(NumeroAttribue)
                       && !string.Equals(NumeroAttribue.Trim(), PieceFournie.Trim(), StringComparison.Ordinal);
            }
        }

        public IEnumerable<Anomalie> ToutesAnomalies
        {
            get { return Anomalies.Concat(Lignes.SelectMany(l => l.Anomalies)); }
        }

        public bool AErreur { get { return ToutesAnomalies.Any(a => a.EstErreur); } }
        public bool AAvertissement { get { return ToutesAnomalies.Any(a => !a.EstErreur); } }
        public int NombreErreurs { get { return ToutesAnomalies.Count(a => a.EstErreur); } }

        public string PieceAffichee
        {
            get
            {
                if (!string.IsNullOrEmpty(NumeroAttribue)) return NumeroAttribue;
                return NumerotationAuto ? "(auto)" : PieceFournie;
            }
        }

        public string PlageEcNo
        {
            get
            {
                if (!EcNoDebut.HasValue) return "";
                return EcNoDebut == EcNoFin ? EcNoDebut.ToString() : EcNoDebut + " → " + EcNoFin;
            }
        }

        public string Resume
        {
            get
            {
                if (!string.IsNullOrEmpty(MessageServeur)) return MessageServeur;
                var premiere = ToutesAnomalies.OrderBy(a => a.EstErreur ? 0 : 1).FirstOrDefault();
                return premiere == null ? "" : premiere.ToString();
            }
        }

        /// <summary>Recalcule le statut à partir des anomalies locales.</summary>
        public void ActualiserStatutLocal()
        {
            Statut = AErreur ? StatutPiece.Erreur : AAvertissement ? StatutPiece.Avertissement : StatutPiece.Valide;
        }

        public void ReinitialiserResultatServeur()
        {
            NumeroAttribue = null;
            EcNoDebut = null;
            EcNoFin = null;
            MessageServeur = null;
            LignesHashees = null;
            foreach (var l in Lignes) l.EcNo = null;
        }
    }

    /// <summary>Données brutes lues dans le fichier (toutes les valeurs en texte).</summary>
    public sealed class TableSource
    {
        public string Chemin { get; set; }
        public string Feuille { get; set; }
        public List<string> Entetes { get; } = new List<string>();
        public List<string[]> Lignes { get; } = new List<string[]>();
        /// <summary>N° de ligne réel dans le fichier pour chaque élément de <see cref="Lignes"/>.</summary>
        public List<int> NumerosLigne { get; } = new List<int>();
        public List<string> FeuillesDisponibles { get; } = new List<string>();

        public string Valeur(int ligne, int colonne)
        {
            var l = Lignes[ligne];
            return colonne >= 0 && colonne < l.Length ? l[colonne] : null;
        }
    }

    /// <summary>Résultat de la correspondance d'un champ logique vers une colonne du fichier.</summary>
    public sealed class CorrespondanceColonne
    {
        public string Champ { get; set; }
        public string Libelle { get; set; }
        public string Candidats { get; set; }
        public int IndexColonne { get; set; } = -1;
        public string EnteteTrouvee { get; set; }
        public bool Obligatoire { get; set; }

        public bool Trouvee { get { return IndexColonne >= 0; } }
        public string Affichage
        {
            get { return Trouvee ? EnteteTrouvee + " (col. " + Fichiers.Colonnes.Lettre(IndexColonne) + ")" : "—"; }
        }
    }
}
