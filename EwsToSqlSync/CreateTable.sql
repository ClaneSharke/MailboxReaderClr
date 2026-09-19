/* ============================================================================
   CreateTable.sql
   Destination table for EwsToSqlSync.exe. MessageId is the primary key so the
   app's "insert if not already present" check (and this table's own PK
   constraint, as a second line of defense) prevent duplicate rows when the
   scheduled task re-reads messages that were already synced on a prior run.
   ============================================================================ */

CREATE TABLE dbo.MailboxMessages (
    MessageId         NVARCHAR(400)  NOT NULL PRIMARY KEY,
    Subject           NVARCHAR(1000) NULL,
    FromAddress       NVARCHAR(320)  NULL,
    FromName          NVARCHAR(200)  NULL,
    ReceivedDateTime  DATETIME2      NULL,
    IsRead            BIT            NULL,
    HasAttachments    BIT            NULL,
    BodyPreview       NVARCHAR(4000) NULL,
    LoadedAtUtc       DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- Grant the sync account just what it needs (adjust the login name):
-- GRANT SELECT, INSERT ON dbo.MailboxMessages TO svc_mailsync;
