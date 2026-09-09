/* ============================================================================
   Deploy.sql
   Registers MailboxReaderClr.dll in SQL Server and creates the stored
   procedure dbo.usp_ReadMailbox.

   Requires SQL Server 2017 CU12+ / SQL Server 2019+ for sys.sp_add_trusted_assembly
   (hash-based trust). On earlier builds you'd need certificate-based signing
   instead -- ask if you're on an older version and I'll swap this section out.

   Edit @DllPath below to wherever MailboxReaderClr.dll ends up after you
   build the project (bin\Release\net472\MailboxReaderClr.dll or similar).
   ============================================================================ */

-- 1) Enable CLR (leave 'clr strict security' ON -- default and recommended)
EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'clr enabled', 1;
RECONFIGURE;
GO

-- 2) Trust the assembly by its file hash (no certificate signing needed)
DECLARE @DllPath NVARCHAR(4000) = N'C:\Path\To\MailboxReaderClr.dll'; -- <-- EDIT THIS
DECLARE @Hash VARBINARY(64);

SELECT @Hash = HASHBYTES('SHA2_512', BulkColumn)
FROM OPENROWSET(BULK 'C:\Path\To\MailboxReaderClr.dll', SINGLE_BLOB) AS x; -- <-- EDIT THIS TOO (OPENROWSET can't take a variable path)

EXEC sys.sp_add_trusted_assembly @hash = @Hash, @description = N'MailboxReaderClr.dll';
GO

-- 3) Register the assembly. EXTERNAL_ACCESS is required for outbound HTTP calls.
CREATE ASSEMBLY MailboxReaderClr
FROM 'C:\Path\To\MailboxReaderClr.dll'   -- <-- EDIT THIS
WITH PERMISSION_SET = EXTERNAL_ACCESS;
GO

-- 4) Create the stored procedure wrapper
CREATE PROCEDURE dbo.usp_ReadMailbox
    @TenantId      NVARCHAR(100),
    @ClientId      NVARCHAR(100),
    @ClientSecret  NVARCHAR(200),
    @MailboxUpn    NVARCHAR(320),
    @FolderName    NVARCHAR(100) = 'inbox',
    @Top           INT = 25,
    @UnreadOnly    BIT = 0
AS EXTERNAL NAME MailboxReaderClr.[MailboxReaderClr.MailReader].ReadMailbox;
GO

/* ============================================================================
   Example usage
   ============================================================================ */

-- Just view results:
EXEC dbo.usp_ReadMailbox
    @TenantId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
    @ClientId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
    @ClientSecret = 'your-client-secret-value',
    @MailboxUpn   = 'serviceaccount@yourdomain.com',
    @FolderName   = 'inbox',
    @Top          = 25,
    @UnreadOnly   = 1;

-- Land results in a staging table:
/*
CREATE TABLE dbo.MailboxMessages (
    MessageId         NVARCHAR(200)  PRIMARY KEY,
    Subject           NVARCHAR(1000),
    FromAddress       NVARCHAR(320),
    FromName          NVARCHAR(200),
    ReceivedDateTime  DATETIME2,
    IsRead            BIT,
    HasAttachments    BIT,
    BodyPreview       NVARCHAR(4000),
    LoadedAtUtc       DATETIME2 DEFAULT SYSUTCDATETIME()
);

INSERT INTO dbo.MailboxMessages (MessageId, Subject, FromAddress, FromName, ReceivedDateTime, IsRead, HasAttachments, BodyPreview)
EXEC dbo.usp_ReadMailbox
    @TenantId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
    @ClientId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
    @ClientSecret = 'your-client-secret-value',
    @MailboxUpn   = 'serviceaccount@yourdomain.com';
*/

/* ============================================================================
   To remove everything later:
   DROP PROCEDURE dbo.usp_ReadMailbox;
   DROP ASSEMBLY MailboxReaderClr;
   EXEC sys.sp_drop_trusted_assembly @hash = <the same hash used above>;
   ============================================================================ */
