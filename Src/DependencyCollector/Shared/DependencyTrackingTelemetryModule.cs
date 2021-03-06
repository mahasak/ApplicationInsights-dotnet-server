﻿namespace Microsoft.ApplicationInsights.DependencyCollector
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.CompilerServices;
    using Microsoft.ApplicationInsights.DependencyCollector.Implementation;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;
#if !NETCORE
    using Microsoft.Diagnostics.Instrumentation.Extensions.Intercept;
#else
    using Microsoft.Extensions.PlatformAbstractions;
    using System.Reflection;
    using System.Runtime.Versioning;
#endif

    /// <summary>
    /// Remote dependency monitoring.
    /// </summary>
    public class DependencyTrackingTelemetryModule : ITelemetryModule, IDisposable
    {
        private readonly object lockObject = new object();

#if NET45
        // Net40 does not support framework event source
        private HttpDesktopDiagnosticSourceListener httpDesktopDiagnosticSourceListener;
        private FrameworkHttpEventListener httpEventListener;
        private FrameworkSqlEventListener sqlEventListener;
#endif

        private HttpCoreDiagnosticSourceListener httpCoreDiagnosticSourceListener;

#if !NETCORE
        private ProfilerSqlCommandProcessing sqlCommandProcessing;
        private ProfilerSqlConnectionProcessing sqlConnectionProcessing;
        private ProfilerHttpProcessing httpProcessing;        
#endif
        private TelemetryConfiguration telemetryConfiguration;
        private bool isInitialized = false;
        private bool disposed = false;
        private bool correlationHeadersEnabled = true;
        private ICollection<string> excludedCorrelationDomains = new SanitizedHostList();

        /// <summary>
        /// Gets or sets a value indicating whether to disable runtime instrumentation.
        /// </summary>
        public bool DisableRuntimeInstrumentation { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to disable Http Desktop DiagnosticSource instrumentation.
        /// </summary>
        public bool DisableDiagnosticSourceInstrumentation { get; set; }

        /// <summary>
        /// Gets the component correlation configuration.
        /// </summary>
        public ICollection<string> ExcludeComponentCorrelationHttpHeadersOnDomains
        {
            get
            {
                return this.excludedCorrelationDomains;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the correlation headers would be set on outgoing http requests.
        /// </summary>
        public bool SetComponentCorrelationHttpHeaders
        {
            get
            {
                return this.correlationHeadersEnabled;
            }

            set
            {
                this.correlationHeadersEnabled = value;
            }
        }

        /// <summary>
        /// Gets or sets the endpoint that is to be used to get the application insights resource's profile (appId etc.).
        /// </summary>
        public string ProfileQueryEndpoint { get; set; }

        internal string EffectiveProfileQueryEndpoint
        {
            get
            {
                return string.IsNullOrEmpty(this.ProfileQueryEndpoint) ? this.telemetryConfiguration.TelemetryChannel.EndpointAddress : this.ProfileQueryEndpoint;
            }
        }

        /// <summary>
        /// IDisposable implementation.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Initialize method is called after all configuration properties have been loaded from the configuration.
        /// </summary>
        public void Initialize(TelemetryConfiguration configuration)
        {
            DependencyCollectorEventSource.Log.RemoteDependencyModuleVerbose("Initializing DependencyTrackingModule");

            // Temporary fix to make sure that we initialize module once.
            // It should be removed when configuration reading logic is moved to Web SDK.
            if (!this.isInitialized)
            {
                lock (this.lockObject)
                {
                    if (!this.isInitialized)
                    {
                        try
                        {                            
                            this.telemetryConfiguration = configuration;                            

#if !NETCORE
                            // Net40 only supports runtime instrumentation
                            // Net45 supports either but not both to avoid duplication
                            this.InitializeForRuntimeInstrumentationOrFramework();
#endif

                            // NET45 referencing .net core System.Net.Http supports diagnostic listener
                            this.httpCoreDiagnosticSourceListener = new HttpCoreDiagnosticSourceListener(
                                configuration,
                                this.EffectiveProfileQueryEndpoint,
                                this.SetComponentCorrelationHttpHeaders,
                                this.ExcludeComponentCorrelationHttpHeadersOnDomains, 
                                null);

                            DependencyCollectorEventSource.Log.RemoteDependencyModuleVerbose("Initializing DependencyTrackingModule completed successfully.");
                        }
                        catch (Exception exc)
                        {
                            string clrVersion;
#if NETCORE                            
                            clrVersion = System.Reflection.Assembly.GetEntryAssembly().GetCustomAttribute<TargetFrameworkAttribute>().FrameworkName;
#else
                            clrVersion = Environment.Version.ToString();
#endif
                            DependencyCollectorEventSource.Log.RemoteDependencyModuleError(exc.ToInvariantString(), clrVersion);
                        }

                        this.PrepareActivity();

                        this.isInitialized = true;
                    }
                }
            }
        }

#if !NETCORE
        internal virtual void InitializeForRuntimeProfiler()
        {
            // initialize instrumentation extension
            var extensionBaseDirectory = string.IsNullOrWhiteSpace(AppDomain.CurrentDomain.RelativeSearchPath)
                ? AppDomain.CurrentDomain.BaseDirectory
                : AppDomain.CurrentDomain.RelativeSearchPath;

            DependencyCollectorEventSource.Log.RemoteDependencyModuleInformation("extesionBaseDirectrory is " + extensionBaseDirectory);
            Decorator.InitializeExtension(extensionBaseDirectory);

            // obtain agent version
            var agentVersion = Decorator.GetAgentVersion();
            DependencyCollectorEventSource.Log.RemoteDependencyModuleInformation("AgentVersion is " + agentVersion);

            this.httpProcessing = new ProfilerHttpProcessing(this.telemetryConfiguration, agentVersion, DependencyTableStore.Instance.WebRequestConditionalHolder, this.SetComponentCorrelationHttpHeaders, this.ExcludeComponentCorrelationHttpHeadersOnDomains, this.EffectiveProfileQueryEndpoint);
            this.sqlCommandProcessing = new ProfilerSqlCommandProcessing(this.telemetryConfiguration, agentVersion, DependencyTableStore.Instance.SqlRequestConditionalHolder);
            this.sqlConnectionProcessing = new ProfilerSqlConnectionProcessing(this.telemetryConfiguration, agentVersion, DependencyTableStore.Instance.SqlRequestConditionalHolder);

            ProfilerRuntimeInstrumentation.DecorateProfilerForHttp(ref this.httpProcessing);
            ProfilerRuntimeInstrumentation.DecorateProfilerForSqlCommand(ref this.sqlCommandProcessing);
            ProfilerRuntimeInstrumentation.DecorateProfilerForSqlConnection(ref this.sqlConnectionProcessing);
        }

        internal virtual bool IsProfilerAvailable()
        {
            return Decorator.IsHostEnabled();
        }
#endif

        /// <summary>
        /// IDisposable implementation.
        /// </summary>
        /// <param name="disposing">The method has been called directly or indirectly by a user's code.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this.disposed)
            {
                if (disposing)
                {
#if NET45
                    // Net40 does not support framework event source and diagnostic source
                    if (this.httpDesktopDiagnosticSourceListener != null)
                    {
                        this.httpDesktopDiagnosticSourceListener.Dispose();
                    }

                    if (this.httpEventListener != null)
                    {
                        this.httpEventListener.Dispose();
                    }

                    if (this.sqlEventListener != null)
                    {
                        this.sqlEventListener.Dispose();
                    }

#endif
                    if (this.httpCoreDiagnosticSourceListener != null)
                    {
                        this.httpCoreDiagnosticSourceListener.Dispose();
                    }
                }

                this.disposed = true;
            }
        }

#if !NETCORE
        /// <summary>
        /// Initialize for framework event source (not supported for Net40).
        /// </summary>
        private void InitializeForDiagnosticAndFrameworkEventSource()
        {
            if (!this.DisableDiagnosticSourceInstrumentation)
            {
                DesktopDiagnosticSourceHttpProcessing desktopHttpProcessing = new DesktopDiagnosticSourceHttpProcessing(
                    this.telemetryConfiguration,
                    DependencyTableStore.Instance.WebRequestCacheHolder,
                    this.SetComponentCorrelationHttpHeaders,
                    this.ExcludeComponentCorrelationHttpHeadersOnDomains,
                    this.EffectiveProfileQueryEndpoint);
                this.httpDesktopDiagnosticSourceListener = new HttpDesktopDiagnosticSourceListener(desktopHttpProcessing, new ApplicationInsightsUrlFilter(this.telemetryConfiguration));
            }

            FrameworkHttpProcessing frameworkHttpProcessing = new FrameworkHttpProcessing(
                this.telemetryConfiguration,
                DependencyTableStore.Instance.WebRequestCacheHolder, 
                this.SetComponentCorrelationHttpHeaders, 
                this.ExcludeComponentCorrelationHttpHeadersOnDomains, 
                this.EffectiveProfileQueryEndpoint);

            // In 4.5 EventListener has a race condition issue in constructor so we retry to create listeners
            this.httpEventListener = RetryPolicy.Retry<InvalidOperationException, TelemetryConfiguration, FrameworkHttpEventListener>(
                config => new FrameworkHttpEventListener(frameworkHttpProcessing),
                this.telemetryConfiguration,
                TimeSpan.FromMilliseconds(10));

            this.sqlEventListener = RetryPolicy.Retry<InvalidOperationException, TelemetryConfiguration, FrameworkSqlEventListener>(
                config => new FrameworkSqlEventListener(config, DependencyTableStore.Instance.SqlRequestCacheHolder),
                this.telemetryConfiguration,
                TimeSpan.FromMilliseconds(10));
        }

        /// <summary>
        /// Initialize for runtime instrumentation or framework event source.
        /// </summary>
        private void InitializeForRuntimeInstrumentationOrFramework()
        {
            if (this.IsProfilerAvailable())
            {
                DependencyCollectorEventSource.Log.RemoteDependencyModuleInformation("Profiler is attached.");
                if (!this.DisableRuntimeInstrumentation)
                {
                    try
                    {
                        this.InitializeForRuntimeProfiler();
                        DependencyTableStore.Instance.IsProfilerActivated = true;
                    }
                    catch (Exception exp)
                    {
                        this.InitializeForDiagnosticAndFrameworkEventSource();
                        DependencyCollectorEventSource.Log.ProfilerFailedToAttachError(exp.ToInvariantString());
                    }
                }
                else
                {
                    // if config is set to disable runtime instrumentation then default to framework event source
                    this.InitializeForDiagnosticAndFrameworkEventSource();
                    DependencyCollectorEventSource.Log.RemoteDependencyModuleVerbose("Runtime instrumentation is set to disabled. Initialize with framework event source instead.");
                }
            }
            else
            {
                // if profiler is not attached then default to diagnositics and framework event source
                this.InitializeForDiagnosticAndFrameworkEventSource();

                // Log a message to indicate the profiler is not attached
                DependencyCollectorEventSource.Log.RemoteDependencyModuleProfilerNotAttached();
            }
        }
#endif

        [MethodImpl(MethodImplOptions.NoOptimization | MethodImplOptions.NoInlining)]
        private void PrepareActivity()
        {
            // when the first Activity is created in the process (on .NET Framework), it syncronizes DateTime.UtcNow 
            // in order to make it's StartTime and duration precise, it may take up to 16ms. 
            // Let's create the first Activity ever here, so we will not miss those 16ms on the first dependency tracking
            var activity = new Activity("Microsoft.ApplicationInights.Init");
            activity.Start();
            activity.Stop();
        }
    }
}
