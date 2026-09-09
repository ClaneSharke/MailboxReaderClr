# MailboxReaderClr

A SQLCLR stored procedure (`dbo.usp_ReadMailbox`) that reads messages from an
Exchange Online / Office 365 mailbox directly from T-SQL, using the Microsoft
Graph API. Built for a dedicated service-account mailbox, not a full mail
client -- it returns a result set of message metadata + body preview per
call.

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
