using Nancy.Extensions;
using System.Net.Http;
using System.Threading.Tasks;

namespace compute.frontend
{
    public class ProxyModule : Nancy.NancyModule
    {
        public ProxyModule()
        {
            int backendPort = Env.GetEnvironmentInt("COMPUTE_BACKEND_PORT", 8081);

            Get["/healthcheck"] = _ => "healthy";

            Get["/", true] =
            Get["/{uri*}", true] = async(p, ct) =>
            {
                var proxy_url = GetProxyUrl((string)p.uri, backendPort);
                var client = CreateProxyClient(Context);
                try
                {
                    var backendResponse = await client.GetAsync(proxy_url, ct);
                    var response = await CreateProxyResponse(backendResponse, Context);
                    return response;
                }
                catch
                {
                    return 500;
                }
            };

            Post["/", true] =
            Post["{uri*}", true] = async (p, ct) =>
            {
                var proxy_url = GetProxyUrl((string)p.uri, backendPort);
                var client = CreateProxyClient(Context);
                object o_content;
                StringContent content;
                if (Context.Items.TryGetValue("request-body", out o_content))
                    content = new StringContent(o_content as string);
                else
                    content = new StringContent(Context.Request.Body.AsString());

                try
                {
                    var backendResponse = await client.PostAsync(proxy_url, content, ct);
                    var response = await CreateProxyResponse(backendResponse, Context);
                    return response;
                }
                catch
                {
                    return 500;
                }
            };
        }

        string GetProxyUrl(string path, int backendPort)
        {
            if (string.IsNullOrWhiteSpace(path))
                return $"http://localhost:{backendPort}/";

            return $"http://localhost:{backendPort}/{path}";
        }

        HttpClient CreateProxyClient(Nancy.NancyContext context)
        {
            var handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            var client = new HttpClient(handler);
            foreach (var header in Context.Request.Headers)
            {
                if (header.Key == "Content-Length")
                    continue;
                if (header.Key == "Content-Type")
                    continue;
                client.DefaultRequestHeaders.Add(header.Key, header.Value);
            }
            client.DefaultRequestHeaders.Add("X-Compute-Id", (string)Context.Items["RequestId"]);
            client.DefaultRequestHeaders.Add("X-Compute-Host", (string)Context.Items["Hostname"]);
            return client;
        }

        async Task<Nancy.Response> CreateProxyResponse(HttpResponseMessage backendResponse, Nancy.NancyContext context)
        {
            string responseBody = await backendResponse.Content.ReadAsStringAsync();
            var response = (Nancy.Response)responseBody;
            response.StatusCode = (Nancy.HttpStatusCode)backendResponse.StatusCode;
            foreach (var header in backendResponse.Headers)
            {
                foreach (var value in header.Value)
                    response.Headers.Add(header.Key, value);
            }
            return response;
        }
    }
}
