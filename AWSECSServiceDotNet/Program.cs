using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Amazon;
using Amazon.CloudWatchLogs.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Api.Models;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace AWSECSTaskCreationDotNet
{
   class DockerPushProgress : IProgress<JSONMessage>
   {
      public void Report( JSONMessage value )
      {
         if ( value.Progress == null )
            Console.WriteLine( $"Progress {value.Status}" );
         else
            Console.WriteLine( $"Progress. Status {value.Status}, ID {value.ID}, Current {value.Progress.Current}, Total {value.Progress.Total}" );
      }
   }

   class Program
   {
      static async System.Threading.Tasks.Task Main( string[] args )
      {
         var configBuilder = new ConfigurationBuilder().AddJsonFile( "appsettings.json" );
         var config = configBuilder.Build();

         var purchasesRepoUri = await CreateECRRepositoryAsync( config, "purchasesapi" );
         await CreateServiceWithServiceDiscovery( config, "purchases-svc", "purchases-svc-task", purchasesRepoUri, false );

         var recommendationsRepoUri = await CreateECRRepositoryAsync( config, "recommendationsapi" );
         await CreateServiceWithServiceDiscovery( config, "recommendations-svc", "recommendations-svc-task", recommendationsRepoUri, false );

         var dashboardRepoUri = await CreateECRRepositoryAsync( config, "dashboardapi" );
         var taskIP = await CreateServiceWithServiceDiscovery( config, "dashboard-svc", "dashboard-svc-task", dashboardRepoUri, true );

         Console.WriteLine( "Giving some time (10 seconds) for the dashboard service task to come up and start accepting HTTP requests..." );
         await System.Threading.Tasks.Task.Delay( 10000 );

         await TestDashboardApiAsync( config, taskIP );
      }

      // Create ECR repository from the given docker image
      static async Task<string> CreateECRRepositoryAsync( IConfiguration config, string dockerImageName )
      {
         Amazon.ECR.AmazonECRClient client = new Amazon.ECR.AmazonECRClient(
                                 config["ECRApiKey"],
                                 config["ECRApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         // Create a repository in the AWS ECR (Elastic Container Registry)
         var response = await client.CreateRepositoryAsync( new Amazon.ECR.Model.CreateRepositoryRequest()
         {
            RepositoryName = $"packages/{dockerImageName}-{Guid.NewGuid().ToString( "N" )}"
         } );

         if ( response.HttpStatusCode != System.Net.HttpStatusCode.OK )
         {
            Console.WriteLine( $"Failed to create repository in AWS ECR. Status {response.HttpStatusCode}" );
            return null;
         }

         Console.WriteLine( $"Repository created. Uri {response.Repository.RepositoryUri}" );

         var packageUri = response.Repository.RepositoryUri;

         // Generate access token to push your local docker image to the newly created repository on ECR
         Console.WriteLine( "Generating access token to push the docker image..." );
         var authResponse = await client.GetAuthorizationTokenAsync( new Amazon.ECR.Model.GetAuthorizationTokenRequest()
         {
         } );

         if ( authResponse.HttpStatusCode != System.Net.HttpStatusCode.OK )
         {
            Console.WriteLine( $"Failed to generate access token for AWS ECR. Status {authResponse.HttpStatusCode}" );
            return null;
         }

         var accessToken = Encoding.UTF8.GetString( Convert.FromBase64String( authResponse.AuthorizationData.First().AuthorizationToken ) ).Split( ":" )[1];

         // Create the local docker client
         DockerClient dockerClient = new DockerClientConfiguration().CreateClient();

         // Associate the remote ECR repository Uri as tag to the local docker image
         Console.WriteLine( "Associating remote repository uri with given local docker image..." );
         await dockerClient.Images.TagImageAsync( dockerImageName, new ImageTagParameters()
         {
            RepositoryName = packageUri,
            Force = true
         } );

         // Push the docker image to the remote ECR repository
         await dockerClient.Images.PushImageAsync( packageUri, new ImagePushParameters()
         {
            Tag = "latest"
         }, new AuthConfig()
         {
            Username = "AWS",
            Password = accessToken,
            ServerAddress = authResponse.AuthorizationData.First().ProxyEndpoint
         }, new DockerPushProgress() );

         Console.WriteLine( "Docker push completed...press Enter to continue..." );
         Console.ReadLine();

         return packageUri;
      }

      // Create ECS log group, task definition, container definition, service defnition and wait until the service task is running,
      // and get the public IP of the task container
      private static async Task<string> CreateServiceWithServiceDiscovery(
         IConfiguration config,
         string serviceName,
         string taskDefinitionName,
         string repoUri,
         bool fetchPublicIP )
      {
         // Create the log group that will be used by the service tasks for logging to cloudwatch logs
         Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient logClient = new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         var logGroupsResponse = await logClient.DescribeLogGroupsAsync( new DescribeLogGroupsRequest()
         {
            LogGroupNamePrefix = $"/ecs/{serviceName}"
         } );

         if ( ( logGroupsResponse.LogGroups == null ) || ( logGroupsResponse.LogGroups.FirstOrDefault( lg => string.Compare( lg.LogGroupName, $"/ecs/{serviceName}", true ) == 0 ) == null ) )
         {
            var logResponse = await logClient.CreateLogGroupAsync( new Amazon.CloudWatchLogs.Model.CreateLogGroupRequest()
            {
               LogGroupName = $"/ecs/{serviceName}"
            } );

            Console.WriteLine( $"Log Group creation status {logResponse.HttpStatusCode}" );
         }
         else
         {
            Console.WriteLine( "Log group exists...proceeding..." );
         }

         // Create a task definition using docker image pushed to our AWS ECR repository
         AmazonECSClient ecsClient = new AmazonECSClient(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );

         var taskResponse = await ecsClient.RegisterTaskDefinitionAsync( new Amazon.ECS.Model.RegisterTaskDefinitionRequest()
         {
            RequiresCompatibilities = new List<string>() { "FARGATE" },
            TaskRoleArn = "ecsTaskExecutionRole",
            ExecutionRoleArn = "ecsTaskExecutionRole",
            Cpu = "256",
            Memory = "512",
            NetworkMode = NetworkMode.Awsvpc,
            Family = taskDefinitionName,
            ContainerDefinitions = new List<Amazon.ECS.Model.ContainerDefinition>()
            {
               new Amazon.ECS.Model.ContainerDefinition()
               {
                  Name = $"{serviceName}-container",
                  Image = repoUri,
                  Cpu = 256,
                  Memory = 512,
                  Essential = true,
                  LogConfiguration = new Amazon.ECS.Model.LogConfiguration()
                  {
                     LogDriver = LogDriver.Awslogs,
                     Options = new Dictionary<string, string>()
                     {
                        { "awslogs-group", $"/ecs/{serviceName}" },
                        { "awslogs-region", config["ECRRegion"] },
                        { "awslogs-stream-prefix", "ecs" }
                     }
                  }
               }
            }
         } );

         Console.WriteLine( $"Task definition creation status {taskResponse.HttpStatusCode}" );

         // Create a service discovery entry to link the service to the Cloud Map namespace
         Amazon.ServiceDiscovery.AmazonServiceDiscoveryClient svcDiscoveryClient =
            new Amazon.ServiceDiscovery.AmazonServiceDiscoveryClient(
                        config["ECSApiKey"],
                        config["ECSApiSecret"],
                        RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );


         var existingSvcs = await svcDiscoveryClient.ListServicesAsync( new Amazon.ServiceDiscovery.Model.ListServicesRequest()
         {
            Filters = new List<Amazon.ServiceDiscovery.Model.ServiceFilter>()
            {
               new Amazon.ServiceDiscovery.Model.ServiceFilter()
               {
                  Condition = Amazon.ServiceDiscovery.FilterCondition.EQ,
                  Name = Amazon.ServiceDiscovery.ServiceFilterName.NAMESPACE_ID,
                  Values = new List<string>() { config["ServiceDiscoveryNamespaceId"] }
               }
            }
         } );

         var existingSvc = existingSvcs.Services.FirstOrDefault( obj => string.Compare( obj.Name, serviceName, true ) == 0 );

         Amazon.ServiceDiscovery.Model.CreateServiceResponse svcDiscoveryServiceResponse = null;

         if ( existingSvc == null )
         {
            svcDiscoveryServiceResponse = await svcDiscoveryClient.CreateServiceAsync( new Amazon.ServiceDiscovery.Model.CreateServiceRequest()
            {
               DnsConfig = new Amazon.ServiceDiscovery.Model.DnsConfig()
               {
                  DnsRecords = new List<Amazon.ServiceDiscovery.Model.DnsRecord>()
                  {
                     new Amazon.ServiceDiscovery.Model.DnsRecord()
                     {
                        Type = Amazon.ServiceDiscovery.RecordType.A,
                        TTL = 60
                     }
                  }
               },
               NamespaceId = config["ServiceDiscoveryNamespaceId"], // namespace id of the "ecom" namespace
               Name = serviceName,
               HealthCheckCustomConfig = new Amazon.ServiceDiscovery.Model.HealthCheckCustomConfig()
               {
                  FailureThreshold = 1
               }
            } );

            Console.WriteLine( $"Service Discovery Service creation status {svcDiscoveryServiceResponse.HttpStatusCode} for service {serviceName}." );

            if ( svcDiscoveryServiceResponse.HttpStatusCode != System.Net.HttpStatusCode.OK )
               throw new ApplicationException( $"Failed to created service discovery service for {serviceName} service" );
         }

         // Create or update the service, including assigning it to the service discovery entry that we create above
         var existingServices = await ecsClient.DescribeServicesAsync( new DescribeServicesRequest()
         {
            Cluster = config["Cluster"],
            Services = new List<string>() { serviceName }
         } );

         CreateServiceResponse svcResponse = null;

         if ( existingServices.Services.FirstOrDefault( obj => string.Compare( obj.ServiceName, serviceName, true ) == 0 ) == null )
         {
            // Create purchases service
            svcResponse = await ecsClient.CreateServiceAsync( new Amazon.ECS.Model.CreateServiceRequest()
            {
               Cluster = config["Cluster"],
               DesiredCount = 1,
               LaunchType = LaunchType.FARGATE,
               NetworkConfiguration = new Amazon.ECS.Model.NetworkConfiguration()
               {
                  AwsvpcConfiguration = new Amazon.ECS.Model.AwsVpcConfiguration()
                  {
                     Subnets = config["Subnet"].Split( ',' ).ToList(),
                     AssignPublicIp = AssignPublicIp.ENABLED,
                     SecurityGroups = new List<string> { config["SecurityGroup"] }
                  }
               },
               ServiceName = serviceName,
               TaskDefinition = taskDefinitionName,
               ServiceRegistries = new List<Amazon.ECS.Model.ServiceRegistry>()
               {
                  new Amazon.ECS.Model.ServiceRegistry()
                  {
                     RegistryArn = (existingServices != null ) ? existingSvc.Arn : svcDiscoveryServiceResponse.Service.Arn
                  }
               }
            } );

            Console.WriteLine( $"{serviceName} service creation status {svcResponse.HttpStatusCode}." );

            if ( svcResponse.HttpStatusCode != System.Net.HttpStatusCode.OK )
               throw new ApplicationException( $"Failed to create {serviceName} service." );
         }
         else
         {
            // Update service
            var svcUpdateResponse = await ecsClient.UpdateServiceAsync( new Amazon.ECS.Model.UpdateServiceRequest()
            {
               Cluster = config["Cluster"],
               DesiredCount = 1,
               NetworkConfiguration = new Amazon.ECS.Model.NetworkConfiguration()
               {
                  AwsvpcConfiguration = new Amazon.ECS.Model.AwsVpcConfiguration()
                  {
                     Subnets = config["Subnet"].Split( ',' ).ToList(),
                     AssignPublicIp = AssignPublicIp.ENABLED,
                     SecurityGroups = new List<string> { config["SecurityGroup"] }
                  }
               },
               Service = serviceName,
               TaskDefinition = taskDefinitionName,
            } );

            Console.WriteLine( $"{serviceName} service update status {svcUpdateResponse.HttpStatusCode}." );

            if ( svcUpdateResponse.HttpStatusCode != System.Net.HttpStatusCode.OK )
               throw new ApplicationException( $"Failed to update {serviceName} service." );

            Console.WriteLine( "Found existing service task. Stopping it..." );

            var taskListResponse = await ecsClient.ListTasksAsync( new Amazon.ECS.Model.ListTasksRequest()
            {
               Cluster = config["Cluster"],
               DesiredStatus = DesiredStatus.RUNNING,
               Family = taskDefinitionName
            } );

            if ( taskListResponse.TaskArns != null && taskListResponse.TaskArns.Count > 0 )
            {
               var taskArnExisting = taskListResponse.TaskArns.First();

               await ecsClient.StopTaskAsync( new StopTaskRequest()
               {
                  Cluster = config["Cluster"],
                  Task = taskArnExisting
               } );

               await System.Threading.Tasks.Task.Delay( 5000 );
            }
         }

         // Wait for the service task to start running
         string taskArn = string.Empty;

         try
         {
            int rounds = 0;

            while ( true )
            {
               Console.WriteLine( $"Waiting for the {serviceName} service to start..." );

               var taskListResponse = await ecsClient.ListTasksAsync( new Amazon.ECS.Model.ListTasksRequest()
               {
                  Cluster = config["Cluster"],
                  DesiredStatus = DesiredStatus.RUNNING,
                  Family = taskDefinitionName
               } );

               if ( taskListResponse.TaskArns != null && taskListResponse.TaskArns.Count > 0 )
               {
                  taskArn = taskListResponse.TaskArns.First();

                  await System.Threading.Tasks.Task.Delay( 5000 );

                  break;
               }

               if ( ++rounds >= 300 ) // Wait for 5 minutes for the task to start
                  throw new ApplicationException( $"Timeout waiting for the {serviceName} service task to start." );

               await System.Threading.Tasks.Task.Delay( 1000 );
            }
         }
         catch ( Exception ex )
         {
            Console.WriteLine( ex.ToString() );
            throw;
         }

         // If we need public IP of the task for http calls, fetch it here
         if ( fetchPublicIP )
         {
            var responseTask = await ecsClient.DescribeTasksAsync( new Amazon.ECS.Model.DescribeTasksRequest()
            {
               Cluster = config["Cluster"],
               Tasks = new List<string> { taskArn },
            } );

            var task = responseTask.Tasks?.FirstOrDefault();
            string ipv4Addr = task.Containers.FirstOrDefault().NetworkInterfaces.FirstOrDefault().PrivateIpv4Address;

            var ec2Client = new Amazon.EC2.AmazonEC2Client(
                                 config["ECSApiKey"],
                                 config["ECSApiSecret"],
                                 RegionEndpoint.GetBySystemName( config["ECRRegion"] ) );
            var describeNetObj = new Amazon.EC2.Model.DescribeNetworkInterfacesRequest();
            describeNetObj.Filters.Add( new Amazon.EC2.Model.Filter()
            {
               Name = "private-ip-address",
               Values = new List<string> { ipv4Addr }
            } );
            var ec2Response = ec2Client.DescribeNetworkInterfacesAsync( describeNetObj ).Result;
            return ec2Response.NetworkInterfaces.FirstOrDefault().Association.PublicIp;
         }

         return null;
      }

      // Test the dashboard from the public uri of the running dashboard service task
      private static async System.Threading.Tasks.Task TestDashboardApiAsync( IConfiguration config, string taskIP )
      {
         Console.WriteLine( "Testing Dashboard API..." );

         var httpClient = new HttpClient();
         var dashboardJson = await httpClient.GetStringAsync( Uri.EscapeUriString( $"http://{taskIP}/dashboard" ) );
         Console.WriteLine( $"Dashboard JSON: {dashboardJson}" );

         var dashboard = JsonConvert.DeserializeObject<Dashboard>( dashboardJson );

         Console.WriteLine( $"\n>>> Purchases <<<\n" );

         foreach ( var purchase in dashboard.Purchases )
            Console.WriteLine( $"Book Name: {purchase.BookName}, Date Of Purchase: {purchase.DateOfPurchase}, Price: {purchase.Price}" );

         Console.WriteLine( $"\n>>> Recommendations <<<\n" );

         foreach ( var recommendation in dashboard.Recommendations )
            Console.WriteLine( $"Book Name: {recommendation.BookName}, Date Of Publishing: {recommendation.DateOfPublication}, Price: {recommendation.Price}" );

         Console.WriteLine( "\nPress Enter to quit..." );
         Console.ReadLine();
      }
   }
}
