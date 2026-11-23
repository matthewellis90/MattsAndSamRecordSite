using System.Data;
using Microsoft.Data.SqlClient;

namespace MattsRecords.Services;

public sealed record SharedCollection(Guid AccountId, string Username);

public interface ICollectionSharing
{
    Task<Guid?> ReadAsync(Guid accountId, CancellationToken cancellationToken);
    Task SetAsync(Guid accountId, Guid? token, CancellationToken cancellationToken);
    Task<SharedCollection?> FindAsync(Guid token, CancellationToken cancellationToken);
}

public sealed class CollectionSharing(CollectionDatabases databases) : ICollectionSharing
{
    public async Task<Guid?> ReadAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT token FROM dbo.RecordSiteCollectionShare WHERE account_id = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = accountId;
        return (Guid?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task SetAsync(Guid accountId, Guid? token, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(token.HasValue ? """
            MERGE dbo.RecordSiteCollectionShare WITH (HOLDLOCK) AS share
            USING (SELECT id FROM dbo.RecordSiteAccount WHERE id = @id AND state = 1) AS account
                ON share.account_id = account.id
            WHEN MATCHED THEN UPDATE SET token = @token
            WHEN NOT MATCHED THEN INSERT (account_id, token) VALUES (account.id, @token);
            """ : "DELETE FROM dbo.RecordSiteCollectionShare WHERE account_id = @id;", connection);
        command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = accountId;
        if (token is Guid value)
            command.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = value;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SharedCollection?> FindAsync(Guid token, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(databases.Owner);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT account.id, account.username FROM dbo.RecordSiteCollectionShare AS share
            JOIN dbo.RecordSiteAccount AS account ON account.id = share.account_id
            WHERE share.token = @token AND account.state = 1;
            """, connection);
        command.Parameters.Add("@token", SqlDbType.UniqueIdentifier).Value = token;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SharedCollection(reader.GetGuid(0), reader.GetString(1)) : null;
    }
}
