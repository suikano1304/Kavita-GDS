using Kavita.Database;
using Kavita.Models.Builders;
using Kavita.Models.Entities.Enums;
using Kavita.Server.ManualMigrations.v0._9._1;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kavita.Server.Tests.ManualMigrations;

public class DefaultMetadataProviderMigrationTests
{
    [Fact]
    public async Task ExistingLibrariesIncludingGds_MigrateOnceWithoutEnablingMatching()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new DataContext(new DbContextOptionsBuilder<DataContext>()
            .UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        foreach (var type in Enum.GetValues<LibraryType>())
        {
            var library = new LibraryBuilder(type.ToString(), type).Build();
            library.AllowMetadataMatching = false;
            context.Library.Add(library);
        }
        await context.SaveChangesAsync();

        var migration = new ManualMigrationSetDefaultMetadataProvidersForLibrary();
        await migration.RunAsync(context, NullLogger<Program>.Instance);
        await migration.RunAsync(context, NullLogger<Program>.Instance);

        var libraries = await context.Library.AsNoTracking().ToListAsync();
        Assert.Equal(Enum.GetValues<LibraryType>().Length, libraries.Count);
        Assert.All(libraries, library =>
        {
            Assert.True(Enum.IsDefined(library.MetadataProvider));
            Assert.False(library.AllowMetadataMatching);
        });
        Assert.Equal(MetadataProvider.Mangabaka,
            libraries.Single(library => library.Type == LibraryType.GDS).MetadataProvider);
        Assert.Single(await context.ManualMigrationHistory.ToListAsync());
    }
}
