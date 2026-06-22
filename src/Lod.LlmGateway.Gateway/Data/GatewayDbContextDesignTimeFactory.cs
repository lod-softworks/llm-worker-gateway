using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lod.LlmGateway.Gateway.Data;

public class GatewayDbContextDesignTimeFactory : IDesignTimeDbContextFactory<GatewayDbContext>
{
    public GatewayDbContext CreateDbContext(string[] args)
    {
        var connectionString = "Server=(localdb)\\mssqllocaldb;Database=Llm.Gateway.Host;Trusted_Connection=true;MultipleActiveResultSets=true";
        var builder = new DbContextOptionsBuilder<GatewayDbContext>()
            .UseSqlServer(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", "llm_gateway"));

        return new GatewayDbContext(builder.Options);
    }
}
