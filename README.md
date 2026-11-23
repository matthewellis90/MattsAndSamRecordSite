# The Record Room

The Record Room is a collection website for vinyl, CDs, and cassettes built with ASP.NET Core MVC, Razor, Entity Framework Core, and SQL Server. Registered users manage private collections in their own databases and can share them with a read-only link.

[Visit The Record Room](https://records.ellishome.casa/)

## What you can do

- Browse collapsible Vinyl, Cassettes, and CDs sections with per-format counts and pagination. Switch between a cover grid and a compact list.
- Search by title, artist, or year, and refine results by release type, media, format details, decade, artist, genre, or label.
- Browse the artist index, collection statistics, and a collapsible list of the ten most recently added records.
- Add releases by hand or find a pressing through Discogs and MusicBrainz. On supported devices, scan a barcode with the camera or type it into the form.
- Review potential duplicates before saving. You can keep both pressings or update an existing entry.
- Manage pressing details, upload front and rear sleeve photos, review missing information, and export your collection as CSV.
- Create a private account, confirm your email address, and request an email link if you forget your password.
- Share a private collection with a revocable, read-only link. Open a record in Spotify or load its player when a direct link is available.

Signed-out visitors see the welcome page, feature checklist, and tribute. The footer links to the FAQs and Privacy page. Browsing a private catalog requires signing in or opening a valid sharing link.

## Documentation

| Guide | What it covers |
| --- | --- |
| [Architecture and repository structure](docs/architecture.md) | Request flow, folder layout, SQL tables, and account lifecycle |
| [Windows deployment and email setup](docs/deployment.md) | Local development, builds, Task Scheduler, Cloudflare, and Namecheap SMTP |
| [Operating and maintaining the site](docs/operations.md) | Health checks, backups, updates, troubleshooting, and one-time collection copies |

Set `ConnectionStrings__DefaultConnection` outside source control for local development. The Windows installer generates the deployed connection from the SQL Server settings supplied at setup.
