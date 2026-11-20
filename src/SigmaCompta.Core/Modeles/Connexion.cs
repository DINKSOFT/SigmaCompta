using System;
using Microsoft.Data.SqlClient;

namespace SigmaCompta.Core.Modeles
{
    /// <summary>Paramètres de connexion au serveur SQL Server hébergeant les bases comptables.</summary>
    public sealed class ParametresConnexion
    {
        public string Serveur { get; set; } = "";
        public bool AuthentificationWindows { get; set; } = true;
        public string Login { get; set; } = "";
        public string MotDePasse { get; set; } = "";
        public int DelaiConnexionSecondes { get; set; } = 8;

        public string ConstruireChaine(string baseDeDonnees = null)
        {
            var b = new SqlConnectionStringBuilder
            {
                DataSource = (Serveur ?? "").Trim(),
                InitialCatalog = string.IsNullOrWhiteSpace(baseDeDonnees) ? "master" : baseDeDonnees,
                IntegratedSecurity = AuthentificationWindows,
                ConnectTimeout = DelaiConnexionSecondes,
                ApplicationName = "SigmaCompta",
                MultipleActiveResultSets = false,
                Pooling = true,
                // Le pilote Microsoft.Data.SqlClient chiffre par défaut et refuse les certificats auto-signés.
                // On garde le comportement de l'ancien pilote : chiffrement seulement si le serveur l'impose.
                Encrypt = SqlConnectionEncryptOption.Optional,
                TrustServerCertificate = true
            };
            if (!AuthentificationWindows)
            {
                b.UserID = Login ?? "";
                b.Password = MotDePasse ?? "";
            }
            return b.ConnectionString;
        }

        public ParametresConnexion Cloner()
        {
            return (ParametresConnexion)MemberwiseClone();
        }

        public string Description
        {
            get
            {
                return (Serveur ?? "") + " — " + (AuthentificationWindows ? "Windows" : "SQL (" + Login + ")");
            }
        }
    }

    public sealed class InfoServeur
    {
        public string Nom { get; set; }
        public string Version { get; set; }
        public string Edition { get; set; }
        public string LoginConnecte { get; set; }
        public bool EstSysAdmin { get; set; }
        public int NombreBases { get; set; }

        public string VersionCommerciale
        {
            get
            {
                if (string.IsNullOrEmpty(Version)) return "";
                var majeure = Version.Split('.')[0];
                switch (majeure)
                {
                    case "11": return "SQL Server 2012";
                    case "12": return "SQL Server 2014";
                    case "13": return "SQL Server 2016";
                    case "14": return "SQL Server 2017";
                    case "15": return "SQL Server 2019";
                    case "16": return "SQL Server 2022";
                    case "17": return "SQL Server 2025";
                    default: return "SQL Server " + majeure;
                }
            }
        }
    }

    public sealed class InfoBase
    {
        public string Nom { get; set; }
        public string Etat { get; set; }
        public bool Accessible { get; set; }
        public int NiveauCompatibilite { get; set; }
        public bool EstComptabilite { get; set; }
        public bool EstGestionCommerciale { get; set; }
        public bool ProcedureInstallee { get; set; }
        public string VersionProcedure { get; set; }
        public string RaisonSociale { get; set; }
        public long NombreEcritures { get; set; }
        public DateTime? DerniereEcriture { get; set; }
        public DateTime? DebutExercice { get; set; }
        public DateTime? FinExercice { get; set; }
        public string Erreur { get; set; }

        public bool EstBaseReconnue { get { return EstComptabilite || EstGestionCommerciale; } }

        public string TypeLibelle
        {
            get
            {
                if (!Accessible) return "Inaccessible";
                if (EstComptabilite && EstGestionCommerciale) return "Compta + Gestion commerciale";
                if (EstComptabilite) return "Comptabilité";
                if (EstGestionCommerciale) return "Gestion commerciale";
                return "Autre base";
            }
        }

        public override string ToString() { return Nom; }
    }

    public sealed class InfoTable
    {
        public string Schema { get; set; }
        public string Nom { get; set; }
        public long NombreLignes { get; set; }
        public int ColonnesRequisesPresentes { get; set; }
        public int ColonnesRequisesTotal { get; set; }

        public bool EstCompatible { get { return ColonnesRequisesTotal > 0 && ColonnesRequisesPresentes == ColonnesRequisesTotal; } }
        public string NomComplet { get { return Schema + "." + Nom; } }
        public bool EstTableEcrituresComptables
        {
            get { return string.Equals(Schema, "dbo", StringComparison.OrdinalIgnoreCase) && string.Equals(Nom, "F_ECRITUREC", StringComparison.OrdinalIgnoreCase); }
        }

        public override string ToString() { return NomComplet; }
    }

    public sealed class ColonneStructure
    {
        public string Nom { get; set; }
        public string Type { get; set; }
        public bool Presente { get; set; }
        public bool Calculee { get; set; }
        public bool Nullable { get; set; }
        public string Role { get; set; }

        /// <summary>Clé binaire cb* : calculée par SQL Server dans les versions récentes, donc toujours acceptée.</summary>
        public bool EstCle { get { return Nom != null && Nom.StartsWith("cb", StringComparison.Ordinal); } }
        public bool Conforme { get { return EstCle || (Presente && !Calculee); } }
    }

    public enum StatutControle { Ok, Info, Avertissement, Erreur }

    public sealed class Controle
    {
        public string Titre { get; set; }
        public string Detail { get; set; }
        public StatutControle Statut { get; set; }
        /// <summary>Un contrôle bloquant en erreur empêche l'import.</summary>
        public bool Bloquant { get; set; }

        public static Controle Creer(string titre, StatutControle statut, string detail, bool bloquant = false)
        {
            return new Controle { Titre = titre, Statut = statut, Detail = detail, Bloquant = bloquant };
        }
    }
}
