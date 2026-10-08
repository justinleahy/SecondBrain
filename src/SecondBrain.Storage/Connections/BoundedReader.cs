using System.Collections;
using System.Data;
using System.Data.Common;

namespace SecondBrain.Storage.Connections;

internal sealed class BoundedReader(BoundedConnection connection, DbDataReader inner, long deadline) : DbDataReader
{
    private bool _disposed;
    private T Run<T>(Func<T> action, CancellationToken cancellationToken = default)
        => connection.Run(action, cancellationToken, statementDeadline: deadline);
    public override bool Read() => Run(inner.Read);
    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Run(inner.Read, cancellationToken));
    public override bool NextResult() => Run(inner.NextResult);
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => Task.FromResult(Run(inner.NextResult, cancellationToken));
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int Depth => Run(() => inner.Depth);
    public override int FieldCount => Run(() => inner.FieldCount);
    public override bool HasRows => Run(() => inner.HasRows);
    public override bool IsClosed => _disposed || connection.State == ConnectionState.Closed || inner.IsClosed;
    public override int RecordsAffected => Run(() => inner.RecordsAffected);
    public override bool GetBoolean(int ordinal) => Run(() => inner.GetBoolean(ordinal));
    public override byte GetByte(int ordinal) => Run(() => inner.GetByte(ordinal));
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => Run(() => inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length));
    public override char GetChar(int ordinal) => Run(() => inner.GetChar(ordinal));
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => Run(() => inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length));
    public override string GetDataTypeName(int ordinal) => Run(() => inner.GetDataTypeName(ordinal));
    public override DateTime GetDateTime(int ordinal) => Run(() => inner.GetDateTime(ordinal));
    public override decimal GetDecimal(int ordinal) => Run(() => inner.GetDecimal(ordinal));
    public override double GetDouble(int ordinal) => Run(() => inner.GetDouble(ordinal));
    public override Type GetFieldType(int ordinal) => Run(() => inner.GetFieldType(ordinal));
    public override float GetFloat(int ordinal) => Run(() => inner.GetFloat(ordinal));
    public override Guid GetGuid(int ordinal) => Run(() => inner.GetGuid(ordinal));
    public override short GetInt16(int ordinal) => Run(() => inner.GetInt16(ordinal));
    public override int GetInt32(int ordinal) => Run(() => inner.GetInt32(ordinal));
    public override long GetInt64(int ordinal) => Run(() => inner.GetInt64(ordinal));
    public override string GetName(int ordinal) => Run(() => inner.GetName(ordinal));
    public override int GetOrdinal(string name) => Run(() => inner.GetOrdinal(name));
    public override string GetString(int ordinal) => Run(() => inner.GetString(ordinal));
    public override object GetValue(int ordinal) => Run(() => inner.GetValue(ordinal));
    public override int GetValues(object[] values) => Run(() => inner.GetValues(values));
    public override bool IsDBNull(int ordinal) => Run(() => inner.IsDBNull(ordinal));
    public override DataTable? GetSchemaTable() => Run(inner.GetSchemaTable);
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
    public override T GetFieldValue<T>(int ordinal) => Run(() => inner.GetFieldValue<T>(ordinal));
    public override void Close() => Dispose(disposing: true);
    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            // Expired leases own native cleanup; disposing a stale reader must be harmless.
            connection.Cleanup(inner.Dispose);
        }
    }
}
