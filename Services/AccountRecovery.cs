using System.Data;
using MattsRecords.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace MattsRecords.Services;

public interface IAccountRecovery
{
    Task RequestAsync(string username, bool sendEmail, CancellationToken cancellationToken);
    Task<IReadOnlyList<RecoveryRequest>> ListAsync(CancellationToken cancellationToken);
    Task<string?> IssueAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> ResetAsync(string token, string password, CancellationToken cancellationToken);
    Task<(string Address, string Code, byte[] Hash)?> ClaimMailAsync(CancellationToken cancellationToken);
    Task CompleteMailAsync(byte[] hash, bool delivered, CancellationToken cancellationToken);
}

public sealed class AccountRecovery(CollectionDatabases databases, IPasswordHasher<CollectionAccount> passwords) : IAccountRecovery
{
    public async Task RequestAsync(string username, bool sendEmail, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteRequestRecovery", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@username", SqlDbType.NVarChar, 32).Value = username.Trim();
        command.Parameters.Add("@send_email", SqlDbType.Bit).Value = sendEmail;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RecoveryRequest>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT a.id, a.username, CAST(CASE WHEN r.expires_utc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit)
            FROM dbo.RecordSiteRecovery AS r
            JOIN dbo.RecordSiteAccount AS a ON a.id = r.account_id
            ORDER BY r.requested_utc DESC, a.username;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var requests = new List<RecoveryRequest>();
        while (await reader.ReadAsync(cancellationToken))
            requests.Add(new RecoveryRequest(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2)));
        return requests;
    }

    public async Task<string?> IssueAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var (code, hash) = AccountLinkToken.Create();
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteIssueRecovery", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@account_id", SqlDbType.UniqueIdentifier).Value = accountId;
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        return (int?)await command.ExecuteScalarAsync(cancellationToken) == 1 ? code : null;
    }

    public async Task<bool> ResetAsync(string token, string password, CancellationToken cancellationToken)
    {
        if (!AccountLinkToken.TryHash(token, out var hash))
            return false;

        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var lookup = new SqlCommand("""
            SELECT a.id, a.username, a.password_hash, a.state
            FROM dbo.RecordSiteRecovery AS r
            JOIN dbo.RecordSiteAccount AS a ON a.id = r.account_id
            WHERE r.token_hash = @token_hash AND r.expires_utc > SYSUTCDATETIME();
            """, connection);
        lookup.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        CollectionAccount account;
        await using (var reader = await lookup.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return false;
            account = new CollectionAccount(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetByte(3));
        }

        await using var command = new SqlCommand("dbo.RecordSiteRedeemRecovery", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        command.Parameters.Add("@password_hash", SqlDbType.NVarChar, 512).Value = passwords.HashPassword(account, password);
        return (int?)await command.ExecuteScalarAsync(cancellationToken) == 1;
    }

    public async Task<(string Address, string Code, byte[] Hash)?> ClaimMailAsync(CancellationToken cancellationToken)
    {
        var (code, hash) = AccountLinkToken.Create();
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteClaimRecoveryMail", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        return await command.ExecuteScalarAsync(cancellationToken) is string address ? (address, code, hash) : null;
    }

    public async Task CompleteMailAsync(byte[] hash, bool delivered, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("dbo.RecordSiteCompleteRecoveryMail", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@token_hash", SqlDbType.Binary, 32).Value = hash;
        command.Parameters.Add("@delivered", SqlDbType.Bit).Value = delivered;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
