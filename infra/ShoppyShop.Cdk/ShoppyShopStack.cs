using Amazon.CDK;
using Amazon.CDK.AWS.AppRunner;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.EC2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.RDS;
using Amazon.CDK.AWS.SecretsManager;

using Constructs;

namespace ShoppyShop.Cdk;

public sealed class DeploymentFoundationProps : StackProps
{
    public required string GitHubOwner { get; init; }
    public required string GitHubOwnerId { get; init; }
    public required string GitHubRepository { get; init; }
    public required string GitHubRepositoryId { get; init; }
}

public sealed class DeploymentFoundation : Stack
{
    public DeploymentFoundation(Construct scope, string id, DeploymentFoundationProps props)
        : base(scope, id, props)
    {
        Repository = new Repository(this, "Repository", new RepositoryProps
        {
            RepositoryName = "shoppy-shop-api",
            ImageScanOnPush = true,
            ImageTagMutability = TagMutability.IMMUTABLE,
            LifecycleRules = [new LifecycleRule { MaxImageCount = 10 }],
            RemovalPolicy = RemovalPolicy.RETAIN,
        });

        var provider = new OpenIdConnectProvider(this, "GitHubProvider", new OpenIdConnectProviderProps
        {
            Url = "https://token.actions.githubusercontent.com",
            ClientIds = ["sts.amazonaws.com"],
        });
        var deployRole = new Role(this, "GitHubDeployRole", new RoleProps
        {
            RoleName = "ShoppyShopGitHubDeployRole",
            AssumedBy = new WebIdentityPrincipal(provider.OpenIdConnectProviderArn, new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, string>
                {
                    ["token.actions.githubusercontent.com:aud"] = "sts.amazonaws.com",
                    ["token.actions.githubusercontent.com:sub"] =
                        $"repo:{props.GitHubOwner}@{props.GitHubOwnerId}/{props.GitHubRepository}@{props.GitHubRepositoryId}:ref:refs/heads/main",
                },
            }),
        });
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["ecr:GetAuthorizationToken"],
            Resources = ["*"],
        }));
        Repository.GrantPullPush(deployRole);
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = ["sts:AssumeRole"],
            Resources = [$"arn:{Aws.PARTITION}:iam::{Aws.ACCOUNT_ID}:role/cdk-*"],
        }));

        _ = new CfnOutput(this, "RepositoryUri", new CfnOutputProps { Value = Repository.RepositoryUri });
        _ = new CfnOutput(this, "GitHubDeployRoleArn", new CfnOutputProps { Value = deployRole.RoleArn });
    }

    public IRepository Repository { get; }
}

public sealed class ShoppyShopInfrastructureProps : StackProps
{
    public required string NotificationEmail { get; init; }
    public required string FrontendOrigin { get; init; }
    public required string AdminEmail { get; init; }
    public required string ImageTag { get; init; }
    public required IRepository Repository { get; init; }
}

public sealed class ShoppyShopInfrastructure : Stack
{
    public ShoppyShopInfrastructure(Construct scope, string id, ShoppyShopInfrastructureProps props)
        : base(scope, id, props)
    {
        const string databaseName = "shoppy_shop";
        var vpc = new Vpc(this, "Vpc", new VpcProps
        {
            MaxAzs = 2,
            NatGateways = 0,
            SubnetConfiguration =
            [
                new SubnetConfiguration
                {
                    Name = "data",
                    SubnetType = SubnetType.PRIVATE_ISOLATED,
                    CidrMask = 24,
                },
            ],
        });

        var appSecurityGroup = new SecurityGroup(this, "AppSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            AllowAllOutbound = true,
            Description = "App Runner VPC connector",
        });
        var databaseSecurityGroup = new SecurityGroup(this, "DatabaseSecurityGroup", new SecurityGroupProps
        {
            Vpc = vpc,
            AllowAllOutbound = false,
            Description = "PostgreSQL access from App Runner only",
        });
        databaseSecurityGroup.AddIngressRule(appSecurityGroup, Port.Tcp(5432), "App Runner to PostgreSQL");

        var database = new DatabaseInstance(this, "Database", new DatabaseInstanceProps
        {
            Vpc = vpc,
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
            SecurityGroups = [databaseSecurityGroup],
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps
            {
                Version = PostgresEngineVersion.Of("17.10", "17"),
            }),
            InstanceType = Amazon.CDK.AWS.EC2.InstanceType.Of(InstanceClass.BURSTABLE4_GRAVITON, InstanceSize.MICRO),
            Credentials = Credentials.FromGeneratedSecret("shoppy"),
            DatabaseName = databaseName,
            AllocatedStorage = 20,
            MaxAllocatedStorage = 25,
            StorageType = StorageType.GP3,
            MultiAz = false,
            PubliclyAccessible = false,
            BackupRetention = Duration.Days(7),
            StorageEncrypted = true,
            DeletionProtection = false,
            RemovalPolicy = RemovalPolicy.SNAPSHOT,
        });

        var jwtSecret = new Secret(this, "JwtSigningKey", new SecretProps
        {
            Description = "ShoppyShop JWT signing key",
            GenerateSecretString = new SecretStringGenerator
            {
                PasswordLength = 64,
                ExcludePunctuation = true,
            },
        });
        var adminPassword = new Secret(this, "BootstrapAdminPassword", new SecretProps
        {
            Description = "One-time ShoppyShop bootstrap administrator password",
            GenerateSecretString = new SecretStringGenerator { PasswordLength = 32 },
        });

        var imageAccessRole = new Role(this, "AppRunnerImageAccessRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("build.apprunner.amazonaws.com"),
        });
        props.Repository.GrantPull(imageAccessRole);

        var instanceRole = new Role(this, "AppRunnerInstanceRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("tasks.apprunner.amazonaws.com"),
        });
        database.Secret!.GrantRead(instanceRole);
        jwtSecret.GrantRead(instanceRole);
        adminPassword.GrantRead(instanceRole);

        var connector = new CfnVpcConnector(this, "VpcConnector", new CfnVpcConnectorProps
        {
            Subnets = vpc.IsolatedSubnets.Select(x => x.SubnetId).ToArray(),
            SecurityGroups = [appSecurityGroup.SecurityGroupId],
            VpcConnectorName = "shoppy-shop-api",
        });
        var scaling = new CfnAutoScalingConfiguration(this, "AutoScaling", new CfnAutoScalingConfigurationProps
        {
            AutoScalingConfigurationName = "shoppy-shop-api",
            MinSize = 1,
            MaxSize = 2,
            MaxConcurrency = 50,
        });

        var service = new CfnService(this, "Service", new CfnServiceProps
        {
            ServiceName = "shoppy-shop-api",
            AutoScalingConfigurationArn = scaling.AttrAutoScalingConfigurationArn,
            SourceConfiguration = new CfnService.SourceConfigurationProperty
            {
                AutoDeploymentsEnabled = false,
                AuthenticationConfiguration = new CfnService.AuthenticationConfigurationProperty
                {
                    AccessRoleArn = imageAccessRole.RoleArn,
                },
                ImageRepository = new CfnService.ImageRepositoryProperty
                {
                    ImageIdentifier = $"{props.Repository.RepositoryUri}:{props.ImageTag}",
                    ImageRepositoryType = "ECR",
                    ImageConfiguration = new CfnService.ImageConfigurationProperty
                    {
                        Port = "8080",
                        RuntimeEnvironmentVariables = new[]
                        {
                            Variable("ASPNETCORE_ENVIRONMENT", "Production"),
                            Variable("Database__Host", database.DbInstanceEndpointAddress),
                            Variable("Database__Name", databaseName),
                            Variable("Jwt__Issuer", "ShoppyShop.Api"),
                            Variable("Jwt__Audience", "ShoppyShop.Web"),
                            Variable("BootstrapAdmin__Email", props.AdminEmail),
                            Variable("Cors__AllowedOrigins__0", props.FrontendOrigin),
                        },
                        RuntimeEnvironmentSecrets = new[]
                        {
                            Variable("Database__Username", $"{database.Secret.SecretArn}:username::"),
                            Variable("Database__Password", $"{database.Secret.SecretArn}:password::"),
                            Variable("Jwt__SigningKey", jwtSecret.SecretArn),
                            Variable("BootstrapAdmin__Password", adminPassword.SecretArn),
                        },
                    },
                },
            },
            InstanceConfiguration = new CfnService.InstanceConfigurationProperty
            {
                Cpu = "0.25 vCPU",
                Memory = "0.5 GB",
                InstanceRoleArn = instanceRole.RoleArn,
            },
            NetworkConfiguration = new CfnService.NetworkConfigurationProperty
            {
                EgressConfiguration = new CfnService.EgressConfigurationProperty
                {
                    EgressType = "VPC",
                    VpcConnectorArn = connector.AttrVpcConnectorArn,
                },
            },
            HealthCheckConfiguration = new CfnService.HealthCheckConfigurationProperty
            {
                Protocol = "HTTP",
                Path = "/health/live",
                Interval = 10,
                Timeout = 5,
                HealthyThreshold = 1,
                UnhealthyThreshold = 5,
            },
        });
        service.AddDependency(connector);
        _ = new LogRetention(this, "ServiceLogRetention", new LogRetentionProps
        {
            LogGroupName = $"/aws/apprunner/shoppy-shop-api/{service.AttrServiceId}/service",
            Retention = RetentionDays.ONE_WEEK,
        });
        _ = new LogRetention(this, "ApplicationLogRetention", new LogRetentionProps
        {
            LogGroupName = $"/aws/apprunner/shoppy-shop-api/{service.AttrServiceId}/application",
            Retention = RetentionDays.ONE_WEEK,
        });

        _ = new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetName = "ShoppyShop API monthly budget",
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty { Amount = 35, Unit = "USD" },
            },
            NotificationsWithSubscribers = new[]
            {
                new CfnBudget.NotificationWithSubscribersProperty
                {
                    Notification = new CfnBudget.NotificationProperty
                    {
                        ComparisonOperator = "GREATER_THAN",
                        NotificationType = "ACTUAL",
                        Threshold = 80,
                        ThresholdType = "PERCENTAGE",
                    },
                    Subscribers = new[]
                    {
                        new CfnBudget.SubscriberProperty
                        {
                            SubscriptionType = "EMAIL",
                            Address = props.NotificationEmail,
                        },
                    },
                },
                new CfnBudget.NotificationWithSubscribersProperty
                {
                    Notification = new CfnBudget.NotificationProperty
                    {
                        ComparisonOperator = "GREATER_THAN",
                        NotificationType = "FORECASTED",
                        Threshold = 80,
                        ThresholdType = "PERCENTAGE",
                    },
                    Subscribers = new[]
                    {
                        new CfnBudget.SubscriberProperty
                        {
                            SubscriptionType = "EMAIL",
                            Address = props.NotificationEmail,
                        },
                    },
                },
            },
        });

        _ = new Alarm(this, "DatabaseCpuAlarm", new AlarmProps
        {
            Metric = database.MetricCPUUtilization(new MetricOptions { Period = Duration.Minutes(5) }),
            Threshold = 80,
            EvaluationPeriods = 3,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
        });
        _ = new Alarm(this, "DatabaseStorageAlarm", new AlarmProps
        {
            Metric = database.MetricFreeStorageSpace(new MetricOptions { Period = Duration.Minutes(5) }),
            Threshold = 2L * 1024 * 1024 * 1024,
            EvaluationPeriods = 2,
            ComparisonOperator = ComparisonOperator.LESS_THAN_THRESHOLD,
        });
        _ = new Alarm(this, "AppRunnerErrorsAlarm", new AlarmProps
        {
            Metric = new Metric(new MetricProps
            {
                Namespace = "AWS/AppRunner",
                MetricName = "5xxStatusResponses",
                Statistic = "Sum",
                Period = Duration.Minutes(5),
                DimensionsMap = new Dictionary<string, string> { ["ServiceName"] = "shoppy-shop-api" },
            }),
            Threshold = 5,
            EvaluationPeriods = 2,
            ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
        });

        _ = new CfnOutput(this, "ServiceUrl", new CfnOutputProps { Value = service.AttrServiceUrl });
        _ = new CfnOutput(this, "ServiceArn", new CfnOutputProps { Value = service.AttrServiceArn });
        _ = new CfnOutput(this, "DatabaseSecretArn", new CfnOutputProps { Value = database.Secret.SecretArn });
        _ = new CfnOutput(this, "BootstrapAdminSecretArn", new CfnOutputProps { Value = adminPassword.SecretArn });
    }

    private static CfnService.KeyValuePairProperty Variable(string name, string value) => new()
    {
        Name = name,
        Value = value,
    };
}