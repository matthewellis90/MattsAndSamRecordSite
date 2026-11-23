# Architecture and repository structure

[Back to the README](../README.md)

## Request flow

```text
Browser over HTTPS
    -> Cloudflare published application route
    -> cloudflared on the Windows host
    -> Kestrel on http://127.0.0.1:5782
    -> ASP.NET Core MVC controllers, Razor views, and application services
    -> SQL Server via EF Core and Microsoft.Data.SqlClient

Application -> MusicBrainz and Discogs for pressing lookups
Application -> Namecheap SMTP through MailKit for account mail
Windows scheduled worker -> SQL Server for personal database provisioning
```

## Source layout

| Path | Responsibility |
| --- | --- |
| `MattsRecords.csproj`, `Program.cs` | .NET 10 project, dependency injection, request pipeline, authentication, rate limits, and readiness checks |
| `Controllers/` | Catalog browsing, record and artist editing, sleeve images, collection sharing, registration, and account recovery |
| `Services/RecordRepository.cs` | Catalog reads, writes, and duplicate review during save |
| `Services/CatalogSearch.cs`, `DuplicateRecords.cs` | Search and duplicate matching |
| `Services/PressingLookup.cs`, `DiscogsLookup.cs`, `RecordBarcode.cs` | Pressing lookup, identifiers, and barcode normalization |
| `Services/CollectionSharing.cs`, `SharingController` | Read-only sharing links and access to the linked catalog |
| `Services/PressingBatch.cs` | Optional batch completion and review reports for pressing metadata |
| `Services/CollectionAccounts.cs`, `AccountEmail.cs`, `AccountRecovery.cs`, `AccountMail.cs` | Account database selection, email verification, password resets, and mail delivery |
| `Models/RecordContext.cs`, `Models/` | EF Core table mappings and data models for records, artists, sleeve photos, and forms |
| `Views/`, `wwwroot/` | Razor templates, styling, browser scripts, and bundled ZXing barcode scanner |
| `database/schema.sql` | Catalog tables, indexes, runtime permissions, and one-time legacy import |
| `database/accounts.sql` | Account registry, verification tokens, recovery queue, and stored procedures in the owner database |
| `deploy/` | Install, launch, provision personal databases, connect Cloudflare, and configure account email |

## Catalog and account storage

| SQL object | Contents |
| --- | --- |
| `dbo.RecordSiteArtist` | Artist names linked to releases |
| `dbo.RecordSiteRelease` | Title, artist credit, release type and year, physical media, pressing metadata, identifiers, cover URL, sleeve references, and date added |
| `dbo.RecordSiteSleeve` | Front and rear images stored as JPEG bytes, keyed by record and side |
| `dbo.RecordSiteLegacyImport` | One-time migration marker and imported record counts |
| `dbo.RecordSiteAccount` | Personal usernames, password hashes, verified email addresses, and provisioning state |
| `dbo.RecordSiteEmailChange`, `dbo.RecordSiteRecovery` | Pending email verification and reset requests with hashed tokens and expiry |
| `dbo.RecordSiteCollectionShare` | Revocable sharing token for a personal collection |

The owner database, `MattVinyl`, stores the catalog for the built-in owner sign-in and the account registry. Initial provisioning imports the legacy `Artist`, `Album`, `Single`, and `Compilation` tables into the application catalog where those source tables exist. Subsequent edits use the application tables.

Each personal catalog uses the same catalog schema in a database named `MattsRecords_User_<account GUID without hyphens>`. The authenticated account determines the active database for reads, edits, exports, and image requests. Read-only sharing links allow recipients to browse the specific personal collection their link identifies.

The home controller serves the welcome page before querying the catalog for a signed-out request. Private catalog routes require sign-in. The signed-in collection groups results by media and paginates within each group while applying the current search, filters, sort, and view.

## Account and record workflows

1. Registration captures a username, email address, and password. A 24-hour email link confirms the address. Confirmation queues personal database creation. The provisioner runs once a minute, and the website shows a setup page until the database is ready.
2. **Forgot password** accepts the username. The application queues an email to the verified address. The reset link expires after 30 minutes and can be redeemed once. Changing the password invalidates old sessions. Existing personal users can verify an address in **Manage → Account settings**.
3. Barcode scanning requires camera permission and a secure browser origin. Manual entry remains available. Lookup results can prefill the edit form and cover metadata. The user reviews the details and any duplicate matches before saving.
4. An uploaded front sleeve takes precedence in the cover grid. Remote cover art remains a URL. Uploaded photos are resized, converted to JPEG, and saved in SQL Server without the source image metadata.
