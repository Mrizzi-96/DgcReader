﻿using DgcReader.Interfaces.BlacklistProviders;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System;
using DgcReader.Providers.Abstractions;
using DgcReader.BlacklistProviders.Italy.Entities;
using DgcReader.Interfaces.Deserializers;
using DgcReader.Deserializers.Italy;

#if !NET452
using Microsoft.Extensions.Options;
#endif

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace DgcReader.BlacklistProviders.Italy
{
    /// <summary>
    /// Blacklist provider using the Italian backend
    /// </summary>
    public class ItalianDrlBlacklistProvider : IBlacklistProvider, ICustomDeserializerDependentService, IDisposable
    {
        private readonly ItalianDrlBlacklistProviderOptions Options;
        private readonly ILogger<ItalianDrlBlacklistProvider>? Logger;
        private readonly ItalianDrlBlacklistManager BlacklistManager;
        private readonly SingleTaskRunner<SyncStatus> RefreshBlacklistTaskRunner;
        private DateTime LastRefreshAttempt;

        /// <inheritdoc cref="ItalianDrlBlacklistManager.DownloadProgressChanged"/>
        public event EventHandler<DownloadProgressEventArgs> DownloadProgressChanged
        {
            add { BlacklistManager.DownloadProgressChanged += value; }
            remove { BlacklistManager.DownloadProgressChanged -= value; }
        }

        #region Constructor
#if NET452
        /// <summary>
        /// Constructor for the provider
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        public ItalianDrlBlacklistProvider(HttpClient httpClient,
            ItalianDrlBlacklistProviderOptions? options = null,
            ILogger<ItalianDrlBlacklistProvider>? logger = null)
        {
            Options = options ?? new ItalianDrlBlacklistProviderOptions();
            Logger = logger;

            var drlClient = new ItalianDrlBlacklistClient(httpClient, logger);
            BlacklistManager = new ItalianDrlBlacklistManager(Options, drlClient, logger);
            RefreshBlacklistTaskRunner = new SingleTaskRunner<SyncStatus>(async ct =>
            {
                LastRefreshAttempt = DateTime.Now;
                return await BlacklistManager.UpdateFromServer(ct);
            }, Logger);
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="ItalianDrlBlacklistProvider"/>
        /// whithout using the DI mechanism. Useful for legacy applications
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        /// <returns></returns>
        public static ItalianDrlBlacklistProvider Create(HttpClient httpClient,
            ItalianDrlBlacklistProviderOptions? options = null,
            ILogger<ItalianDrlBlacklistProvider>? logger = null)
        {
            return new ItalianDrlBlacklistProvider(httpClient, options, logger);
        }
#else
        /// <summary>
        /// Constructor for the provider
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        public ItalianDrlBlacklistProvider(HttpClient httpClient,
            IOptions<ItalianDrlBlacklistProviderOptions>? options = null,
            ILogger<ItalianDrlBlacklistProvider>? logger = null)
        {
            Options = options?.Value ?? new ItalianDrlBlacklistProviderOptions();
            Logger = logger;

            var drlClient = new ItalianDrlBlacklistClient(httpClient, logger);
            BlacklistManager = new ItalianDrlBlacklistManager(Options, drlClient, logger);
            RefreshBlacklistTaskRunner = new SingleTaskRunner<SyncStatus>(async ct =>
            {
                LastRefreshAttempt = DateTime.Now;
                return await BlacklistManager.UpdateFromServer(ct);
            }, Logger);
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="ItalianDrlBlacklistProvider"/>
        /// whithout using the DI mechanism. Useful for legacy applications
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        /// <returns></returns>
        public static ItalianDrlBlacklistProvider Create(HttpClient httpClient,
            ItalianDrlBlacklistProviderOptions? options = null,
            ILogger<ItalianDrlBlacklistProvider>? logger = null)
        {
            return new ItalianDrlBlacklistProvider(httpClient,
                options == null ? null : Microsoft.Extensions.Options.Options.Create(options),
                logger);
        }
#endif
        #endregion

        #region Implementation of IBlacklistProvider

        /// <inheritdoc/>
        public async Task<bool> IsBlacklisted(string certificateIdentifier, CancellationToken cancellationToken = default)
        {
            // Get latest check datetime
            var status = await BlacklistManager.GetSyncStatus(true, cancellationToken);


            if (status.LastCheck.Add(Options.MaxFileAge) < DateTime.Now)
            {
                // MaxFileAge expired

                var refreshTask = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);

                try
                {
                    // Wait for the task to complete
                    await refreshTask;
                }
                catch (Exception e)
                {
                    Logger?.LogError(e, $"Can not refresh ItalianDrlBlacklist from remote server. " +
                        $"Values from DRL version {status.CurrentVersion}, checked on {status.LastCheck} have reached MaxFileAge and can no longer be used.");
                    throw;
                }

            }
            else if (status.LastCheck.Add(Options.RefreshInterval) < DateTime.Now ||
                status.HasPendingDownload())
            {
                // Normal expiration

                // If min refresh expired
                if (LastRefreshAttempt.Add(Options.MinRefreshInterval) < DateTime.Now)
                {
                    var refreshTask = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);
                    if (!Options.UseAvailableValuesWhileRefreshing)
                    {
                        try
                        {
                            // Wait for the task to complete
                            await refreshTask;
                        }
                        catch (Exception e)
                        {
                            // If refresh fail, continue until MaxFileAge
                            Logger?.LogWarning(e, $"Can not refresh ItalianDrlBlacklist from remote server: {e.Message}. Values from DRL version {status.CurrentVersion}, checked on {status.LastCheck} will be used");
                        }

                    }
                }
            }

            return await BlacklistManager.ContainsUCVI(certificateIdentifier, cancellationToken);
        }

        /// <inheritdoc/>
        public async Task RefreshBlacklist(CancellationToken cancellationToken = default)
        {
            var task = await RefreshBlacklistTaskRunner.RunSingleTask(cancellationToken);
            await task;
        }
        #endregion

        #region Implementation of ICustomDeserializerDependentService
        /// <inheritdoc/>
        public IDgcDeserializer GetCustomDeserializer() => new ItalianDgcDeserializer();
        #endregion

        /// <inheritdoc/>
        public void Dispose()
        {
            RefreshBlacklistTaskRunner.Dispose();
        }
    }
}