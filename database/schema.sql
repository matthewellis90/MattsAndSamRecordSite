-- Run in the target database as its provisioning identity.
-- The four legacy tables are read once and remain available in their original form.
SET NOCOUNT ON;
SET XACT_ABORT ON;
-- SQL Server requires these session settings for the filtered legacy-source index.
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET QUOTED_IDENTIFIER ON;
SET NUMERIC_ROUNDABORT OFF;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock
        @Resource = N'MattsRecords.Schema',
        @LockMode = 'Exclusive',
        @LockOwner = 'Transaction',
        @LockTimeout = 15000;
    IF @lockResult < 0
        THROW 50001, 'The record site schema is being provisioned by another session.', 1;

    IF OBJECT_ID(N'dbo.RecordSiteArtist', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.RecordSiteArtist
        (
            id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RecordSiteArtist PRIMARY KEY,
            artist_name nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC NOT NULL,
            row_version rowversion NOT NULL
        );
    END;

    IF OBJECT_ID(N'dbo.RecordSiteRelease', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.RecordSiteRelease
        (
            id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_RecordSiteRelease PRIMARY KEY,
            artist_id int NULL CONSTRAINT FK_RecordSiteRelease_RecordSiteArtist REFERENCES dbo.RecordSiteArtist(id),
            artist_name nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC NULL,
            album_name nvarchar(200) COLLATE Latin1_General_100_CI_AI_SC NOT NULL,
            [year] int NULL,
            record_kind int NOT NULL CONSTRAINT CK_RecordSiteRelease_Kind CHECK (record_kind BETWEEN 0 AND 3),
            record_format nvarchar(100) NULL,
            notes nvarchar(2000) NULL,
            cover_url nvarchar(2048) NULL,
            legacy_source varchar(11) NULL,
            legacy_id int NULL,
            row_version rowversion NOT NULL,
            CONSTRAINT CK_RecordSiteRelease_LegacySource CHECK (
                (legacy_source IS NULL AND legacy_id IS NULL) OR
                (legacy_source IS NOT NULL AND legacy_source IN ('Album', 'Single', 'Compilation') AND legacy_id IS NOT NULL)
            )
        );

        CREATE INDEX IX_RecordSiteRelease_Artist ON dbo.RecordSiteRelease(artist_id);
        CREATE UNIQUE INDEX UX_RecordSiteRelease_LegacySource
            ON dbo.RecordSiteRelease(legacy_source, legacy_id) WHERE legacy_source IS NOT NULL;
    END;

    IF OBJECT_ID(N'dbo.RecordSiteLegacyImport', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.RecordSiteLegacyImport
        (
            id int NOT NULL CONSTRAINT PK_RecordSiteLegacyImport PRIMARY KEY,
            imported_at datetime2(0) NOT NULL,
            artist_count int NOT NULL,
            album_count int NOT NULL,
            single_count int NOT NULL,
            compilation_count int NOT NULL
        );
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.RecordSiteLegacyImport WHERE id = 1)
    BEGIN
        DECLARE @artists int = 0, @albums int = 0, @singles int = 0, @compilations int = 0;
        IF DB_NAME() NOT LIKE N'MattsRecords[_]User[_]%'
        BEGIN

        IF OBJECT_ID(N'dbo.Artist', N'U') IS NOT NULL
        BEGIN
            EXEC sys.sp_executesql N'
                SET IDENTITY_INSERT dbo.RecordSiteArtist ON;
                INSERT dbo.RecordSiteArtist (id, artist_name)
                SELECT ID, artist_name FROM dbo.Artist;
                SET @count = @@ROWCOUNT;
                SET IDENTITY_INSERT dbo.RecordSiteArtist OFF;',
                N'@count int OUTPUT', @artists OUTPUT;
        END;

        IF OBJECT_ID(N'dbo.Album', N'U') IS NOT NULL
        BEGIN
            EXEC sys.sp_executesql N'
                SET IDENTITY_INSERT dbo.RecordSiteRelease ON;
                INSERT dbo.RecordSiteRelease (id, artist_id, artist_name, album_name, [year], record_kind, legacy_source, legacy_id)
                SELECT source.id, artist.id, source.artist_name, source.album_name, source.[year], 0, ''Album'', source.id
                FROM dbo.Album AS source LEFT JOIN dbo.RecordSiteArtist AS artist ON artist.id = source.artist_id;
                SET @count = @@ROWCOUNT;
                SET IDENTITY_INSERT dbo.RecordSiteRelease OFF;',
                N'@count int OUTPUT', @albums OUTPUT;
        END;

        IF OBJECT_ID(N'dbo.Single', N'U') IS NOT NULL
        BEGIN
            EXEC sys.sp_executesql N'
                INSERT dbo.RecordSiteRelease (artist_id, artist_name, album_name, [year], record_kind, legacy_source, legacy_id)
                SELECT artist.id, source.artist_name, source.single_name, source.[year], 1, ''Single'', source.id
                FROM dbo.Single AS source LEFT JOIN dbo.RecordSiteArtist AS artist ON artist.id = source.artist_id
                ORDER BY source.id;
                SET @count = @@ROWCOUNT;',
                N'@count int OUTPUT', @singles OUTPUT;
        END;

        IF OBJECT_ID(N'dbo.Compilation', N'U') IS NOT NULL
        BEGIN
            EXEC sys.sp_executesql N'
                INSERT dbo.RecordSiteRelease (artist_id, artist_name, album_name, record_kind, legacy_source, legacy_id)
                SELECT artist.id, source.artist_name, source.compilation_name, 3, ''Compilation'', source.id
                FROM dbo.Compilation AS source LEFT JOIN dbo.RecordSiteArtist AS artist ON artist.id = source.artist_id
                ORDER BY source.id;
                SET @count = @@ROWCOUNT;',
                N'@count int OUTPUT', @compilations OUTPUT;
        END;

        END;
        INSERT dbo.RecordSiteLegacyImport (id, imported_at, artist_count, album_count, single_count, compilation_count)
        VALUES (1, SYSUTCDATETIME(), @artists, @albums, @singles, @compilations);
    END;

    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'record_label') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD record_label nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'genre') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD genre nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'country') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD country nvarchar(80) NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'catalog_number') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD catalog_number nvarchar(100) NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'barcode') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD barcode nvarchar(32) NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'pressing_year') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD pressing_year int NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'musicbrainz_id') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD musicbrainz_id uniqueidentifier NULL;

    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'added_at_utc') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD added_at_utc datetime2(7) NULL
            CONSTRAINT DF_RecordSiteRelease_AddedAtUtc DEFAULT (SYSUTCDATETIME());
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'discogs_id') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD discogs_id int NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'front_sleeve_key') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD front_sleeve_key uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'back_sleeve_key') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD back_sleeve_key uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.RecordSiteRelease', N'pressing_reviewed') IS NULL
        ALTER TABLE dbo.RecordSiteRelease ADD pressing_reviewed bit NOT NULL
            CONSTRAINT DF_RecordSiteRelease_PressingReviewed DEFAULT (0);

    IF OBJECT_ID(N'dbo.RecordSiteSleeve', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.RecordSiteSleeve (
            record_id int NOT NULL,
            side int NOT NULL CONSTRAINT CK_RecordSiteSleeve_Side CHECK (side IN (0, 1)),
            revision uniqueidentifier NOT NULL,
            jpeg varbinary(max) NOT NULL,
            CONSTRAINT PK_RecordSiteSleeve PRIMARY KEY (record_id, side),
            CONSTRAINT FK_RecordSiteSleeve_Record FOREIGN KEY (record_id)
                REFERENCES dbo.RecordSiteRelease(id) ON DELETE CASCADE
        );
    END;

    IF DATABASE_PRINCIPAL_ID(N'record_site_runtime') IS NULL
        CREATE ROLE [record_site_runtime] AUTHORIZATION [dbo];

    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.RecordSiteArtist TO [record_site_runtime];
    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.RecordSiteRelease TO [record_site_runtime];

    GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.RecordSiteSleeve TO [record_site_runtime];

    COMMIT TRANSACTION;

    SELECT artist_count AS imported_artists, album_count AS imported_albums, single_count AS imported_singles,
        compilation_count AS imported_compilations, imported_at AS imported_at_utc
    FROM dbo.RecordSiteLegacyImport WHERE id = 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
