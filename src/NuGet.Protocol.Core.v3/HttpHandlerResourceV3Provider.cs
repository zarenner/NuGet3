﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Configuration;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3.Data;

namespace NuGet.Protocol.Core.v3
{
    public class HttpHandlerResourceV3Provider : ResourceProvider
    {
        private static readonly string[] _authenticationSchemes = new[] { "Basic", "NTLM", "Negotiate" };

        public HttpHandlerResourceV3Provider()
            : base(typeof(HttpHandlerResource),
                  nameof(HttpHandlerResourceV3Provider),
                  NuGetResourceProviderPositions.Last)
        {
        }

        public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token)
        {
            HttpHandlerResourceV3 curResource = null;

            // Everyone gets a dataclient
            var httpHandler = TryGetCredentialAndProxy(source.PackageSource) ?? DataClient.DefaultHandler;
            curResource = new HttpHandlerResourceV3(httpHandler);

            return Task.FromResult(new Tuple<bool, INuGetResource>(curResource != null, curResource));
        }

#if DNXCORE50

        private HttpClientHandler TryGetCredentialAndProxy(PackageSource packageSource)
        {
            return new HttpClientHandler();
        }
#else

        private HttpClientHandler TryGetCredentialAndProxy(PackageSource packageSource)
        {
            var uri = new Uri(packageSource.Source);
            var proxy = ProxyCache.Instance.GetProxy(uri);
            var credential = CredentialStore.Instance.GetCredentials(uri);

            if (proxy != null
                && proxy.Credentials == null)
            {
                proxy.Credentials = CredentialCache.DefaultCredentials;
            }

            if (credential == null
                && !String.IsNullOrEmpty(packageSource.UserName)
                && !String.IsNullOrEmpty(packageSource.Password))
            {
                var cache = new CredentialCache();
                foreach (var scheme in _authenticationSchemes)
                {
                    cache.Add(uri, scheme, new NetworkCredential(packageSource.UserName, packageSource.Password));
                }
                credential = cache;
            }

            if (proxy != null)
            {
                ProxyCache.Instance.Add(proxy);
            }
            if (credential != null)
            {
                CredentialStore.Instance.Add(uri, credential);
            }

            return new CredentialPromptWebRequestHandler(uri)
            {
                Proxy = proxy,
                Credentials = credential
            };
        }

        private class CredentialPromptWebRequestHandler : WebRequestHandler
        {
            private Uri _serviceIndexUri;

            public CredentialPromptWebRequestHandler(Uri serviceIndexUri)
            {
                _serviceIndexUri = serviceIndexUri;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                while (true)
                {
                    try
                    {
                        AddBasicAuthorizationHeaderIfAvailable(request);

                        var response = await base.SendAsync(request, cancellationToken);
                        if (HttpHandlerResourceV3.ProxyPassed != null && Proxy != null)
                        {
                            HttpHandlerResourceV3.ProxyPassed(Proxy);
                        }

                        return response;
                    }
                    catch (HttpRequestException ex)
                    {
                        if (ProxyAuthenticationRequired(ex) &&
                            HttpHandlerResourceV3.PromptForProxyCredentials != null)
                        {
                            // prompt use for proxy credentials.
                            var credentials = HttpHandlerResourceV3.PromptForProxyCredentials(request.RequestUri, Proxy);
                            if (credentials == null)
                            {
                                throw;
                            }

                            // use the user provider credential to send the request again.
                            Proxy.Credentials = credentials;
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }

            /// <summary>
            /// Adds an explicit Authorization header to the request, if basic auth credentials for the package source
            /// are available, no authorization header already exists on the request, and the request is for the
            /// same scheme and authority as the service index.
            /// </summary>
            /// <remarks>
            /// Resolves https://github.com/NuGet/Home/issues/742: NuGet should set PreAuthenticate to true to reduce the number of web requests
            /// Note that PreAuthenticate doesn't do what you'd expect (it still doesn't use creds on the very first request),
            /// so we set Authorization manually.
            /// </remarks>
            private void AddBasicAuthorizationHeaderIfAvailable(HttpRequestMessage request)
            {
                if (this.Credentials != null &&
                    Uri.Compare(request.RequestUri, _serviceIndexUri, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.InvariantCultureIgnoreCase) == 0 &&
                    !request.Headers.Contains("Authorization"))
                {
                    NetworkCredential basicCred = this.Credentials.GetCredential(_serviceIndexUri, "Basic");

                    if (basicCred != null)
                    {
                        byte[] rawUserAndPass = System.Text.Encoding.UTF8.GetBytes(String.Format("{0}:{1}", basicCred.UserName, basicCred.Password));
                        string authValueEncoded = Convert.ToBase64String(rawUserAndPass);
                        request.Headers.Add("Authorization", String.Format("Basic {0}", authValueEncoded));
                    }
                }
            }

            // Returns true if the cause of the exception is proxy authentication failure
            private bool ProxyAuthenticationRequired(Exception ex)
            {
                var webException = ex.InnerException as WebException;
                if (webException == null)
                {
                    return false;
                }

                var response = webException.Response as HttpWebResponse;
                return response?.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
            }
        }

#endif
    }
}