using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SigmaCompta.Core.Modeles
{
    [DataContract(Name = "Execution", Namespace = "")]
    public sealed class ExecutionImport
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new ExecutionImport(), this); }

        [DataMember(Order = 0)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [DataMember(Order = 1)] public DateTime Date { get; set; } = DateTime.Now;
        [DataMember(Order = 2)] public string Serveur { get; set; }
        [DataMember(Order = 3)] public string Base { get; set; }
        [DataMember(Order = 4)] public string Fichier { get; set; }
        [DataMember(Order = 5)] public string Profil { get; set; }
        [DataMember(Order = 6)] public string Mode { get; set; }
        [DataMember(Order = 7)] public bool Simulation { get; set; }
        [DataMember(Order = 8)] public int NombrePieces { get; set; }
        [DataMember(Order = 9)] public int NombreReussies { get; set; }
        [DataMember(Order = 10)] public int NombreEchecs { get; set; }
        [DataMember(Order = 11)] public decimal TotalDebit { get; set; }
        [DataMember(Order = 12)] public string Utilisateur { get; set; }
        [DataMember(Order = 13)] public string Bilan { get; set; }
        [DataMember(Order = 14)] public List<ResultatPieceHistorique> Pieces { get; set; } = new List<ResultatPieceHistorique>();

        public string Statut
        {
            get
            {
                if (Simulation) return "Simulation";
                if (NombreEchecs == 0) return "Réussi";
                return NombreReussies == 0 ? "Échec" : "Partiel";
            }
        }
    }

    [DataContract(Name = "Piece", Namespace = "")]
    public sealed class ResultatPieceHistorique
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new ResultatPieceHistorique(), this); }

        [DataMember(Order = 0)] public string Journal { get; set; }
        [DataMember(Order = 1)] public DateTime? Date { get; set; }
        [DataMember(Order = 2)] public string Piece { get; set; }
        [DataMember(Order = 3)] public string Statut { get; set; }
        [DataMember(Order = 4)] public string EcNo { get; set; }
        [DataMember(Order = 5)] public decimal Montant { get; set; }
        [DataMember(Order = 6)] public string Message { get; set; }
    }

    [DataContract(Name = "Parametres", Namespace = "")]
    public sealed class ParametresApplication
    {
        [OnDeserializing] private void AvantLecture(StreamingContext c) { ValeursInitiales.Copier(new ParametresApplication(), this); }

        [DataMember(Order = 0)] public string DernierServeur { get; set; } = "";
        [DataMember(Order = 1)] public bool AuthentificationWindows { get; set; } = true;
        [DataMember(Order = 2)] public string Login { get; set; } = "";
        /// <summary>Mot de passe chiffré par DPAPI (compte Windows courant), en Base64.</summary>
        [DataMember(Order = 3)] public string MotDePasseProtege { get; set; } = "";
        [DataMember(Order = 4)] public bool MemoriserMotDePasse { get; set; }
        [DataMember(Order = 5)] public List<string> ServeursRecents { get; set; } = new List<string>();
        [DataMember(Order = 6)] public string DerniereBase { get; set; } = "";
        [DataMember(Order = 7)] public string DernierProfil { get; set; } = "";
        [DataMember(Order = 8)] public string DernierDossierDonnees { get; set; } = "";
    }
}
