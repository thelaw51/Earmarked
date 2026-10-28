using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Earmarked.Core.Configuration;
using Earmarked.Core.Data;

namespace Earmarked.Web.Services;

/// <summary>
/// Tells the UI whether the Worker has created and migrated the database. The UI never migrates, so until this
/// returns true every page shows a waiting message. Once ready it stays ready: both containers run the same
/// image, so the schema can't move under a running UI.
/// </summary>
public sealed class SchemaStatus(IDbContextFactory<AppDbContext> dbFactory, IOptions<PathsOptions> paths)
{
    private volatile bool _ready;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (_ready)
        {
            return true;
        }

        // Opening a missing SQLite file creates it, so don't touch the database until the Worker has made it.
        if (!File.Exists(paths.Value.DatabaseFile))
        {
            return false;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        _ready = !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
        return _ready;
    }
}
