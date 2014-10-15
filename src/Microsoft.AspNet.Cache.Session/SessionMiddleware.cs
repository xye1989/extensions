﻿using System;
using System.Threading.Tasks;
using Microsoft.AspNet.Builder;
using Microsoft.AspNet.Http;
using Microsoft.Framework.Logging;
using Microsoft.Framework.OptionsModel;

namespace Microsoft.AspNet.Cache.Session
{
    public class SessionMiddleware
    {
        private static readonly Func<bool> ReturnTrue = () => true;
        private readonly RequestDelegate _next;
        private readonly SessionOptions _options;
        private readonly ILogger _logger;

        public SessionMiddleware([NotNull] RequestDelegate next, [NotNull] ILoggerFactory loggerFactory, [NotNull] IOptions<SessionOptions> options, [NotNull] ConfigureOptions<SessionOptions> configureOptions)
        {
            _next = next;
            _logger = loggerFactory.Create<SessionMiddleware>();
            if (configureOptions != null)
            {
                _options = options.GetNamedOptions(configureOptions.Name);
                configureOptions.Configure(_options);
            }
            else
            {
                _options = options.Options;
            }

            if (_options.Store == null)
            {
                throw new ArgumentException("ISessionStore must be specified");
            }

            _options.Store.Connect();
        }

        public async Task Invoke(HttpContext context)
        {
            // TODO: bool isNewSession = false;
            Func<bool> tryEstablishSession = ReturnTrue;
            var sessionKey = context.Request.Cookies.Get(_options.CookieName);
            if (string.IsNullOrEmpty(sessionKey))
            {
                // No cookie, new session.
                // TODO: isNewSession = true;
                sessionKey = Guid.NewGuid().ToString(); // TODO: Crypto-random GUID
                var establisher = new SessionEstablisher(context, sessionKey, _options);
                tryEstablishSession = establisher.TryEstablishSession;
            }

            var feature = new SessionFeature();
            feature.Factory = new SessionFactory(sessionKey, _options.Store, _options.IdleTimeout, tryEstablishSession);
            feature.Session = feature.Factory.Create();
            context.SetFeature<ISessionFeature>(feature);

            try
            {
                await _next(context);
            }
            finally
            {
                // context.SetFeature<ISessionFeature>(null); // TODO: Not supported yet

                if (feature.Session != null)
                {
                    try
                    {
                        // TODO: try/catch log?
                        feature.Session.Commit();
                    }
                    catch (Exception ex)
                    {
                        _logger.WriteError("Error closing the session.", ex);
                    }
                }
            }
        }

        private class SessionEstablisher
        {
            private HttpContext _context;
            private string _sessionKey;
            private SessionOptions _options;
            private bool _responseSent;
            private bool _shouldEstablishSession;

            public SessionEstablisher(HttpContext context, string sessionKey, SessionOptions options)
            {
                _context = context;
                _sessionKey = sessionKey;
                _options = options;
                context.Response.OnSendingHeaders(OnSendingHeadersCallback, state: this);
            }

            private static void OnSendingHeadersCallback(object state)
            {
                var establisher = (SessionEstablisher)state;
                establisher._responseSent = true;
                if (establisher._shouldEstablishSession)
                {
                    establisher.SetCookie();
                }
            }

            private void SetCookie()
            {
                var cookieOptions = new CookieOptions
                {
                    Domain = _options.CookieDomain,
                    HttpOnly = _options.CookieHttpOnly,
                    Path = _options.CookiePath ?? "/",
                };

                _context.Response.Cookies.Append(_options.CookieName, _sessionKey, cookieOptions);

                _context.Response.Headers.Set(
                    "Cache-Control",
                    "no-cache");

                _context.Response.Headers.Set(
                    "Pragma",
                    "no-cache");

                _context.Response.Headers.Set(
                    "Expires",
                    "-1");
            }

            // Returns true if the session has already been established, or if it still can be because the reponse has not been sent.
            internal bool TryEstablishSession()
            {
                return (_shouldEstablishSession |= !_responseSent);
            }
        }
    }
}