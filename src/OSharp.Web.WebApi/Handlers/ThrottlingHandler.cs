﻿using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using OSharp.Web.Http.Caching;
using OSharp.Web.Http.Extensions;


namespace OSharp.Web.Http.Handlers
{
    /// <summary>
    /// API限流拦截处理器
    /// </summary>
    public class ThrottlingHandler : DelegatingHandler
    {
        private readonly IThrottleStore _store;
        private readonly Func<string, long> _maxRequestsForUserIdentifier;
        private readonly TimeSpan _period;
        private readonly string _message;

        /// <summary>
        /// 初始化一个<see cref="ThrottlingHandler"/>类型的新实例
        /// </summary>
        public ThrottlingHandler(IThrottleStore store, Func<string, long> maxRequestsForUserIdentifier, TimeSpan period)
            : this(store, maxRequestsForUserIdentifier, period, "The allowed number of requests has been exceeded.")
        { }

        /// <summary>
        /// 初始化一个<see cref="ThrottlingHandler"/>类型的新实例
        /// </summary>
        public ThrottlingHandler(IThrottleStore store, Func<string, long> maxRequestsForUserIdentifier, TimeSpan period, string message)
        {
            _store = store;
            _maxRequestsForUserIdentifier = maxRequestsForUserIdentifier;
            _period = period;
            _message = message;
        }

        /// <summary>
        /// 获取请求标识，这里返回请求的IP地址
        /// </summary>
        /// <param name="request"></param>
        /// <returns></returns>
        protected virtual string GetUserIdentifier(HttpRequestMessage request)
        {
            return request.GetClientIpAddress();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var identifier = GetUserIdentifier(request);

            if (string.IsNullOrEmpty(identifier))
            {
                return CreateResponse(request, HttpStatusCode.Forbidden, "Could not identify client.");
            }

            var maxRequests = _maxRequestsForUserIdentifier(identifier);

            ThrottleEntry entry = null;
            if (_store.TryGetValue(identifier, out entry))
            {
                if (entry.PeriodStart + _period < DateTime.UtcNow)
                {
                    _store.Rollover(identifier);
                }
            }
            _store.IncrementRequests(identifier);
            if (!_store.TryGetValue(identifier, out entry))
            {
                return CreateResponse(request, HttpStatusCode.Forbidden, "Could not identify client.");
            }

            Task<HttpResponseMessage> response = null;
            if (entry.Requests > maxRequests)
            {
                response = CreateResponse(request, HttpStatusCode.Conflict, _message);
            }
            else
            {
                response = base.SendAsync(request, cancellationToken);
            }

            return response.ContinueWith(task =>
            {
                var remaining = maxRequests - entry.Requests;
                if (remaining < 0)
                {
                    remaining = 0;
                }

                var httpResponse = task.Result;
                httpResponse.Headers.Add("RateLimit-Limit", maxRequests.ToString());
                httpResponse.Headers.Add("RateLimit-Remaining", remaining.ToString());

                return httpResponse;
            }, cancellationToken);
        }

        protected Task<HttpResponseMessage> CreateResponse(HttpRequestMessage request, HttpStatusCode statusCode, string message)
        {
            var tsc = new TaskCompletionSource<HttpResponseMessage>();
            var response = request.CreateResponse(statusCode);
            response.ReasonPhrase = message;
            response.Content = new StringContent(message);
            tsc.SetResult(response);
            return tsc.Task;
        }
    }

}
