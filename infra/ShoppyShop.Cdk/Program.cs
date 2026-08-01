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
            Repository = foundation.Repository,
            Env = environment,
        });

        app.Synth();
    }
}