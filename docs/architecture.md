# Architecture and repository structure

[Back to the README](../README.md)

## Request flow

Browser → Cloudflare HTTPS route → cloudflared on Windows → Kestrel on Windows loopback → ASP.NET Core MVC → SQL Server.

MusicBrainz and Discogs provide pressing lookups. MailKit sends account email through Namecheap SMTP. A separate Windows scheduled task provisions personal databases.

## Restored source layout

| Path | Responsibility |
| --- | --- |
| MattsRecords.csproj and Program.cs | .NET 10 project, startup, authentication, rate limits, and health checks |
| Controllers/ | Catalog, record and artist editing, sleeves, registration, and account recovery |
| Services/ | Catalog queries and writes, search, duplicate matching, lookup, email, and account logic |
| Models/ | EF Core schema mapping, records, artists, sleeves, and input models |
| Views/ | Razor pages and shared templates |
| wwwroot/ | CSS, browser JavaScript, images, and the bundled ZXing camera scanner |
| database/schema.sql | Catalog schema, indexes, runtime grants, and one-time legacy import |
| database/accounts.sql | Account registry, email confirmation, and recovery procedures |
| deploy/ | Windows installation, launch, provisioning, Cloudflare connector, and SMTP setup |

## SQL storage

| SQL object | Contents |
| --- | --- |
| dbo.RecordSiteArtist | Artists linked to releases |
| dbo.RecordSiteRelease | Release types, titles, credits, pressing metadata, artwork URLs, sleeve references, and dates added |
| dbo.RecordSiteSleeve | Front and rear JPEGs keyed by record and side |
| dbo.RecordSiteLegacyImport | One-time import marker and counts |
| dbo.RecordSiteAccount | Usernames, password hashes, verified addresses, and provisioning state |
| dbo.RecordSiteEmailChange and dbo.RecordSiteRecovery | Pending verification and reset requests with hashed tokens and expiry |

MattVinyl serves the public collection and stores the account registry. Initial provisioning imports the legacy Artist, Album, Single, and Compilation tables where present. Each personal collection uses the shared catalog schema in its own MattsRecords_User_<account GUID without hyphens> database. The signed-in account determines the database used for browsing, edits, exports, and image requests.

## Workflows

1. Registration collects a username, email address, and password. Confirmation links expire after 24 hours. The scheduled worker provisions the personal database after confirmation.
2. Forgot password takes a username and queues mail to the verified address. The reset link expires in 30 minutes, can be used once, and changing the password invalidates old sessions. Existing users can verify an address under **Manage → Account settings**.
3. Barcode scanning needs HTTPS and camera permission. Manual barcode entry remains available. Review the lookup results and possible duplicates before saving a record.
4. Uploaded front sleeves take precedence in the cover grid. The application stores resized JPEGs in SQL Server after removing source image metadata.
