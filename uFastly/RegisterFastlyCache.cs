using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using System.Web.Configuration;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.Models;
using Umbraco.Core.Publishing;
using Umbraco.Core.Services;
using Umbraco.Web;
using Umbraco.Web.Routing;

namespace uFastly
{
    public class RegisterFastlyCache : ApplicationEventHandler
    {
        private static readonly HttpClient Client;
        private static readonly int MaxAge;

        private const string CacheControlPropertyName = "cacheControlMaxAge";
        private const string FastlyApplicationIdKey = "Fastly:ApplicationId";
        private const string FastlyMaxAgeKey = "Fastly:MaxAge";
        private const string FastlyApiKey = "Fastly:ApiKey";
        private const string FastlyStaleWhileInvalidateKey = "Fastly:StaleWhileInvalidate";
        private const string FastlyDisableAzureARRAffinityKey = "Fastly:DisableAzureARRAffinity";
        private const string FastlyPurgeAllOnPublishKey = "Fastly:PurgeAllOnPublish";

        protected override void ApplicationStarted(UmbracoApplicationBase umbracoApplication, ApplicationContext applicationContext)
        {
            PublishedContentRequest.Prepared += ConfigurePublishedContentRequestCaching;

            bool purgeOnPublish;
            bool.TryParse(WebConfigurationManager.AppSettings[FastlyPurgeAllOnPublishKey], out purgeOnPublish);
            if (purgeOnPublish) ContentService.Published += PurgeAll;
        }

        static RegisterFastlyCache()
        {
            Client = new HttpClient {BaseAddress = new Uri("https://api.fastly.com/")};

            Client.DefaultRequestHeaders.Accept.Clear();
            Client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Client.DefaultRequestHeaders.Add("Fastly-Key", WebConfigurationManager.AppSettings[FastlyApiKey]);

            if (WebConfigurationManager.AppSettings.AllKeys.Contains(FastlyMaxAgeKey))
            {
                int.TryParse(WebConfigurationManager.AppSettings[FastlyMaxAgeKey], out MaxAge);
            }
        }

        protected void PurgeAll(IPublishingStrategy strategy, PublishEventArgs<IContent> e)
        {
            var appId = WebConfigurationManager.AppSettings[FastlyApplicationIdKey];
            using (var task = Client.PostAsync($"service/{appId}/purge_all", new StringContent("")))
                task.Wait();
        }

        private void ConfigurePublishedContentRequestCaching(object sender, EventArgs eventArgs)
        {
            var req = sender as PublishedContentRequest;
            var res = HttpContext.Current.Response;

            if (req == null || req.HasPublishedContent == false) return;
            if (HttpContext.Current == null) return;

            var content = req.PublishedContent;
            var maxAge = MaxAge;

            if (content.HasProperty(CacheControlPropertyName) && content.HasValue(CacheControlPropertyName))
            {
                maxAge = content.GetPropertyValue<int>(CacheControlPropertyName);
            }

            if (maxAge <= 0) return;

            var expires = DateTime.Now.AddSeconds(maxAge);
            res.Cache.SetCacheability(HttpCacheability.Public);
            res.Cache.SetExpires(expires);
            res.Cache.SetMaxAge(new TimeSpan(0, 0, maxAge));

            // stale while invalidate - https://docs.fastly.com/guides/performance-tuning/serving-stale-content
            int staleWhileInvalidate;
            if (int.TryParse(WebConfigurationManager.AppSettings[FastlyStaleWhileInvalidateKey], out staleWhileInvalidate) && staleWhileInvalidate > 0)
            {
                res.Cache.AppendCacheExtension($"stale-while-revalidate={staleWhileInvalidate}");
            }
            
            // disable ARRAffinity Set-Cookie, which results in a cache miss - UNDERSTAND THE IMPLICATIONS OF THIS ON AZURE
            bool disableARRAffinity;
            if (bool.TryParse(WebConfigurationManager.AppSettings[FastlyDisableAzureARRAffinityKey], out disableARRAffinity) && disableARRAffinity)
            {
                //res.Headers.Add("Arr-Disable-Session-Affinity", "True");
            }
        }
    }
}
