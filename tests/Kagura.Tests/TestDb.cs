using Kagura.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Kagura.Tests;

internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn;

    public KaguraDbContext Context { get; }

    private TestDb(SqliteConnection conn, KaguraDbContext ctx)
    {
        _conn = conn;
        Context = ctx;
    }

    public static TestDb Create()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        var opts = new DbContextOptionsBuilder<KaguraDbContext>()
            .UseSqlite(conn)
            .Options;

        var ctx = new KaguraDbContext(opts, new EphemeralDataProtectionProvider());
        ctx.Database.EnsureCreated();
        return new TestDb(conn, ctx);
    }

    public KaguraDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<KaguraDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new KaguraDbContext(opts, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        Context.Dispose();
        _conn.Dispose();
    }
}
