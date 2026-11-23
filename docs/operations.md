# Operating and maintaining the site

[Back to the README](../README.md)

## Routine checks

| Check | Where to look |
| --- | --- |
| Web process available | `GET /health` |
| Catalog database accessible | `GET /health/ready` on loopback and public HTTPS |
| Web task | Task Scheduler → `MattsAndSamRecords` |
| Personal database provisioning | Task Scheduler → `MattsAndSamRecords-PersonalCollections` |
| Web logs | `%ProgramData%\MattsAndSamRecords\state\logs` |
| Provisioning log | `provisioning.log` in the active release folder |
| Active release | `%ProgramData%\MattsAndSamRecords\current-release.txt` |
| Cloudflare connection | Tunnel status and published application route in the Cloudflare dashboard |

The account limit is 20 personal collections. New personal databases are provisioned with a 256 MB data-file limit and a 64 MB log-file limit. Account administrators can use **Manage → Account recovery** to issue a short-lived reset link for a personal user who lacks a verified address. The owner sign-in is backed by its Windows deployment hash. Use the installer with a new owner password to rotate it.

## Backups and updates

Back up `MattVinyl` and all `MattsRecords_User_*` databases with SQL Server. The owner database contains the account-to-database mapping. Personal databases contain the collection rows and uploaded images. Keep a protected copy of the deployed release metadata and key material for same-host recovery. Re-enter SMTP and tunnel credentials when rebuilding on a new host as needed.

Test restores in an isolated SQL instance. Check that the registry and personal database names still line up, then sign into a test account and open its collection and sleeve images. CSV export is useful for an inventory copy. Uploaded image bytes and account data are in the database backups.

Before updating, take current backups. Build or extract the new release and run `Update The Record Room.cmd` as administrator for the existing default deployment, or use the PowerShell installer with the installed settings. The installer checks readiness and attempts to restore the prior task configuration if startup fails. SQL backups are the recovery point for database changes.

After an update, check that signed-out visitors see the welcome page and that direct catalog links require sign-in. With a disposable test account and a mailbox you control, check the media sections, filters, record editing, collection isolation, a camera or manual barcode lookup, and both email flows. Open a valid sharing link while signed out to check read-only access.

## Share a collection and listen on Spotify

Sign in to a personal account and open **Sharing**. Create a link and copy it to a friend. The link gives read-only access to the username, records, pressing details, and sleeve images. Create a new link or turn sharing off to revoke access through the current link.

Record pages offer a Spotify search by title and artist. To link a specific album or track, open **Edit record** and paste the Spotify URL. MusicBrainz lookups can also fill this field when the release has a single Spotify link. Opening Spotify or loading its player connects to Spotify. Playback availability depends on Spotify and the listener’s browser, account, and region.

## Copy the owner collection into a personal account

Verify the account's email and allow its database to finish provisioning. For the existing installation, the separately supplied **Copy Collection to Matt** launcher copies the owner collection into the `matt` account and reports record, artist, and image totals.

The merge keeps populated fields on confidently matched pressings and fills missing details and sleeve images from the owner collection. Uncertain matches are copied separately so distinct pressings are preserved. The SQL transaction rolls back on failure and records completion so the same merge cannot be replayed. Once copied, edits in the personal catalog are independent of the owner collection.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| SQL provisioning fails | Check the reported SQL error, the configured server and database, Windows integrated login rights, and the `sqlcmd` installation. |
| Browser cannot open the loopback HTTPS URL | Check `http://127.0.0.1:5782/health/ready` locally. Browse through the public HTTPS hostname. |
| Cloudflare shows an origin error | Check the Windows web task and local readiness, then verify the connector is on the same host and the published route points to the HTTP loopback origin. |
| A personal collection stays on the setup page | Verify the confirmation link was redeemed, then check the worker status and provisioning log. |
| No email arrives | Verify the address, check spam, inspect the web logs, and confirm the SMTP app password, TLS setting, and DNS authentication. |
| Barcode scanner cannot open the camera | Check the public HTTPS URL and browser camera permission. Type the barcode into the form if needed. |
| No pressing matches | Try the title and artist, use a direct lookup link, or enter the details manually. Third-party catalog coverage and availability vary. |
| Cover art is missing or represents a different edition | Use a verified cover URL or upload front and rear images from the copy you own. |
