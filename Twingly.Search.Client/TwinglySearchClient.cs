﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using Twingly.Search.Client.Domain;
using Twingly.Search.Client.Infrastructure;

namespace Twingly.Search.Client
{
    /// <summary>
    /// Enables querying the Twingly Search API.
    /// </summary>
    public class TwinglySearchClient : ITwinglySearchClient
    {
        private readonly HttpClient internalClient = null;
        private readonly TwinglyConfiguration config = null;
        private static readonly string requestFormat = "?key={0}&" + Constants.SearchPattern + "={1}&xmloutputversion=2";

        private static readonly string userAgentTemplate = "{0}/.NET v." + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

        private string userAgent = null;

        /// <summary>
        /// Gets or sets the HTTP request User-Agent property.
        /// </summary>
        public string UserAgent
        {
            get
            {
                return userAgent;
            }
            set
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    userAgent = string.Format(userAgentTemplate, value);
                    internalClient.DefaultRequestHeaders.Remove("User-Agent");
                    internalClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
                }
            }
        }


        /// <summary>
        /// Initializes a new instance of <see cref="TwinglySearchClient"/>
        /// with the API key configured in the application configuration file.
        /// </summary>
        public TwinglySearchClient() : this(new LocalConfiguration())
        {

        }

        /// <summary>
        /// Initializes a new instance of <see cref="TwinglySearchClient"/>
        /// with the given <paramref name="config"/>
        /// </summary>
        public TwinglySearchClient(TwinglyConfiguration config)
        {
            this.config = config;
            internalClient = new HttpClient();
            InitializeClient();
        }

        /// <summary>
        /// Initializes a new instance of <see cref="TwinglySearchClient"/>.
        /// Intended for automated testing only.
        /// </summary>
        internal TwinglySearchClient(TwinglyConfiguration clientConfig, HttpClient client)
        {
            config = clientConfig;
            internalClient = client;
            InitializeClient();
        }

        private void InitializeClient()
        {
            internalClient.BaseAddress = new Uri(Constants.ApiBaseAddress);
            internalClient.Timeout = TimeSpan.FromMilliseconds(config.RequestTimeoutMilliseconds);
            UserAgent = "Twingly Search API Client";
        }

        /// <summary>
        /// Executes <paramref name="theQuery"/> and returns the result asynchronously.
        /// </summary>
        /// <param name="theQuery">The blog search query that will be sent to the API.</param>
        /// <returns>
        /// Result of <paramref name="theQuery"/>.
        /// </returns>
        /// <exception cref="TwinglyServiceUnavailableException">Thrown when the Twingly Search API reports that service is unavailable.</exception>
        /// <exception cref="ApiKeyDoesNotExistException">Thrown when the supplied API key was not recognized by the remote server.</exception>
        /// <exception cref="UnauthorizedApiKeyException">
        /// Thrown when the supplied API key can't be used to service the query. 
        /// </exception>
        /// <exception cref="TwinglyRequestException">Thrown when any other error occurs.</exception>
        public async Task<QueryResult> QueryAsync(Query theQuery)
        {
            if (theQuery == null)
            {
                throw new ArgumentNullException(nameof(theQuery));
            }
            theQuery.ThrowIfInvalid();

            string requestUri = BuildRequestUriFrom(theQuery);
            string result = string.Empty;
            QueryResult returnValue = null;
            try
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();

                using (Stream resultStream =
                    await internalClient.GetStreamAsync(requestUri).ConfigureAwait(false)) // continue on the thread pool to avoid deadlocks
                {
                    sw.Stop();
                    Debug.WriteLine("Received server response in {0} ms", sw.ElapsedMilliseconds);
                    sw.Restart();
                    result = resultStream.ReadStreamIntoString();
                }

                returnValue = result.DeserializeXml<QueryResult>();
                returnValue.Posts = returnValue.Posts?.Where(p => p.ContentType.Equals(ContentType.Blog)).ToList();
                sw.Stop();
                Debug.WriteLine("Deserialized server response in {0} ms", sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                var wrapperException = MapResponseToException(result, ex);
                throw wrapperException;
            }

            return returnValue;
        }

        /// <summary>
        /// Executes <paramref name="theQuery"/> and returns the result synchronously.
        /// </summary>
        /// <param name="theQuery">The blog search query that will be sent to the API.</param>
        /// <returns>
        /// Result of <paramref name="theQuery"/>.
        /// </returns>
        /// <exception cref="TwinglyServiceUnavailableException">Thrown when the Twingly Search API reports that service is unavailable.</exception>
        /// <exception cref="ApiKeyDoesNotExistException">Thrown when the supplied API key was not recognized by the remote server.</exception>
        /// <exception cref="UnauthorizedApiKeyException">
        /// Thrown when the supplied API key can't be used to service the query. 
        /// </exception>
        /// <exception cref="TwinglyRequestException">Thrown when any other error occurs.</exception>
        public QueryResult Query(Query theQuery)
        {
            QueryResult returnValue = null;

            try
            {
                returnValue = QueryAsync(theQuery).Result;
            }
            catch (AggregateException asyncEx)
            {
                // throw the underlying exception without messing up the stack trace
                ExceptionDispatchInfo.Capture(asyncEx.InnerException).Throw();
            }

            return returnValue;
        }

        private Exception MapResponseToException(string responseString, Exception inner)
        {

            BlogStream errorResponse = null;
            if (inner is TaskCanceledException)
            {
                return new TwinglyRequestException("The request has timed out", inner);
            }
            if (string.IsNullOrWhiteSpace(responseString))
            {
                return new TwinglyRequestException("Twingly Search API returned an empty response", inner);
            }
            try
            {
                errorResponse = responseString.DeserializeXml<BlogStream>();
            }
            catch (Exception ex)
            {
                return new TwinglyRequestException
                    ("Couldn't deserialize API response. See the inner exception for details", ex);
            }

            if (errorResponse != null)
            {
                if (errorResponse.Result.Text.Equals(Constants.ServiceUnavailable, StringComparison.InvariantCultureIgnoreCase))
                {
                    return new TwinglyServiceUnavailableException(inner);
                }
                else if (errorResponse.Result.Text.Equals(Constants.ApiKeyDoesNotExist, StringComparison.InvariantCultureIgnoreCase))
                {
                    return new ApiKeyDoesNotExistException(inner);
                }
                else if (errorResponse.Result.Text.Equals(Constants.UnauthorizedApiKey, StringComparison.InvariantCultureIgnoreCase))
                {
                    return new UnauthorizedApiKeyException(inner);
                }
                else
                {
                    // since we managed to deserialize into BlogStream, we just don't know what the error is.
                    // This means that it's reasonable not to be afraid that the responseString is too large.
                    // It also shouldn't contain any sensitive data. Hence, including it into the error message is safe.
                    return new TwinglyRequestException
                        (string.Format("Twingly Search API returned an unknown error: {0}", responseString), inner);
                }
            }
            else
            {
                return new TwinglyRequestException(inner);
            }
        }

        private string BuildRequestUriFrom(Query theQuery)
        {
            string initialRequest = string.Format(requestFormat, config.ApiKey, Uri.EscapeDataString(theQuery.SearchPattern));
            var builder = new StringBuilder(initialRequest);
            if (!string.IsNullOrWhiteSpace(theQuery.Language))
            {
                builder.AppendFormat("&{0}={1}", Constants.DocumentLanguage, theQuery.Language);
            }
            if (theQuery.StartTime.HasValue)
            {
                builder.AppendFormat("&{0}={1}", Constants.StartTime, theQuery.StartTime.Value.ToString(Constants.ApiDateFormat));
            }
            if (theQuery.EndTime.HasValue)
            {
                builder.AppendFormat("&{0}={1}", Constants.EndTime, theQuery.EndTime.Value.ToString(Constants.ApiDateFormat));
            }

            return builder.ToString();
        }
    }
}
