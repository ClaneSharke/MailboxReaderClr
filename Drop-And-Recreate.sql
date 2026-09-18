/* ============================================================================
   Drop-And-Recreate.sql
   Fully removes and re-registers MailboxReaderClr (both the Graph proc,
   usp_ReadMailbox, and the EWS proc, usp_ReadMailbox_EWS) from a clean
   slate, then re-deploys from the current build of the DLL.

   Use this when the registered assembly is stale/wrong (e.g. "Could not
   find Type ... in assembly 'MailboxReaderClr'") and you just want a known
   -good starting point rather than patching in place with ALTER ASSEMBLY.

   EDIT @DllPath below to point at your current build -- e.g. the renamed
   C:\Path\To\MailboxReaderClr101.dll. OPENROWSET can't take a variable, so
   the literal path has to be edited in TWO places (marked below).
   ============================================================================ */

-- 1) Enable CLR (harmless to re-run if already enabled)
EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'clr enabled', 1;
RECONFIGURE;
GO

-- 2) Drop the stored procedures first (DROP ASSEMBLY fails while anything
--    still references it)
IF OBJECT_ID('dbo.usp_ReadMailbox_EWS', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_ReadMailbox_EWS;
GO

IF OBJECT_ID('dbo.usp_ReadMailbox', 'P') IS NOT NULL
    DROP PROCEDURE dbo.usp_ReadMailbox;
GO

-- 3) Drop the assembly itself, if it's registered
IF EXISTS (SELECT 1 FROM sys.assemblies WHERE name = 'MailboxReaderClr')
    DROP ASSEMBLY MailboxReaderClr;
GO

-- (Optional cleanup: drop the old trusted-assembly hash entries so
--  sys.trusted_assemblies doesn't accumulate stale rows. Safe to skip --
--  an untrusted old hash just sits there unused.)
-- SELECT * FROM sys.trusted_assemblies;
-- EXEC sys.sp_drop_trusted_assembly @hash = 0x...;

-- 4) Trust and register the CURRENT build
DECLARE @DllPath NVARCHAR(4000) = N'C:\Path\To\MailboxReaderClr101.dll'; -- <-- EDIT THIS
DECLARE @Hash VARBINARY(64);

SELECT @Hash = HASHBYTES('SHA2_512', BulkColumn)
FROM OPENROWSET(BULK 'C:\Path\To\MailboxReaderClr101.dll', SINGLE_BLOB) AS x; -- <-- EDIT THIS TOO

IF NOT EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE hash = @Hash)
    EXEC sys.sp_add_trusted_assembly @hash = @Hash, @description = N'MailboxReaderClr101.dll';

CREATE ASSEMBLY MailboxReaderClr
FROM 'C:\Path\To\MailboxReaderClr101.dll'   -- <-- EDIT THIS
WITH PERMISSION_SET = EXTERNAL_ACCESS;
GO

-- 5) Recreate both stored procedures against the freshly-registered assembly

-- Graph / Exchange Online proc
CREATE PROCEDURE dbo.usp_ReadMailbox
    @TenantId      NVARCHAR(100),
    @ClientId      NVARCHAR(100),
    @ClientSecret  NVARCHAR(200) = NULL,
    @MailboxUpn    NVARCHAR(320),
    @Password      NVARCHAR(200) = NULL,
    @FolderName    NVARCHAR(100) = 'inbox',
    @Top           INT = 25,
    @UnreadOnly    BIT = 0
AS EXTERNAL NAME MailboxReaderClr.[MailboxReaderClr.MailReader].ReadMailbox;
GO

-- On-premises Exchange (EWS) proc
CREATE PROCEDURE dbo.usp_ReadMailbox_EWS
    @EwsUrl      NVARCHAR(500),
    @MailboxUpn  NVARCHAR(320),
    @Domain      NVARCHAR(100) = NULL,
    @Username    NVARCHAR(100) = NULL,
    @Password    NVARCHAR(200) = NULL,
    @FolderName  NVARCHAR(100) = 'inbox',
    @Top         INT = 25,
    @UnreadOnly  BIT = 0
AS EXTERNAL NAME MailboxReaderClr.[MailboxReaderClr.EwsMailReader].ReadMailboxEws;
GO

-- 6) Sanity check -- confirm both procs exist and are pointed at the new assembly
SELECT
    p.name AS ProcedureName,
    a.name AS AssemblyName,
    am.assembly_class,
    am.assembly_method
FROM sys.procedures p
JOIN sys.assembly_modules am ON am.object_id = p.object_id
JOIN sys.assemblies a ON a.assembly_id = am.assembly_id
WHERE p.name IN ('usp_ReadMailbox', 'usp_ReadMailbox_EWS');

/* ============================================================================
   Example calls
   ============================================================================ */

-- Graph (app-only):
-- EXEC dbo.usp_ReadMailbox
--     @TenantId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
--     @ClientId     = 'xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx',
--     @ClientSecret = 'your-client-secret-value',
--     @MailboxUpn   = 'serviceaccount@yourdomain.com',
--     @Top          = 25;

-- EWS (on-prem, default Windows identity):
-- EXEC dbo.usp_ReadMailbox_EWS
--     @EwsUrl     = 'https://webmail.yourdomain.co.za/EWS/Exchange.asmx',
--     @MailboxUpn = 'serviceaccount@yourdomain.co.za',
--     @Top        = 25;
