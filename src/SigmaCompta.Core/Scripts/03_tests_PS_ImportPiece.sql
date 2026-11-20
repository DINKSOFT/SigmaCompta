/* =============================================================================
   03 — Tests de imp.PS_ImportPiece (aucun test ne laisse de trace en base)
   Adapter journal / comptes / tiers / date aux données du dossier.
   ============================================================================= */
SET NOCOUNT ON;
GO

/* ---------------------------------------------------------------------------
   TEST 1 — Simulation nominale : pièce OD équilibrée, numéro automatique.
   Les déclencheurs de la base s'exécutent réellement, puis tout est annulé.
   Attendu : un jeu de lignes « SIMULATION » avec EC_No > 0 et un n° de pièce.
   --------------------------------------------------------------------------- */
DECLARE @L imp.TT_LigneEcriture, @Piece varchar(13), @No1 int, @No2 int;

INSERT @L (NumLigne, CG_Num, CT_Num, EC_Intitule, Debit, Credit) VALUES
    (1, '6610000', NULL, 'Salaires octobre 2026', 1500000, 0),   -- compte de charge à adapter
    (2, '4220000', NULL, 'Salaires octobre 2026', 0, 1500000);

EXEC imp.PS_ImportPiece
     @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L,
     @EC_RefPiece = 'PAIE2610', @Simulation = 1,
     @EC_PieceOut = @Piece OUTPUT, @EC_NoDebut = @No1 OUTPUT, @EC_NoFin = @No2 OUTPUT;

SELECT 'TEST 1' AS Test, @Piece AS Piece, @No1 AS EC_NoDebut, @No2 AS EC_NoFin, @@TRANCOUNT AS TranCount_attendu_0;
GO

/* ---------------------------------------------------------------------------
   TEST 2 — Refus groupé : déséquilibre + compte inconnu + ligne débit ET crédit
   Attendu : erreur 50001 listant les 3 problèmes, rien d'inséré.
   --------------------------------------------------------------------------- */
DECLARE @L imp.TT_LigneEcriture;

INSERT @L (NumLigne, CG_Num, Debit, Credit) VALUES
    (1, '6610000', 1000, 0),
    (2, '9999999', 0, 900),        -- compte inexistant
    (3, '4220000', 50, 50);        -- débit ET crédit

BEGIN TRY
    EXEC imp.PS_ImportPiece @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L, @Simulation = 1;
    SELECT 'TEST 2 — ÉCHEC : aucune erreur levée' AS Resultat;
END TRY
BEGIN CATCH
    SELECT 'TEST 2' AS Test, ERROR_NUMBER() AS N_attendu_50001, ERROR_MESSAGE() AS Message;
END CATCH;
GO

/* ---------------------------------------------------------------------------
   TEST 3 — Comparaison colonne par colonne avec une ligne saisie manuellement.
   Insère réellement (sans simulation) DANS une transaction externe, compare,
   puis annule. Les écarts attendus : cbMarq, EC_No, montants, dates, libellé,
   cbCreation*… Tout autre écart = colonne à ajouter à l'INSERT de la proc.
   --------------------------------------------------------------------------- */
SET XACT_ABORT ON;                 -- toute erreur annule la transaction du test
DECLARE @Ref int = 18078;          -- EC_No d'une ligne saisie manuellement (même journal, même type de compte)
DECLARE @L imp.TT_LigneEcriture, @No1 int, @No2 int;

INSERT @L (NumLigne, CG_Num, EC_Intitule, Debit, Credit) VALUES
    (1, '6610000', 'Test comparaison', 1000, 0),
    (2, '4220000', 'Test comparaison', 0, 1000);

BEGIN TRANSACTION;

EXEC imp.PS_ImportPiece @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L,
     @EC_NoDebut = @No1 OUTPUT, @EC_NoFin = @No2 OUTPUT;

DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N' UNION ALL SELECT ' + CAST(c.column_id AS nvarchar(5)) + N', N''' + c.name + N''', '
            +  N'CONVERT(nvarchar(400), r.' + QUOTENAME(c.name) + s.Style + N'), '
            +  N'CONVERT(nvarchar(400), n.' + QUOTENAME(c.name) + s.Style + N')'
            +  N' FROM dbo.F_ECRITUREC r, dbo.F_ECRITUREC n WHERE r.EC_No = @Ref AND n.EC_No = @New'
FROM sys.columns c
CROSS APPLY (SELECT CASE WHEN TYPE_NAME(c.user_type_id) IN ('varbinary','binary','timestamp') THEN N', 1'
                         WHEN TYPE_NAME(c.user_type_id) IN ('datetime','smalldatetime','datetime2','date') THEN N', 121'
                         ELSE N'' END AS Style) s
WHERE c.object_id = OBJECT_ID(N'dbo.F_ECRITUREC')
  AND TYPE_NAME(c.user_type_id) NOT IN ('image','text','ntext','xml');

SET @sql = N'SELECT Colonne, Reference, Import, CASE WHEN ISNULL(Reference, N''¤'') <> ISNULL(Import, N''¤'') THEN ''≠'' ELSE '''' END AS Ecart '
         + N'FROM (' + STUFF(@sql, 1, 11, N'') + N') d(Ordre, Colonne, Reference, Import) ORDER BY Ordre;';

EXEC sys.sp_executesql @sql, N'@Ref int, @New int', @Ref = @Ref, @New = @No1;

ROLLBACK TRANSACTION;
SELECT 'TEST 3 annulé' AS Test, @@TRANCOUNT AS TranCount_attendu_0;
GO

/* ---------------------------------------------------------------------------
   TEST 4 — N° de pièce déjà utilisé : renumérotation (@RenumeroterDoublon = 1,
   valeur par défaut). On reprend le n° de la dernière pièce du journal OD.
   Attendu : Piece_attribuee ≠ Piece_demandee (n° suivant de la séquence),
   puis, avec @RenumeroterDoublon = 0, l'erreur 50005.
   --------------------------------------------------------------------------- */
DECLARE @L imp.TT_LigneEcriture, @Demandee varchar(13), @Attribuee varchar(13);

SELECT TOP (1) @Demandee = RTRIM(EC_Piece)
FROM   dbo.F_ECRITUREC
WHERE  JO_Num = 'OD' AND EC_Piece <> ''
ORDER BY cbMarq DESC;

INSERT @L (NumLigne, CG_Num, EC_Intitule, Debit, Credit) VALUES
    (1, '6610000', 'Test renumérotation', 1000, 0),
    (2, '4220000', 'Test renumérotation', 0, 1000);

EXEC imp.PS_ImportPiece @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L,
     @EC_Piece = @Demandee, @Simulation = 1, @EC_PieceOut = @Attribuee OUTPUT;

SELECT 'TEST 4' AS Test, @Demandee AS Piece_demandee, @Attribuee AS Piece_attribuee;

BEGIN TRY
    EXEC imp.PS_ImportPiece @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L,
         @EC_Piece = @Demandee, @Simulation = 1, @RenumeroterDoublon = 0;
    SELECT 'TEST 4 bis — ÉCHEC : aucune erreur levée' AS Resultat;
END TRY
BEGIN CATCH
    SELECT 'TEST 4 bis' AS Test, ERROR_NUMBER() AS N_attendu_50005, ERROR_MESSAGE() AS Message;
END CATCH;
GO

/* ---------------------------------------------------------------------------
   IMPORT RÉEL (à décommenter une fois les tests concluants)
   ---------------------------------------------------------------------------
DECLARE @L imp.TT_LigneEcriture, @Piece varchar(13), @No1 int, @No2 int;
INSERT @L (NumLigne, CG_Num, CT_Num, EC_Intitule, Debit, Credit) VALUES
    (1, '6610000', NULL, 'Salaires octobre 2026', 1500000, 0),
    (2, '4220000', NULL, 'Salaires octobre 2026', 0, 1500000);
EXEC imp.PS_ImportPiece @JO_Num = 'OD', @EC_Date = '20261031', @Lignes = @L,
     @EC_RefPiece = 'PAIE2610',
     @EC_PieceOut = @Piece OUTPUT, @EC_NoDebut = @No1 OUTPUT, @EC_NoFin = @No2 OUTPUT;
SELECT @Piece AS Piece, @No1 AS EC_NoDebut, @No2 AS EC_NoFin;
*/
