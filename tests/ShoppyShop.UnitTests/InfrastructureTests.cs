using Amazon.CDK;
using Amazon.CDK.Assertions;

using ShoppyShop.Cdk;

namespace ShoppyShop.UnitTests;

public sealed class InfrastructureTests
{
    [Fact]
    public void ApplicationStackKeepsDatabasePrivateAndEncrypted()
    {
        var app = new App();
        var environment = new Amazon.CDK.Environment { Account = "123456789012", Region = "eu-central-1" };
        var foundation = new DeploymentFoundation(app, "Foundation", new DeploymentFoundationProps
        {
            GitHubOwner = "owner",
            GitHubRepository = "repository",
            Env = environment,
        });
        var stack = new ShoppyShopInfrastructure(app, "Api", new ShoppyShopInfrastructureProps
        {
            NotificationEmail = "owner@example.test",
            FrontendOrigin = "https://example.test",
            AdminEmail = "admin@example.test",
            ImageTag = "test-sha",
            Repository = foundation.Repository,
            Env = environment,
        });
        var template = Template.FromStack(stack);

        template.ResourceCountIs("AWS::EC2::NatGateway", 0);
        template.HasResourceProperties("AWS::RDS::DBInstance", new Dictionary<string, object>
        {
            ["BackupRetentionPeriod"] = 7,
            ["Engine"] = "postgres",
            ["EngineVersion"] = "17.10",
            ["MultiAZ"] = false,
            ["PubliclyAccessible"] = false,
            ["StorageEncrypted"] = true,
        });
        template.HasResourceProperties("AWS::AppRunner::AutoScalingConfiguration", new Dictionary<string, object>
        {
            ["MinSize"] = 1,
            ["MaxSize"] = 2,
        });
    }
}