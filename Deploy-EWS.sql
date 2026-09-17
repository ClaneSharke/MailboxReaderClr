/* ============================================================================
   Deploy-EWS.sql
   Registers the on-premises Exchange (EWS) mailbox reader alongside the
   existing Graph-based proc, and creates dbo.usp_ReadMailbox_EWS.

   Uses the same DLL as Deploy.sql (MailboxReaderClr.dll) -- MailReader (Graph)
   and EwsMailReader (EWS) are two classes in the same assembly. If you've
   already run Deploy.sql, the assembly is already registered: skip straight
   to the CREATE PROCEDURE step below. If not, run steps 1-2 first (same as
   Deploy.sql).

   Requires SQL Server 2017 CU12+ / SQL Server 2019+ (sys.sp_add_trusted_assembly).
   Edit @DllPath to wherever MailboxReaderClr.dll ends up after building.
   ============================================================================ */

-- 1) Enable CLR (skip if already done)
EXEC sp_configure 'show advanced options', 1;
RECONFIGURE;
EXEC sp_configure 'clr enabled', 1;
RECONFIGURE;
GO

-- 2) Trust + register the assembly (skip if already done via Deploy.sql)
DECLARE @DllPath NVARCHAR(4000) = N'C:\Path\To\MailboxReaderClr.dll'; -- <-- EDIT THIS
DECLARE @Hash VARBINARY(64);

SELECT @Hash = HASHBYTES('SHA2_512', BulkColumn)
FROM OPENROWSET(BULK 'C:\Path\To\MailboxReaderClr.dll', SINGLE_BLOB) AS x; -- <-- EDIT THIS TOO

IF NOT EXISTS (SELECT 1 FROM sys.trusted_assemblies WHERE hash = @Hash)
    EXEC sys.sp_add_trusted_assembly @hash = @Hash, @description = N'MailboxReaderClr.dll';

IF NOT EXISTS (SELECT 1 FROM sys.assemblies WHERE name = 'MailboxReaderClr')
    EXEC('CREATE ASSEMBLY MailboxReaderClr FROM ''' + @DllPath + ''' WITH PERMISSION_SET = EXTERNAL_ACCESS');
GO

-- 3) Create the EWS stored procedure wrapper
CREATE PROCEDURE dbo.usp_ReadMailbox_EWS
    @EwsUrl      NVARCHAR(500),   -- e.g. https://mail.yourdomain.local/EWS/Exchange.asmx
    @MailboxUpn  NVARCHAR(320),   -- mailbox to read, via Exchange Impersonation
    @Domain      NVARCHAR(100) = NULL,  -- omit all three of Domain/Username/Password to use
    @Username    NVARCHAR(100) = NULL,  -- the SQL Server service account's own Windows identity
    @Password    NVARCHAR(200) = NULL,
    @FolderName  NVARCHAR(100) = 'inbox',
    @Top         INT = 25,
    @UnreadOnly  BIT = 0
AS EXTERNAL NAME MailboxReaderClr.[MailboxReaderClr.EwsMailReader].ReadMailboxEws;
GO

/* ============================================================================
   Example usage
   ============================================================================ */

-- Using the SQL Server service account's own Windows identity (no Domain/Username/Password):
EXEC dbo.usp_ReadMailbox_EWS
    @EwsUrl     = 'https://mail.yourdomain.local/EWS/Exchange.asmx',
    @MailboxUpn = 'serviceaccount@yourdomain.local',
    @Top        = 25;

-- Using explicit Windows credentials instead:
EXEC dbo.usp_ReadMailbox_EWS
    @EwsUrl     = 'https://mail.yourdomain.local/EWS/Exchange.asmx',
    @MailboxUpn = 'serviceaccount@yourdomain.local',
    @Domain     = 'YOURDOMAIN',
    @Username   = 'svc-mailreader',
    @Password   = 'the-account-password',
    @Top        = 25;

/* ============================================================================
   To remove just this proc (leaving the assembly and Graph proc in place):
   DROP PROCEDURE dbo.usp_ReadMailbox_EWS;
   ============================================================================ */
