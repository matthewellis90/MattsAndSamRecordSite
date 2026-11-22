using System.Data;
using Microsoft.Data.SqlClient;

namespace MattsRecords.Services;

public sealed record EmailAddresses(string? Verified, string? Pending);

public interface IAccountEmail
{
    Task<EmailAddresses> ReadAsync(Guid id, CancellationToken cancellationToken);
    Task<string?> BeginAsync(CollectionAccount account, string address, CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(string token, CancellationToken cancellationToken);
}

public sealed class AccountEmail(CollectionDatabases databases) : IAccountEmail
{
    public async Task<EmailAddresses> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT a.verified_email, p.address
            FROM dbo.RecordSiteAccount AS a
            LEFT JOIN dbo.RecordSiteEmailChange AS p ON p.account_id = a.id AND p.expires_utc > SYSUTCDATETIME()
            WHERE a.id = @account_id;
            """, connection);
        command.Parameters.Add("@account_id", SqlDbType.UniqueIdentifier).Value = id;
        await using var result = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await result.ReadAsync(cancellationToken)
            ? new EmailAddresses(result.IsDBNull(0) ? null : result.GetString(0), result.IsDBNull(1) ? null : result.GetString(1))
            : new EmailAddresses(null, null);
    }

    public async Task<string?> BeginAsync(CollectionAccount account, string address, CancellationToken cancellationToken)
    {
        var (code, hash) = AccountLinkToken.Create();
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteBeginEmailChange", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@account_id", SqlDbType.UniqueIdentifier).Value = account.Id;
        command.Parameters.Add("@password_hash", SqlDbType.NVarChar, 512).Value = account.PasswordHash;
        command.Parameters.Add("@address", SqlDbType.NVarChar, 254).Value = address;
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        return (int?)await command.ExecuteScalarAsync(cancellationToken) == 1 ? code : null;
    }

    public async Task<bool> ConfirmAsync(string token, CancellationToken cancellationToken)
    {
        if (!AccountLinkToken.TryHash(token, out var hash))
            return false;
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteConfirmEmail", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        return (int?)await command.ExecuteScalarAsync(cancellationToken) == 1;
    }
}
