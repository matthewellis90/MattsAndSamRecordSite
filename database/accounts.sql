SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.RecordSiteAccount', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecordSiteAccount (
        id uniqueidentifier NOT NULL CONSTRAINT PK_RecordSiteAccount PRIMARY KEY,
        username nvarchar(32) COLLATE Latin1_General_100_CI_AS_SC NOT NULL
            CONSTRAINT UQ_RecordSiteAccount_Username UNIQUE,
        password_hash nvarchar(512) NOT NULL,
        state tinyint NOT NULL CONSTRAINT DF_RecordSiteAccount_State DEFAULT (0)
            CONSTRAINT CK_RecordSiteAccount_State CHECK (state IN (0, 1, 2, 3)),
        attempts int NOT NULL CONSTRAINT DF_RecordSiteAccount_Attempts DEFAULT (0),
        last_attempt_utc datetime2(0) NULL,
        created_utc datetime2(0) NOT NULL CONSTRAINT DF_RecordSiteAccount_Created DEFAULT (SYSUTCDATETIME())
    );
END;
GO

GRANT SELECT ON OBJECT::dbo.RecordSiteAccount TO [record_site_runtime];
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints
    WHERE parent_object_id = OBJECT_ID(N'dbo.RecordSiteAccount')
        AND name = N'CK_RecordSiteAccount_State' AND definition NOT LIKE '%(3)%')
BEGIN
    BEGIN TRANSACTION;
    ALTER TABLE dbo.RecordSiteAccount DROP CONSTRAINT CK_RecordSiteAccount_State;
    ALTER TABLE dbo.RecordSiteAccount WITH CHECK
        ADD CONSTRAINT CK_RecordSiteAccount_State CHECK (state IN (0, 1, 2, 3));
    COMMIT TRANSACTION;
END;
GO

IF OBJECT_ID(N'dbo.RecordSiteRecovery', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecordSiteRecovery (
        account_id uniqueidentifier NOT NULL CONSTRAINT PK_RecordSiteRecovery PRIMARY KEY
            CONSTRAINT FK_RecordSiteRecovery_Account REFERENCES dbo.RecordSiteAccount(id) ON DELETE CASCADE,
        requested_utc datetime2(0) NOT NULL,
        token_hash binary(32) NULL,
        expires_utc datetime2(0) NULL
    );
END;
IF COL_LENGTH(N'dbo.RecordSiteAccount', N'verified_email') IS NULL
    ALTER TABLE dbo.RecordSiteAccount ADD verified_email nvarchar(254) NULL;
IF COL_LENGTH(N'dbo.RecordSiteRecovery', N'mail_due_utc') IS NULL
    ALTER TABLE dbo.RecordSiteRecovery ADD mail_due_utc datetime2(0) NULL;
IF COL_LENGTH(N'dbo.RecordSiteRecovery', N'mail_attempts') IS NULL
    ALTER TABLE dbo.RecordSiteRecovery ADD mail_attempts int NOT NULL CONSTRAINT DF_RecordSiteRecovery_MailAttempts DEFAULT (0);

IF OBJECT_ID(N'dbo.RecordSiteEmailChange', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecordSiteEmailChange (
        account_id uniqueidentifier NOT NULL CONSTRAINT PK_RecordSiteEmailChange PRIMARY KEY
            CONSTRAINT FK_RecordSiteEmailChange_Account REFERENCES dbo.RecordSiteAccount(id) ON DELETE CASCADE,
        address nvarchar(254) NOT NULL,
        token_hash binary(32) NOT NULL,
        requested_utc datetime2(0) NOT NULL,
        expires_utc datetime2(0) NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dbo.RecordSiteCollectionShare', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RecordSiteCollectionShare (
        account_id uniqueidentifier NOT NULL CONSTRAINT PK_RecordSiteCollectionShare PRIMARY KEY
            CONSTRAINT FK_RecordSiteCollectionShare_Account REFERENCES dbo.RecordSiteAccount(id) ON DELETE CASCADE,
        token uniqueidentifier NOT NULL CONSTRAINT UQ_RecordSiteCollectionShare_Token UNIQUE
    );
END;
GRANT SELECT, INSERT, UPDATE, DELETE ON OBJECT::dbo.RecordSiteCollectionShare TO [record_site_runtime];
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteLockAccountChanges
AS
BEGIN
    DECLARE @lock int;
    EXEC @lock = sys.sp_getapplock @Resource = N'MattsRecords.AccountChanges', @LockMode = 'Exclusive',
        @LockOwner = 'Transaction', @LockTimeout = 5000;
    IF @lock < 0
        THROW 50004, 'Account changes are busy. Try again.', 1;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteRegisterWithEmail
    @id uniqueidentifier, @username nvarchar(32), @password_hash nvarchar(512),
    @address nvarchar(254), @token_hash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;
    DECLARE @now datetime2(0) = SYSUTCDATETIME();

    DELETE a FROM dbo.RecordSiteAccount AS a
    WHERE a.state = 3 AND a.created_utc < DATEADD(day, -7, @now)
        AND NOT EXISTS (SELECT 1 FROM dbo.RecordSiteEmailChange AS p
            WHERE p.account_id = a.id AND p.expires_utc > @now);

    DECLARE @result int = 0;
    IF EXISTS (SELECT 1 FROM dbo.RecordSiteAccount WHERE username = @username)
        SET @result = 1;
    ELSE IF (SELECT COUNT(*) FROM dbo.RecordSiteAccount) >= 20
        SET @result = 2;
    ELSE
    BEGIN
        INSERT dbo.RecordSiteAccount (id, username, password_hash, state) VALUES (@id, @username, @password_hash, 3);
        INSERT dbo.RecordSiteEmailChange (account_id, address, token_hash, requested_utc, expires_utc)
        VALUES (@id, @address, @token_hash, @now, DATEADD(hour, 24, @now));
    END;

    COMMIT TRANSACTION;
    SELECT @result;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteRequestRecovery @username nvarchar(32), @send_email bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @id uniqueidentifier = (SELECT id FROM dbo.RecordSiteAccount WHERE username = @username);
    IF @id IS NULL
        RETURN;

    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;
    DECLARE @now datetime2(0) = SYSUTCDATETIME();
    DECLARE @due datetime2(0) = CASE WHEN @send_email = 1 AND
        EXISTS (SELECT 1 FROM dbo.RecordSiteAccount WHERE id = @id AND verified_email IS NOT NULL) THEN @now END;
    IF NOT EXISTS (SELECT 1 FROM dbo.RecordSiteRecovery WITH (UPDLOCK, HOLDLOCK) WHERE account_id = @id)
        INSERT dbo.RecordSiteRecovery (account_id, requested_utc, mail_due_utc) VALUES (@id, @now, @due);
    ELSE IF @due IS NOT NULL
        UPDATE dbo.RecordSiteRecovery
        SET requested_utc = @now, mail_due_utc = @due, mail_attempts = 0, token_hash = NULL, expires_utc = NULL
        WHERE account_id = @id AND requested_utc <= DATEADD(minute, -5, @now) AND mail_due_utc IS NULL
            AND (expires_utc IS NULL OR expires_utc <= @now);
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteIssueRecovery @account_id uniqueidentifier, @token_hash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.RecordSiteRecovery
    SET token_hash = @token_hash, expires_utc = DATEADD(minute, 30, SYSUTCDATETIME()), mail_due_utc = NULL
    WHERE account_id = @account_id;
    SELECT @@ROWCOUNT;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteRedeemRecovery @token_hash binary(32), @password_hash nvarchar(512)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @consumed TABLE (account_id uniqueidentifier PRIMARY KEY);
    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;

    DELETE FROM dbo.RecordSiteRecovery
    OUTPUT deleted.account_id INTO @consumed (account_id)
    WHERE token_hash = @token_hash AND expires_utc > SYSUTCDATETIME();

    UPDATE a
    SET password_hash = @password_hash
    FROM dbo.RecordSiteAccount AS a
    JOIN @consumed AS used ON used.account_id = a.id;

    DECLARE @updated int = @@ROWCOUNT;
    DELETE p FROM dbo.RecordSiteEmailChange AS p JOIN @consumed AS used ON used.account_id = p.account_id;
    COMMIT TRANSACTION;
    SELECT @updated;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteBeginEmailChange
    @account_id uniqueidentifier, @password_hash nvarchar(512), @address nvarchar(254), @token_hash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;
    DECLARE @result int = 0;
    DECLARE @now datetime2(0) = SYSUTCDATETIME();
    IF EXISTS (SELECT 1 FROM dbo.RecordSiteAccount WHERE id = @account_id AND password_hash COLLATE Latin1_General_100_BIN2 = @password_hash)
        AND NOT EXISTS (SELECT 1 FROM dbo.RecordSiteEmailChange WHERE account_id = @account_id
            AND requested_utc > DATEADD(minute, -1, @now))
    BEGIN
        DELETE FROM dbo.RecordSiteEmailChange WHERE account_id = @account_id;
        INSERT dbo.RecordSiteEmailChange (account_id, address, token_hash, requested_utc, expires_utc)
        VALUES (@account_id, @address, @token_hash, @now, DATEADD(hour, 24, @now));
        SET @result = 1;
    END;
    COMMIT TRANSACTION;
    SELECT @result;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteConfirmEmail @token_hash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @confirmed TABLE (account_id uniqueidentifier PRIMARY KEY, address nvarchar(254));
    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;
    DELETE FROM dbo.RecordSiteEmailChange
    OUTPUT deleted.account_id, deleted.address INTO @confirmed (account_id, address)
    WHERE token_hash = @token_hash AND expires_utc > SYSUTCDATETIME();

    UPDATE a SET verified_email = c.address, state = CASE WHEN a.state = 3 THEN 0 ELSE a.state END
    FROM dbo.RecordSiteAccount AS a JOIN @confirmed AS c ON c.account_id = a.id;
    DECLARE @updated int = @@ROWCOUNT;
    DELETE r FROM dbo.RecordSiteRecovery AS r JOIN @confirmed AS c ON c.account_id = r.account_id;
    COMMIT TRANSACTION;
    SELECT @updated;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteClaimRecoveryMail @token_hash binary(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @now datetime2(0) = SYSUTCDATETIME();
    BEGIN TRANSACTION;
    EXEC dbo.RecordSiteLockAccountChanges;

    UPDATE dbo.RecordSiteRecovery SET mail_due_utc = NULL, token_hash = NULL, expires_utc = NULL
    WHERE mail_due_utc <= @now AND mail_attempts >= 3;

    DECLARE @id uniqueidentifier;
    SELECT TOP (1) @id = r.account_id
    FROM dbo.RecordSiteRecovery AS r JOIN dbo.RecordSiteAccount AS a ON a.id = r.account_id
    WHERE r.mail_due_utc <= @now AND a.verified_email IS NOT NULL
    ORDER BY r.mail_due_utc, r.account_id;

    UPDATE dbo.RecordSiteRecovery
    SET token_hash = @token_hash, expires_utc = DATEADD(minute, 30, @now),
        mail_due_utc = DATEADD(minute, 2, @now), mail_attempts = mail_attempts + 1
    WHERE account_id = @id;
    DECLARE @address nvarchar(254) = (SELECT verified_email FROM dbo.RecordSiteAccount WHERE id = @id);
    COMMIT TRANSACTION;
    SELECT @address;
END;
GO

CREATE OR ALTER PROCEDURE dbo.RecordSiteCompleteRecoveryMail @token_hash binary(32), @delivered bit
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.RecordSiteRecovery
    SET mail_due_utc = CASE WHEN @delivered = 1 OR mail_attempts >= 3 THEN NULL ELSE mail_due_utc END,
        token_hash = CASE WHEN @delivered = 1 THEN token_hash END,
        expires_utc = CASE WHEN @delivered = 1 THEN expires_utc END
    WHERE token_hash = @token_hash;
END;
GO

GRANT SELECT ON OBJECT::dbo.RecordSiteRecovery TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteRegisterWithEmail TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteRequestRecovery TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteIssueRecovery TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteRedeemRecovery TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteBeginEmailChange TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteConfirmEmail TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteClaimRecoveryMail TO [record_site_runtime];
GRANT EXECUTE ON OBJECT::dbo.RecordSiteCompleteRecoveryMail TO [record_site_runtime];
GRANT SELECT ON OBJECT::dbo.RecordSiteEmailChange TO [record_site_runtime];
GO
