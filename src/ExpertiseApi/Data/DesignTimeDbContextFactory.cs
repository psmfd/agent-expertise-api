using ExpertiseApi.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ExpertiseApi.Data;

internal class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ExpertiseDbContext>
{
    public ExpertiseDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ExpertiseDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=expertise", o => o.UseVector());

        return new ExpertiseDbContext(optionsBuilder.Options, new NoOpTenantContextAccessor());
    }
}
