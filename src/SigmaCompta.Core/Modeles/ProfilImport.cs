using System.Runtime.Serialization;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>
    /// Fichier de paramètres d'import (.json) : décrit le fichier de données,
    /// la correspondance des colonnes, le regroupement en pièces et les options.
    /// Plusieurs noms d'en-tête possibles sont séparés par « | » ;
    /// « [C] » désigne directement la colonne C.
    /// </summary>
    [DataContract(Name = "ProfilImport", Namespace = "")]
    public sealed class ProfilImport
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new ProfilImport(), this); }

        public const int VersionFormatCourante = 1;

        [DataMember(Order = 0)] public int VersionFormat { get; set; } = VersionFormatCourante;
        [DataMember(Order = 1)] public string Nom { get; set; } = "Nouveau profil";
        [DataMember(Order = 2)] public string Description { get; set; } = "";
        [DataMember(Order = 3)] public SourceProfil Source { get; set; } = new SourceProfil();
        [DataMember(Order = 4)] public ColonnesProfil Colonnes { get; set; } = new ColonnesProfil();
        [DataMember(Order = 5)] public string Regroupement { get; set; } = ModesRegroupement.JournalDatePiece;
        [DataMember(Order = 6)] public DefautsProfil Defauts { get; set; } = new DefautsProfil();
        [DataMember(Order = 7)] public OptionsProfil Options { get; set; } = new OptionsProfil();

        /// <summary>Garantit qu'aucune sous-section n'est nulle (fichier JSON incomplet).</summary>
        public ProfilImport Normaliser()
        {
            if (Source == null) Source = new SourceProfil();
            if (Colonnes == null) Colonnes = new ColonnesProfil();
            if (Defauts == null) Defauts = new DefautsProfil();
            if (Options == null) Options = new OptionsProfil();
            if (string.IsNullOrWhiteSpace(Regroupement)) Regroupement = ModesRegroupement.JournalDatePiece;
            if (string.IsNullOrWhiteSpace(Source.FormatsDate)) Source.FormatsDate = SourceProfil.FormatsDateParDefaut;
            if (Source.LigneEntete < 1) Source.LigneEntete = 1;
            return this;
        }

        public static ProfilImport BrouillardStandard()
        {
            return new ProfilImport
            {
                Nom = "Brouillard standard",
                Description = "Colonnes JO_Num, Date, EC_Piece, EC_Reference, CG_Num, CT_Num, EC_Intitule, Débit, Crédit (Excel ou CSV).",
                Regroupement = ModesRegroupement.JournalDatePiece
            };
        }
    }

    [DataContract(Name = "Source", Namespace = "")]
    public sealed class SourceProfil
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new SourceProfil(), this); }

        public const string FormatsDateParDefaut = "dd/MM/yyyy|d/M/yyyy|dd/MM/yy|yyyy-MM-dd|dd-MM-yyyy|dd.MM.yyyy|ddMMyyyy|ddMMyy|yyyyMMdd";

        /// <summary>Auto | Excel | Csv</summary>
        [DataMember(Order = 0)] public string Type { get; set; } = "Auto";
        /// <summary>Nom de la feuille Excel (vide = première feuille).</summary>
        [DataMember(Order = 1)] public string Feuille { get; set; } = "";
        /// <summary>N° de la ligne d'en-tête (1 = première ligne).</summary>
        [DataMember(Order = 2)] public int LigneEntete { get; set; } = 1;
        /// <summary>CSV : auto | ; | , | tab | |</summary>
        [DataMember(Order = 3)] public string Separateur { get; set; } = "auto";
        /// <summary>CSV : windows-1252 | utf-8 (un BOM UTF-8 est toujours détecté).</summary>
        [DataMember(Order = 4)] public string Encodage { get; set; } = "windows-1252";
        /// <summary>Formats de date acceptés, séparés par « | ». Les dates Excel natives sont toujours reconnues.</summary>
        [DataMember(Order = 5)] public string FormatsDate { get; set; } = FormatsDateParDefaut;
        /// <summary>auto | , | .</summary>
        [DataMember(Order = 6)] public string SeparateurDecimal { get; set; } = "auto";
    }

    [DataContract(Name = "Colonnes", Namespace = "")]
    public sealed class ColonnesProfil
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new ColonnesProfil(), this); }

        [DataMember(Order = 0)] public string Journal { get; set; } = "JO_Num|Journal|Code journal";
        [DataMember(Order = 1)] public string Date { get; set; } = "Date|EC_Date|Date pièce|Date piece";
        [DataMember(Order = 2)] public string Piece { get; set; } = "EC_Piece|N° pièce|N° piece|Pièce|Piece|No piece";
        [DataMember(Order = 3)] public string Reference { get; set; } = "EC_Reference|Référence|Reference";
        [DataMember(Order = 4)] public string RefPiece { get; set; } = "EC_RefPiece|N° facture|Ref piece|Référence pièce";
        [DataMember(Order = 5)] public string CompteGeneral { get; set; } = "CG_Num|Compte|Compte général|Compte general|N° compte";
        [DataMember(Order = 6)] public string CompteTiers { get; set; } = "CT_Num|Tiers|Compte tiers";
        [DataMember(Order = 7)] public string Libelle { get; set; } = "EC_Intitule|Libellé|Libelle|Intitulé";
        [DataMember(Order = 8)] public string Debit { get; set; } = "Débit|Debit|Montant débit";
        [DataMember(Order = 9)] public string Credit { get; set; } = "Crédit|Credit|Montant crédit";
        /// <summary>Alternative à Débit/Crédit : un montant signé ou associé à la colonne Sens.</summary>
        [DataMember(Order = 10)] public string Montant { get; set; } = "Montant|EC_Montant";
        /// <summary>Sens : D/C, 0/1, Débit/Crédit.</summary>
        [DataMember(Order = 11)] public string Sens { get; set; } = "Sens|EC_Sens";
        [DataMember(Order = 12)] public string Echeance { get; set; } = "EC_Echeance|Échéance|Echeance";
    }

    [DataContract(Name = "Defauts", Namespace = "")]
    public sealed class DefautsProfil
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new DefautsProfil(), this); }

        /// <summary>Journal utilisé quand la colonne Journal est absente ou vide.</summary>
        [DataMember(Order = 0)] public string Journal { get; set; } = "";
        /// <summary>Libellé utilisé quand la ligne n'en a pas.</summary>
        [DataMember(Order = 1)] public string Libelle { get; set; } = "";
        /// <summary>Si &gt; 0 : complète à droite par des zéros les comptes généraux numériques (ex. 7 → 422 devient 4220000).</summary>
        [DataMember(Order = 2)] public int LongueurComptes { get; set; } = 0;
    }

    [DataContract(Name = "Options", Namespace = "")]
    public sealed class OptionsProfil
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new OptionsProfil(), this); }

        /// <summary>Ignore les n° de pièce du fichier : la procédure numérote toutes les pièces.</summary>
        [DataMember(Order = 0)] public bool NumerotationAutomatique { get; set; } = false;
        [DataMember(Order = 1)] public bool AutoriserDoublon { get; set; } = false;
        [DataMember(Order = 2)] public bool IgnorerHash { get; set; } = false;
        /// <summary>ParPiece | ToutOuRien</summary>
        [DataMember(Order = 3)] public string ModeTransaction { get; set; } = ModesTransaction.ParPiece;
        [DataMember(Order = 4)] public bool ArreterPremiereErreur { get; set; } = false;
        /// <summary>Un montant négatif est porté dans l'autre colonne (débit ↔ crédit).</summary>
        [DataMember(Order = 5)] public bool InverserMontantsNegatifs { get; set; } = true;
        /// <summary>Ignore les lignes dont débit et crédit sont nuls (lignes vides de tableur).</summary>
        [DataMember(Order = 6)] public bool IgnorerLignesSansMontant { get; set; } = true;
        /// <summary>
        /// N° de pièce du fichier déjà utilisé dans le journal : la pièce prend le n° suivant
        /// de la séquence au lieu d'être refusée. Sans effet si AutoriserDoublon est activé.
        /// </summary>
        [DataMember(Order = 7)] public bool RenumeroterDoublon { get; set; } = true;
    }

    public static class ModesRegroupement
    {
        public const string JournalDatePiece = "JournalDatePiece";
        public const string JournalPiece = "JournalPiece";
        public const string JournalDateReference = "JournalDateReference";
        public const string Equilibre = "Equilibre";

        public static readonly string[] Tous = { JournalDatePiece, JournalPiece, JournalDateReference, Equilibre };

        public static string Libelle(string mode)
        {
            switch (mode)
            {
                case JournalPiece: return "Journal + N° de pièce";
                case JournalDateReference: return "Journal + Date + Référence";
                case Equilibre: return "Lignes consécutives jusqu'à équilibre";
                default: return "Journal + Date + N° de pièce";
            }
        }
    }

    public static class ModesTransaction
    {
        public const string ParPiece = "ParPiece";
        public const string ToutOuRien = "ToutOuRien";
    }
}
