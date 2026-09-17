# MailboxReaderClr

SQLCLR stored procedures that read messages from a mailbox directly from
T-SQL, for a dedicated service-account mailbox rather than as a full mail
client. Two separate procs, because the protocol and auth model genuinely
differ by mailbox location:

| Proc | Mailbox is on | Protocol | Auth |
|---|---|---|---|
| `dbo.usp_ReadMailbox` | Exchange Online / Microsoft 365 | Microsoft Graph (REST/JSON) | Azure AD OAuth2 (app-only or ROPC) |
| `dbo.usp_ReadMailbox_EWS` | On-premises Exchange Server | EWS (SOAP/XML) | Windows (NTLM/Kerberos) + Exchange Impersonation |

Both live in the same `MailboxReaderClr.dll` -- `MailReader.cs` for Graph,
`EwsMailReader.cs` for EWS -- so you only register one assembly in SQL
Server either way and just create whichever proc(s) you need. The rest of
this section covers the Graph/Exchange Online proc; jump to
[**On-premises Exchange (EWS)**](#on-premises-exchange-ews) for the on-prem
one.

Two auth modes, chosen by whether you pass `@Password`:

| Mode | Trigger | Notes |
|---|---|---|
| **App-only** (default, recommended) | `@Password` omitted/NULL | `client_credentials` grant using Tenant/Client/ClientSecret. Requires an Application permission (`Mail.Read`). |
| **Delegated password (ROPC)** | `@Password` supplied | Resource-owner password grant using the mailbox's own username + password. Requires a Delegated permission (`Mail.Read`) and a mailbox excluded from MFA/Conditional Access. |

App-only is the one to use if you can. ROPC exists because it's sometimes the
only option available (e.g. no rights to set up an Application Access
Policy, or a simpler dev/test setup) -- but Microsoft explicitly discourages
it for new integrations: it can't satisfy MFA or Conditional Access
challenges, it's on Microsoft's list of legacy auth flows being phased out
over time, and it means the mailbox's actual password is stored/passed
around by whatever calls this proc. Use it deliberately, not by default.

No third-party NuGet packages are used, on purpose: SQL Server has to have
every referenced assembly individually cataloged, and packages like the
Microsoft.Graph SDK or MSAL pull in a long dependency chain that's genuinely
painful to register under SQLCLR's strict security model. Instead the code
makes two plain HTTP calls (token request + Graph REST call) with
`HttpWebRequest`, which ships in .NET Framework itself -- so the only
assembly you need to register in SQL Server is your own compiled DLL.

## 1. Azure AD app registration (one-time, in Entra admin center / Azure Portal)

### App-only mode (default -- do this unless you have a specific reason to use ROPC)

1. **App registrations > New registration.** Name it something like
   `SQL-MailboxReader`. Single tenant is fine.
2. **API permissions > Add a permission > Microsoft Graph > Application
   permissions.** Add `Mail.Read` (full message access, including body) or
   `Mail.ReadBasic.All` if you only need subject/sender/metadata, not body
   content. Then **Grant admin consent**.
3. **Restrict the permission to just the service account mailbox.** By
   default, an application permission like `Mail.Read` grants access to
   *every* mailbox in the tenant -- you don't want that for a service
   account. Lock it down with an Exchange Online PowerShell Application
   Access Policy:
   ```powershell
   Connect-ExchangeOnline
   New-ApplicationAccessPolicy `
     -AppId <client-id-from-step-1> `
     -PolicyScopeGroupId serviceaccount@yourdomain.com `
     -AccessRight RestrictAccess `
     -Description "Restrict SQL-MailboxReader app to the service account mailbox only"
   Test-ApplicationAccessPolicy -AppId <client-id> -Identity serviceaccount@yourdomain.com
   ```
   `Test-ApplicationAccessPolicy` should report access as granted only for
   that mailbox.
4. **Certificates & secrets > New client secret.** Copy the secret value
   immediately -- you won't be able to see it again. This is `@ClientSecret`.
5. Note down from the app's **Overview** page:
   - Directory (tenant) ID -> `@TenantId`
   - Application (client) ID -> `@ClientId`

### Delegated password (ROPC) mode -- only if you're deliberately using `@Password`

1. Same **App registrations > New registration** as above (can be the same
   app or a separate one).
2. **API permissions > Add a permission > Microsoft Graph > Delegated
   permissions.** Add `Mail.Read`. **Grant admin consent** (required since
   there's no interactive user to consent).
3. **Authentication > Advanced settings > "Allow public client flows"** ->
   Yes. (Skip this if you're deliberately using a confidential client by
   also supplying `@ClientSecret` alongside `@Password`.)
4. The service account's password is `@Password`; the account itself is
   `@MailboxUpn` (used as both mailbox and username in this mode).
5. **The service account must be excluded from MFA and any Conditional
   Access policy that would challenge it** -- ROPC has no way to respond to
   an MFA prompt or device-compliance check, and the token request will
   simply fail (often with a vague error) if either applies. Also note some
   tenants have "Security Defaults" enabled, which blocks ROPC entirely
   tenant-wide.

## 2. Build the DLL

Open `MailboxReaderClr.csproj` in Visual Studio or VS Code, or from a
terminal in this folder:

```
dotnet build -c Release
```

Output lands at `bin\Release\net472\MailboxReaderClr.dll`.

If the build fails complaining it can't find the .NET Framework 4.7.2
targeting pack, install it via Visual Studio Installer -> Individual
Components -> ".NET Framework 4.7.2 targeting pack" (SQLCLR runs on .NET
Framework only, even on current SQL Server versions -- it doesn't support
.NET Core/5+ assemblies).

## 3. Deploy to SQL Server

Open `Deploy.sql`, edit the two `@DllPath` / `OPENROWSET` lines to point at
your built DLL, and run it against the target database. It:

1. Enables CLR (`clr enabled`), leaving `clr strict security` on.
2. Trusts the assembly by its file hash via `sys.sp_add_trusted_assembly`
   (SQL Server 2017 CU12+ / 2019+ -- avoids the older, messier
   certificate-signing or `TRUSTWORTHY ON` approaches, which you should
   avoid as a security anti-pattern).
3. Registers the assembly with `PERMISSION_SET = EXTERNAL_ACCESS` (needed
   for outbound HTTPS).
4. Creates `dbo.usp_ReadMailbox`.

## 4. Call it

App-only (default):
```sql
EXEC dbo.usp_ReadMailbox
    @TenantId     = '...',
    @ClientId     = '...',
    @ClientSecret = '...',
    @MailboxUpn   = 'serviceaccount@yourdomain.com',
    @FolderName   = 'inbox',   -- optional, default 'inbox'
    @Top          = 25,       -- optional, default 25, max 999
    @UnreadOnly   = 1;         -- optional, default 0
```

Delegated password / ROPC (only if you've deliberately set this up per the
section above):
```sql
EXEC dbo.usp_ReadMailbox
    @TenantId   = '...',
    @ClientId   = '...',
    @MailboxUpn = 'serviceaccount@yourdomain.com',
    @Password   = 'the-mailbox-password',
    @Top        = 25;
```

Returns one row per message: `MessageId, Subject, FromAddress, FromName,
ReceivedDateTime, IsRead, HasAttachments, BodyPreview`. See `Deploy.sql` for
an example of inserting the result set straight into a staging table.

## On-premises Exchange (EWS)

`dbo.usp_ReadMailbox_EWS` reads a mailbox on your own on-premises Exchange
Server (2013+) via EWS -- no internet access, no Azure AD, no Graph. It
POSTs a SOAP `FindItem` request to your Exchange server's own EWS endpoint
and authenticates with Windows credentials instead of an OAuth2 token.

> **Built but not field-verified.** This was written from EWS's documented
> `FindItem` schema, but I don't have a live on-premises Exchange server to
> test it against in the environment that built this. Treat the SOAP
> request shape, field names, and `RequestServerVersion` as a strong first
> draft -- validate against your actual server (see Troubleshooting below)
> rather than assuming it's correct out of the box, the way the Graph proc
> has been. It does compile cleanly (`dotnet build -c Release`, 0 warnings /
> 0 errors) and its only assembly references are `mscorlib`, `System`,
> `System.Data`, and `System.Xml` -- see the `System.Xml` trust note in
> step 4 below.

### 1. Grant impersonation rights (Exchange Management Shell, on your Exchange server)

Whichever Windows account calls this proc -- the SQL Server service
account's own identity, or an explicit account you pass via
`@Domain`/`@Username`/`@Password` -- needs the **ApplicationImpersonation**
management role, scoped to just the target mailbox (the on-prem equivalent
of the Application Access Policy used for the Graph proc):

```powershell
New-ManagementScope -Name "MailboxReaderScope" `
  -RecipientRestrictionFilter "PrimarySmtpAddress -eq 'serviceaccount@yourdomain.local'"

New-ManagementRoleAssignment -Name "MailboxReaderImpersonation" `
  -Role "ApplicationImpersonation" `
  -User "YOURDOMAIN\svc-mailreader" `
  -CustomRecipientWriteScope "MailboxReaderScope"
```

Replace `YOURDOMAIN\svc-mailreader` with whichever account is actually
making the call -- the SQL Server service account if you're using
`UseDefaultCredentials`, or the account named in `@Username` otherwise.

### 2. Find your EWS URL

Usually `https://<your-exchange-server>/EWS/Exchange.asmx`. If you're not
sure, Exchange Management Shell can confirm it: `Get-WebServicesVirtualDirectory | fl Name,*Url*`.

### 3. TLS certificate

If your Exchange server uses an internal-CA or self-signed certificate,
`HttpWebRequest` will refuse it by default. Install your internal CA's root
certificate into the SQL Server machine's trusted root store -- don't
disable certificate validation in code to work around it.

### 4. Build and deploy

Same DLL, same build step as above (`dotnet build -c Release`). Run
`Deploy-EWS.sql` -- if you've already run `Deploy.sql` for the Graph proc,
the assembly is already registered and `Deploy-EWS.sql` just adds the new
`dbo.usp_ReadMailbox_EWS` proc; otherwise it registers the assembly too.

**A note on `System.Xml`.** The EWS code uses `System.Xml.XmlDocument` to
build and parse the SOAP request/response, so the built DLL now references
`System.Xml` in addition to `mscorlib`/`System`/`System.Data`.
`System.Xml` is on Microsoft's own documented list of assemblies SQL
Server's CLR host supports (the same list `System.Data` is on) -- unlike
`System.Web.Extensions`, which caused the v1.0.0 bug described in
Troubleshooting below and is *not* on that list. So `System.Xml` should
already be trusted without any extra step, the same way `System.Data` is.
That said, this hasn't been confirmed against a live SQL Server in this
project the way the `System.Web.Extensions` failure was. If you deploy
`Deploy-EWS.sql` and hit a **Msg 10301** error naming `system.xml`, it
means your SQL Server instance doesn't auto-trust it either -- register it
exactly like the DLL itself is registered, by trusting its hash:
```sql
DECLARE @Hash VARBINARY(64) = HASHBYTES('SHA2_512',
    (SELECT content FROM sys.assembly_files WHERE assembly_id =
        (SELECT assembly_id FROM sys.assemblies WHERE name = 'System.Xml')));
-- or, simpler: locate System.Xml.dll under
-- C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ and trust that file's
-- hash with sys.sp_add_trusted_assembly, the same way Deploy-EWS.sql
-- trusts MailboxReaderClr.dll, then CREATE ASSEMBLY [System.Xml] FROM
-- that path WITH PERMISSION_SET = SAFE.
```
Open an issue with the exact error text if this happens -- it'll confirm
one way or the other and the README can drop the hedge.

### 5. Call it

Using the SQL Server service account's own Windows identity (omit
`@Domain`/`@Username`/`@Password`):
```sql
EXEC dbo.usp_ReadMailbox_EWS
    @EwsUrl     = 'https://mail.yourdomain.local/EWS/Exchange.asmx',
    @MailboxUpn = 'serviceaccount@yourdomain.local',
    @Top        = 25;
```

With explicit Windows credentials instead:
```sql
EXEC dbo.usp_ReadMailbox_EWS
    @EwsUrl     = 'https://mail.yourdomain.local/EWS/Exchange.asmx',
    @MailboxUpn = 'serviceaccount@yourdomain.local',
    @Domain     = 'YOURDOMAIN',
    @Username   = 'svc-mailreader',
    @Password   = 'the-account-password',
    @Top        = 25;
```

Returns the same shape as the Graph proc: `MessageId, Subject, FromAddress,
FromName, ReceivedDateTime, IsRead, HasAttachments, BodyPreview`.

### EWS troubleshooting

- **401 Unauthorized** -- the connecting account either doesn't have
  `ApplicationImpersonation` scoped to the target mailbox (step 1), or the
  credentials themselves are wrong. Test the same credentials against EWS
  outside of SQL Server first if possible (e.g. a small standalone .NET
  console app) to isolate whether it's an auth problem or a SQL Server
  problem.
- **A SOAP fault, or an empty result set with no error** -- the proc
  surfaces both SOAP-level faults and EWS-level `ResponseClass="Error"`
  messages as thrown exceptions, so read the actual error text; it usually
  names the problem directly (bad folder id, impersonation not permitted,
  schema version too old for a requested field, etc.).
- **Certificate errors** -- see the TLS note in step 3.
- **Nothing matches your server's exact behavior** -- since this hasn't
  been run against a live server, the most likely failure point is a
  mismatch between the exact `FindItem` request shape here and what your
  specific Exchange version/CU expects. Capture the raw SOAP response
  (temporarily have the proc surface it, or capture the request with a tool
  like Fiddler between a test client and the server) and we can adjust the
  request in `EwsMailReader.cs` from there.

## Security notes worth acting on

- **If you use `@Password` (ROPC mode), treat it exactly like `@ClientSecret`
  below** -- it's the mailbox's actual account password flowing through SQL
  parameters and, potentially, job history/logs. Prefer app-only mode unless
  you have a specific reason not to.
- **Don't hardcode `@ClientSecret` in scripts or jobs.** For a first pass
  it's fine to pass it as a parameter, but for anything scheduled, store it
  encrypted (e.g. a certificate-encrypted value in a config table, or pull it
  from Windows Credential Manager / Azure Key Vault via a small wrapper) and
  decrypt only inside the proc call.
- **Rotate the client secret periodically** and update wherever it's stored.
  Consider a certificate credential instead of a client secret for the app
  registration if you want something longer-lived and easier to rotate via
  cert renewal.
- **Keep the Application Access Policy in place.** Without it, the app
  registration's `Mail.Read` permission can read *any* mailbox in the
  tenant, not just the service account -- that's the difference between a
  scoped integration and a standing security risk.
- `PERMISSION_SET = EXTERNAL_ACCESS` is required (not `SAFE`) because the
  code makes outbound HTTP calls. This is expected and fine, but means the
  DLL runs with more trust than a pure-`SAFE` assembly -- keep the source
  under version control and reviewed like any other code with network
  access from inside SQL Server.

## Troubleshooting

**`Msg 10301 ... references assembly 'system.web.extensions' ... which is not
present in the current database`** -- this was a bug in v1.0.0: it used
`JavaScriptSerializer` (from `System.Web.Extensions`), which SQL Server does
*not* auto-trust the way it does `mscorlib`/`System`/`System.Data`, so it
needed its own separate `CREATE ASSEMBLY` registration. Fixed in v1.0.1 by
replacing it with a small dependency-free JSON parser (`JsonMini.cs`) --
`MailboxReaderClr.dll` now references only `mscorlib`, `System`, and
`System.Data`, all of which SQL Server trusts by default. If you hit this
error, drop the old assembly/trusted-assembly entry and redeploy with the
current release.

## Uninstall

```sql
DROP PROCEDURE dbo.usp_ReadMailbox;
DROP ASSEMBLY MailboxReaderClr;
EXEC sys.sp_drop_trusted_assembly @hash = <hash used at deploy time>;
```
