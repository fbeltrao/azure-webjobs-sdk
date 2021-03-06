﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DependencyCollector;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.ApplicationInsights.Extensibility.PerfCounterCollector.QuickPulse;
using Microsoft.ApplicationInsights.SnapshotCollector;
using Microsoft.ApplicationInsights.WindowsServer;
using Microsoft.ApplicationInsights.WindowsServer.Channel.Implementation;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection
{
    internal static class ApplicationInsightsServiceCollectionExtensions
    {
        public static IServiceCollection AddApplicationInsights(this IServiceCollection services, Action<ApplicationInsightsLoggerOptions> configure)
        {
            services.AddApplicationInsights();
            if (configure != null)
            {
                services.Configure<ApplicationInsightsLoggerOptions>(configure);
            }
            return services;
        }

        public static IServiceCollection AddApplicationInsights(this IServiceCollection services)
        {
            // Bind to the configuration section registered with 
            services.AddOptions<ApplicationInsightsLoggerOptions>()
                .Configure<ILoggerProviderConfiguration<ApplicationInsightsLoggerProvider>>((options, config) =>
                {
                    config.Configuration?.Bind(options);
                });

            services.AddSingleton<ITelemetryInitializer, HttpDependenciesParsingTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsRoleEnvironmentTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsTelemetryInitializer>();
            services.AddSingleton<ITelemetryInitializer, WebJobsSanitizingInitializer>();
            services.AddSingleton<ITelemetryModule, QuickPulseTelemetryModule>();
            services.AddSingleton<ITelemetryModule, DependencyTrackingTelemetryModule>(provider =>
            {
                var dependencyCollector = new DependencyTrackingTelemetryModule();
                var excludedDomains = dependencyCollector.ExcludeComponentCorrelationHttpHeadersOnDomains;
                excludedDomains.Add("core.windows.net");
                excludedDomains.Add("core.chinacloudapi.cn");
                excludedDomains.Add("core.cloudapi.de");
                excludedDomains.Add("core.usgovcloudapi.net");
                excludedDomains.Add("localhost");
                excludedDomains.Add("127.0.0.1");

                return dependencyCollector;
            });
            services.AddSingleton<ITelemetryModule, AppServicesHeartbeatTelemetryModule>();

            ServerTelemetryChannel serverChannel = new ServerTelemetryChannel();
            services.AddSingleton<ITelemetryChannel>(serverChannel);
            services.AddSingleton<TelemetryConfiguration>(provider =>
            {
                ApplicationInsightsLoggerOptions options = provider.GetService<IOptions<ApplicationInsightsLoggerOptions>>().Value;
                LoggerFilterOptions filterOptions = CreateFilterOptions(provider.GetService<IOptions<LoggerFilterOptions>>().Value);

                // Because of https://github.com/Microsoft/ApplicationInsights-dotnet-server/issues/943
                // we have to touch (and create) Active configuration before initializing telemetry modules 
                TelemetryConfiguration activeConfig = TelemetryConfiguration.Active;


                ITelemetryChannel channel = provider.GetService<ITelemetryChannel>();
                TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();
                SetupTelemetryConfiguration(
                    config,
                    options.InstrumentationKey,
                    options.SamplingSettings,
                    options.SnapshotConfiguration,
                    channel,
                    provider.GetServices<ITelemetryInitializer>(),
                    provider.GetServices<ITelemetryModule>(),
                    filterOptions);

                // Function users have no access to TelemetryConfiguration from host DI container,
                // so we'll expect user to work with TelemetryConfiguration.Active
                // Also, some ApplicationInsights internal operations (heartbeats) depend on
                // the TelemetryConfiguration.Active being set so, we'll set up Active once per process lifetime.
                if (string.IsNullOrEmpty(activeConfig.InstrumentationKey))
                {
                    SetupTelemetryConfiguration(
                        activeConfig,
                        options.InstrumentationKey,
                        options.SamplingSettings,
                        options.SnapshotConfiguration,
                        new ServerTelemetryChannel(),
                        provider.GetServices<ITelemetryInitializer>(),
                        provider.GetServices<ITelemetryModule>(),
                        filterOptions);
                }
                return config;
            });

            services.AddSingleton<TelemetryClient>(provider =>
            {
                TelemetryConfiguration configuration = provider.GetService<TelemetryConfiguration>();
                TelemetryClient client = new TelemetryClient(configuration);

                string assemblyVersion = GetAssemblyFileVersion(typeof(JobHost).Assembly);
                client.Context.GetInternalContext().SdkVersion = $"webjobs: {assemblyVersion}";

                return client;
            });

            services.AddSingleton<ILoggerProvider, ApplicationInsightsLoggerProvider>();

            return services;
        }

        internal static LoggerFilterOptions CreateFilterOptions(LoggerFilterOptions registeredOptions)
        {
            // We want our own copy of the rules, excluding the 'allow-all' rule that we added for this provider.
            LoggerFilterOptions customFilterOptions = new LoggerFilterOptions
            {
                MinLevel = registeredOptions.MinLevel
            };

            ApplicationInsightsLoggerFilterRule allowAllRule = registeredOptions.Rules.OfType<ApplicationInsightsLoggerFilterRule>().Single();

            // Copy all existing rules
            foreach (LoggerFilterRule rule in registeredOptions.Rules)
            {
                if (rule != allowAllRule)
                {
                    customFilterOptions.Rules.Add(rule);
                }
            }

            // Copy 'hidden' rules
            foreach (LoggerFilterRule rule in allowAllRule.ChildRules)
            {
                customFilterOptions.Rules.Add(rule);
            }

            return customFilterOptions;
        }

        private static void SetupTelemetryConfiguration(
            TelemetryConfiguration configuration,
            string instrumentationKey,
            SamplingPercentageEstimatorSettings samplingSettings,
            SnapshotCollectorConfiguration snapshotCollectorConfiguration,
            ITelemetryChannel channel,
            IEnumerable<ITelemetryInitializer> telemetryInitializers,
            IEnumerable<ITelemetryModule> telemetryModules,
            LoggerFilterOptions filterOptions)
        {
            if (instrumentationKey != null)
            {
                configuration.InstrumentationKey = instrumentationKey;
            }

            configuration.TelemetryChannel = channel;

            foreach (ITelemetryInitializer initializer in telemetryInitializers)
            {
                configuration.TelemetryInitializers.Add(initializer);
            }

            (channel as ServerTelemetryChannel)?.Initialize(configuration);

            QuickPulseTelemetryModule quickPulseModule = null;
            foreach (ITelemetryModule module in telemetryModules)
            {
                if (module is QuickPulseTelemetryModule telemetryModule)
                {
                    quickPulseModule = telemetryModule;
                }

                module.Initialize(configuration);
            }

            QuickPulseTelemetryProcessor quickPulseProcessor = null;
            configuration.TelemetryProcessorChainBuilder
                .Use((next) =>
                {
                    quickPulseProcessor = new QuickPulseTelemetryProcessor(next);
                    return quickPulseProcessor;
                })
                .Use((next) => new FilteringTelemetryProcessor(filterOptions, next));

            if (samplingSettings != null)
            {
                configuration.TelemetryProcessorChainBuilder.Use((next) =>
                    new AdaptiveSamplingTelemetryProcessor(samplingSettings, null, next));
            }

            if (snapshotCollectorConfiguration != null)
            {
                configuration.TelemetryProcessorChainBuilder.UseSnapshotCollector(snapshotCollectorConfiguration);
            }

            configuration.TelemetryProcessorChainBuilder.Build();
            quickPulseModule?.RegisterTelemetryProcessor(quickPulseProcessor);

            foreach (ITelemetryProcessor processor in configuration.TelemetryProcessors)
            {
                if (processor is ITelemetryModule module)
                {
                    module.Initialize(configuration);
                }
            }
        }
        internal static string GetAssemblyFileVersion(Assembly assembly)
        {
            AssemblyFileVersionAttribute fileVersionAttr = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>();
            return fileVersionAttr?.Version ?? LoggingConstants.Unknown;
        }
    }
}