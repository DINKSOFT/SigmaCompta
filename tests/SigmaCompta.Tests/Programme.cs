using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SigmaCompta.Core.Comptabilite;
using SigmaCompta.Core.Fichiers;
using SigmaCompta.Core.Import;
using SigmaCompta.Core.Modeles;
using SigmaCompta.Core.Sql;
using SigmaCompta.Core.Stockage;

namespace SigmaCompta.Tests
{
    /// <summary>Tests du cœur métier (sans SQL Server) : lancer le projet, code de sortie 0 = tout est vert.</summary>
    static class Programme
    {
        static int ok = 0, ko = 0;
        static void Verif(bool cond, string nom)
        {
            if (cond) { ok++; Console.WriteLine("  OK  " + nom); }
            else { ko++; Console.WriteLine("  KO  " + nom); }
        }

        static int Main()
        {
            Encodages.Initialiser();
            var dir = Path.Combine(Path.GetTempPath(), "sc_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Chemins.Racine = Path.Combine(dir, "appdata");

            Console.WriteLine("== Analyseurs");
            decimal m;
            Verif(Analyseurs.EssayerMontant("1 500 000", "auto", out m) && m == 1500000m, "montant espaces");
            Verif(Analyseurs.EssayerMontant("1 500 000,50", "auto", out m) && m == 1500000.50m, "montant nbsp + virgule");
            Verif(Analyseurs.EssayerMontant("1.500.000,25", "auto", out m) && m == 1500000.25m, "montant points milliers");
            Verif(Analyseurs.EssayerMontant("1,500,000.75", "auto", out m) && m == 1500000.75m, "montant anglais");
            Verif(Analyseurs.EssayerMontant("(250)", "auto", out m) && m == -250m, "montant parenthèses");
            Verif(Analyseurs.EssayerMontant("12,5", "auto", out m) && m == 12.5m, "montant virgule décimale");
            Verif(Analyseurs.EssayerMontant("", "auto", out m) && m == 0m, "montant vide");
            Verif(!Analyseurs.EssayerMontant("abc", "auto", out m), "montant invalide");
            Verif(Analyseurs.EssayerMontant("25 000 FCFA", "auto", out m) && m == 25000m, "montant FCFA");
            DateTime d;
            Verif(Analyseurs.EssayerDate("31/10/2026", SourceProfil.FormatsDateParDefaut, out d) && d == new DateTime(2026, 10, 31), "date jj/mm/aaaa");
            Verif(Analyseurs.EssayerDate("2026-10-31", SourceProfil.FormatsDateParDefaut, out d) && d == new DateTime(2026, 10, 31), "date ISO");
            Verif(Analyseurs.EssayerDate("46326", SourceProfil.FormatsDateParDefaut, out d) && d == DateTime.FromOADate(46326), "date série Excel");
            Verif(Analyseurs.EssayerDate("311026", SourceProfil.FormatsDateParDefaut, out d) && d == new DateTime(2026, 10, 31), "date jjmmaa");
            Verif(!Analyseurs.EssayerDate("32/13/2026", SourceProfil.FormatsDateParDefaut, out d), "date invalide");
            Verif(Analyseurs.NormaliserEntete("N° Pièce") == "nopiece", "normalisation en-tête");
            Verif(Analyseurs.CompleterCompte("422", 7) == "4220000" && Analyseurs.CompleterCompte("C100003", 7) == "C100003", "complétion comptes");
            Verif(Colonnes.Lettre(0) == "A" && Colonnes.Lettre(27) == "AB" && Colonnes.Index("AB") == 27, "lettres colonnes");

            Console.WriteLine("== Xlsx aller-retour");
            var xlsx = Path.Combine(dir, "modele.xlsx");
            ExportRapport.GenererModele(xlsx, ProfilImport.BrouillardStandard());
            var t = LecteurSource.Lire(xlsx, new SourceProfil());
            Verif(t.Entetes.SequenceEqual(new[] { "JO_Num", "Date", "EC_Piece", "EC_Reference", "CG_Num", "CT_Num", "EC_Intitule", "Débit", "Crédit" }), "en-têtes modèle : " + string.Join(",", t.Entetes));
            Verif(t.Lignes.Count == 2 && t.NumerosLigne[0] == 2, "2 lignes d'exemple");
            Verif(t.Lignes[0][1] == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).ToString("yyyy-MM-dd"), "date relue en ISO : " + t.Lignes[0][1]);
            Verif(t.Lignes[0][7] == "1500000" && t.Lignes[1][8] == "1500000", "montants relus");
            Verif(t.Lignes[0][4] == "6611000", "compte texte relu");

            Console.WriteLine("== CSV ANSI ; guillemets");
            var csv = Path.Combine(dir, "brouillard.csv");
            var contenu = "JO_Num;Date;EC_Piece;CG_Num;CT_Num;EC_Intitule;Débit;Crédit\r\n" +
                          "od;05/10/2026;OD0012;6611000;;\"Salaires; octobre\";1 500 000;\r\n" +
                          "OD;05/10/2026;OD0012;4220000;;Salaires octobre;;1 500 000\r\n" +
                          ";;;;;;;\r\n" +
                          "BQ;06/10/2026;;5210000;;Encaissement \"client\" X;250000;\r\n" +
                          "BQ;06/10/2026;;4110000;c100003;Encaissement client X;;250000\r\n" +
                          "BQ;07/10/2026;;5210000;;Frais;;-1500\r\n" +
                          "BQ;07/10/2026;;6310000;;Frais;1500;\r\n" +
                          "OD;08/10/2026;OD0013;6611000;;Déséquilibre;1000;\r\n" +
                          "OD;08/10/2026;OD0013;9999999;;Déséquilibre;;900\r\n";
            File.WriteAllBytes(csv, Encoding.GetEncoding(1252).GetBytes(contenu));
            var tc = LecteurSource.Lire(csv, new SourceProfil());
            Verif(tc.Entetes.Count == 8 && tc.Entetes[6] == "Débit", "en-têtes CSV ANSI (accents) : " + tc.Entetes[6]);
            Verif(tc.Lignes.Count == 8, "lignes vides ignorées : " + tc.Lignes.Count);
            Verif(tc.Lignes[0][5] == "Salaires; octobre", "champ entre guillemets avec ;");
            Verif(tc.NumerosLigne[2] == 5, "n° de ligne réel conservé : " + tc.NumerosLigne[2]);

            Console.WriteLine("== Construction des pièces");
            var profil = ProfilImport.BrouillardStandard();
            var an = ConstructeurPieces.Construire(tc, profil);
            Verif(!an.Bloquee, "colonnes résolues");
            foreach (var cc in an.Correspondances) Console.WriteLine("     " + cc.Champ + " -> " + cc.Affichage);
            Verif(an.Pieces.Count == 4, "4 pièces (OD0012, BQ 06, BQ 07, OD0013) : " + an.Pieces.Count);
            var bq07 = an.Pieces.First(p => p.Date == new DateTime(2026, 10, 7));
            Verif(bq07.Lignes[0].Debit == 1500 && bq07.Lignes[0].Credit == 0, "montant négatif inversé en débit");
            Verif(an.Pieces[1].NumerotationAuto && an.Pieces[0].PieceFournie == "OD0012", "pièce fournie / auto");

            Console.WriteLine("== Validation");
            var r = new Referentiel();
            r.Journaux["OD"] = new Journal { Code = "OD", Intitule = "Opérations diverses", Type = 3, ModeNumerotation = 1 };
            r.Journaux["BQ"] = new Journal { Code = "BQ", Intitule = "Banque", Type = 2, ModeNumerotation = 3, CompteTresorerie = "5210000" };
            foreach (var c in new[] { "6611000", "4220000", "5210000", "4110000", "6310000" }) r.Comptes[c] = new CompteGeneral { Numero = c, Intitule = "Compte " + c, Type = 0 };
            r.Comptes["6"] = new CompteGeneral { Numero = "6", Intitule = "Total classe 6", Type = 1 };
            r.Tiers["C100003"] = new Tiers { Numero = "C100003", Intitule = "Client X" };
            r.Exercices.Add(new Exercice { Debut = new DateTime(2026, 1, 1), Fin = new DateTime(2026, 12, 31) });
            Validateur.Valider(an.Pieces, r, profil);
            var od12 = an.Pieces[0];
            Verif(od12.Journal == "OD" && od12.Lignes.All(l => l.Journal == "OD"), "journal canonique (od → OD)");
            Verif(od12.Statut == StatutPiece.Valide, "OD0012 valide : " + od12.Statut + " " + od12.Resume);
            var bq06 = an.Pieces[1];
            Verif(bq06.Lignes[1].CompteTiers == "C100003", "tiers canonique (c100003 → C100003)");
            Verif(bq06.Statut == StatutPiece.Valide, "BQ 06 valide : " + bq06.Resume);
            var od13 = an.Pieces[3];
            Verif(od13.Statut == StatutPiece.Erreur && od13.NombreErreurs == 2, "OD0013 : déséquilibre + compte inconnu : " + string.Join(" | ", od13.ToutesAnomalies));
            Validateur.Valider(an.Pieces, r, profil);
            Verif(od13.NombreErreurs == 2, "revalidation sans doublon d'anomalies");
            r.PeriodesCloturees.Add(Referentiel.ClePeriode("OD", new DateTime(2026, 10, 1)));
            Validateur.Valider(an.Pieces, r, profil);
            Verif(od12.Statut == StatutPiece.Erreur && od12.Resume.Contains("clôturée"), "période clôturée détectée");

            Console.WriteLine("== Doublons de n° de pièce selon la numérotation du journal");
            var csvD = Path.Combine(dir, "doublons.csv");
            var contenuD = "JO_Num;Date;EC_Piece;CG_Num;CT_Num;EC_Intitule;Débit;Crédit\r\n" +
                           "AC;05/01/2026;1;6051000;;Eau janvier;100;\r\n" +
                           "AC;05/01/2026;1;40100000;F100005;Eau janvier;;100\r\n" +
                           "AC;03/02/2026;1;6051000;;Eau février;200;\r\n" +
                           "AC;03/02/2026;1;40100000;F100005;Eau février;;200\r\n" +
                           "AC;20/02/2026;1;6051000;;Eau février bis;300;\r\n" +
                           "AC;20/02/2026;1;40100000;F100005;Eau février bis;;300\r\n";
            File.WriteAllBytes(csvD, Encoding.GetEncoding(1252).GetBytes(contenuD));
            var anD = ConstructeurPieces.Construire(LecteurSource.Lire(csvD, new SourceProfil()), ProfilImport.BrouillardStandard());
            Verif(anD.Pieces.Count == 3, "3 pièces n° 1 (05/01, 03/02, 20/02) : " + anD.Pieces.Count);
            var rD = new Referentiel();
            rD.Journaux["AC"] = new Journal { Code = "AC", Intitule = "Achats", Type = 0, ModeNumerotation = 3 };
            foreach (var cpt in new[] { "6051000", "40100000" }) rD.Comptes[cpt] = new CompteGeneral { Numero = cpt, Intitule = "Compte " + cpt, Type = 0 };
            rD.Tiers["F100005"] = new Tiers { Numero = "F100005", Intitule = "Fournisseur eau" };
            rD.Exercices.Add(new Exercice { Debut = new DateTime(2026, 1, 1), Fin = new DateTime(2026, 12, 31) });
            Func<PieceImport, bool> doublonSignale = pc => pc.Anomalies.Any(a => a.Message.Contains("apparaît dans"));
            Validateur.Valider(anD.Pieces, rD, profil);
            var pJanvier = anD.Pieces.First(pc => pc.Date == new DateTime(2026, 1, 5));
            Verif(!doublonSignale(pJanvier) && pJanvier.Statut == StatutPiece.Valide, "mensuelle : n° 1 de janvier non signalé : " + pJanvier.Resume);
            Verif(anD.Pieces.Where(pc => pc.Date.Value.Month == 2).All(pc => pc.Anomalies.Any(a => a.Message.Contains("apparaît dans 2") && a.Message.Contains("02/2026"))),
                  "mensuelle : les deux n° 1 de février signalés");
            rD.Journaux["AC"].ModeNumerotation = 1;
            Validateur.Valider(anD.Pieces, rD, profil);
            Verif(anD.Pieces.All(pc => pc.Anomalies.Any(a => a.Message.Contains("apparaît dans 3"))), "continue par journal : les trois n° 1 signalés");
            rD.Journaux["AC"].ModeNumerotation = 3;
            Validateur.Valider(anD.Pieces, rD, profil);
            Verif(!doublonSignale(pJanvier), "revalidation en mensuelle : l'avertissement de janvier disparaît");

            Console.WriteLine("== Regroupement par équilibre");
            var p2 = ProfilImport.BrouillardStandard();
            p2.Regroupement = ModesRegroupement.Equilibre;
            var an2 = ConstructeurPieces.Construire(tc, p2);
            Verif(an2.Pieces.Count == 4 && an2.Pieces.All(p => p.Lignes.Count == 2), "équilibre : 4 pièces de 2 lignes : " + an2.Pieces.Count);

            Console.WriteLine("== Montant + Sens, colonne par lettre");
            var csv2 = Path.Combine(dir, "sens.csv");
            File.WriteAllText(csv2, "Jnl,Dt,Cpte,Lib,Mt,S\nOD,2026-10-05,6611000,A,100.5,D\nOD,2026-10-05,4220000,A,100.5,C\n", new UTF8Encoding(false));
            var p3 = ProfilImport.BrouillardStandard();
            p3.Colonnes.Journal = "[A]"; p3.Colonnes.Date = "Dt"; p3.Colonnes.CompteGeneral = "[C]"; p3.Colonnes.Montant = "Mt"; p3.Colonnes.Sens = "S";
            var an3 = ConstructeurPieces.Construire(LecteurSource.Lire(csv2, p3.Source), p3);
            Verif(!an3.Bloquee && an3.Pieces.Count == 1 && an3.Pieces[0].TotalDebit == 100.5m && an3.Pieces[0].Ecart == 0, "montant + sens, séparateur virgule auto");

            Console.WriteLine("== Colonne obligatoire manquante");
            var p4 = ProfilImport.BrouillardStandard();
            p4.Colonnes.CompteGeneral = "Inexistante";
            var an4 = ConstructeurPieces.Construire(tc, p4);
            Verif(an4.Bloquee && an4.AnomaliesGlobales[0].Message.Contains("Compte général"), "erreur globale colonne manquante");

            Console.WriteLine("== TVP");
            var dt = ServiceImport.ConstruireLignes(bq06);
            Verif(dt.Columns.Count == 9 && dt.Columns[0].ColumnName == "NumLigne" && dt.Columns[7].ColumnName == "EC_RefPiece"
                  && dt.Columns[8].ColumnName == "EC_Reference", "colonnes TVP dans l'ordre du type");
            Verif(dt.Rows[0]["CT_Num"] == DBNull.Value && (string)dt.Rows[1]["CT_Num"] == "C100003", "tiers NULL / renseigné");

            Console.WriteLine("== Profils JSON");
            var cheminProfil = Path.Combine(dir, "profil.json");
            var p5 = ProfilImport.BrouillardStandard();
            p5.Defauts.Journal = "OD"; p5.Options.ModeTransaction = ModesTransaction.ToutOuRien; p5.Defauts.LongueurComptes = 7;
            MagasinProfils.Enregistrer(cheminProfil, p5);
            var json = File.ReadAllText(cheminProfil);
            Console.WriteLine(json.Substring(0, Math.Min(400, json.Length)).Replace("\n", "\n     "));
            var p6 = MagasinProfils.Charger(cheminProfil);
            Verif(p6.Defauts.Journal == "OD" && p6.Options.ModeTransaction == ModesTransaction.ToutOuRien && p6.Defauts.LongueurComptes == 7, "aller-retour profil");
            Verif(json.Contains("\"Débit|Debit|Montant débit\"") || json.Contains("D\\u00e9bit"), "accents conservés");
            File.WriteAllText(cheminProfil, "{ \"Nom\": \"Partiel\", \"Options\": { \"AutoriserDoublon\": true } }");
            var p7 = MagasinProfils.Charger(cheminProfil);
            Verif(p7.Nom == "Partiel" && p7.Options.AutoriserDoublon && p7.Options.InverserMontantsNegatifs && p7.Colonnes.CompteGeneral.StartsWith("CG_Num") && p7.Source.LigneEntete == 1,
                  "JSON partiel : valeurs par défaut conservées");
            Verif(p7.Options.RenumeroterDoublon && new OptionsProfil().RenumeroterDoublon, "renumérotation des doublons activée par défaut");
            var pr = new PieceImport { PieceFournie = " 12 " };
            pr.NumeroAttribue = "12"; bool r1 = pr.Renumerotee;
            pr.NumeroAttribue = "51"; bool r2 = pr.Renumerotee;
            var pa2 = new PieceImport { PieceFournie = "", NumeroAttribue = "51" };
            Verif(!r1 && r2 && !pa2.Renumerotee, "pièce renumérotée détectée (n° fourni ≠ n° attribué)");
            var liste = MagasinProfils.Lister();
            Verif(liste.Count == 1 && liste[0].Value.Nom == "Brouillard standard", "profil par défaut créé au premier lancement");

            Console.WriteLine("== Paramètres + historique");
            var pa = new ParametresApplication { DernierServeur = "SRV\\COMPTA", ServeursRecents = new List<string> { "SRV\\COMPTA" } };
            MagasinParametres.Enregistrer(pa);
            Verif(MagasinParametres.Charger().DernierServeur == "SRV\\COMPTA", "paramètres relus");
            var bilan = ServiceImport.Bilan(an.Pieces, true, "ParPiece");
            MagasinHistorique.Ajouter(bilan);
            var h = MagasinHistorique.Charger();
            Verif(h.Count == 1 && h[0].Pieces.Count == 4 && h[0].Simulation, "historique relu");

            Console.WriteLine("== Scripts embarqués");
            var s01 = ScriptsEmbarques.Lire(ScriptsEmbarques.PreVerification);
            var lots01 = ExecuteurScript.DecouperLots(s01);
            Verif(lots01.Count == 11, "script 01 : 11 lots (SET NOCOUNT + 10 contrôles) : " + lots01.Count);
            foreach (var l in lots01.Select((x, i) => ExecuteurScript.TitreLot(x, i + 1))) Console.WriteLine("     · " + l);
            var lots02 = ExecuteurScript.DecouperLots(ScriptsEmbarques.Lire(ScriptsEmbarques.Procedure));
            Verif(lots02.Count == 6 && lots02[5].StartsWith("CREATE PROCEDURE"), "script 02 : 6 lots, le dernier crée la procédure : " + lots02.Count);
            Verif(!lots02.Any(x => x.Contains("GRANT EXECUTE ON TYPE") && !x.Contains("CREATE PROCEDURE")), "commentaire final (GRANT) non exécuté seul");
            Verif(ScriptsEmbarques.VersionProcedureEmbarquee == "1.4", "version embarquée 1.4");
            var scriptProc = ScriptsEmbarques.Lire(ScriptsEmbarques.Procedure);
            Verif(scriptProc.Contains("EC_Reference varchar(17)    NULL") && scriptProc.Contains("--#SI:F_ECRITUREC.EC_Reference")
                  && !InstallateurProcedure.AdapterScript(scriptProc, new HashSet<string>()).Script.Contains("l.EC_Reference                                --"),
                  "procédure 1.4 : référence de ligne (EC_Reference), retirée si la base n'a pas la colonne");
            var p02 = ScriptsEmbarques.Lire(ScriptsEmbarques.Procedure);
            Verif(p02.Contains("@RenumeroterDoublon bit         = 1") && p02.IndexOf("IF @Piece LIKE '%[0-9]' SET @Modele = @Piece;", StringComparison.Ordinal) > 0,
                  "procédure : renumérotation d'un n° déjà utilisé (par défaut)");
            Verif(ExecuteurScript.DecouperLots("SELECT 1\nGO\n/* GO */\nSELECT 2\ngo  \nSELECT 3").Count == 3, "découpage GO (casse, commentaire)");


            Console.WriteLine("== Adaptation de la procédure (clés cb* calculées)");
            var proc = ScriptsEmbarques.Lire(ScriptsEmbarques.Procedure);
            var toutesCles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "F_ECRITUREC.cbJO_Num", "F_ECRITUREC.cbEC_Piece", "F_ECRITUREC.cbEC_RefPiece", "F_ECRITUREC.cbCG_Num", "F_ECRITUREC.cbCT_Num", "F_JMOUV.cbJO_Num", "F_ECRITUREC.cbHash", "F_ECRITUREC.EC_Reference" };
            var ancienne = InstallateurProcedure.AdapterScript(proc, toutesCles);
            Verif(ancienne.ColonnesRetirees.Count == 0 && ancienne.Script.Contains("cbCT_Num") && ancienne.Script.Contains("e.cbHash IS NOT NULL"), "colonnes cb* à remplir : tout est conservé");
            var existantes = new HashSet<string>(toutesCles, StringComparer.OrdinalIgnoreCase);
            var recente = InstallateurProcedure.AdapterScript(proc, new HashSet<string>(), existantes);
            Verif(recente.ColonnesRetirees.Count == 6 && !recente.Script.Contains("cbEC_Piece") && !recente.Script.Contains("CB_FntStr(l.CG_Num)") && recente.Script.Contains("e.cbHash IS NOT NULL"),
                  "clés calculées (version récente) : 6 lignes retirées, diagnostic cbHash gardé");
            var sansHash = InstallateurProcedure.AdapterScript(proc, new HashSet<string>(), new HashSet<string>());
            Verif(!sansHash.Script.Contains("e.cbHash IS NOT NULL") && sansHash.Script.Contains("@NbHash AS NbHash"), "sans colonne cbHash : diagnostic retiré, résultat NULL");
            Verif(ExecuteurScript.DecouperLots(recente.Script).Count == 6, "script adapté : 6 lots");

            Console.WriteLine("== Contrôle version / compatibilité");
            var cv = InspecteurSchema.ControleVersion("11.0.2100.60", 100);
            Verif(cv.Statut == StatutControle.Info && !cv.Bloquant, "serveur 2012, base niveau 100 : accepté (info) : " + cv.Statut);
            cv = InspecteurSchema.ControleVersion("15.0.2000.5", 150);
            Verif(cv.Statut == StatutControle.Ok && !cv.Bloquant, "serveur 2019, base niveau 150 : ok");
            cv = InspecteurSchema.ControleVersion("10.50.6000.34", 100);
            Verif(cv.Statut == StatutControle.Erreur && cv.Bloquant, "serveur 2008 R2 : bloquant");
            cv = InspecteurSchema.ControleVersion("", 100);
            Verif(cv.Statut == StatutControle.Erreur && cv.Bloquant, "version illisible et niveau 100 : bloquant par prudence");
            cv = InspecteurSchema.ControleVersion(null, 110);
            Verif(cv.Statut == StatutControle.Ok, "version illisible et niveau 110 : ok");
            Verif(InspecteurSchema.VersionMajeure(" 16.0.1000.6") == 16 && InspecteurSchema.VersionMajeure("abc") == 0, "version majeure");

            Console.WriteLine("== Consultation de compte");
            var exos = new List<Exercice>
            {
                new Exercice { Debut = new DateTime(2025, 1, 1), Fin = new DateTime(2025, 12, 31) },
                new Exercice { Debut = new DateTime(2026, 1, 1), Fin = new DateTime(2026, 12, 31) }
            };
            Verif(ServiceConsultation.DebutReport(new DateTime(2026, 3, 1), exos) == new DateTime(2026, 1, 1), "report depuis le début de l'exercice");
            Verif(ServiceConsultation.DebutReport(new DateTime(2026, 1, 1), exos) == null, "pas de report au premier jour de l'exercice");
            Verif(ServiceConsultation.DebutReport(new DateTime(2027, 2, 1), exos) == null, "pas de report hors exercice");
            var rc = new ResultatConsultation { Compte = "40100000", Du = new DateTime(2026, 3, 1), Au = new DateTime(2026, 3, 31),
                                                DebutReport = new DateTime(2026, 1, 1), ReportDebit = 1500, ReportCredit = 500 };
            rc.Lignes.Add(new LigneConsultation { Date = new DateTime(2026, 3, 2), Debit = 500 });
            rc.Lignes.Add(new LigneConsultation { Date = new DateTime(2026, 3, 9), Credit = 2000, EstLettree = true, Lettrage = "AA" });
            ServiceConsultation.Calculer(rc);
            Verif(rc.Lignes[0].Solde == 1500 && rc.Lignes[1].Solde == -500 && rc.SoldeFinal == -500, "solde progressif depuis le report");
            Verif(rc.MouvementsDebit == 500 && rc.MouvementsCredit == 2000 && rc.NombreNonLettrees == 1, "totaux et lignes non lettrées");
            Verif(rc.LignesAvecReport.Count() == 3 && rc.LignesAvecReport.First().EstReport && rc.LignesAvecReport.First().Solde == 1000, "ligne de report en tête");
            Verif(Montants.AvecSens(-500m).EndsWith(" C") && Montants.AvecSens(1000m).EndsWith(" D") && Montants.AvecSens(0m) == "0", "solde avec sens D / C");
            var sqlC = ServiceConsultation.RequeteLignes(NatureCompte.Tiers, FiltreLettrage.NonLettrees, true, true);
            Verif(sqlC.Contains("e.CT_Num = @compte") && sqlC.Contains("NOT (ISNULL(e.EC_Lettre, 0) <> 0)"), "requête : tiers non lettrés");
            Verif(ServiceConsultation.ConditionLettrage(FiltreLettrage.Lettrees, false, false) == "1 = 0"
                  && ServiceConsultation.ConditionLettrage(FiltreLettrage.NonLettrees, false, false) == null, "sans colonne de lettrage");
            Verif(ServiceConsultation.RequeteLignes(NatureCompte.General, FiltreLettrage.Toutes, false, false).Contains("CAST(NULL AS smallint) AS EC_Lettre"),
                  "colonnes de lettrage absentes remplacées par NULL");
            var refC = new Referentiel();
            refC.Comptes["6051000"] = new CompteGeneral { Numero = "6051000", Intitule = "Eau", Type = 0 };
            refC.Comptes["605"] = new CompteGeneral { Numero = "605", Intitule = "Total 605", Type = 1 };
            refC.Tiers["F100005"] = new Tiers { Numero = "F100005", Intitule = "ONEA" };
            var lgC = ServiceConsultation.ComptesConsultables(refC, NatureCompte.General);
            Verif(lgC.Count == 1 && lgC[0].Numero == "6051000", "comptes de total exclus de la liste");
            Verif(ServiceConsultation.ComptesConsultables(refC, NatureCompte.Tiers)[0].Correspond("onea") && lgC[0].Correspond("6051")
                  && !lgC[0].Correspond("605100x"), "recherche par n° ou intitulé");
            Verif(ServiceConsultation.Intitule(refC, NatureCompte.Tiers, " F100005 ") == "ONEA", "intitulé du tiers");
            var xc = Path.Combine(dir, "consultation.xlsx");
            ExportConsultation.Ecrire(xc, rc);
            var txc = LecteurSource.Lire(xc, new SourceProfil());
            Verif(txc.Lignes.Count == 5 && txc.Entetes[9] == "Solde", "export Excel : report + 2 lignes + 2 totaux : " + txc.Lignes.Count);

            Console.WriteLine("== États comptables");
            var refE = new Referentiel();
            foreach (var cg in new[] { "1010000|Capital social", "4011000|Fournisseurs", "5211000|Banque", "6051000|Eau", "7011000|Ventes" })
                refE.Comptes[cg.Split('|')[0]] = new CompteGeneral { Numero = cg.Split('|')[0], Intitule = cg.Split('|')[1], Type = 0 };
            refE.Tiers["F100005"] = new Tiers { Numero = "F100005", Intitule = "ONEA", Type = TypesTiers.Fournisseur };
            refE.Tiers["C100001"] = new Tiers { Numero = "C100001", Intitule = "Client A", Type = TypesTiers.Client };
            refE.Journaux["ACH"] = new Journal { Code = "ACH", Intitule = "Achats" };
            refE.Journaux["RAN"] = new Journal { Code = "RAN", Intitule = "Report à nouveau" };
            refE.Exercices.Add(new Exercice { Debut = new DateTime(2026, 1, 1), Fin = new DateTime(2026, 12, 31) });
            var an2026 = new DateTime(2026, 1, 1);
            var fin2026 = new DateTime(2026, 12, 31);

            var pl = new PlageComptes("6", "6");
            Verif(pl.Contient("6051000") && !pl.Contient("7011000") && !pl.Contient("5211000"), "plage : de 6 à 6 = toute la classe 6");
            var pl2 = new PlageComptes("401", "409");
            Verif(pl2.Contient("4011000") && pl2.Contient("4099999") && !pl2.Contient("4100000") && !pl2.Contient("4000000")
                  && new PlageComptes(" ", null).Contient("X1"), "plage : bornes sur le début du numéro");

            var pB = new ParametresEtat { Etat = TypeEtat.Balance, Nature = NatureCompte.General, Du = an2026, Au = fin2026, JournalANouveaux = "RAN" };
            var cumulsB = new List<CumulCompte>
            {
                new CumulCompte { Compte = "1010000", OuvertureCredit = 1000000 },
                new CumulCompte { Compte = "5211000", OuvertureDebit = 1000000, MouvementDebit = 500000, MouvementCredit = 200000 },
                new CumulCompte { Compte = "6051000", MouvementDebit = 200000 },
                new CumulCompte { Compte = "7011000", MouvementCredit = 500000 }
            };
            var rb = ServiceEtats.ConstruireBalance(pB, null, cumulsB, refE);
            var tgB = rb.Lignes.Last();
            Verif(rb.Lignes.Count == 9 && rb.Lignes.Count(l => l.Genre == GenreLigne.SousTotal) == 4 && tgB.Genre == GenreLigne.TotalGeneral
                  && rb.NombreComptes == 4, "balance : 4 comptes, 4 totaux de classe, total général");
            var banque = rb.Lignes.First(l => l.Compte == "5211000");
            Verif(banque.OuvertureDebit == 1000000 && banque.OuvertureCredit == null && banque.MouvementDebit == 500000 && banque.MouvementCredit == 200000
                  && banque.SoldeDebit == 1300000 && banque.SoldeCredit == null && banque.Libelle == "Banque", "balance : les six colonnes d'un compte");
            Verif(tgB.OuvertureDebit == 1000000 && tgB.OuvertureCredit == 1000000 && tgB.MouvementDebit == 700000 && tgB.MouvementCredit == 700000
                  && tgB.SoldeDebit == 1500000 && tgB.SoldeCredit == 1500000 && rb.Avertissements.Count == 0, "balance : total général équilibré, sans avertissement");
            Verif(rb.Lignes[1].Genre == GenreLigne.SousTotal && rb.Lignes[1].Libelle.StartsWith("Total classe 1") && rb.Lignes[1].SoldeCredit == 1000000,
                  "balance : total de classe sous ses comptes");
            var pB2 = new ParametresEtat { Etat = TypeEtat.Balance, Nature = NatureCompte.General, Du = an2026, Au = fin2026, CompteDe = "5", CompteA = "6" };
            Verif(ServiceEtats.ConstruireBalance(pB2, null, cumulsB, refE).NombreComptes == 2, "balance : plage de comptes");
            cumulsB.Add(new CumulCompte { Compte = "6058000", MouvementDebit = 100, MouvementCredit = 100 });
            pB.InclureComptesSoldes = false;
            int sansSoldes = ServiceEtats.ConstruireBalance(pB, null, cumulsB, refE).NombreComptes;
            pB.InclureComptesSoldes = true;
            Verif(sansSoldes == 4 && ServiceEtats.ConstruireBalance(pB, null, cumulsB, refE).NombreComptes == 5, "balance : comptes soldés inclus ou non");
            var rbKo = ServiceEtats.ConstruireBalance(new ParametresEtat { Etat = TypeEtat.Balance, Du = an2026, Au = fin2026 }, null,
                                                      new[] { new CumulCompte { Compte = "6051000", MouvementDebit = 100 } }, refE);
            Verif(rbKo.Avertissements.Any(a => a.StartsWith("Mouvements de la période déséquilibrés")) && rbKo.Avertissements.Any(a => a.StartsWith("Aucun journal d'à-nouveaux"))
                  && rbKo.Ecart == 100, "balance : déséquilibre et journal d'à-nouveaux absent signalés");

            var pT = new ParametresEtat { Etat = TypeEtat.Balance, Nature = NatureCompte.Tiers, Du = an2026, Au = fin2026, JournalANouveaux = "RAN" };
            var cumulsT = new[]
            {
                new CumulCompte { Compte = "F100005", MouvementDebit = 20000, MouvementCredit = 50000 },
                new CumulCompte { Compte = "C100001", MouvementDebit = 10000 }
            };
            var rt = ServiceEtats.ConstruireBalance(pT, null, cumulsT, refE);
            Verif(rt.Lignes.Count == 5 && rt.Lignes[0].Compte == "C100001" && rt.Lignes[1].Libelle == "Total clients" && rt.Lignes[2].SoldeCredit == 30000
                  && rt.Lignes[3].Libelle == "Total fournisseurs", "balance des tiers : groupée par type de tiers");
            pT.TypeTiers = TypesTiers.Fournisseur;
            Verif(ServiceEtats.ConstruireBalance(pT, null, cumulsT, refE).NombreComptes == 1, "balance des tiers : filtre fournisseurs");

            var pG = new ParametresEtat { Etat = TypeEtat.GrandLivre, Nature = NatureCompte.General, Du = new DateTime(2026, 3, 1), Au = new DateTime(2026, 3, 31),
                                          CompteDe = "5", CompteA = "5" };
            var ouvG = new[]
            {
                new CumulCompte { Compte = "5211000", OuvertureDebit = 1000000, OuvertureCredit = 250000 },
                new CumulCompte { Compte = "6051000", OuvertureDebit = 9 }
            };
            var ecrG = new[]
            {
                new EcritureEtat { Journal = "BQ", Date = new DateTime(2026, 3, 9), Piece = "2", CompteGeneral = "5211000", Libelle = "Encaissement", Debit = 100000 },
                new EcritureEtat { Journal = "BQ", Date = new DateTime(2026, 3, 2), Piece = "1", CompteGeneral = "5211000", CompteTiers = "F100005", Libelle = "ONEA", Credit = 50000 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 3, 2), Piece = "1", CompteGeneral = "6051000", Libelle = "Eau", Debit = 50000 }
            };
            var rg = ServiceEtats.ConstruireGrandLivre(pG, an2026, ouvG, ecrG, refE);
            Verif(rg.Lignes.Count == 6 && rg.Lignes[0].Genre == GenreLigne.Entete && rg.Lignes[0].Libelle.Contains("Banque")
                  && rg.Lignes[1].Genre == GenreLigne.Report && rg.Lignes[1].Solde == 750000, "grand livre : titre du compte et solde d'ouverture");
            Verif(rg.Lignes[2].Libelle == "ONEA" && rg.Lignes[2].Solde == 700000 && rg.Lignes[2].CompteAssocie == "F100005" && rg.Lignes[3].Solde == 800000,
                  "grand livre : écritures par date, solde progressif");
            Verif(rg.Lignes[4].Genre == GenreLigne.Total && rg.Lignes[4].Debit == 100000 && rg.Lignes[4].Credit == 50000 && rg.Lignes[4].Solde == 800000
                  && rg.NombreEcritures == 2 && rg.NombreComptes == 1 && rg.TotalDebit == 100000, "grand livre : total du compte et plage");

            var pJ = new ParametresEtat { Etat = TypeEtat.Journal, Du = an2026, Au = new DateTime(2026, 2, 28) };
            var ecrJ = new[]
            {
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 1, 15), Piece = "1", CompteGeneral = "6051000", Debit = 1000 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 1, 15), Piece = "1", CompteGeneral = "4011000", CompteTiers = "F100005", Credit = 1000 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 2, 3), Piece = "1", CompteGeneral = "6051000", Debit = 500 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 2, 3), Piece = "1", CompteGeneral = "4011000", Credit = 400 },
                new EcritureEtat { Journal = "BQ", Date = new DateTime(2026, 1, 20), Piece = "1", CompteGeneral = "5211000", Debit = 10 },
                new EcritureEtat { Journal = "BQ", Date = new DateTime(2026, 1, 20), Piece = "1", CompteGeneral = "7011000", Credit = 10 }
            };
            var rj = ServiceEtats.ConstruireJournal(pJ, ecrJ, refE);
            Verif(rj.Lignes.Count == 14 && rj.Lignes[0].Genre == GenreLigne.Entete && rj.Lignes[0].Libelle == "ACH · Achats · Janvier 2026",
                  "journaux : en-tête journal et mois : " + rj.Lignes[0].Libelle);
            Verif(rj.Lignes[3].Genre == GenreLigne.SousTotal && rj.Lignes[3].Debit == 1000 && rj.Lignes[8].Genre == GenreLigne.Total
                  && rj.Lignes[8].Debit == 1500 && rj.Lignes[8].Credit == 1400, "journaux : total du mois et du journal");
            Verif(rj.Avertissements.Count == 1 && rj.Avertissements[0].Contains("Février 2026") && rj.TotalDebit == 1510 && rj.TotalCredit == 1410
                  && rj.NombreJournaux == 2 && rj.NombreEcritures == 6, "journaux : mois déséquilibré signalé, totaux");
            pJ.Journal = "bq";
            Verif(ServiceEtats.ConstruireJournal(pJ, ecrJ, refE).NombreJournaux == 1, "journaux : un seul journal");

            var rce = ServiceEtats.ConstruireCentralisateur(new ParametresEtat { Etat = TypeEtat.Centralisateur, Du = an2026, Au = new DateTime(2026, 2, 28) }, new[]
            {
                new CumulJournal { Journal = "BQ", Mois = an2026, Debit = 10, Credit = 10, Nombre = 2 },
                new CumulJournal { Journal = "ACH", Mois = an2026, Debit = 1000, Credit = 1000, Nombre = 2 },
                new CumulJournal { Journal = "ACH", Mois = new DateTime(2026, 2, 1), Debit = 500, Credit = 400, Nombre = 2 }
            }, refE);
            Verif(rce.Lignes.Count == 6 && rce.Lignes[0].Journal == "ACH" && rce.Lignes[0].Libelle == "Achats" && rce.Lignes[2].Debit == 1010
                  && rce.Lignes[5].Debit == 1510 && rce.Lignes[5].Credit == 1410 && rce.NombreEcritures == 6 && rce.Avertissements.Count == 1,
                  "centralisateur : totaux par mois et par journal");

            var refAN = new Referentiel();
            refAN.Journaux["ACH"] = new Journal { Code = "ACH", Intitule = "Achats" };
            refAN.Journaux["OD2"] = new Journal { Code = "OD2", Intitule = "Écritures d'À-NOUVEAUX" };
            Verif(ServiceEtats.DetecterJournalANouveaux(refE) == "RAN" && ServiceEtats.DetecterJournalANouveaux(refAN) == "OD2"
                  && ServiceEtats.DetecterJournalANouveaux(new Referentiel()) == null, "journal des à-nouveaux reconnu (code ou intitulé)");

            bool refuse = false;
            try { ServiceEtats.Valider(new ParametresEtat { Etat = TypeEtat.Balance, Du = new DateTime(2026, 6, 1), Au = new DateTime(2027, 1, 31) }, refE); }
            catch (ArgumentException) { refuse = true; }
            Verif(refuse && ServiceEtats.Valider(new ParametresEtat { Etat = TypeEtat.Journal, Du = new DateTime(2026, 6, 1), Au = new DateTime(2027, 1, 31) }, refE) == null
                  && ServiceEtats.Valider(new ParametresEtat { Etat = TypeEtat.Balance, Du = new DateTime(2026, 6, 1), Au = new DateTime(2026, 6, 30) }, refE) == an2026,
                  "période : un seul exercice pour la balance, ouverture depuis le début d'exercice");

            var sqlB = ServiceEtats.RequeteBalance(NatureCompte.Tiers, new PlageComptes("C", "F"), true);
            Verif(sqlB.Contains("e.CT_Num >= @de") && sqlB.Contains("LEFT(e.CT_Num, LEN(@a)) <= @a") && sqlB.Contains("NOT (DATEADD(day, e.EC_Jour - 1, e.JM_Date) < @du OR e.JO_Num = @jan)")
                  && sqlB.Contains("GROUP BY e.CT_Num"), "requête balance : tiers, plage et à-nouveaux");
            var sqlG = ServiceEtats.RequeteLignesGrandLivre(NatureCompte.General, new PlageComptes("", ""), true, false);
            Verif(sqlG.Contains("e.JO_Num <> @jan") && !sqlG.Contains("@de") && sqlG.Contains("CAST(NULL AS varchar(3)) AS EC_Lettrage")
                  && sqlG.Contains("ORDER BY e.CG_Num"), "requête grand livre : à-nouveaux hors des lignes");
            Verif(ServiceEtats.RequeteJournal(true).Contains("e.JO_Num = @journal") && !ServiceEtats.RequeteCentralisateur(false).Contains("@journal")
                  && ServiceEtats.RequeteOuverturesGrandLivre(NatureCompte.General, new PlageComptes("", ""), false).Contains("(DATEADD(day, e.EC_Jour - 1, e.JM_Date) < @du)"), "requêtes journaux et ouvertures");

            // Date comptable = période (JM_Date) + jour (EC_Jour) ; EC_Date est la date de saisie et ne sert jamais de filtre.
            var requetesDates = new[]
            {
                sqlB, sqlG, ServiceEtats.RequeteJournal(true), ServiceEtats.RequeteCentralisateur(true),
                ServiceEtats.RequeteOuverturesGrandLivre(NatureCompte.Tiers, new PlageComptes("", ""), true),
                ServiceConsultation.RequeteLignes(NatureCompte.General, FiltreLettrage.Toutes, true, true), ServiceConsultation.RequeteReport(NatureCompte.Tiers),
                ServiceSaisie.RequeteSoldeAnterieur(true, true)
            };
            Verif(requetesDates.All(q => !q.Contains("e.EC_Date")) && ServiceEtats.RequeteJournal(false).Contains("DATEADD(day, e.EC_Jour - 1, e.JM_Date) AS EC_Date")
                  && ServiceEtats.RequeteJournal(false).Contains("ORDER BY e.JO_Num, DATEADD(day, e.EC_Jour - 1, e.JM_Date), e.EC_Piece"),
                  "requêtes sur la date comptable (JM_Date + EC_Jour), jamais sur la date de saisie");

            var xb = Path.Combine(dir, "balance.xlsx");
            ExportEtats.Ecrire(xb, rb);
            var txb = LecteurSource.Lire(xb, new SourceProfil());
            Verif(txb.Lignes.Count == 9 && txb.Entetes[6] == "Solde débiteur", "export Excel de la balance relu : " + txb.Lignes.Count);
            Verif(ExportEtats.NomFichier(rb) == "Balance_generale_20260101_20261231.xlsx", "nom du fichier exporté : " + ExportEtats.NomFichier(rb));
            var xg = Path.Combine(dir, "grandlivre.xlsx");
            ExportEtats.Ecrire(xg, rg);
            var txg = LecteurSource.Lire(xg, new SourceProfil());
            Verif(txg.Lignes.Count == 6 && txg.Entetes.Count == 13, "export Excel du grand livre relu : " + txg.Lignes.Count);

            Console.WriteLine("== Saisie des écritures");
            var refS = new Referentiel();
            foreach (var cg in new[] { "401|Fournisseurs (total)|1", "4010000|Fournisseurs|0", "4011000|Fournisseurs groupe|0", "4454000|TVA récupérable|0",
                                       "5211000|Banque BOA|0", "605|Total 605|1", "6051000|Eau|0", "6052000|Électricité|0" })
            {
                var x = cg.Split('|');
                refS.Comptes[x[0]] = new CompteGeneral { Numero = x[0], Intitule = x[1], Type = int.Parse(x[2]) };
            }
            refS.Tiers["F100003"] = new Tiers { Numero = "F100003", Intitule = "SONABEL", Type = TypesTiers.Fournisseur, Collectif = "4010000" };
            refS.Tiers["F100005"] = new Tiers { Numero = "F100005", Intitule = "ONEA", Type = TypesTiers.Fournisseur, Collectif = "4010000" };
            refS.Tiers["P000001"] = new Tiers { Numero = "P000001", Intitule = "Prospect X", EstProspect = true };
            refS.Journaux["ACH"] = new Journal { Code = "ACH", Intitule = "Achats", ModeNumerotation = 3 };
            refS.Journaux["VIEUX"] = new Journal { Code = "VIEUX", Intitule = "Ancien journal", EnSommeil = true };
            refS.Exercices.Add(new Exercice { Debut = new DateTime(2025, 1, 1), Fin = new DateTime(2025, 12, 31) });
            refS.Exercices.Add(new Exercice { Debut = new DateTime(2026, 1, 1), Fin = new DateTime(2026, 12, 31) });
            refS.PeriodesCloturees.Add(Referentiel.ClePeriode("ACH", new DateTime(2025, 12, 1)));
            var oct = new PeriodeSaisie(new DateTime(2026, 10, 9));

            var pers = ServiceSaisie.Periodes(refS);
            Verif(pers.Count == 24 && pers[0].Mois == new DateTime(2025, 1, 1) && pers[23].Libelle == "Décembre 2026", "périodes : tous les mois des exercices");
            Verif(ServiceSaisie.PeriodeParDefaut(pers, new DateTime(2026, 10, 17)).Equals(oct)
                  && ServiceSaisie.PeriodeParDefaut(pers, new DateTime(2030, 1, 1)).Mois == new DateTime(2026, 12, 1), "période par défaut : le mois en cours");
            Verif(ServiceSaisie.JournauxSaisissables(refS).Count == 1 && ServiceSaisie.EstCloturee(refS, "ach", new PeriodeSaisie(new DateTime(2025, 12, 15)))
                  && !ServiceSaisie.EstCloturee(refS, "ACH", oct), "journaux en sommeil exclus, période clôturée reconnue");
            Verif(ServiceSaisie.Date(oct, "17") == new DateTime(2026, 10, 17) && ServiceSaisie.Date(oct, "17/10") == new DateTime(2026, 10, 17)
                  && ServiceSaisie.Date(oct, "17/10/2026") == new DateTime(2026, 10, 17) && ServiceSaisie.Date(oct, "32") == null
                  && ServiceSaisie.Date(oct, "17/11/2026") == null && ServiceSaisie.Date(oct, " ") == null, "jour tapé → date dans la période");

            Verif(ServiceSaisie.ResoudreCompte(refS, "401").Numero == "4010000", "« 401 » complété par des zéros, malgré le compte de total 401");
            Verif(ServiceSaisie.ResoudreCompte(refS, "4011").Numero == "4011000" && ServiceSaisie.ResoudreCompte(refS, "445").Numero == "4454000"
                  && ServiceSaisie.ResoudreCompte(refS, "électricité").Numero == "6052000", "compte reconnu par début de n° ou intitulé");
            Verif(ServiceSaisie.ResoudreCompte(refS, "605") == null && ServiceSaisie.ResoudreCompte(refS, "999") == null
                  && ServiceSaisie.ResoudreCompte(refS, " 6051000 ").Intitule == "Eau", "compte ambigu ou inconnu non reconnu");
            Verif(ServiceSaisie.ResoudreTiers(refS, "sonabel").Numero == "F100003" && ServiceSaisie.ResoudreTiers(refS, "F1000") == null,
                  "tiers reconnu par intitulé, début de n° ambigu refusé");
            var sugg = ServiceSaisie.Filtrer(ServiceSaisie.ComptesSaisissables(refS, NatureCompte.General), "605");
            Verif(sugg.Count == 2 && sugg[0].Numero == "6051000" && ServiceSaisie.ComptesSaisissables(refS, NatureCompte.Tiers).Count == 2
                  && ServiceSaisie.Filtrer(ServiceSaisie.ComptesSaisissables(refS, NatureCompte.General), "eau")[0].Numero == "6051000",
                  "suggestions : n° puis intitulé, sans comptes de total ni prospects");

            string err;
            var l1 = ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Tiers = "sonabel", Credit = "118 000" }, "Facture SONABEL", out err);
            Verif(l1 != null && l1.CompteGeneral == "4010000" && l1.CompteTiers == "F100003" && l1.Credit == 118000 && l1.Libelle == "Facture SONABEL",
                  "tiers sans compte : collectif du tiers et libellé de la pièce repris");
            Verif(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "605", Debit = "100" }, "", out err) == null && err.Contains("ambigu"),
                  "compte ambigu refusé");
            Verif(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Debit = "100", Credit = "5" }, "", out err) == null
                  && err.StartsWith("Renseignez soit"), "débit et crédit sur la même ligne refusés");
            Verif(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Debit = "-5" }, "", out err) == null && err.StartsWith("Montant négatif")
                  && ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052" }, "", out err) == null && err == "Montant manquant.",
                  "montant négatif ou absent refusé");
            Verif(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Tiers = "P000001", Compte = "4010000", Debit = "5" }, "", out err) == null
                  && err.Contains("prospect"), "prospect refusé");
            Verif(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Debit = "1 000,50", Libelle = new string('x', 70) }, "", out err) == null
                  && err.StartsWith("Libellé trop long"), "libellé limité à 69 caractères");

            var lignesS = new List<LigneSaisie> { l1 };
            decimal sd, sc;
            ServiceSaisie.SoldeAProposer(lignesS, out sd, out sc);
            Verif(sd == 118000 && sc == 0, "montant proposé pour équilibrer la pièce");
            lignesS.Add(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Debit = "100000" }, "Facture SONABEL", out err));
            lignesS.Add(ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "445", Debit = "18 000", Echeance = "31/10/2026" }, "Facture SONABEL", out err));
            Verif(lignesS.All(l => l != null) && ServiceSaisie.Ecart(lignesS) == 0 && lignesS[2].Echeance == new DateTime(2026, 10, 31), "pièce équilibrée");

            var pS = ServiceSaisie.Construire(new EntetePiece { Journal = "ach", Date = ServiceSaisie.Date(oct, "17"), Piece = " ", RefPiece = "FAC-0045", Libelle = "Facture SONABEL" }, lignesS);
            var anS = ServiceSaisie.Controler(pS, refS, oct);
            Verif(pS.Lignes.Count == 3 && pS.NumerotationAuto && pS.Journal == "ACH" && pS.Lignes[2].NumLigne == 3 && pS.Lignes[2].RefPiece == "FAC-0045"
                  && anS.Count == 0, "pièce construite et contrôlée sans anomalie " + string.Join(" | ", anS));
            var dtS = ServiceImport.ConstruireLignes(pS);
            Verif(dtS.Rows.Count == 3 && (string)dtS.Rows[0]["CT_Num"] == "F100003" && dtS.Rows[1]["CT_Num"] == DBNull.Value
                  && (decimal)dtS.Rows[1]["Debit"] == 100000, "lignes transmises à la procédure");
            var avecRef = ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Reference = " 070139 ", Debit = "5" }, "", out err);
            var pRef = ServiceSaisie.Construire(new EntetePiece { Journal = "ACH", Date = ServiceSaisie.Date(oct, "17") }, new List<LigneSaisie> { avecRef });
            Verif(avecRef != null && avecRef.Reference == "070139" && avecRef.VersSaisie().Reference == "070139"
                  && (string)ServiceImport.ConstruireLignes(pRef).Rows[0]["EC_Reference"] == "070139"
                  && ServiceSaisie.InterpreterLigne(refS, new SaisieLigne { Compte = "6052", Reference = new string('9', 18), Debit = "5" }, "", out err) == null
                  && err.StartsWith("Référence trop longue"), "référence de ligne saisie, transmise à la procédure, limitée à 17 caractères");

            var anKo = ServiceSaisie.Controler(ServiceSaisie.Construire(new EntetePiece { Journal = "ACH", Date = new DateTime(2025, 12, 5) },
                                                                        new List<LigneSaisie> { lignesS[1] }), refS, oct);
            Verif(anKo.Any(a => a.Message.Contains("au moins 2 lignes")) && anKo.Any(a => a.Message.StartsWith("Pièce déséquilibrée"))
                  && anKo.Any(a => a.Message.Contains("n'appartient pas à la période")) && anKo.Any(a => a.Message.Contains("clôturée")),
                  "pièce refusée : une ligne, déséquilibre, hors période, période clôturée");
            Verif(ServiceSaisie.Controler(ServiceSaisie.Construire(new EntetePiece { Journal = "ACH" }, lignesS), refS, oct)
                  .Any(a => a.Message.StartsWith("Jour manquant")), "jour manquant signalé");

            var optS = ServiceSaisie.Options(false);
            Verif(!optS.RenumeroterDoublon && !optS.AutoriserDoublon && !optS.IgnorerHash && ServiceSaisie.Options(true).IgnorerHash,
                  "options : un n° déjà pris est signalé, jamais remplacé");
            Verif(ServiceSaisie.Expliquer(50005, "x").Contains("déjà utilisé") && ServiceSaisie.Expliquer(50001, "Chaînage d'inaltérabilité (cbHash*) actif").Contains("hors chaînage")
                  && ServiceSaisie.Expliquer(50001, "autre") == null, "refus du serveur expliqués");
            Verif(ServiceSaisie.MessageProcedure(false, null, "1.3").Contains("n'est pas installée") && ServiceSaisie.MessageProcedure(true, "1.2", "1.3").Contains("version 1.2")
                  && ServiceSaisie.MessageProcedure(true, "1.3", "1.3") == null, "procédure d'enregistrement absente ou ancienne détectée");

            var br = ServiceSaisie.Brouillard(new[]
            {
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 10, 2), Piece = "1", CompteGeneral = "6051000", Debit = 50 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 10, 2), Piece = "1", CompteGeneral = "4010000", CompteTiers = "F100005", Credit = 50 },
                new EcritureEtat { Journal = "ACH", Date = new DateTime(2026, 10, 3), Piece = "2", CompteGeneral = "6051000", Debit = 10 }
            });
            Verif(br.Lignes.Count == 4 && br.Lignes[3].Genre == GenreLigne.TotalGeneral && br.TotalDebit == 60 && br.TotalCredit == 50
                  && br.NombrePieces == 2 && br.Lignes[1].Debit == null, "brouillard de la période et son total");
            var sit = ServiceSaisie.Situation("5711000", "Caisse", -224054m, new[]
            {
                new LigneEtat { Genre = GenreLigne.Detail, Compte = "5711000", Debit = 1755300m },
                new LigneEtat { Genre = GenreLigne.Detail, Compte = "5711000 ", Credit = 2223148m },
                new LigneEtat { Genre = GenreLigne.Detail, Compte = "4211010", Debit = 70000m },
                new LigneEtat { Genre = GenreLigne.TotalGeneral, Compte = "5711000", Debit = 9m }
            });
            // Chiffres de la fenêtre Journal du logiciel comptable : 224 054 C + 1 755 300 D − 2 223 148 C = 691 902 C.
            Verif(sit.MouvementsDebit == 1755300m && sit.MouvementsCredit == 2223148m && sit.NouveauSolde == -691902m
                  && sit.AncienCredit == 224054m && sit.AncienDebit == null && sit.NouveauCredit == 691902m && sit.NouveauDebit == null,
                  "trésorerie du journal : ancien solde, mouvements du compte, nouveau solde");
            var reqSolde = ServiceSaisie.RequeteSoldeAnterieur(true, true);
            Verif(reqSolde.Contains("DATEADD(day, e.EC_Jour - 1, e.JM_Date) >= @debut") && reqSolde.Contains("DATEADD(day, e.EC_Jour - 1, e.JM_Date) < @du OR") && reqSolde.Contains("e.JO_Num = @jan")
                  && !reqSolde.Contains("@journal") && !ServiceSaisie.RequeteSoldeAnterieur(false, false).Contains("@jan"),
                  "ancien solde : compte de trésorerie tous journaux, depuis le début de l'exercice, à-nouveaux compris");
            // Janvier 2026 dans le logiciel comptable : à-nouveau 10 800 D + 1 840 600 D − 1 506 905 C = 344 495 D.
            var janvier = ServiceSaisie.Situation("571100", "Caisse", 10800m, new[]
            {
                new LigneEtat { Genre = GenreLigne.Detail, Compte = "571100", Debit = 1840600m },
                new LigneEtat { Genre = GenreLigne.Detail, Compte = "571100", Credit = 1506905m }
            });
            Verif(janvier.AncienDebit == 10800m && janvier.NouveauDebit == 344495m && janvier.NouveauCredit == null,
                  "trésorerie du premier mois : l'à-nouveau forme l'ancien solde");
            var relue = ServiceSaisie.InterpreterLigne(refS, lignesS[1].VersSaisie(), "", out err);
            Verif(relue != null && relue.Debit == 100000 && relue.CompteGeneral == "6052000" && relue.Libelle == "Facture SONABEL",
                  "ligne remise en saisie pour modification, relue à l'identique");

            Console.WriteLine("== Impression (PDF)");
            Verif(Math.Abs(DocumentPdf.Largeur("A", 10, PolicePdf.Normal) - 6.67) < 0.01 && DocumentPdf.Largeur("é", 10, PolicePdf.Gras) > 5,
                  "largeur du texte en Helvetica");
            var tronque = DocumentPdf.Tronquer("Libellé très long à raccourcir pour la colonne", 60, 7.5, PolicePdf.Normal);
            Verif(tronque.Length < 20 && DocumentPdf.Largeur(tronque, 7.5, PolicePdf.Normal) <= 60, "texte tronqué pour tenir dans la colonne : " + tronque);
            Verif(DocumentPdf.Couper("un deux trois quatre cinq six sept huit", 50, 10, PolicePdf.Normal).Count > 1, "texte coupé sur plusieurs lignes");
            Verif(ImpressionEtats.Montant(1250000.5m) == "1 250 000,5" && ImpressionEtats.Solde(-500m) == "500 C" && ImpressionEtats.Solde(0m) == "0",
                  "montants imprimés : espace des milliers, virgule, sens");

            var pdfB = Encoding.ASCII.GetString(ImpressionEtats.Generer(rb, "SODEM SA", "GESTION_SODEM", new DateTime(2026, 10, 17, 15, 0, 0), false));
            Verif(pdfB.StartsWith("%PDF-1.4") && pdfB.Contains("/Count 1") && pdfB.Contains(DocumentPdf.ChainePdf("Balance générale"))
                  && pdfB.Contains("(Page 1 / 1)") && pdfB.Contains("(1 000 000)") && pdfB.Contains("(SODEM SA)"), "balance en PDF : titre, société, montants, pied de page");
            int sx = pdfB.LastIndexOf("startxref", StringComparison.Ordinal);
            int depart = int.Parse(pdfB.Substring(sx + 10).Split('\n')[0]);
            Verif(pdfB.Substring(depart, 4) == "xref" && pdfB.TrimEnd().EndsWith("%%EOF"), "structure du PDF (table des références)");

            var ecrLong = Enumerable.Range(1, 150).Select(i => new EcritureEtat
            {
                Journal = "BQ", Date = new DateTime(2026, 3, 1 + i % 28), Piece = i.ToString(), CompteGeneral = "5211000", Libelle = "Ligne " + i, Debit = i
            }).ToList();
            var rgLong = ServiceEtats.ConstruireGrandLivre(pG, an2026, ouvG, ecrLong, refE);
            var pdfG = Encoding.ASCII.GetString(ImpressionEtats.Generer(rgLong, "SODEM SA", "GESTION_SODEM", null, false));
            int nbPages = int.Parse(System.Text.RegularExpressions.Regex.Match(pdfG, @"/Count (\d+)").Groups[1].Value);
            Verif(nbPages >= 3 && pdfG.Contains("(Page " + nbPages + " / " + nbPages + ")") && pdfG.Contains("(Ligne 150)"),
                  "grand livre sur plusieurs pages : " + nbPages + " pages");
            var compresse = ImpressionEtats.Generer(rgLong, "SODEM SA", "GESTION_SODEM");
            Verif(compresse.Length < pdfG.Length / 2, "flux compressés : " + compresse.Length + " octets au lieu de " + pdfG.Length);

            var extrait = ImpressionEtats.ExtraitDeCompte(rc);
            Verif(extrait.Lignes.Count == 5 && extrait.Lignes[0].Genre == GenreLigne.Entete && extrait.Lignes[1].Genre == GenreLigne.Report
                  && extrait.Lignes[4].Solde == -500 && extrait.Titre == "Extrait de compte", "extrait de compte : titre, report, écritures, total");
            var fichierPdf = Path.Combine(dir, "balance.pdf");
            ImpressionEtats.Ecrire(fichierPdf, rb, "SODEM SA", "GESTION_SODEM");
            Verif(File.Exists(fichierPdf) && new FileInfo(fichierPdf).Length > 500, "PDF écrit sur disque");

            Console.WriteLine("== Exports");
            var rap = Path.Combine(dir, "rapport.xlsx");
            ExportRapport.ExporterPieces(rap, an.Pieces);
            var tr = LecteurSource.Lire(rap, new SourceProfil());
            Verif(tr.Lignes.Count == 4 && tr.Entetes[0] == "Statut", "rapport xlsx relu");
            ExportRapport.ExporterLignes(Path.Combine(dir, "anomalies.csv"), an.Pieces, true);
            var ta = LecteurSource.Lire(Path.Combine(dir, "anomalies.csv"), new SourceProfil());
            Verif(ta.Lignes.Count >= 2, "anomalies csv relu : " + ta.Lignes.Count);

            Console.WriteLine();
            Console.WriteLine(ok + " OK, " + ko + " KO");
            if (Environment.UserInteractive && !Console.IsInputRedirected) { Console.WriteLine("Appuie sur une touche…"); Console.ReadKey(); }
            return ko == 0 ? 0 : 1;
        }
    }
}
