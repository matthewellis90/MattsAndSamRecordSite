# The Record Room

The Record Room is a vinyl collection website built with ASP.NET Core MVC, Razor, Entity Framework Core, and SQL Server. Visitors can browse the public collection. Registered users can manage private collections in their own databases.

## What you can do

- Search by title, artist, or year, and refine results by release type, physical format, decade, artist, genre, or label. Switch between cover grid and list views.
- Browse the artist index, collection statistics, and the ten most recently added records.
- Add records by hand or look up a pressing. On supported devices, scan a barcode with the camera or type it into the form.
- Review potential duplicates before saving. You can keep both pressings or update an existing entry.
- Manage pressing details, upload front and rear sleeve photos, review missing information, and export your collection as CSV.
- Create a private account, confirm your email address, and request an email link if you forget your password.

## Documentation

| Guide | What it covers |
| --- | --- |
| [Architecture and repository structure](docs/architecture.md) | Request flow, folder layout, SQL tables, and account lifecycle |
| [Windows deployment and email setup](docs/deployment.md) | Local development, builds, Task Scheduler, Cloudflare, and Namecheap SMTP |
| [Operating and maintaining the site](docs/operations.md) | Health checks, backups, updates, troubleshooting, and one-time collection copies |

Set `ConnectionStrings__DefaultConnection` outside source control for local development. The Windows installer generates the deployed connection from the SQL Server settings supplied at setup.
