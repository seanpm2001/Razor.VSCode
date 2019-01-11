﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.LanguageServer.Serialization;
using Microsoft.AspNetCore.Razor.OmniSharpPlugin;
using Microsoft.Build.Execution;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OmniSharp;
using OmniSharp.MSBuild.Notification;

namespace Microsoft.AspNetCore.Razor.OmnisharpPlugin
{
    [Export(typeof(IMSBuildEventSink))]
    internal class ProjectLoadListener : IMSBuildEventSink
    {
        // Internal for testing
        internal const string IntermediateOutputPathPropertyName = "IntermediateOutputPath";
        internal const string MSBuildProjectDirectoryPropertyName = "MSBuildProjectDirectory";
        internal const string RazorConfigurationFileName = "project.razor.json";
        internal const string ProjectCapabilityItemType = "ProjectCapability";
        internal const string TargetFrameworkPropertyName = "TargetFramework";
        internal const string TargetFrameworkVersionPropertyName = "TargetFrameworkVersion";

        private const string MSBuildProjectFullPathPropertyName = "MSBuildProjectFullPath";
        private const string DebugRazorOmnisharpPluginPropertyName = "_DebugRazorOmnisharpPlugin_";
        private readonly ILogger _logger;
        private readonly IEnumerable<RazorConfigurationProvider> _projectConfigurationProviders;
        private readonly ProjectEngineFactory _projectEngineFactory;
        private readonly TagHelperResolver _tagHelperResolver;
        private readonly OmniSharpWorkspace _workspace;

        [ImportingConstructor]
        public ProjectLoadListener(
            [ImportMany] IEnumerable<RazorConfigurationProvider> projectConfigurationProviders,
            ProjectEngineFactory projectEngineFactory,
            TagHelperResolver tagHelperResolver,
            OmniSharpWorkspace workspace,
            ILoggerFactory loggerFactory)
        {
            if (projectConfigurationProviders == null)
            {
                throw new ArgumentNullException(nameof(projectConfigurationProviders));
            }

            if (projectEngineFactory == null)
            {
                throw new ArgumentNullException(nameof(projectEngineFactory));
            }

            if (tagHelperResolver == null)
            {
                throw new ArgumentNullException(nameof(tagHelperResolver));
            }

            if (workspace == null)
            {
                throw new ArgumentNullException(nameof(workspace));
            }

            if (loggerFactory == null)
            {
                throw new ArgumentNullException(nameof(loggerFactory));
            }

            _logger = loggerFactory.CreateLogger<ProjectLoadListener>();
            _projectConfigurationProviders = projectConfigurationProviders;
            _projectEngineFactory = projectEngineFactory;
            _tagHelperResolver = tagHelperResolver;
            _workspace = workspace;
        }

        public async void ProjectLoaded(ProjectLoadedEventArgs args)
        {
            try
            {
                HandleDebug(args.ProjectInstance);

                if (!TryResolveConfigurationOutputPath(args.ProjectInstance, out var configPath))
                {
                    return;
                }

                var projectFilePath = args.ProjectInstance.GetPropertyValue(MSBuildProjectFullPathPropertyName);
                if (string.IsNullOrEmpty(projectFilePath))
                {
                    // This should never be true but we're being extra careful.
                    return;
                }

                var targetFramework = GetTargetFramework(args.ProjectInstance);
                if (string.IsNullOrEmpty(targetFramework))
                {
                    // This should never be true but we're being extra careful.
                    return;
                }

                var razorConfiguration = GetRazorConfiguration(args.ProjectInstance);
                var projectDirectory = args.ProjectInstance.GetPropertyValue(MSBuildProjectDirectoryPropertyName);
                var fileSystem = RazorProjectFileSystem.Create(projectDirectory);
                var projectEngine = _projectEngineFactory.Create(razorConfiguration, fileSystem, (builder) => { });
                var project = _workspace.CurrentSolution.GetProject(args.Id);
                var tagHelpers = await _tagHelperResolver.GetTagHelpersAsync(project, projectEngine, CancellationToken.None);

                var projectConfiguration = new RazorProjectConfiguration()
                {
                    ProjectFilePath = projectFilePath,
                    Configuration = razorConfiguration,
                    TargetFramework = targetFramework,
                    TagHelpers = tagHelpers,
                };

                var serializedOutput = JsonConvert.SerializeObject(
                    projectConfiguration,
                    Formatting.Indented,
                    JsonConverterCollectionExtensions.RazorConverters.ToArray());

                try
                {
                    File.WriteAllText(configPath, serializedOutput);
                }
                catch (Exception)
                {
                    // TODO: Add retry.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Unexpected exception got thrown from the Razor plugin: " + ex);
            }
        }

        // Internal for testing
        internal RazorConfiguration GetRazorConfiguration(ProjectInstance projectInstance)
        {
            var projectCapabilities = projectInstance
                .GetItems(ProjectCapabilityItemType)
                .Select(capability => capability.EvaluatedInclude)
                .ToList();

            var context = new RazorConfigurationProviderContext(projectCapabilities, projectInstance);
            foreach (var projectConfigurationProvider in _projectConfigurationProviders)
            {
                if (projectConfigurationProvider.TryResolveConfiguration(context, out var configuration))
                {
                    return configuration;
                }
            }

            if (FallbackConfigurationProvider.Instance.TryResolveConfiguration(context, out var fallbackConfiguration))
            {
                return fallbackConfiguration;
            }

            return null;
        }

        private void HandleDebug(ProjectInstance projectInstance)
        {
            var debugPlugin = projectInstance.GetPropertyValue(DebugRazorOmnisharpPluginPropertyName);
            if (!string.IsNullOrEmpty(debugPlugin) && string.Equals(debugPlugin, "true", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Waiting for a debugger to attach to the Razor Plugin. Process id: {Process.GetCurrentProcess().Id}");
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(1000);
                }
                Debugger.Break();
            }
        }

        // Internal for testing
        internal static bool TryResolveConfigurationOutputPath(ProjectInstance projectInstance, out string path)
        {
            var intermediateOutputPath = projectInstance.GetPropertyValue(IntermediateOutputPathPropertyName);
            if (string.IsNullOrEmpty(intermediateOutputPath))
            {
                path = null;
                return false;
            }

            if (!Path.IsPathRooted(intermediateOutputPath))
            {
                // Relative path, need to convert to absolute.
                var projectDirectory = projectInstance.GetPropertyValue(MSBuildProjectDirectoryPropertyName);
                if (string.IsNullOrEmpty(projectDirectory))
                {
                    // This should never be true but we're beign extra careful.
                    path = null;
                    return false;
                }

                intermediateOutputPath = Path.Combine(projectDirectory, intermediateOutputPath);
            }

            intermediateOutputPath = intermediateOutputPath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            path = Path.Combine(intermediateOutputPath, RazorConfigurationFileName);
            return true;
        }

        // Internal for testing
        internal static string GetTargetFramework(ProjectInstance projectInstance)
        {
            var targetFramework = projectInstance.GetPropertyValue(TargetFrameworkPropertyName);
            if (string.IsNullOrEmpty(targetFramework))
            {
                targetFramework = projectInstance.GetPropertyValue(TargetFrameworkVersionPropertyName);
            }

            return targetFramework;
        }
    }
}
