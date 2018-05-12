// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.Service
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Tracing;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using Autofac;
    using Autofac.Extensions.DependencyInjection;
    using DotNetty.Common.Internal.Logging;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Mvc;
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Edge.Hub.CloudProxy;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Config;
    using Microsoft.Azure.Devices.Edge.Hub.Http;
    using Microsoft.Azure.Devices.Edge.Hub.Http.Middleware;
    using Microsoft.Azure.Devices.Edge.Hub.Mqtt;
    using Microsoft.Azure.Devices.Edge.Hub.Service.Modules;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Logging;
    using Microsoft.Azure.Devices.ProtocolGateway.Instrumentation;
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;

    public class Startup : IStartup
    {
        const string IotHubConnectionStringVariableName = "IotHubConnectionString";
        const string IotHubHostnameVariableName = "IOTEDGE_IOTHUBHOSTNAME";
        const string DeviceIdVariableName = "IOTEDGE_DEVICEID";
        const string ModuleIdVariableName = "IOTEDGE_MODULEID";
        readonly string iotHubHostname;
        readonly string edgeDeviceId;
        readonly string edgeModuleId;
        readonly Option<string> connectionString;

        // ReSharper disable once UnusedParameter.Local
        public Startup(IHostingEnvironment env)
        {
            this.Configuration = new ConfigurationBuilder()
                .AddJsonFile(Constants.ConfigFileName)
                .AddEnvironmentVariables()
                .Build();

            string edgeHubConnectionString = this.Configuration.GetValue<string>(IotHubConnectionStringVariableName);
            if (!string.IsNullOrWhiteSpace(edgeHubConnectionString))
            {
                IotHubConnectionStringBuilder iotHubConnectionStringBuilder = Client.IotHubConnectionStringBuilder.Create(edgeHubConnectionString);
                this.iotHubHostname = iotHubConnectionStringBuilder.HostName;
                this.edgeDeviceId = iotHubConnectionStringBuilder.DeviceId;
                this.edgeModuleId = iotHubConnectionStringBuilder.ModuleId;
                this.connectionString = Option.Some(edgeHubConnectionString);
            }
            else
            {
                this.iotHubHostname = this.Configuration.GetValue<string>(IotHubHostnameVariableName);
                this.edgeDeviceId = this.Configuration.GetValue<string>(DeviceIdVariableName);
                this.edgeModuleId = this.Configuration.GetValue<string>(ModuleIdVariableName);
                this.connectionString = Option.None<string>();
            }
            
            this.VersionInfo = VersionInfo.Get(Constants.VersionInfoFileName);
        }

        public IConfigurationRoot Configuration { get; }

        internal IContainer Container { get; private set; }

        public VersionInfo VersionInfo { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddMemoryCache();
            services.AddMvc(options => options.Filters.Add(typeof(ExceptionFilter)));

            services.Configure<MvcOptions>(options =>
            {
                options.Filters.Add(new RequireHttpsAttribute());
            });

            services.AddSingleton<IStartup>(sp => this);
            this.Container = this.BuildContainer(services);

            return new AutofacServiceProvider(this.Container);
        }

        IContainer BuildContainer(IServiceCollection services)
        {
            int connectionPoolSize = this.Configuration.GetValue<int>("IotHubConnectionPoolSize");
            bool optimizeForPerformance = this.Configuration.GetValue("OptimizeForPerformance", true);
            var topics = new MessageAddressConversionConfiguration(
                this.Configuration.GetSection(Constants.TopicNameConversionSectionName + ":InboundTemplates").Get<List<string>>(),
                this.Configuration.GetSection(Constants.TopicNameConversionSectionName + ":OutboundTemplates").Get<Dictionary<string, string>>());

            string configSource = this.Configuration.GetValue<string>("configSource");
            bool useTwinConfig = !string.IsNullOrWhiteSpace(configSource) && configSource.Equals("twin", StringComparison.OrdinalIgnoreCase);

            var routes = this.Configuration.GetSection("routes").Get<Dictionary<string, string>>();
            (bool isEnabled, bool usePersistentStorage, StoreAndForwardConfiguration config, string storagePath) storeAndForward = this.GetStoreAndForwardConfiguration();

            IConfiguration mqttSettingsConfiguration = this.Configuration.GetSection("appSettings");
            Option<UpstreamProtocol> upstreamProtocolOption = Enum.TryParse(this.Configuration.GetValue("UpstreamProtocol", string.Empty), false, out UpstreamProtocol upstreamProtocol)
                ? Option.Some(upstreamProtocol)
                : Option.None<UpstreamProtocol>();
            int connectivityCheckFrequencySecs = this.Configuration.GetValue<int>("ConnectivityCheckFrequencySecs", 300);
            TimeSpan connectivityCheckFrequency = connectivityCheckFrequencySecs < 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(connectivityCheckFrequencySecs);

            // Get hub's server cert
            string certPath = Path.Combine(
                this.Configuration.GetValue<string>(Constants.SslCertPathEnvName),
                this.Configuration.GetValue<string>(Constants.SslCertEnvName));
            var tlsCertificate = new X509Certificate2(certPath);

            bool clientCertAuthEnabled = this.Configuration.GetValue("ClientCertAuthEnabled", false);
            string caChainPath = this.Configuration.GetValue("EdgeModuleHubServerCAChainCertificateFile", string.Empty);

            IConfiguration amqpSettings = this.Configuration.GetSection("amqp");

            var builder = new ContainerBuilder();
            builder.Populate(services);

            builder.RegisterModule(new LoggingModule());
            builder.RegisterBuildCallback(
                c =>
                {
                    // set up loggers for dotnetty
                    var loggerFactory = c.Resolve<ILoggerFactory>();
                    InternalLoggerFactory.DefaultFactory = loggerFactory;

                    var eventListener = new LoggerEventListener(loggerFactory.CreateLogger("ProtocolGateway"));
                    eventListener.EnableEvents(CommonEventSource.Log, EventLevel.Informational);
                });

            // Register modules
            builder.RegisterModule(
                new CommonModule(
                    this.GetProductInfo(),
                    this.iotHubHostname,
                    this.edgeDeviceId));
            builder.RegisterModule(
                new RoutingModule(
                    this.iotHubHostname,
                    this.edgeDeviceId,
                    this.edgeModuleId,
                    this.connectionString,
                    routes,
                    storeAndForward.isEnabled,
                    storeAndForward.usePersistentStorage,
                    storeAndForward.config,
                    storeAndForward.storagePath,
                    connectionPoolSize,
                    useTwinConfig,
                    this.VersionInfo,
                    upstreamProtocolOption,
                    optimizeForPerformance,
                    connectivityCheckFrequency));

            builder.RegisterModule(new MqttModule(mqttSettingsConfiguration, topics, tlsCertificate, storeAndForward.isEnabled, clientCertAuthEnabled, caChainPath, optimizeForPerformance));
            builder.RegisterModule(new AmqpModule(amqpSettings["scheme"], amqpSettings.GetValue<ushort>("port"), tlsCertificate, this.iotHubHostname));
            builder.RegisterModule(new HttpModule());
            builder.RegisterInstance<IStartup>(this);

            IContainer container = builder.Build();
            return container;
        }

        public void Configure(IApplicationBuilder app)
        {
            var webSocketListenerRegistry = app.ApplicationServices.GetService(typeof(IWebSocketListenerRegistry)) as IWebSocketListenerRegistry;

            app.UseWebSockets();
            app.UseWebSocketHandlingMiddleware(webSocketListenerRegistry);
            app.UseAuthenticationMiddleware(this.iotHubHostname);
            app.UseMvc();
        }

        (bool isEnabled, bool usePersistentStorage, StoreAndForwardConfiguration config, string storagePath) GetStoreAndForwardConfiguration()
        {
            int defaultTtl = -1;
            bool isEnabled = this.Configuration.GetValue<bool>("storeAndForwardEnabled");
            bool usePersistentStorage = this.Configuration.GetValue<bool>("usePersistentStorage");
            int timeToLiveSecs = defaultTtl;
            string storagePath = string.Empty;
            if (isEnabled)
            {
                IConfiguration storeAndForwardConfigurationSection = this.Configuration.GetSection("storeAndForward");
                timeToLiveSecs = storeAndForwardConfigurationSection.GetValue("timeToLiveSecs", defaultTtl);

                if (usePersistentStorage)
                {
                    storagePath = this.GetStoragePath();
                }
            }
            var storeAndForwardConfiguration = new StoreAndForwardConfiguration(timeToLiveSecs);
            return (isEnabled, usePersistentStorage, storeAndForwardConfiguration, storagePath);
        }

        string GetProductInfo()
        {
            string name = "Microsoft.Azure.Devices.Edge.Hub";
            string version = FileVersionInfo.GetVersionInfo(typeof(Startup).Assembly.Location).ProductVersion;
            return $"{name}/{version}";
        }

        string GetStoragePath()
        {
            string baseStoragePath = this.Configuration.GetValue<string>("storageFolder");
            if (string.IsNullOrWhiteSpace(baseStoragePath) || !Directory.Exists(baseStoragePath))
            {
                baseStoragePath = Path.GetTempPath();
            }
            string storagePath = Path.Combine(baseStoragePath, Constants.EdgeHubStorageFolder);
            Directory.CreateDirectory(storagePath);
            return storagePath;
        }
    }
}
