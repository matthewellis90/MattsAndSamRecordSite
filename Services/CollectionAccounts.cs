using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace MattsRecords.Services;

public sealed class CollectionDatabases(IConfiguration configuration)
{
    public const string PersonalPrefix = "MattsRecords_User_";

    public string Owner => configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("The catalog database connection is missing.");

    public string ForRequest(ClaimsPrincipal? principal)
    {
        if (!CollectionSession.IsPersonal(principal))
            return Owner;
        if (!CollectionSession.IsReady(principal!) || CollectionSession.GetId(principal!) is not Guid id)
            throw new InvalidOperationException("This personal collection is not ready.");

        return new SqlConnectionStringBuilder(Owner) { InitialCatalog = PersonalPrefix + id.ToString("N") }.ConnectionString;
    }
}

public sealed record CollectionAccount(Guid Id, string Username, string PasswordHash, byte State);

public enum RegistrationResult { Created, UsernameTaken, CapacityReached }

public interface ICollectionAccounts
{
    Task<CollectionAccount?> FindByNameAsync(string name, CancellationToken cancellationToken);
    Task<CollectionAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<(RegistrationResult Result, CollectionAccount Account, string Code)> RegisterAsync(string name, string password, string email,
        CancellationToken cancellationToken);
    bool Verify(CollectionAccount account, string password);
}

public sealed class CollectionAccounts(CollectionDatabases databases, IPasswordHasher<CollectionAccount> hasher)
    : ICollectionAccounts
{
    public Task<CollectionAccount?> FindByNameAsync(string name, CancellationToken cancellationToken) =>
        ReadAsync("username", SqlDbType.NVarChar, name.Trim(), cancellationToken);

    public Task<CollectionAccount?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        ReadAsync("id", SqlDbType.UniqueIdentifier, id, cancellationToken);

    public bool Verify(CollectionAccount account, string password) =>
        hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;

    public async Task<(RegistrationResult Result, CollectionAccount Account, string Code)> RegisterAsync(string name, string password, string email,
        CancellationToken cancellationToken)
    {
        var account = new CollectionAccount(Guid.NewGuid(), name.Trim(), "", CollectionSession.PendingEmailState);
        account = account with { PasswordHash = hasher.HashPassword(account, password) };
        var (code, hash) = AccountLinkToken.Create();
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteRegisterWithEmail", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = account.Id;
        command.Parameters.Add("@username", SqlDbType.NVarChar, 32).Value = account.Username;
        command.Parameters.Add("@password_hash", SqlDbType.NVarChar, 512).Value = account.PasswordHash;
        command.Parameters.Add("@address", SqlDbType.NVarChar, 254).Value = email;
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        var result = (int?)await command.ExecuteScalarAsync(cancellationToken);
        return ((RegistrationResult)(result ?? throw new InvalidOperationException("Registration returned no result.")), account, code);
    }

    private async Task<CollectionAccount?> ReadAsync(string column, SqlDbType type, object value, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            $"SELECT id, username, password_hash, state FROM dbo.RecordSiteAccount WHERE {column} = @lookup", connection);
        command.Parameters.Add("@lookup", type).Value = value;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CollectionAccount(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetByte(3)) : null;
    }
}

public static class CollectionSession
{
    public const byte PendingEmailState = 3;
    private const string Account = "mattsrecords:collection-account";
    private const string Stamp = "mattsrecords:collection-stamp";
    private const string State = "mattsrecords:collection-state";

    public static bool IsPersonal(ClaimsPrincipal? principal) => principal?.HasClaim(claim => claim.Type == Account) == true;
    public static bool IsReady(ClaimsPrincipal principal) => principal.HasClaim(State, "1");
    public static bool HasFailed(ClaimsPrincipal principal) => principal.HasClaim(State, "2");
    public static bool NeedsEmailConfirmation(ClaimsPrincipal principal) => principal.HasClaim(State, "3");
    public static Guid? GetId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Account), out var id) ? id : null;

    public static ClaimsPrincipal Create(CollectionAccount account) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, account.Username), new Claim(Account, account.Id.ToString()),
            new Claim(Stamp, SessionStamp(account)), new Claim(State, account.State.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        CookieAuthenticationDefaults.AuthenticationScheme));

    public static async Task<bool> RefreshAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is not { } principal || GetId(principal) is not Guid id)
            return false;
        var accounts = context.HttpContext.RequestServices.GetRequiredService<ICollectionAccounts>();
        var account = await accounts.FindByIdAsync(id, context.HttpContext.RequestAborted);
        if (account is null || principal.FindFirstValue(Stamp) != SessionStamp(account))
            return false;
        context.ReplacePrincipal(Create(account));
        return true;
    }

    private static string SessionStamp(CollectionAccount account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.PasswordHash)));
}
