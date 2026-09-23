using Amazon.CDK;

namespace ShoppyShop.Cdk;

public static class Program
{
    public static void Main()
    {
        var app = new App();
        var notificationEmail = app.Node.TryGetContext("notificationEmail") as string;
        if (string.IsNullOrWhiteSpace(notificationEmail))
        {
            throw new InvalidOperationException("CDK context 'notificationEmail' is required (use -c notificationEmail=you@example.com).");
        }

        var trustedProxyNetworks = ParseTrustedProxyNetworks(app.Node.TryGetContext("trustedProxyNetworks") as string);

        var environment = new Amazon.CDK.Environment
        {
            Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
            Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION") ?? "eu-central-1",
        };
        var foundation = new DeploymentFoundation(app, "ShoppyShopFoundation", new DeploymentFoundationProps
        {
            GitHubOwner = app.Node.TryGetContext("githubOwner") as string ?? "korolvitalii",
            GitHubOwnerId = app.Node.TryGetContext("githubOwnerId") as string
                ?? throw new InvalidOperationException("CDK context 'githubOwnerId' is required."),
            GitHubRepository = app.Node.TryGetContext("githubRepository") as string ?? "shoppy-shop-api",
            GitHubRepositoryId = app.Node.TryGetContext("githubRepositoryId") as string
                ?? throw new InvalidOperationException("CDK context 'githubRepositoryId' is required."),
            Env = environment,
        });
        _ = new ShoppyShopInfrastructure(app, "ShoppyShopApi", new ShoppyShopInfrastructureProps
        {
            NotificationEmail = notificationEmail,
            FrontendOrigin = app.Node.TryGetContext("frontendOrigin") as string ?? "https://zeta.vercel.app",
            AdminEmail = app.Node.TryGetContext("adminEmail") as string ?? notificationEmail,
            ImageTag = app.Node.TryGetContext("imageTag") as string ?? "latest",
            TrustedProxyNetworks = trustedProxyNetworks,
            Repository = foundation.Repository,
            Env = environment,
        });

        app.Synth();
    }

    /// <summary>
    /// Required, and checked here rather than at container start: without a trusted proxy network
    /// the API only logs a warning and every per-IP rate limit collapses into one global bucket, and
    /// a malformed value would surface only as a failed App Runner deployment.
    /// </summary>
    public static IReadOnlyList<string> ParseTrustedProxyNetworks(string? value)
    {
        var networks = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (networks.Length == 0)
        {
            throw new InvalidOperationException(
                "CDK context 'trustedProxyNetworks' is required: the App Runner ingress CIDRs, comma-separated " +
                "(use -c trustedProxyNetworks=<cidr>[,<cidr>...]). The API will not start in Production without it.");
        }

        foreach (var network in networks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                throw new InvalidOperationException($"CDK context 'trustedProxyNetworks' contains an invalid CIDR: '{network}'.");
            }
        }

        return networks;
    }
}