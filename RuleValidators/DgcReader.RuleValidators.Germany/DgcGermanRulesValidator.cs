﻿using GreenpassReader.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using DgcReader.Models;
using System.Threading;
using Microsoft.Extensions.Logging;
using DgcReader.Interfaces.RulesValidators;
using DgcReader.Exceptions;
using DgcReader.RuleValidators.Germany.Models;
using Newtonsoft.Json.Linq;
using DgcReader.RuleValidators.Germany.CovpassDgcCertlogic.Data;
using DgcReader.RuleValidators.Germany.CovpassDgcCertlogic;
using DgcReader.RuleValidators.Germany.CovpassDgcCertlogic.Domain.Rules;
using DgcReader.RuleValidators.Germany.Providers;
using Newtonsoft.Json;

#if NETSTANDARD2_0_OR_GREATER || NET5_0_OR_GREATER || NET47_OR_GREATER
using Microsoft.Extensions.Options;
#endif

// Copyright (c) 2021 Davide Trevisan
// Licensed under the Apache License, Version 2.0

namespace DgcReader.RuleValidators.Germany
{
    /// <summary>
    /// Unofficial porting of the German rules from https://github.com/Digitaler-Impfnachweis/covpass-android.
    /// </summary>
    public class DgcGermanRulesValidator : IRulesValidator
    {

        protected readonly ILogger? Logger;
        private readonly DgcGermanRulesValidatorOptions Options;

        private readonly RuleIdentifiersProvider _ruleIdentifiersProvider;
        private readonly RulesProvider _rulesProvider;
        private readonly ValueSetIdentifiersProvider _valueSetIdentifiersProvider;
        private readonly ValueSetsProvider _valueSetsProvider;

        private readonly DefaultCertLogicEngine _certLogicEngine;
        private readonly CovPassGetRulesUseCase _rulesUseCase;

#if NET452
        /// <summary>
        /// Constructor
        /// </summary>
        public DgcGermanRulesValidator(HttpClient httpClient,
            DgcGermanRulesValidatorOptions? options = null,
            ILogger<DgcGermanRulesValidator>? logger = null)
        {
            Options = options ?? new DgcGermanRulesValidatorOptions();
            Logger = logger;

            // Valueset providers
            _ruleIdentifiersProvider = new RuleIdentifiersProvider(httpClient, Options, logger);
            _rulesProvider = new RulesProvider(httpClient, Options, _ruleIdentifiersProvider, logger);
            _valueSetIdentifiersProvider = new ValueSetIdentifiersProvider(httpClient, Options, logger);
            _valueSetsProvider = new ValueSetsProvider(httpClient, Options, _valueSetIdentifiersProvider, logger);

            // Validator implemnentations
            var affectedFieldsDataRetriever = new DefaultAffectedFieldsDataRetriever(Logger);
            var jsonLogicValidator = new DefaultJsonLogicValidator();

            _certLogicEngine = new DefaultCertLogicEngine(affectedFieldsDataRetriever, jsonLogicValidator, Logger);
            _rulesUseCase = new CovPassGetRulesUseCase();
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="DgcGermanRulesValidator"/>
        /// whithout using the DI mechanism. Useful for legacy applications
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        /// <returns></returns>
        public static DgcGermanRulesValidator Create(HttpClient httpClient,
            DgcGermanRulesValidatorOptions? options = null,
            ILogger<DgcGermanRulesValidator>? logger = null)
        {
            return new DgcGermanRulesValidator(httpClient, options, logger);
        }

#else
        /// <summary>
        /// Constructor
        /// </summary>
        public DgcGermanRulesValidator(HttpClient httpClient,
            IOptions<DgcGermanRulesValidatorOptions>? options = null,
            ILogger<DgcGermanRulesValidator>? logger = null)
        {
            Options = options?.Value ?? new DgcGermanRulesValidatorOptions();

            // Valueset providers
            _ruleIdentifiersProvider = new RuleIdentifiersProvider(httpClient, Options, logger);
            _rulesProvider = new RulesProvider(httpClient, Options, _ruleIdentifiersProvider, logger);
            _valueSetIdentifiersProvider = new ValueSetIdentifiersProvider(httpClient, Options, logger);
            _valueSetsProvider = new ValueSetsProvider(httpClient, Options, _valueSetIdentifiersProvider, logger);


            // Validator implemnentations
            var affectedFieldsDataRetriever = new DefaultAffectedFieldsDataRetriever(Logger);
            var jsonLogicValidator = new DefaultJsonLogicValidator();

            _certLogicEngine = new DefaultCertLogicEngine(affectedFieldsDataRetriever, jsonLogicValidator, Logger);
            _rulesUseCase = new CovPassGetRulesUseCase();
        }

        /// <summary>
        /// Factory method for creating an instance of <see cref="DgcGermanRulesValidator"/>
        /// whithout using the DI mechanism. Useful for legacy applications
        /// </summary>
        /// <param name="httpClient">The http client instance that will be used for requests to the server</param>
        /// <param name="options">The options for the provider</param>
        /// <param name="logger">Instance of <see cref="ILogger"/> used by the provider (optional).</param>
        /// <returns></returns>
        public static DgcGermanRulesValidator Create(HttpClient httpClient,
            DgcGermanRulesValidatorOptions? options = null,
            ILogger<DgcGermanRulesValidator>? logger = null)
        {
            return new DgcGermanRulesValidator(httpClient,
                options == null ? null : Microsoft.Extensions.Options.Options.Create(options),
                logger);
        }
#endif

        #region Implementation of IRulesValidator

        /// <inheritdoc/>
        public async Task<IRulesValidationResult> GetRulesValidationResult(EuDGC dgc, DateTimeOffset validationInstant, string countryCode = "DE", CancellationToken cancellationToken = default)
        {
            if (!await SupportsCountry(countryCode))
                throw new DgcException($"Rules validation for country {countryCode} is not supported by this provider");

            var result = new DgcRulesValidationResult();

            if (dgc == null)
            {
                result.Status = DgcResultStatus.NotEuDCC;
                return result;
            }

            try
            {
                var rulesSet = await _rulesProvider.GetValueSet(countryCode, cancellationToken);
                if (rulesSet == null)
                    throw new Exception("Unable to get validation rules");


                var certEntry = dgc.GetCertificateEntry();
                var issuerCountryCode = certEntry.Country;
                var certificateType = dgc.GetCertificateType();

                var rules = _rulesUseCase.Invoke(rulesSet.Rules,
                    validationInstant,
                    countryCode,
                    issuerCountryCode,
                    certificateType);

                var valueSets = await GetValueSetsJson();

                var externalParameters = new ExternalParameter
                {
                    ValidationClock = validationInstant,
                    ValueSets = valueSets, // TODO
                    CountryCode = countryCode,
                    Expiration = DateTimeOffset.MaxValue,   // Signature validation is done by another module
                    ValidFrom = DateTimeOffset.MinValue,    // Signature validation is done by another module
                    IssuerCountryCode = dgc.GetCertificateEntry()?.Country ?? string.Empty,
                    Kid = "",                               // Signature validation is done by another module
                    Region = "",
                };

                var certString = JsonConvert.SerializeObject(dgc);

                var test = _certLogicEngine.Validate(certificateType,
                    dgc.SchemaVersion,
                    rules,
                    externalParameters,
                    certString).ToArray();

            }
            catch (Exception e)
            {
                Logger?.LogError(e, $"Validation failed with error {e.Message}");
                result.Status = DgcResultStatus.NotValid;
            }
            return result;
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<string>> GetSupportedCountries(CancellationToken cancellationToken = default)
        {
            var rulesIdentifiersValueSet = await _ruleIdentifiersProvider.GetValueSet(cancellationToken);
            return rulesIdentifiersValueSet.Identifiers.Select(r => r.Country).Distinct().OrderBy(r => r).ToArray();
        }

        /// <inheritdoc/>
        public async Task RefreshRules(string? countryCode = null, CancellationToken cancellationToken = default)
        {
            if (!string.IsNullOrEmpty(countryCode))
            {
                await _rulesProvider.RefreshValueSet(countryCode, cancellationToken);
            }
            else
            {
                var supportedCountries = await GetSupportedCountries();
                foreach (var country in supportedCountries.Distinct().ToArray())
                {
                    await _rulesProvider.RefreshValueSet(country, cancellationToken);
                }
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsCountry(string countryCode, CancellationToken cancellationToken = default)
        {
            var supportedCountries = await GetSupportedCountries();
            return supportedCountries.Any(r=>r.Equals(countryCode, StringComparison.InvariantCultureIgnoreCase));
        }

        #endregion

        #region Private

        private async Task<Dictionary<string, JObject>> GetValueSetsJson(CancellationToken cancellationToken = default)
        {
            var valueSetsIdentifiers = await _valueSetIdentifiersProvider.GetValueSet(cancellationToken);

            var temp = new Dictionary<string, JObject>();

            if (valueSetsIdentifiers == null)
                return temp;

            foreach(var identifier in valueSetsIdentifiers.Identifiers)
            {
                var values = await _valueSetsProvider.GetValueSet(identifier.Id, cancellationToken);
                if (values != null)
                    temp.Add(values.Id, JObject.FromObject(values.Values));
            }
            return temp;
        }
        #endregion
    }
}