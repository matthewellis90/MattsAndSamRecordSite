using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MattsRecords.Services;

public sealed class AdminCredentials
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
}

public sealed class AdminAuthentication(
    IOptions<AdminCredentials> credentials, IPasswordHasher<AdminCredentials> passwordHasher)
{
    public const int HashIterations = 250_000;
    public const string LoginRateLimit = "owner-login";
    private const string SessionStampClaim = "mattsrecords:owner-stamp";

    private readonly AdminCredentials _credentials = credentials.Value;
    private readonly string _sessionStamp = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(credentials.Value.PasswordHash)));

    public bool IsOwnerName(string name) =>
        string.Equals(name.Trim(), _credentials.Username, StringComparison.OrdinalIgnoreCase);

    public ClaimsPrincipal? SignIn(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(_credentials.PasswordHash) || !IsOwnerName(username))
            return null;

        PasswordVerificationResult verification;
        try
        {
            verification = passwordHasher.VerifyHashedPassword(_credentials, _credentials.PasswordHash, password);
        }
        catch (FormatException)
        {
            return null;
        }

        if (verification == PasswordVerificationResult.Failed)
            return null;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, _credentials.Username), new Claim(SessionStampClaim, _sessionStamp)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        var authentication = context.HttpContext.RequestServices.GetRequiredService<AdminAuthentication>();
        if (CollectionSession.IsPersonal(context.Principal)
            ? await CollectionSession.RefreshAsync(context) : authentication.IsCurrent(context.Principal))
            return;

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private bool IsCurrent(ClaimsPrincipal? principal) =>
        !string.IsNullOrWhiteSpace(_credentials.PasswordHash) &&
        principal?.Identity?.Name == _credentials.Username &&
        principal.FindFirstValue(SessionStampClaim) == _sessionStamp;

    public static int WritePasswordHash()
    {
        if (Console.IsInputRedirected)
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var password = Console.IsInputRedirected ? Console.ReadLine() : ReadInteractivePassword();
        if (password is null || string.IsNullOrWhiteSpace(password) || password.Length is < 16 or > 256)
        {
            Console.Error.WriteLine("Enter a password with 16 to 256 characters.");
            return 1;
        }

        var options = Options.Create(new PasswordHasherOptions { IterationCount = HashIterations });
        var owner = new AdminCredentials();
        Console.WriteLine(new PasswordHasher<AdminCredentials>(options).HashPassword(owner, password));
        return 0;
    }

    private static string ReadInteractivePassword()
    {
        Console.Error.Write("Owner password: ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                    value.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return value.ToString();
    }
}
