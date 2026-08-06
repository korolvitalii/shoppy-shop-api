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
            GitHubOwnerId = "123456",
            GitHubRepository = "repository",
            GitHubRepositoryId = "789012",
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
        template.HasResourceProperties("AWS::SecretsManager::Secret", new Dictionary<string, object>
        {
            ["Description"] = "Anthropic API key for the shopping assistant",
        });
        template.HasResourceProperties("AWS::AppRunner::Service", new Dictionary<string, object>
        {
            ["SourceConfiguration"] = new Dictionary<string, object>
            {
                ["ImageRepository"] = new Dictionary<string, object>
                {
                    ["ImageConfiguration"] = new Dictionary<string, object>
                    {
                        ["RuntimeEnvironmentSecrets"] = Match.ArrayWith(new[]
                        {
                            Match.ObjectLike(new Dictionary<string, object>
                            {
                                ["Name"] = "Anthropic__ApiKey",
                            }),
                        }),
                    },
                },
            },
        });
    }
    [Fact]
    public void FoundationTrustsOnlyTheImmutableMainBranchSubject()
    {
        var app = new App();
        var foundation = new DeploymentFoundation(app, "Foundation", new DeploymentFoundationProps
        {
            GitHubOwner = "owner",
            GitHubOwnerId = "123456",
            GitHubRepository = "repository",
            GitHubRepositoryId = "789012",
            Env = new Amazon.CDK.Environment { Account = "123456789012", Region = "eu-central-1" },
        });
        var template = Template.FromStack(foundation);

        template.HasResourceProperties("AWS::IAM::Role", new Dictionary<string, object>
        {
            ["RoleName"] = "ShoppyShopGitHubDeployRole",
            ["AssumeRolePolicyDocument"] = new Dictionary<string, object>
            {
                ["Statement"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["Action"] = "sts:AssumeRoleWithWebIdentity",
                        ["Condition"] = new Dictionary<string, object>
                        {
                            ["StringEquals"] = new Dictionary<string, string>
                            {
                                ["token.actions.githubusercontent.com:aud"] = "sts.amazonaws.com",
                                ["token.actions.githubusercontent.com:sub"] =
                                    "repo:owner@123456/repository@789012:ref:refs/heads/main",
                            },
                        },
                        ["Effect"] = "Allow",
                    },
                },
            },
        });
    }
}