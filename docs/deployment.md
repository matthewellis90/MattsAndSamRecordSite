# Windows deployment and email setup

[Back to the README](../README.md)

## Prerequisites

- The restored source checkout or a matching Windows release containing `publish/`, `deploy/`, and `database/`.
- A Windows host with SQL Server, the SQL Server `sqlcmd` utility, and an elevated Windows account with SQL permissions to provision databases and roles.
- The .NET 10 SDK where you build the project. A self-contained Windows release includes its runtime.
- A Cloudflare-managed DNS zone and a tunnel connector on the Windows host for public hosting.
- An SMTP account for registration and recovery email. The existing installation uses Namecheap Private Email.

## Run from source

Create a development SQL database and run `database/schema.sql` followed by `database/accounts.sql` in it. Give the Windows login used to run the application membership in the database role `record_site_runtime`. Use a development catalog to avoid writing to the live collection.

```powershell
$env:ConnectionStrings__DefaultConnection = 'Data Source=lpc:.;Initial Catalog=RecordRoomDev;Integrated Security=True;Encrypt=True;TrustServerCertificate=True'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet restore
dotnet build
dotnet run --no-launch-profile
```

Open `http://127.0.0.1:5782` for the catalog. Full sign-up also requires SMTP, HTTPS links, and the Windows personal database provisioner. Use a separate deployment for end-to-end account testing.

## Install or update on the Windows host

From the restored source root, publish the application. [Microsoft .NET publish reference](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish)

```powershell
dotnet publish .\MattsRecords.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

Run the installer from an elevated Windows PowerShell session. Use a unique owner password of 16 to 256 characters for first-time setup.

```powershell
$ownerPassword = Read-Host 'Owner password' -AsSecureString
& .\deploy\Install-Windows.ps1 -PublishedDirectory .\publish -PublicHost records.example.com -AdminPassword $ownerPassword
```

The default owner username is `admin`. The installer defaults to the local default SQL instance, `MattVinyl`, and port 5782. Supply your public hostname on the first installation. Updates reuse the installed hostname and owner password hash when those parameters are omitted. Supply the same SQL server, database, and port parameters if your installation uses custom values.

The installer applies the schemas, deploys a versioned release, registers the site as a Local Service task, and registers the Local System worker for personal collections. It checks that the application can read the catalog. Ready personal databases receive schema updates during installation.

## Publish through Cloudflare

Create or select the remotely managed tunnel for the Windows host. Run `Install Cloudflare Connector.cmd` as administrator and enter the tunnel installation command or token at its masked prompt. In the Cloudflare tunnel's **Routes** tab, add a published application using the public hostname and service URL `http://127.0.0.1:5782`. The connector must run on the same Windows host for that loopback address to work. [Cloudflare Tunnel setup](https://developers.cloudflare.com/tunnel/get-started/)

```powershell
curl.exe --fail http://127.0.0.1:5782/health/ready
curl.exe --fail https://records.example.com/health/ready
```

Use your actual public hostname in the second check. Open the public HTTPS URL in the browser. The loopback health endpoint uses HTTP.

## Configure Namecheap SMTP

In Namecheap Private Email, enable third-party SMTP access and create an app password for the sender mailbox. In Cloudflare DNS, publish the mailbox provider's MX, SPF and DKIM records. If SPF already exists, include all authorized senders in a single policy. The existing setup started with `v=DMARC1; p=none` at `_dmarc` while testing delivery. [Namecheap SMTP settings](https://www.namecheap.com/support/knowledgebase/article.aspx/1179/2175/general-private-email-configuration-for-mail-clients-and-mobile-devices/), [Namecheap app passwords](https://www.namecheap.com/support/knowledgebase/article.aspx/10816/2178/how-to-use-app-passwords-for-private-email/)

With the site installed, run `Configure The Record Room Email.cmd` as administrator. Use `mail.privateemail.com`, port `587` with STARTTLS, and the full mailbox address for both From and the SMTP username. Enter the app password at the masked Windows prompt. Namecheap also supports port `465` with implicit TLS. Other configured ports use STARTTLS. The combined email installer supplies the Namecheap host and port and prompts for the mailbox address and app password.

Windows DPAPI protects the stored SMTP password. The site constructs account links with the public HTTPS hostname from the deployment settings. Once the installer reports **Ready**, use an address you control to test sign-up confirmation and password reset from the public site. The readiness endpoint checks the SQL catalog. Inbox delivery requires this separate test.
