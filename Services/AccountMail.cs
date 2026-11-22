using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace MattsRecords.Services;

public sealed class AccountMailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string From { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string PublicOrigin { get; set; } = "";

    public bool Configured => !string.IsNullOrWhiteSpace(Host) && Port is > 0 and <= 65535 &&
        MailboxAddress.TryParse(From, out _) && (Username.Length == 0 || Password.Length > 0) &&
        Uri.TryCreate(PublicOrigin, UriKind.Absolute, out var origin) && origin.Scheme == "https" &&
        origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 && origin.UserInfo.Length == 0;
}

public interface IAccountMail
{
    bool Configured { get; }
    Task SendAsync(string address, string subject, string instructions, string path, string code, CancellationToken cancellationToken);
}

public sealed class SmtpAccountMail(IOptions<AccountMailOptions> options) : IAccountMail
{
    public bool Configured => options.Value.Configured;

    public async Task SendAsync(string address, string subject, string instructions, string path, string code,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var link = settings.PublicOrigin.TrimEnd('/') + path + "#" + code;
        using var message = new MimeMessage();
        message.From.Add(new MailboxAddress(Models.SiteBrand.Name, settings.From));
        message.To.Add(MailboxAddress.Parse(address));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = instructions + "\n\n" + link };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(settings.Host, settings.Port,
            settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, timeout.Token);
        if (settings.Username.Length > 0)
            await smtp.AuthenticateAsync(settings.Username, settings.Password, timeout.Token);
        await smtp.SendAsync(FormatOptions.Default, new SmtpSendRequest(message), timeout.Token);
    }
}

public sealed class RecoveryMailWorker(IServiceScopeFactory scopes, IAccountMail sender, ILogger<RecoveryMailWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!sender.Configured)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var recovery = scope.ServiceProvider.GetRequiredService<IAccountRecovery>();
                if (await recovery.ClaimMailAsync(stoppingToken) is not { } delivery)
                    continue;
                var delivered = false;
                try
                {
                    await sender.SendAsync(delivery.Address, "Reset your Record Room password",
                        "This link expires in 30 minutes. If you did not request a password reset, you can ignore this email.",
                        "/Account/Reset", delivery.Code, stoppingToken);
                    delivered = true;
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Recovery email delivery failed ({ErrorType}).", error.GetType().Name);
                }
                await recovery.CompleteMailAsync(delivery.Hash, delivered, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Recovery email queue unavailable ({ErrorType}).", error.GetType().Name);
            }
        }
    }
}
