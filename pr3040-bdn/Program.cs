using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.LoadBalancing;

var job = Environment.GetEnvironmentVariable("BDN_JOB") switch
{
    "Dry" => Job.Dry,
    "Medium" => Job.MediumRun,
    _ => Job.ShortRun,
};

var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(job
        .WithMsBuildArguments("/p:YarpPackageVersion=0.0.0-baseline")
        .WithEnvironmentVariable("EXPECTED_YARP_SHA", "0c813f18eeea95564753baa3032c9a8a2dc1b92caa25f2985bdba313400bd9f4")
        .WithId("baseline-49b23da6d8")
        .AsBaseline())
    .AddJob(job
        .WithMsBuildArguments("/p:YarpPackageVersion=0.0.0-candidate")
        .WithEnvironmentVariable("EXPECTED_YARP_SHA", "d1e6a57fa93b0aa829680b05f46a859da3b58404a15c19813f59448ecb7ac190")
        .WithId("candidate-4e8c0103f2"))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddColumn(StatisticalTestColumn.Create("5%"))
    .AddExporter(MarkdownExporter.GitHub, CsvExporter.Default);
config.BuildTimeout = TimeSpan.FromMinutes(15);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);

public enum PipelineScenario
{
    DefaultMinimalAsync,
    DefaultMinimalSync,
    Routes1000Async,
    Destinations64P2CAsync,
    PassiveHealthEnabledAsync,
    CustomNoPassiveAsync,
}

public class PassiveHealthPipelineBenchmark
{
    private RequestDelegate _pipeline = null!;
    private IServiceProvider _services = null!;
    private StubForwarder _forwarder = null!;
    private string _path = "/";

    [ParamsAllValues]
    public PipelineScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        VerifyProductAssembly();
        var settings = GetSettings(Scenario);
        var (routes, clusters) = BuildConfig(settings);
        (_pipeline, _services, _forwarder) = BuildProxyPipeline(
            routes, clusters, settings.ForceAsync, settings.CustomNoPassive);
        _path = settings.RouteCount == 1 ? "/" : $"/r{settings.RouteCount - 1}/item";
    }

    private static void VerifyProductAssembly()
    {
        var expected = Environment.GetEnvironmentVariable("EXPECTED_YARP_SHA")
            ?? throw new InvalidOperationException("EXPECTED_YARP_SHA is not set.");
        var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(RouteConfig).Assembly.Location)));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Loaded Yarp.ReverseProxy.dll hash {actual}, expected {expected}.");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => (_services as IDisposable)?.Dispose();

    [Benchmark]
    public Task Invoke()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = _services,
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = _path;
        context.Request.Protocol = "HTTP/1.1";
        var task = _pipeline(context);
        _forwarder.Complete();
        return task;
    }

    private static ScenarioSettings GetSettings(PipelineScenario scenario) => scenario switch
    {
        PipelineScenario.DefaultMinimalAsync => new(1, 1, null, false, true, false),
        PipelineScenario.DefaultMinimalSync => new(1, 1, null, false, false, false),
        PipelineScenario.Routes1000Async => new(1000, 1, null, false, true, false),
        PipelineScenario.Destinations64P2CAsync => new(1, 64, LoadBalancingPolicies.PowerOfTwoChoices, false, true, false),
        PipelineScenario.PassiveHealthEnabledAsync => new(1, 1, null, true, true, false),
        PipelineScenario.CustomNoPassiveAsync => new(1, 1, null, false, true, true),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static (RouteConfig[] Routes, ClusterConfig[] Clusters) BuildConfig(ScenarioSettings settings)
    {
        var routes = new RouteConfig[settings.RouteCount];
        for (var i = 0; i < routes.Length; i++)
        {
            routes[i] = new RouteConfig
            {
                RouteId = $"route{i}",
                ClusterId = "cluster0",
                Order = 0,
                Match = new RouteMatch
                {
                    Path = settings.RouteCount == 1 ? "/{**catchall}" : $"/r{i}/{{**catchall}}",
                },
            };
        }

        var destinations = new Dictionary<string, DestinationConfig>(settings.DestinationCount);
        for (var i = 0; i < settings.DestinationCount; i++)
        {
            destinations[$"dest{i}"] = new DestinationConfig { Address = $"http://127.0.0.1:{5000 + i}/" };
        }

        var cluster = new ClusterConfig
        {
            ClusterId = "cluster0",
            Destinations = destinations,
            LoadBalancingPolicy = settings.LoadBalancingPolicy,
            HealthCheck = settings.PassiveHealth
                ? new HealthCheckConfig
                {
                    Passive = new PassiveHealthCheckConfig
                    {
                        Enabled = true,
                        Policy = "TransportFailureRate",
                        ReactivationPeriod = TimeSpan.FromSeconds(10),
                    },
                }
                : null,
        };

        return (routes, [cluster]);
    }

    private static (RequestDelegate Pipeline, IServiceProvider Services, StubForwarder Forwarder) BuildProxyPipeline(
        RouteConfig[] routes,
        ClusterConfig[] clusters,
        bool forceAsync,
        bool customNoPassive)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddMetrics();
        var listener = new DiagnosticListener("PassiveHealthBdn");
        services.AddSingleton(listener);
        services.AddSingleton<DiagnosticSource>(listener);
        services.AddReverseProxy().LoadFromMemory(routes, clusters);
        services.RemoveAll<IHttpForwarder>();
        var forwarder = new StubForwarder(forceAsync);
        services.AddSingleton<IHttpForwarder>(forwarder);

        var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            if (customNoPassive)
            {
                endpoints.MapReverseProxy(proxy =>
                {
                    proxy.UseSessionAffinity();
                    proxy.UseLoadBalancing();
                });
            }
            else
            {
                endpoints.MapReverseProxy();
            }
        });

        return (app.Build(), provider, forwarder);
    }

    private sealed record ScenarioSettings(
        int RouteCount,
        int DestinationCount,
        string? LoadBalancingPolicy,
        bool PassiveHealth,
        bool ForceAsync,
        bool CustomNoPassive);

    private sealed class StubForwarder : IHttpForwarder, IValueTaskSource<ForwarderError>
    {
        private ManualResetValueTaskSourceCore<ForwarderError> _source;
        private bool _pending;
        private readonly bool _forceAsync;

        public StubForwarder(bool forceAsync)
        {
            _forceAsync = forceAsync;
            _source.RunContinuationsAsynchronously = false;
        }

        public ValueTask<ForwarderError> SendAsync(
            HttpContext context,
            string destinationPrefix,
            HttpMessageInvoker httpClient,
            ForwarderRequestConfig requestConfig,
            HttpTransformer transformer)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            if (!_forceAsync)
            {
                return ValueTask.FromResult(ForwarderError.None);
            }

            _source.Reset();
            _pending = true;
            return new ValueTask<ForwarderError>(this, _source.Version);
        }

        public void Complete()
        {
            if (_pending)
            {
                _pending = false;
                _source.SetResult(ForwarderError.None);
            }
        }

        ForwarderError IValueTaskSource<ForwarderError>.GetResult(short token) => _source.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<ForwarderError>.GetStatus(short token) => _source.GetStatus(token);

        void IValueTaskSource<ForwarderError>.OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) => _source.OnCompleted(continuation, state, token, flags);
    }
}
