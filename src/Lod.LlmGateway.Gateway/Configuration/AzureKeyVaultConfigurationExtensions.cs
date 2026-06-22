using Azure.Identity;
using Azure.Extensions.AspNetCore.Configuration.Secrets;

namespace Lod.LlmGateway.Gateway.Configuration;

public static class AzureKeyVaultConfigurationExtensions
{
    public static IConfigurationBuilder AddConfiguredAzureKeyVault(this ConfigurationManager configuration)
    {
        string? vaultUri = configuration["AzureKeyVault:VaultUri"] ?? configuration["KeyVault:VaultUri"];
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            return configuration;
        }

        return configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
    }
}
