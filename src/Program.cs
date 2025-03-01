﻿/*----------------------------------------------------------------
 *  Copyright (c) ThoughtWorks, Inc.
 *  Licensed under the Apache License, Version 2.0
 *  See LICENSE.txt in the project root for license information.
 *----------------------------------------------------------------*/


using System.Diagnostics;
using System.Net;
using System.Reflection;
using Gauge.Dotnet.DataStore;
using Gauge.Dotnet.Exceptions;
using Gauge.Dotnet.Executors;
using Gauge.Dotnet.Extensions;
using Gauge.Dotnet.Loaders;
using Gauge.Dotnet.Processors;
using Gauge.Dotnet.Registries;
using Gauge.Dotnet.Wrappers;
using Gauge.Messages;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Console;

namespace Gauge.Dotnet;

internal static class Program
{
    private static ILogger _logger = null;

    [STAThread]
    [DebuggerHidden]
    private static async Task Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "--start")
        {
            Console.WriteLine("usage: {0} --start", AppDomain.CurrentDomain.FriendlyName);
            Environment.Exit(1);
        }

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Configuration.SetupConfiguration();
            builder.Logging.SetupLogging();

            Environment.CurrentDirectory = builder.Configuration.GetGaugeProjectRoot();
            var buildSucceeded = new GaugeProjectBuilder(builder.Configuration).BuildTargetGaugeProject();

            builder.WebHost.ConfigureKestrel(opts =>
            {
                opts.Listen(IPAddress.Parse("127.0.0.1"), 0, (opt) => { opt.Protocols = HttpProtocols.Http2; });
            });
            builder.Services.ConfigureServices(builder.Configuration);
            var app = builder.Build();
            _logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Gauge");

            if (!buildSucceeded && !app.Configuration.IgnoreBuildFailures())
            {
                return;
            }

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                var ports = app.Urls.Select(x => new Uri(x).Port).Distinct();
                foreach (var port in ports)
                {
                    Console.WriteLine($"Listening on port:{port}");
                }
            });

            if (buildSucceeded)
            {
                // Generate step registry before starting GRPC service
                _ = app.Services.GetRequiredService<IAssemblyLoader>().GetStepRegistry();
                app.MapGrpcService<ExecutableRunnerServiceHandler>();
            }
            else
            {
                app.MapGrpcService<AuthoringRunnerServiceHandler>();
            }
            app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client. To learn how to create a client, visit: https://go.microsoft.com/fwlink/?linkid=2086909");

            await app.RunAsync();
        }
        catch (TargetInvocationException e)
        {
            if (e.InnerException is not GaugeLibVersionMismatchException)
                throw;
            _logger?.LogCritical(e.InnerException.Message);
            Environment.Exit(1);
        }
    }

    private static IConfigurationBuilder SetupConfiguration(this IConfigurationBuilder builder) =>
        builder.AddEnvironmentVariables();

    public static ILoggingBuilder SetupLogging(this ILoggingBuilder builder) =>
        builder.ClearProviders()
            .SetMinimumLevel(LogLevel.Debug)
            .AddFilter("Microsoft", LogLevel.Error)
            .AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Error)
            .AddFilter("Grpc.AspNetCore", LogLevel.Error)
            .AddConsole((opt) =>
            {
                opt.FormatterName = "GaugeLoggingFormatter";
            })
            .AddConsoleFormatter<GaugeLoggingFormatter, ConsoleFormatterOptions>((opt) =>
            {
                opt.IncludeScopes = true;
                opt.TimestampFormat = "HH:mm:ss ";
            });


    private static IServiceCollection ConfigureServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddGrpc();
        services.AddSingleton<IFileProvider>(new PhysicalFileProvider(config.GetGaugeBinDir()));
        services.AddSingleton<IAssemblyLocater, AssemblyLocater>();
        services.AddSingleton<IReflectionWrapper, ReflectionWrapper>();
        services.AddSingleton<IActivatorWrapper, ActivatorWrapper>();
        services.AddSingleton<IGaugeLoadContext>((sp) =>
        {
            return config.IsDaemon() ?
                new LockFreeGaugeLoadContext(sp.GetRequiredService<IAssemblyLocater>(), sp.GetRequiredService<ILogger<LockFreeGaugeLoadContext>>()) :
                new GaugeLoadContext(sp.GetRequiredService<IAssemblyLocater>(), sp.GetRequiredService<ILogger<GaugeLoadContext>>());
        });
        services.AddSingleton<IAssemblyLoader, AssemblyLoader>();
        services.AddSingleton<IDirectoryWrapper, DirectoryWrapper>();
        services.AddSingleton<IStaticLoader, StaticLoader>();
        services.AddSingleton<IAttributesLoader, AttributesLoader>();
        services.AddSingleton<IHookRegistry, HookRegistry>();
        services.AddSingleton<IStepRegistry>(s => s.GetRequiredService<IStaticLoader>().GetStepRegistry());
        services.AddSingleton<IExecutor, Executor>();
        services.AddSingleton<IStepExecutor, StepExecutor>();
        services.AddSingleton<IHookExecutor, HookExecutor>();
        services.AddSingleton<ITableFormatter, TableFormatter>();
        services.AddSingleton<IExecutionOrchestrator, ExecutionOrchestrator>();
        services.AddSingleton<IExecutionInfoMapper, ExecutionInfoMapper>();
        services.AddSingleton<IDataStoreFactory, DataStoreFactory>();
        services.AddTransient<IGaugeProcessor<StepValidateRequest, StepValidateResponse>, StepValidationProcessor>();
        services.AddTransient<IGaugeProcessor<CacheFileRequest, Empty>, CacheFileProcessor>();
        services.AddTransient<IGaugeProcessor<Empty, ImplementationFileGlobPatternResponse>, ImplementationFileGlobPatterProcessor>();
        services.AddTransient<IGaugeProcessor<Empty, ImplementationFileListResponse>, ImplementationFileListProcessor>();
        services.AddTransient<IGaugeProcessor<StepNameRequest, StepNameResponse>, StepNameProcessor>();
        services.AddTransient<IGaugeProcessor<StepNamesRequest, StepNamesResponse>, StepNamesProcessor>();
        services.AddTransient<IGaugeProcessor<StepPositionsRequest, StepPositionsResponse>, StepPositionsProcessor>();
        services.AddTransient<IGaugeProcessor<StubImplementationCodeRequest, FileDiff>, StubImplementationCodeProcessor>();
        services.AddTransient<IGaugeProcessor<RefactorRequest, RefactorResponse>, RefactorProcessor>();
        services.AddTransient<IGaugeProcessor<SuiteDataStoreInitRequest, ExecutionStatusResponse>, SuiteDataStoreInitProcessor>();
        services.AddTransient<IGaugeProcessor<SuiteDataStoreInitRequest, ExecutionStatusResponse>, SuiteDataStoreInitProcessor>();
        services.AddTransient<IGaugeProcessor<ExecuteStepRequest, ExecutionStatusResponse>, ExecuteStepProcessor>();
        services.AddTransient<IGaugeProcessor<ExecutionEndingRequest, ExecutionStatusResponse>, ExecutionEndingProcessor>();
        services.AddTransient<IGaugeProcessor<ScenarioExecutionEndingRequest, ExecutionStatusResponse>, ScenarioExecutionEndingProcessor>();
        services.AddTransient<IGaugeProcessor<SpecExecutionEndingRequest, ExecutionStatusResponse>, SpecExecutionEndingProcessor>();
        services.AddTransient<IGaugeProcessor<StepExecutionEndingRequest, ExecutionStatusResponse>, StepExecutionEndingProcessor>();
        services.AddTransient<IGaugeProcessor<ScenarioDataStoreInitRequest, ExecutionStatusResponse>, ScenarioDataStoreInitProcessor>();
        services.AddTransient<IGaugeProcessor<SpecDataStoreInitRequest, ExecutionStatusResponse>, SpecDataStoreInitProcessor>();
        services.AddTransient<IGaugeProcessor<ExecutionStartingRequest, ExecutionStatusResponse>, ExecutionStartingProcessor>();
        services.AddTransient<IGaugeProcessor<ScenarioExecutionStartingRequest, ExecutionStatusResponse>, ScenarioExecutionStartingProcessor>();
        services.AddTransient<IGaugeProcessor<SpecExecutionStartingRequest, ExecutionStatusResponse>, SpecExecutionStartingProcessor>();
        services.AddTransient<IGaugeProcessor<StepExecutionStartingRequest, ExecutionStatusResponse>, StepExecutionStartingProcessor>();
        services.AddTransient<IGaugeProcessor<ConceptExecutionStartingRequest, ExecutionStatusResponse>, ConceptExecutionStartingProcessor>();
        services.AddTransient<IGaugeProcessor<ConceptExecutionEndingRequest, ExecutionStatusResponse>, ConceptExecutionEndingProcessor>();
        return services;
    }
}